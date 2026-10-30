using System.Text.Json;

namespace ShockWave.Overlay;

internal sealed partial class OverlayForm
{
    private void ArrangeActivity()
    {
        if (activityLayout is null || arrangingActivity) return;
        var initialWidth = shockerGrid.ClientSize.Width;
        arrangingActivity = true;
        try
        {
            var available = Math.Max(3, shockerGrid.ClientSize.Width - 24);
            var widths = new[] { (int)(available * .33333), (int)(available * .33333), 0 };
            widths[2] = available - widths[0] - widths[1];
            var top = 0;
            for (var row = 0; row * 3 < shockerCards.Count; row++)
            {
                var x = 0;
                var rowHeight = 0;
                for (var column = 0; column < 3 && row * 3 + column < shockerCards.Count; column++)
                {
                    var card = shockerCards[row * 3 + column];
                    card.SetBounds(x, top, widths[column], card.Expanded ? ShockerCard.OptionsHeight : ShockerCard.CompactHeight);
                    rowHeight = Math.Max(rowHeight, card.Height);
                    x += widths[column] + 12;
                }
                top += rowHeight + 12;
            }
            activityLayout.RowStyles[4].Height = Math.Max(12, top);
            localToysPage.ArrangeCards();
            activityLayout.RowStyles[6].Height = localToysPage.GridHeight;
            var fixedHeight = activityLayout.RowStyles.Cast<RowStyle>().Take(8).Sum(style => style.Height);
            activityLayout.Height = Math.Max(toyPanelPage.ClientSize.Height, (int)fixedHeight);
        }
        finally { arrangingActivity = false; }
        if (shockerGrid.ClientSize.Width != initialWidth) ArrangeActivity();
    }

    private void AddShocker()
    {
        FlushShockerChanges();
        if (config.IsPiShock) { AddPiShockShocker(); return; }
        var number = 1;
        while (config.ActiveShockers.Any(s => s.AvatarGroup == (number == 1 ? "PiCtrl" : $"PiCtrl_{number}"))) number++;
        var device = new ShockerConfig { Name = $"Toy {number}", AvatarGroup = number == 1 ? "PiCtrl" : $"PiCtrl_{number}" };
        config.ActiveShockers.Add(device);
        try
        {
            saveConfig(config);
            RebuildShockerCards();
            SendShockerConfiguration();
            var card = shockerCards.Last();
            card.Expanded = true;
            toyPanelPage.ScrollControlIntoView(card);
            AppendLog("overlay", $"Added {device.Name}. Enter its UUID and avatar group under Options.");
        }
        catch (Exception error)
        {
            config.ActiveShockers.Remove(device);
            RebuildShockerCards();
            AppendLog("error", "Could not add shocker: " + error.Message);
        }
    }

    private void SaveShocker(ShockerCard card, ShockerConfig candidate)
    {
        var index = config.ActiveShockers.FindIndex(s => s.Key == candidate.Key);
        if (index < 0) return;
        var previous = config.ActiveShockers[index];
        config.ActiveShockers[index] = candidate;
        var error = config.Validate(apiToken, requireToken: false);
        if (error is not null)
        {
            config.ActiveShockers[index] = previous;
            card.ShowError(error);
            return;
        }
        try
        {
            saveConfig(config);
            card.ApplyDevice(candidate);
            card.ShowSaved();
            if (previous.Uuid != candidate.Uuid)
            {
                nameRequests.Remove(candidate.Key); failedNames.Remove(candidate.Key); deferredNames.Remove(candidate.Key);
            }
            SendShockerConfiguration(checkConnection: previous.Uuid != candidate.Uuid);
            FillSelfTestDevices();
            SyncName(card);
        }
        catch (Exception exception)
        {
            config.ActiveShockers[index] = previous;
            card.ApplyDevice(previous);
            card.ShowError("Could not save: " + exception.Message);
        }
    }

    private void RemoveShocker(ShockerCard card)
    {
        FlushShockerChanges();
        var index = config.ActiveShockers.FindIndex(s => s.Key == card.Device.Key);
        if (index < 0) return;
        var device = config.ActiveShockers[index];
        var previousTestKeys = config.ActiveSelfTestShockerKeys.ToList();
        var previousSelfTest = config.ActiveSelfTestEnabled;
        config.ActiveShockers.RemoveAt(index);
        config.ActiveSelfTestShockerKeys.Remove(device.Key);
        if (config.ActiveSelfTestShockerKeys.Count == 0) config.ActiveSelfTestEnabled = false;
        try
        {
            saveConfig(config);
            SendShockerConfiguration();
            RebuildShockerCards();
            if (previousSelfTest != config.ActiveSelfTestEnabled) selfTestInput.Checked = config.ActiveSelfTestEnabled;
            SetModeBanner();
            AppendLog("overlay", $"Removed {device.Name} from the overlay.");
        }
        catch (Exception error)
        {
            config.ActiveShockers.Insert(index, device);
            config.ActiveSelfTestShockerKeys = previousTestKeys;
            config.ActiveSelfTestEnabled = previousSelfTest;
            card.ShowError("Could not remove: " + error.Message);
        }
    }

    private void SendShockerConfiguration(bool checkConnection = false)
    {
        RefreshApiSetupStatus(checkConnection);
        if (bridge is not { HasExited: false }) return;
        try { bridge.StandardInput.WriteLine("{\"kind\":\"configure\",\"config\":" + config.ToJson(true) + "}"); }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        { AppendLog("error", "Bridge could not apply shocker settings: " + error.Message); StopBridge(); }
    }

    private void RunShockerAction(ShockerCard card, string type)
    {
        card.FlushPendingChanges();
        if (bridge is not { HasExited: false } || card.Device.Paused || stopRequested || restartPending) return;
        try
        {
            bridge.StandardInput.WriteLine(JsonSerializer.Serialize(new { kind = "control", key = card.Device.Key, type }));
        }
        catch (Exception error) when (error is IOException or InvalidOperationException)
        { AppendLog("error", "Could not send action to bridge: " + error.Message); }
    }

}
