using System.Diagnostics;
using System.Text.Json;

namespace ShockWave.Overlay;

internal sealed partial class OverlayForm
{
    private readonly Action<BridgeConfig> saveConfig;
    private readonly Label hubStatus = StatusLabel("Unknown");
    private sealed record NameOperation(string RequestId, string Uuid, string Name);
    private readonly Dictionary<string, NameOperation> nameRequests = [];
    private readonly Dictionary<string, (string Uuid, string Name)> failedNames = [];
    private readonly Dictionary<string, (string Uuid, string Name)> deferredNames = [];

    private TableLayoutPanel BuildStatusRow(bool local = false)
    {
        var row = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 7, RowCount = 1,
            BackColor = Background, Margin = Padding.Empty, Padding = new Padding(0, 0, 0, 12) };
        for (var column = 0; column < 7; column++)
            row.ColumnStyles.Add(column % 2 == 0 ? new ColumnStyle(SizeType.Percent, 25) : new ColumnStyle(SizeType.Absolute, 12));
        row.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        if (local)
        {
            Add("Bluetooth", "Bluetooth", bluetoothStatus,
                "Shows whether this app's Bluetooth adapter is ready. Active means the adapter is available; each toy still needs its own connection. Simulated means no hardware is used. Use Settings → Bluetooth toys for the source or Reset Bluetooth.", 0);
            Add("VRC toy control", "VRC control", curveStatus,
                "Shows the local avatar panel connection. Enable OSC in VRChat and show VRC toy control on your avatar. Connected toys appear automatically. Desktop input replaces that function's VR loop; Toy panel sensitivity and Max % apply to all input.", 2);
            Add("Scan toys", "Scan toys", scanStatus,
                "Click this panel to start or stop scanning. Scanning discovers nearby toys; Inactive means no scan is running. Scans finish automatically. Stopping also pauses background scans until you start scanning again or reset Bluetooth. Starting a scan stops current toy activity.", 4);
            Add("Reset Bluetooth", "Reset BT", resetBluetoothStatus,
                "Click this panel to stop current toy activity, release the app's Bluetooth sessions and reconnect with a fresh session. Saved sensitivity and maximums are kept; Input returns to zero. Busy means another operation is still running. This resets the app connection, not the Windows Bluetooth radio.", 6);
        }
        else
        {
            Add("Bridge", "Bridge", bridgeStatus,
                $"The bridge connects your VRChat avatar controls with {config.Provider}.\n\nIt starts automatically in Test mode when you open this app. If stopped, click Start bridge. Stop bridge pauses the connection.\n\nTest mode shows commands without activating a device.", 0);
            Add(config.Provider + " API", "API", apiStatus,
                config.IsPiShock ? "Connect directly using your PiShock username and API key in Settings. Save, Start bridge, then use Add shocker to choose an available device. No extra program is required. Connected confirms API access; it does not confirm hub/shocker power."
                : "The API receives OpenShock logs and sends commands in Live mode.\n\nCreate an API token in OpenShock and enter its generated value in Settings. Add each shocker UUID under Options. Remember token saves it with Windows encryption.\n\nConnected confirms API access; check Hub for the hardware connection.", 2);
            Add(config.Provider + " Hub", "Hub", hubStatus,
                config.IsPiShock ? "The PiShock hub is the powered Wi-Fi device that sends commands to your shockers. API Connected is separate from hub availability. Unknown means no supported hub status report is available. Check PiShock's website for hub connectivity; shocker power and RF reception are not confirmed here."
                : "The hub is the powered Wi-Fi device that sends RF commands to your shockers.\n\nOnline means OpenShock reports that the hub is connected. If Offline, plug it in and check its Wi-Fi connection in OpenShock.\n\nOnly hubs for shockers added here are counted; a shared hub is counted once. Unknown means live status is unavailable. Online does not confirm shocker power or RF reception.", 4);
            Add("VRChat OSC", "OSC", oscStatus,
                $"OSC carries avatar controls between VRChat and this app.\n\nEnable OSC in VRChat's Action Menu. Keep ports 9001 for input and 9000 for feedback. Toy control shares this listener with PiCtrl; {config.Provider} still needs Start bridge. Listening means the port is open; VRChat still needs to be running.", 6);
        }
        var lastWidth = -1;
        row.SizeChanged += (_, _) => FitColumns();
        FitColumns();
        return row;

