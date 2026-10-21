using System.Text.Json;

namespace ShockWave.Overlay;

internal sealed partial class OverlayForm
{
    private readonly ComboBox providerInput = new SettingChoiceInput { DropDownStyle = ComboBoxStyle.DropDownList, BackColor = InputColor, ForeColor = TextColor, FlatStyle = FlatStyle.Flat };
    private readonly TextBox piShockUsernameInput = InputBox();
    private readonly TextBox piShockKeyInput = InputBox();
    private readonly CheckBox rememberPiShockInput = new() { AutoSize = true, ForeColor = TextColor, BackColor = PanelColor };
    private Panel? piShockSettingsCard;
    private Panel? openShockSettingsCard;
    private TokenStore? piShockKeyStore;
    private string piShockApiKey = Environment.GetEnvironmentVariable("PISHOCK_API_KEY")?.Trim() ?? "";
    private readonly List<PiShockDevice> piShockDevices = [];
    private string ActiveApiToken => config.IsPiShock ? piShockApiKey : apiToken;

    private sealed class PiShockDevice
    {
        public string Uuid { get; set; } = "";
        public string Name { get; set; } = "";
        public string HubName { get; set; } = "";
        public bool Shared { get; set; }
        public override string ToString() => Name + (Shared ? " · Shared" : HubName.Length > 0 ? " · " + HubName : " · Own");
    }
    private sealed class PiShockInventory
    {
        public string Kind { get; set; } = "";
        public List<PiShockDevice> Devices { get; set; } = [];
    }

