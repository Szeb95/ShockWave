using ShockWave.Overlay.Toys;

namespace ShockWave.Overlay;

internal sealed class ToyFunctionRow : RoundedPanel
{
    public const int CompactHeight = 182, OptionsHeight = 384;
    private static readonly Color PanelColor = ColorTranslator.FromHtml("#2b2d31"),
        InputColor = ColorTranslator.FromHtml("#383a40"), DisabledInputColor = ColorTranslator.FromHtml("#232428"),
        TextColor = ColorTranslator.FromHtml("#f2f3f5"),
        Muted = ColorTranslator.FromHtml("#b5bac1"), Accent = ColorTranslator.FromHtml("#10a37f");
    private readonly PercentSlider input = new(), sensitivity = new(), maximum = new();
    private readonly Label title = Label("", 10, true), function = Label("", 8), state = Label("", 8),
        inputLabel = Label("Input", 8, true), inputValue = Label("", 8),
        sensitivityLabel = Label("Sensitivity", 8, true), sensitivityValue = Label("", 8),
        maxLabel = Label("Max %", 8, true), maxValue = Label("", 8), batteryLabel = Label("Battery unknown", 8);
    private readonly CheckBox options = new ThemeToggle() { Text = "Options", TextBeforeSwitch = true, BackColor = PanelColor, ForeColor = Muted, Font = new Font("Segoe UI", 8) };
    private readonly Button readBattery = Button("Read battery"),
        forget = Button("Forget toy"), stopToy = Button("Stop toy");
    private readonly Panel advanced = new() { Visible = false };
    private readonly ToolTip help = new() { ShowAlways = true };
    private readonly LocalToyController controller;
    private LocalToyDescriptor device;
    private bool updating;
    public string DeviceKey => device.DeviceKey;
    public string ChannelId { get; }
    public bool Expanded { get => options.Checked; set => options.Checked = value; }
    public event Action? ExpandedChanged;
    public event Action<string>? ReadBatteryRequested, ForgetRequested, StopToyRequested;

    public ToyFunctionRow(LocalToyController controller, LocalToyDescriptor device, ToyCapability? capability)
    {
        this.controller = controller; this.device = device; ChannelId = capability?.ChannelId ?? "";
        Width = 243; Height = CompactHeight; Margin = Padding.Empty; BackColor = PanelColor; ForeColor = TextColor;
        function.Text = capability?.DisplayName ?? "Functions unknown";
        title.AutoEllipsis = function.AutoEllipsis = state.AutoEllipsis = true;
        function.ForeColor = state.ForeColor = options.ForeColor = Muted;
        inputValue.TextAlign = sensitivityValue.TextAlign = maxValue.TextAlign = ContentAlignment.TopRight;
        Controls.AddRange([title, function, state, options,
            inputLabel, inputValue, input, sensitivityLabel, sensitivityValue, sensitivity, advanced]);
        advanced.Controls.AddRange([maxLabel, maxValue, maximum, batteryLabel, readBattery, forget, stopToy]);
        input.AccessibleName = function.Text + " input";
        input.AccessibleDescription = "0 is off; 100 requests full power within Sensitivity and Max %.";
        sensitivity.AccessibleName = function.Text + " sensitivity";
        maximum.AccessibleName = function.Text + " maximum percent";
        input.ValueChanged += () => { if (!updating && ChannelId.Length > 0) Act(() => controller.Request(DeviceKey, ChannelId, input.Value)); };
        sensitivity.ValueChanged += SavePreference;
        maximum.ValueChanged += SavePreference;
        readBattery.Click += (_, _) => ReadBatteryRequested?.Invoke(DeviceKey);
        forget.Click += (_, _) => ForgetRequested?.Invoke(DeviceKey);
        stopToy.Click += (_, _) => StopToyRequested?.Invoke(DeviceKey);
        options.CheckedChanged += (_, _) =>
        {
            Height = Expanded ? OptionsHeight : CompactHeight;
            advanced.Visible = Expanded; Arrange(); ExpandedChanged?.Invoke();
        };
        SizeChanged += (_, _) => Arrange();
        help.SetToolTip(input, input.AccessibleDescription);
        help.SetToolTip(sensitivity, "Scales this function's Input. Preferences save automatically; editing while idle does not start output.");
        help.SetToolTip(maximum, "Maximum output for this function. Clips the scaled input; 80 with a maximum of 40 becomes 40.");
        help.SetToolTip(stopToy, "Stop every function of this toy. Other toys keep their own values.");
        help.SetToolTip(forget, "Available when this toy is offline. Remove it from remembered inventory and skip automatic reconnect for this session.");
        UpdateDevice(device, false); Arrange();
    }