        void FitColumns()
        {
            if (row.ClientSize.Width == lastWidth || row.ClientSize.Width <= 36) return;
            lastWidth = row.ClientSize.Width;
            var available = lastWidth - 36;
            for (var column = 0; column < 4; column++)
            {
                var style = row.ColumnStyles[column * 2];
                style.SizeType = SizeType.Absolute;
                style.Width = available / 4 + (column < available % 4 ? 1 : 0);
            }
        }

        void Add(string title, string compact, Label status, string help, int column)
        {
            var panel = StatusCard(title, status, help);
            panel.AccessibleName = title;
            panel.Padding = new Padding(10, 11, 10, 11);
            var header = panel.Controls.OfType<TableLayoutPanel>().Single();
            var heading = header.Controls.OfType<Label>().Single();
            heading.AccessibleName = title;
            if (ReferenceEquals(status, scanStatus) || ReferenceEquals(status, resetBluetoothStatus))
            {
                var reset = ReferenceEquals(status, resetBluetoothStatus);
                foreach (var target in new Control[] { panel, header, heading, status })
                {
                    target.Cursor = Cursors.Hand;
                    target.Click += (_, _) =>
                    {
                        if (reset) localToysPage.ResetBluetooth();
                        else localToysPage.ToggleScan();
                    };
                }
                panel.AccessibleDescription = reset ? "Press to reset Bluetooth connections." : "Press to toggle toy scanning.";
            }
            panel.SizeChanged += (_, _) =>
            {
                var available = Math.Max(0, panel.ClientSize.Width - panel.Padding.Horizontal - 20);
                var text = TextRenderer.MeasureText(title, heading.Font).Width <= available ? title : compact;
                if (heading.Text != text) heading.Text = text;
                var size = panel.Width < 155 ? 10 : 12;
                if (status.Font.SizeInPoints != size)
                {
                    var old = status.Font;
                    status.Font = new Font("Segoe UI Semibold", size, FontStyle.Bold);
                    old.Dispose();
                }
            };
            row.Controls.Add(panel, column, 0);
        }
    }

    private void RebuildShockerCards()
    {
        var existing = shockerCards.ToDictionary(card => card.Device.Key);
        shockerCards.Clear();
        foreach (var device in config.ActiveShockers)
        {
            if (existing.Remove(device.Key, out var card)) card.ApplyDevice(device, true);
            else
            {
                card = new ShockerCard(device, statusHelp);
                card.SettingsChanged += SaveShocker;
                card.ActionRequested += RunShockerAction;
                card.RemoveRequested += RemoveShocker;
                card.ApplySharesRequested += ApplyShockerShares;
                card.NameEditingFinished += ApplyDeferredName;
                card.NameRetryRequested += current => SyncName(current, retry: true);
                card.ExpandedChanged += (_, _) => ArrangeActivity();
                shockerGrid.Controls.Add(card);
            }
            card.SetRuntime(bridge is { HasExited: false }, config.DryRun);
            card.SetApiState(apiHealth?.Shockers.FirstOrDefault(status => status.Key == device.Key));
            card.SetHubState(apiHealth?.Hub?.Shockers.FirstOrDefault(status => status.Key == device.Key)?.State);
            shockerCards.Add(card);
        }
        foreach (var removed in existing.Values)
        {
            nameRequests.Remove(removed.Device.Key); failedNames.Remove(removed.Device.Key); deferredNames.Remove(removed.Device.Key);
            shockerGrid.Controls.Remove(removed); removed.Dispose();
        }
        ArrangeActivity(); FillSelfTestDevices();
    }

    private void FlushShockerChanges()
    {
        foreach (var card in shockerCards.ToArray()) card.FlushPendingChanges();
    }

    private void ApplyShockerShares(ShockerCard card, string requestId)
    {
        if (closing || stopRequested || restartPending || config.DryRun || bridge is not { HasExited: false })
        { card.SetShareResult(requestId, false, "Not applied: a running live bridge is needed."); return; }
        try { bridge.StandardInput.WriteLine(JsonSerializer.Serialize(new { kind = "applyLimits", key = card.Device.Key, requestId })); }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            card.SetShareResult(requestId, false, "Could not apply: " + error.Message);
            AppendLog("error", "Could not send share limits to bridge: " + error.Message);
        }
    }

    private void SyncName(ShockerCard card, bool retry = false)
    {
        if (config.IsPiShock) return;
        var device = card.Device;
        if (closing || stopRequested || restartPending || config.DryRun || !device.NameSyncPending ||
            device.Uuid.Length == 0 || bridge is not { HasExited: false } || nameRequests.ContainsKey(device.Key)) return;
        if (!retry && failedNames.TryGetValue(device.Key, out var failed) && failed == (device.Uuid, device.Name)) return;
        failedNames.Remove(device.Key);
        var operation = new NameOperation(Guid.NewGuid().ToString("D"), device.Uuid, device.Name);
        nameRequests[device.Key] = operation;
        try
        {
            bridge.StandardInput.WriteLine(JsonSerializer.Serialize(new { kind = "rename", key = device.Key, name = device.Name, requestId = operation.RequestId }));
            card.ShowNameSync("Saving name to OpenShock…", true);
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        {
            nameRequests.Remove(device.Key); failedNames[device.Key] = (device.Uuid, device.Name);
            card.ShowError("Name sync failed: " + error.Message + " Save options to retry.");
            AppendLog("error", "Could not send name to bridge: " + error.Message);
        }
    }

    private void ApplyRemoteName(ShockerSyncSnapshot snapshot)
    {
        if (config.IsPiShock) return;
        var card = shockerCards.FirstOrDefault(current => current.Device.Key == snapshot.Key);
        if (card is null || !string.Equals(card.Device.Uuid, snapshot.Uuid, StringComparison.OrdinalIgnoreCase) ||
            card.Device.NameSyncPending || !ShockerConfig.ValidSyncedName(snapshot.Name)) return;
        if (card.NameIsEditing) { deferredNames[snapshot.Key] = (snapshot.Uuid, snapshot.Name); return; }
        if (card.Device.Name == snapshot.Name) return;
        var updated = card.Device.Clone(); updated.Name = snapshot.Name;
        SaveShocker(card, updated);
        if (card.Device.Name == snapshot.Name) AppendLog("api", $"Name synced from OpenShock: {snapshot.Name}");
    }

    private void ApplyDeferredName(ShockerCard card)
    {
        if (deferredNames.Remove(card.Device.Key, out var deferred))
            ApplyRemoteName(new ShockerSyncSnapshot { Key = card.Device.Key, Uuid = deferred.Uuid, Name = deferred.Name });
    }

    private void HandleNameResult(ShockerSyncSnapshot snapshot)
    {
        if (!nameRequests.TryGetValue(snapshot.Key, out var operation) || operation.RequestId != snapshot.RequestId ||
            !string.Equals(operation.Uuid, snapshot.Uuid, StringComparison.OrdinalIgnoreCase) || operation.Name != snapshot.Name) return;
        nameRequests.Remove(snapshot.Key);
        var card = shockerCards.FirstOrDefault(current => current.Device.Key == snapshot.Key);
        if (card is null || !string.Equals(card.Device.Uuid, snapshot.Uuid, StringComparison.OrdinalIgnoreCase)) return;
        if (card.Device.Name != snapshot.Name) { SyncName(card); return; }
        if (snapshot.Success)
        {
            failedNames.Remove(snapshot.Key);
            var updated = card.Device.Clone(); updated.NameSyncPending = false;
            SaveShocker(card, updated);
            if (!card.Device.NameSyncPending) card.ShowNameSync("Name saved to OpenShock.", true);
        }
        else
        {
            failedNames[snapshot.Key] = (card.Device.Uuid, card.Device.Name);
            card.ShowError(snapshot.Detail + " Save options to retry.");
        }
    }

    private bool HandleShockerUpdate(string line)
    {
        if (!line.StartsWith("@name ", StringComparison.Ordinal) && !line.StartsWith("@rename ", StringComparison.Ordinal) &&
            !line.StartsWith("@limits ", StringComparison.Ordinal)) return false;
        if (closing || stopRequested || restartPending) return true;
        try
        {
            var snapshot = JsonSerializer.Deserialize<ShockerSyncSnapshot>(line[(line.IndexOf(' ') + 1)..],
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (snapshot is null || string.IsNullOrEmpty(snapshot.Key)) throw new JsonException("Missing shocker details");
            if (line.StartsWith("@name ", StringComparison.Ordinal) && snapshot.Kind == "shocker-name") ApplyRemoteName(snapshot);
            else if (line.StartsWith("@rename ", StringComparison.Ordinal) && snapshot.Kind == "shocker-rename") HandleNameResult(snapshot);
            else if (line.StartsWith("@limits ", StringComparison.Ordinal) && snapshot.Kind == "share-limits")
                shockerCards.FirstOrDefault(card => card.Device.Key == snapshot.Key)?.SetShareResult(snapshot.RequestId, snapshot.Success, snapshot.Detail);
        }
        catch (JsonException error) { AppendLog("error", "Could not read shocker update: " + error.Message); }
        return true;
    }

    private void ApplyHubHealth()
    {
        var hub = apiHealth?.Hub;
        foreach (var card in shockerCards)
            card.SetHubState(!stopRequested && ActiveApiToken.Length > 0
                ? hub?.Shockers.FirstOrDefault(status => status.Key == card.Device.Key)?.State : null);
        string value; Color color;
        if (ActiveApiToken.Length == 0) { value = config.IsPiShock ? "Key needed" : "Token needed"; color = Warning; }
        else if (!config.ActiveShockers.Any(device => device.Uuid.Length > 0)) { value = config.IsPiShock ? "Add shocker" : "UUID needed"; color = Warning; }
        else if (stopRequested) { value = "Stopped"; color = Faint; }
        else if (hub is null) { value = bridge is { HasExited: false } ? "Unknown" : "Stopped"; color = Faint; }
        else
        {
            (value, color) = hub.State switch
            {
                "Online" => (hub.Total > 1 ? $"Online ({hub.Total})" : "Online", Accent),
                "Offline" => (hub.Total > 1 ? $"Offline ({hub.Total})" : "Offline", ErrorColor),
                "Mixed" => ($"{hub.Online}/{hub.Total} online", Warning),
                "Checking" => ("Checking", Warning),
                "TokenNeeded" => ("Token needed", Warning),
                "UuidNeeded" => ("UUID needed", Warning),
                _ => ("Unknown", Warning),
            };
        }
        hubStatus.Text = "●  " + value; hubStatus.ForeColor = color;
        var details = hub?.Detail ?? (config.IsPiShock ? "Start the PiShock bridge and add a shocker. Hub availability is separate from API access." : "Start the bridge with an API token and shocker UUID to read live hub status.");
        if (hub?.Shockers is not null)
            foreach (var device in config.ActiveShockers.Where(device => device.Uuid.Length > 0))
                details += "\n" + device.Name + ": hub " + (hub.Shockers.FirstOrDefault(status => status.Key == device.Key)?.State ?? "Unknown");
        statusHelp.SetToolTip(hubStatus, details);
    }
}