    private void LoadPiShockKey(bool usePersonalStore)
    {
        if (!usePersonalStore) return;
        piShockKeyStore = new TokenStore(Path.Combine(Path.GetDirectoryName(BridgeConfig.ConfigPath)!, "pishock-api-key.bin"));
        if (!config.RememberPiShockApiKey) return;
        try { if (piShockApiKey.Length == 0) piShockApiKey = piShockKeyStore.Load(); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        { piShockApiKey = ""; }
    }

    private void BuildProviderSettings(FlowLayoutPanel flow)
    {
        var service = Card(224);
        AddCardHeading(service, "Shocker service", "Choose the connection used by your shocker cards and PiCtrl avatar controls.");
        providerInput.Items.AddRange(["OpenShock", "PiShock"]);
        AddField(service, "Service", providerInput, 87);
        AddField(service, "Connection retry interval (ms)", pollIntervalInput, 150);
        providerInput.SelectedIndexChanged += (_, _) => UpdateProviderSettingsVisibility();
        flow.Controls.Add(service);

        piShockSettingsCard = Card(358);
        AddCardHeading(piShockSettingsCard, "PiShock", "Connect directly to PiShock. No additional program is needed.");
        piShockKeyInput.UseSystemPasswordChar = true;
        piShockKeyInput.PlaceholderText = "Paste your PiShock API key";
        AddField(piShockSettingsCard, "PiShock username", piShockUsernameInput, 87);
        AddField(piShockSettingsCard, "API key", piShockKeyInput, 150);
        rememberPiShockInput.Text = "Remember PiShock key on this PC";
        rememberPiShockInput.SetBounds(18, 210, 560, 28);
        piShockSettingsCard.Controls.Add(rememberPiShockInput);
        var hint = Label("Save settings, then Start bridge. Use Add shocker on Toy panel to choose your own or already-shared devices. Create a new API key on PiShock if an older key cannot connect.", 9, false, Muted);
        hint.SetBounds(18, 247, 660, 62);
        hint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        piShockSettingsCard.Controls.Add(hint);
        var refresh = StyledButton("Refresh devices", false);
        refresh.FlatAppearance.BorderSize = 0;
        refresh.SetBounds(18, 315, 148, 28);
        refresh.Click += (_, _) => RefreshPiShockDevices();
        piShockSettingsCard.Controls.Add(refresh);
        statusHelp.SetToolTip(piShockKeyInput, "Generate the API key on PiShock's account page. The key is separate from your OpenShock token and is encrypted for this Windows account when remembered. The PiShock website does not need to stay open.");
        flow.Controls.Add(piShockSettingsCard);
    }

    private void FillProviderSettings()
    {
        providerInput.SelectedItem = config.Provider;
        piShockUsernameInput.Text = config.PiShockUsername;
        piShockKeyInput.Text = piShockApiKey;
        rememberPiShockInput.Checked = config.RememberPiShockApiKey;
        UpdateProviderSettingsVisibility();
    }

    private void UpdateProviderSettingsVisibility()
    {
        var showPiShock = Equals(providerInput.SelectedItem, "PiShock");
        if (piShockSettingsCard is not null) piShockSettingsCard.Visible = showPiShock;
        if (openShockSettingsCard is not null) openShockSettingsCard.Visible = !showPiShock;
    }

    private void UpdateProviderStatusCards()
    {
        if (activityLayout.GetControlFromPosition(0, 1) is not Control previous) return;
        activityLayout.SuspendLayout();
        var row = BuildStatusRow(); // Status labels are reparented before the previous row is disposed.
        activityLayout.Controls.Remove(previous);
        activityLayout.Controls.Add(row, 0, 1);
        previous.Dispose();
        activityLayout.ResumeLayout(true);
    }

    private bool HandlePiShockDevices(string line)
    {
        if (!line.StartsWith("@devices ", StringComparison.Ordinal)) return false;
        if (closing || stopRequested || restartPending || !config.IsPiShock) return true;
        try
        {
            var inventory = JsonSerializer.Deserialize<PiShockInventory>(line[9..], new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (inventory is not { Kind: "pishock-devices", Devices: not null } || inventory.Devices.Count > 2000 ||
                inventory.Devices.Any(device => !ShockerConfig.ValidPiShockTarget(device.Uuid) || !ShockerConfig.ValidSyncedName(device.Name)))
                throw new JsonException("Invalid PiShock device list");
            piShockDevices.Clear(); piShockDevices.AddRange(inventory.Devices);
        }
        catch (JsonException) { AppendLog("error", "Could not read the PiShock device list."); }
        return true;
    }

    private void RefreshPiShockDevices()
    {
        if (!config.IsPiShock || bridge is not { HasExited: false } || stopRequested || restartPending)
        { saveMessage.Text = "Save PiShock settings, then start the bridge on Toy panel."; return; }
        try { bridge.StandardInput.WriteLine("{\"kind\":\"refreshDevices\"}"); AppendLog("overlay", "Requested a PiShock device refresh."); }
        catch (Exception error) when (error is IOException or InvalidOperationException) { AppendLog("error", "Could not request PiShock devices."); }
    }

    private void AddPiShockShocker()
    {
        if (bridge is not { HasExited: false } || stopRequested || restartPending || apiHealth?.Connection?.State != "Connected")
        { AppendLog("overlay", "Enter your PiShock username and API key in Settings, save, then Start bridge. Wait for PiShock API Connected before adding a shocker."); return; }
        var available = piShockDevices.Where(device => !config.PiShockShockers.Any(s => string.Join('/', s.Uuid.Split('/').Take(2)) == string.Join('/', device.Uuid.Split('/').Take(2)))).ToArray();
        if (available.Length == 0)
        { AppendLog("overlay", "No additional PiShock devices found. Refresh devices in Settings. Claim shared codes on the PiShock website first."); return; }
        using var picker = new Form { Text = "Add PiShock shocker", Size = new Size(500, 310), MinimumSize = new Size(420, 270),
            StartPosition = FormStartPosition.CenterParent, BackColor = Background, ForeColor = TextColor, Font = Font,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false };
        var list = new ListBox { Dock = DockStyle.Fill, BackColor = InputColor, ForeColor = TextColor, BorderStyle = BorderStyle.None, IntegralHeight = false };
        list.Items.AddRange(available); list.SelectedIndex = 0;
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, Padding = new Padding(8), FlowDirection = FlowDirection.RightToLeft };
        var add = StyledButton("Add shocker", true); add.DialogResult = DialogResult.OK;
        var cancel = StyledButton("Cancel", false); cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.AddRange([add, cancel]); picker.Controls.Add(list); picker.Controls.Add(buttons);
        picker.AcceptButton = add; picker.CancelButton = cancel;
        if (picker.ShowDialog(this) != DialogResult.OK || list.SelectedItem is not PiShockDevice selected) return;
        var number = 1;
        while (config.ActiveShockers.Any(s => s.AvatarGroup == (number == 1 ? "PiCtrl" : $"PiCtrl_{number}"))) number++;
        var device = new ShockerConfig { Service = "PiShock", Name = selected.Name, Model = selected.Shared ? "PiShock · Shared" : "PiShock",
            Uuid = selected.Uuid, AvatarGroup = number == 1 ? "PiCtrl" : $"PiCtrl_{number}" };
        config.PiShockShockers.Add(device);
        try { saveConfig(config); RebuildShockerCards(); SendShockerConfiguration(); AppendLog("overlay", "PiShock shocker added. Your limits, pause and cooldown apply to this card."); }
        catch (Exception) { config.PiShockShockers.Remove(device); RebuildShockerCards(); AppendLog("error", "Could not save the PiShock shocker."); }
    }
}