    private static Label Label(string text, int size, bool bold = false) => new()
    { Text = text, ForeColor = TextColor, BackColor = Color.Transparent, Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular) };
    private static Button Button(string text)
    {
        var button = new RoundedButton { Text = text, FlatStyle = FlatStyle.Flat, BackColor = InputColor, ForeColor = TextColor, DisabledForeColor = TextColor,
            Font = new Font("Segoe UI", 8), Cursor = Cursors.Hand };
        button.FlatAppearance.BorderSize = 0; return button;
    }
    private void Arrange()
    {
        var track = Math.Max(1, Width - 20);
        var half = track / 2;
        title.SetBounds(10, 9, track, 21); function.SetBounds(10, 31, track, 18);
        state.SetBounds(10, 46, track, 16);
        options.SetBounds(10, 63, track, 20); options.Text = Width < 200 ? "More" : "Options";
        inputLabel.SetBounds(10, 84, half, 16); inputValue.SetBounds(10 + half, 84, track - half, 16);
        input.SetBounds(10, 100, track, 32);
        sensitivityLabel.Text = Width < 200 ? "Sens." : "Sensitivity";
        sensitivityLabel.SetBounds(10, 134, half, 16); sensitivityValue.SetBounds(10 + half, 134, track - half, 16);
        sensitivity.SetBounds(10, 150, track, 30);
        advanced.SetBounds(0, CompactHeight, Width, OptionsHeight - CompactHeight);
        maxLabel.SetBounds(10, 8, half, 16); maxValue.SetBounds(10 + half, 8, track - half, 16);
        maximum.SetBounds(10, 26, track, 32);
        batteryLabel.SetBounds(10, 62, track, 26);
        readBattery.SetBounds(10, 92, track, 28); forget.SetBounds(10, 126, track, 28); stopToy.SetBounds(10, 160, track, 28);
        Invalidate();
    }
    private void Act(Action action)
    {
        try { action(); RefreshState(device.ConnectionState == ToyConnectionState.Connected); }
        catch (Exception error) when (error is InvalidOperationException or IOException or UnauthorizedAccessException)
        { state.Text = error.Message; help.SetToolTip(state, error.Message); }
    }
    private void SavePreference()
    {
        if (updating || ChannelId.Length == 0) return;
        Act(() => controller.SetPreference(controller.State(DeviceKey, ChannelId).Preference with
            { Sensitivity = sensitivity.Value, Maximum = maximum.Value }));
    }
    public void UpdateDevice(LocalToyDescriptor next, bool busy)
    {
        device = next; title.Text = device.DisplayName;
        help.SetToolTip(title, device.DisplayName + " · " + device.Model);
        help.SetToolTip(function, function.Text);
        batteryLabel.Text = device.BatteryPercent is int percent ? $"Battery {percent}%" : "Battery unknown";
        readBattery.Enabled = !busy && device.ConnectionState == ToyConnectionState.Connected;
        forget.Enabled = !busy && device.IsRemembered && device.ConnectionState is
            ToyConnectionState.Discovered or ToyConnectionState.Disconnected or ToyConnectionState.Unavailable;
        forget.BackColor = forget.Enabled ? InputColor : DisabledInputColor;
        stopToy.Enabled = device.ConnectionState == ToyConnectionState.Connected;
        RefreshState(device.ConnectionState == ToyConnectionState.Connected);
    }
    public void RefreshState(bool connected)
    {
        title.ForeColor = connected ? Accent : TextColor;
        if (ChannelId.Length == 0)
        {
            input.Enabled = sensitivity.Enabled = maximum.Enabled = false;
            state.Text = connected ? "Connected" : "Offline";
            return;
        }
        var current = controller.State(DeviceKey, ChannelId);
        updating = true;
        try
        {
            input.Value = current.RequestedPercent; inputValue.Text = current.RequestedPercent + "%";
            input.Enabled = connected && current.Armed;
            sensitivity.Value = current.Preference.Sensitivity; sensitivityValue.Text = current.Preference.Sensitivity + "%";
            maximum.Value = current.Preference.Maximum; maxValue.Text = current.Preference.Maximum + "%";
            state.Text = connected ? $"{(controller.TestMode ? "Preview" : "Sent")} {current.SentPercent:0.#}%" :
                device.ConnectionState == ToyConnectionState.Connecting ? "Connecting…" : "Offline";
            state.ForeColor = connected ? Accent : Muted;
            help.SetToolTip(state, current.Detail + (controller.TestMode ? "" :
                $"\nWait {current.WaitMilliseconds:0} ms / submit {current.SubmitMilliseconds:0} ms") +
                (device.AutoConnectPaused ? "\nForgotten for this session." : ""));
        }
        finally { updating = false; }
    }
    protected override void Dispose(bool disposing) { if (disposing) help.Dispose(); base.Dispose(disposing); }
}
