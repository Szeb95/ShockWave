using System.Diagnostics;
using System.Drawing;
using ShockWave.Overlay.Toys;

namespace ShockWave.Overlay;

internal sealed partial class OverlayForm : Form
{
    private static readonly Color Background = Hex("#1e1f22");
    private static readonly Color Sidebar = Hex("#111214");
    private static readonly Color PanelColor = Hex("#2b2d31");
    private static readonly Color PanelHover = Hex("#35373c");
    private static readonly Color InputColor = Hex("#383a40");
    private static readonly Color TextColor = Hex("#f2f3f5");
    private static readonly Color Muted = Hex("#b5bac1");
    private static readonly Color Faint = Hex("#80848e");
    private static readonly Color Accent = Hex("#10a37f");
    private static readonly Color Warning = Hex("#ffb74d");
    private static readonly Color ErrorColor = Hex("#ed6a6a");

    private readonly string bridgeDirectory = Path.Combine(AppContext.BaseDirectory, "bridge");
    private BridgeConfig config;
    private string apiToken = Environment.GetEnvironmentVariable("OPENSHOCK_TOKEN")?.Trim() ?? "";
    private Process? bridge;
    private Process? lastStartedBridge;
    private bool stopRequested;
    private bool restartPending;
    private bool closing;
    private ApiStatusSnapshot? apiHealth;
    private readonly List<string> recentLog = [];
    private readonly SessionLog? sessionLog;
    private readonly TokenStore? tokenStore;
    private bool tokenRemembered;

    private readonly Panel toyPanelPage = new ThemedScrollPanel() { Dock = DockStyle.Fill };
    private readonly Panel logsPage = new() { Dock = DockStyle.Fill };
    private readonly Panel settingsPage = new() { Dock = DockStyle.Fill };
    private readonly LocalToysPage localToysPage;
    private readonly ToyControlPage toyControlPage;
    private readonly SpsPage spsPage;
    private readonly RemoteSession remoteSession;
    private readonly RemotePage remotePage;
    private readonly Button toyPanelNav = NavButton("◫", "Toy panel");
    private readonly Button toyControlNav = NavButton("∿", "Toy control");
    private readonly Button shockerControlNav = NavButton("◉", "Shocker control");
    private readonly Button spsNav = NavButton("≋", "SPS");
    private readonly Button remoteNav = NavButton("⇄", "Connect");
    private readonly Button logsNav = NavButton("▤", "Log");
    private readonly Button settingsNav = NavButton("⚙", "Settings");
    private bool localToysClosed;
    private bool localToysClosing;
    private bool closePromptOpen;
    private readonly ShockerOutputActivity shockerActivity = new();
    internal Func<CloseOutputState, bool> ConfirmActiveClose { get; set; } = state => CloseOutputWarning.Confirm(state);
    private readonly Button bridgeButton = StyledButton("Start bridge", true, rounded: true);
    private readonly DataGridView logGrid = new();
    private readonly Label bridgeStatus = StatusLabel("Stopped");
    private readonly Label apiStatus = StatusLabel("Token needed");
    private readonly Label oscStatus = StatusLabel("Stopped");
    private readonly Label bluetoothStatus = StatusLabel("Inactive");
    private readonly Label curveStatus = StatusLabel("Inactive");
    private readonly Label scanStatus = StatusLabel("Inactive");
    private readonly Label resetBluetoothStatus = StatusLabel("Reset");
    private readonly Label modeBanner = new RoundedLabel();
    private readonly Label tokenHint = new();
    private readonly Label saveMessage = new();
    private readonly TextBox apiUrlInput = InputBox();
    private readonly TextBox tokenInput = InputBox();
    private readonly CheckBox rememberTokenInput = new();
    private readonly CheckBox testModeInput = new();
    private readonly CheckBox selfTestInput = new();
    private readonly TextBox selfTestGroupInput = InputBox();
    private readonly CheckedListBox selfTestDevicesInput = new();
    private readonly NumericUpDown pollIntervalInput = NumberInput(500, 60000);
    private readonly NumericUpDown listenPortInput = NumberInput(1, 65535);
    private readonly NumericUpDown sendPortInput = NumberInput(1, 65535);
    private readonly Panel shockerGrid = new() { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(0, 0, 0, 12) };
    private readonly List<ShockerCard> shockerCards = [];
    private TableLayoutPanel activityLayout = null!;
    private bool arrangingActivity;
    private readonly Font helpFont = new("Segoe UI", 9);
    private readonly ToolTip statusHelp = new()
    {
        OwnerDraw = true,
        InitialDelay = 250,
        ReshowDelay = 100,
        AutoPopDelay = 20000,
        ShowAlways = true,
        UseFading = false,
    };

    public OverlayForm(BridgeConfig? initialConfig = null, TokenStore? initialTokenStore = null, Action<BridgeConfig>? initialSaveConfig = null,
        LocalToysPage? initialLocalToys = null, Sps.SpsStore? initialSpsStore = null, Updates.AppUpdater? initialUpdater = null,
        IRemoteFriendStore? initialRemoteFriends = null)
    {
        localToysPage = initialLocalToys ?? new LocalToysPage();
        config = initialConfig ?? BridgeConfig.Load(bridgeDirectory);
        updater = initialUpdater ?? Updates.AppUpdater.Create(initialConfig is null);
        updatePage = new UpdatePage(updater);
        toyControlPage = new(localToysPage, ForwardOscToBridge, placementStore: new SteamVr.WristPlacementStore(initialConfig is null ? SteamVr.WristPlacementStore.DefaultPath : null));
        toyControlPage.ConfigureVrOptions(config.PanelHand,config.PanelStartMode,config.PanelAutoOpenInVr);
        spsPage = new(localToysPage, () => toyControlPage.Listening, initialSpsStore, () => toyControlPage.AvatarPort);
        remoteSession = new(() => localToysPage.Snapshot, () => localToysPage.Controller, () => config, SendRemoteShocker,
            initialRemoteFriends ?? (initialConfig is null ? new RemoteFriendStore(Path.Combine(Path.GetDirectoryName(BridgeConfig.ConfigPath)!, "remote-friends.bin")) : new MemoryRemoteFriendStore()));
        remotePage = new(remoteSession, NodePath, bridgeDirectory);
        toyControlPage.Session.Remote = remoteSession.Toys;
        toyControlPage.Session.Shockers.Inventory = ShockerControlInventory;
        toyControlPage.Session.Shockers.Send = SendPanelShocker;
        toyControlPage.Session.Shockers.IsTestMode = () => config.DryRun;
        toyControlPage.Session.PageChanged += () => { if(toyControlPage.Visible) ShowPage(toyControlPage,toyControlPage.Session.ShockerPage ? shockerControlNav : toyControlNav); };
        remoteSession.Changed += UpdateRemoteGate;
        remoteSession.Log += message => AppendLog("remote", message);
        remoteSession.Toys.Log += message => AppendLog("remote", message);
        toyControlPage.Session.Log += message => AppendLog("remote", message);
        toyControlPage.Diagnostic += message => AppendLog("remote", message);
        toyControlPage.OscInput += spsPage.Receive;
        toyControlPage.OscReset += spsPage.ResetInput;
        toyControlPage.SpsPresence = () => spsPage.SignalsEnabled;
        saveConfig = initialSaveConfig ?? (settings => settings.Save());
        tokenStore = initialTokenStore;
        if (initialConfig is null)
        {
            sessionLog = new SessionLog(Path.Combine(Path.GetDirectoryName(BridgeConfig.ConfigPath)!, "activity.log"));
            tokenStore ??= new TokenStore(Path.Combine(Path.GetDirectoryName(BridgeConfig.ConfigPath)!, "api-token.bin"));
        }
        string? tokenLoadError = null;
        if (config.RememberApiToken && tokenStore is not null)
        {
            try
            {
                var storedToken = tokenStore.Load();
                if (apiToken.Length == 0 && storedToken.Length > 0) { apiToken = storedToken; tokenRemembered = true; }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
            { tokenLoadError = "Could not load the remembered token. Enter it again and save Settings."; }
        }
        LoadPiShockKey(initialConfig is null);
        Text = "ShockWave";
        Size = new Size(1040, 760);
        MinimumSize = new Size(790, 590);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Background;
        ForeColor = TextColor;
        Font = new Font("Segoe UI", 10);
        ConfigureHelpTooltips();
        ConfigureWindowFrame();
        BuildShell();
        BuildWindowTitleBar();
        BuildToyPanelPage();
        BuildLogsPage();
        BuildSettingsPage();
        ConfigureUpdates();
        localToysPage.Changed += RefreshLocalStatus;
        localToysPage.CardsLayoutChanged += ArrangeActivity;
        localToysPage.Log += AppendLog;
        spsPage.Log += AppendLog;
        toyControlPage.Changed += RefreshToyControlStatus;
        Deactivate += (_, _) => toyControlPage.StopRenderingTouch();
        RefreshLocalStatus();
        FillSettings();
        SetModeBanner();
        ShowPage(toyPanelPage, toyPanelNav);
        RefreshApiSetupStatus();
        if (tokenLoadError is not null) AppendLog("error", tokenLoadError);
        apiStatus.Cursor = Cursors.Hand;
        apiStatus.Click += (_, _) =>
        {
            ShowPage(logsPage, logsNav);
            for (var index = logGrid.RowCount - 1; index >= 0; index--)
            {
                if (!Equals(logGrid.Rows[index].Cells[1].Value, "ERROR")) continue;
                logGrid.ClearSelection();
                logGrid.Rows[index].Selected = true;
                TryScrollLog(index);
                break;
            }
        };
        Shown += (_, _) =>
        {
            toyControlPage.ConfigureOsc(config.Osc.ListenPort,config.Osc.SendPort,config.Osc.ListenHost);
            if(initialConfig is null) toyControlPage.StartWithApp();
            spsPage.StartMonitoring();
            localToysPage.SetTestMode(config.DryRun);
            localToysPage.StartRememberedConnections();
            if (config.DryRun) StartBridge();
            else AppendLog("overlay", "Live mode is configured. Press Start bridge when ready.");
        };
        FormClosing += async (_, args) =>
        {
            if (localToysClosed) return;
            args.Cancel = true;
            if (localToysClosing || closePromptOpen) return;
            var outputs = new CloseOutputState(localToysPage.Snapshot?.Devices.Any(device =>
                device.ConnectionState == ToyConnectionState.Connected && !device.IsSimulated) == true, shockerActivity.ActiveCount);
            if (outputs.HasActivity)
            {
                closePromptOpen = true;
                bool confirmed;
                try { confirmed = ConfirmActiveClose(outputs); }
                finally { closePromptOpen = false; }
                if (!confirmed) { updateRestartRequested = false; return; }
            }
            localToysClosing = true;
            foreach (var card in shockerCards) card.PrepareToClose();
            FlushShockerChanges();
            closing = true;
            remotePage.Shutdown();
            spsPage.Shutdown();
            toyControlPage.Shutdown();
            try
            {
                if (bridge is { HasExited: false }) bridge.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            try { await localToysPage.ShutdownAsync(); }
            catch (Exception) { AppendLog("error", "Local Bluetooth cleanup failed while closing the app."); }
            finally { localToysClosed = true; Close(); }
        };
    }

    private static Color Hex(string value) => ColorTranslator.FromHtml(value);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            updateTimer.Dispose();
            updater.Changed -= RefreshUpdateBadge;
            updater.Dispose();
            statusHelp.Dispose();
            helpFont.Dispose();
        }
        base.Dispose(disposing);
    }

    private void ConfigureHelpTooltips()
    {
        statusHelp.Popup += (_, args) =>
        {
            var text = statusHelp.GetToolTip(args.AssociatedControl);
            var measured = TextRenderer.MeasureText(text, helpFont,
                new Size(360, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
            args.ToolTipSize = new Size(measured.Width + 18, measured.Height + 12);
        };
        statusHelp.Draw += (_, args) =>
        {
            using var background = new SolidBrush(Sidebar);
            using var border = new Pen(InputColor);
            args.Graphics.FillRectangle(background, args.Bounds);
            args.Graphics.DrawRectangle(border, 0, 0, args.Bounds.Width - 1, args.Bounds.Height - 1);
            var textBounds = new Rectangle(9, 6, args.Bounds.Width - 18, args.Bounds.Height - 12);
            TextRenderer.DrawText(args.Graphics, args.ToolTipText, helpFont, textBounds, TextColor,
                TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        };
    }

    private static Label Label(string text, int size = 10, bool bold = false, Color? color = null) => new()
    {
        Text = text,
        AutoSize = false,
        ForeColor = color ?? TextColor,
        BackColor = Color.Transparent,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
    };

    private static Label StatusLabel(string text) => new()
    {
        Text = "●  " + text,
        Dock = DockStyle.Bottom,
        Height = 44,
        ForeColor = Faint,
        BackColor = Color.Transparent,
        Font = new Font("Segoe UI Semibold", 12, FontStyle.Bold),
    };

    private static Button StyledButton(string text, bool primary, bool rounded = false)
    {
        Button button = rounded ? new RoundedButton() : new Button();
        button.Text = text;
        button.FlatStyle = FlatStyle.Flat;
        button.BackColor = primary ? Accent : InputColor;
        button.ForeColor = TextColor;
        button.Font = new Font("Segoe UI Semibold", 10, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
        button.Size = new Size(primary ? 152 : 112, 38);
        return button;
    }

    private static Button NavButton(string icon, string text) => new NavigationButton()
    {
        Icon = icon,
        Text = text,
        TextAlign = ContentAlignment.MiddleLeft,
        FlatStyle = FlatStyle.Flat,
        BackColor = Sidebar,
        ForeColor = Muted,
        Font = new Font("Segoe UI Semibold", 10, FontStyle.Bold),
        Cursor = Cursors.Hand,
        Size = new Size(190, 46),
    };

    private static TextBox InputBox() => new()
    {
        BackColor = InputColor,
        ForeColor = TextColor,
        BorderStyle = BorderStyle.None,
        Font = new Font("Segoe UI", 10),
        Height = 30,
    };

    private static NumericUpDown NumberInput(int min, int max) => new SettingNumberInput()
    {
        ShowSpinner = false,
        Minimum = min,
        Maximum = max,
        BackColor = InputColor,
        ForeColor = TextColor,
        BorderStyle = BorderStyle.None,
        Font = new Font("Segoe UI", 10),
        Height = 30,
        ThousandsSeparator = false,
    };

    private static Panel Card(int height)
    {
        var card = new RoundedPanel { BackColor = PanelColor, Height = height, Width = 720, Margin = new Padding(0, 0, 0, 14) };
        return card;
    }

    private void BuildShell()
    {
        var shell = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 212));
        shell.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        shell.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(shell);

        var navigation = new Panel { Dock = DockStyle.Fill, BackColor = Sidebar, Margin = Padding.Empty };
        shell.Controls.Add(navigation, 0, 0);
        var brand = Label("ShockWave", 16, true);
        brand.SetBounds(22, 27, 170, 62);
        navigation.Controls.Add(brand);
        toyPanelNav.SetBounds(10, 118, 192, 46);
        toyControlNav.SetBounds(10, 168, 192, 46);
        shockerControlNav.SetBounds(10, 218, 192, 46);
        spsNav.SetBounds(10, 268, 192, 46);
        remoteNav.SetBounds(10, 318, 192, 46);
        logsNav.SetBounds(10, 368, 192, 46);
        settingsNav.SetBounds(10, 418, 192, 46);
        updateNav.SetBounds(10, 468, 192, 46);
        toyPanelNav.FlatAppearance.BorderSize = 0;
        toyControlNav.FlatAppearance.BorderSize = 0;
        shockerControlNav.FlatAppearance.BorderSize = 0;
        spsNav.FlatAppearance.BorderSize = 0;
        remoteNav.FlatAppearance.BorderSize = 0;
        logsNav.FlatAppearance.BorderSize = 0;
        settingsNav.FlatAppearance.BorderSize = 0;
        updateNav.FlatAppearance.BorderSize = 0;
        toyPanelNav.Click += (_, _) => ShowPage(toyPanelPage, toyPanelNav);
        toyControlNav.Click += (_, _) => { toyControlPage.Session.SetPage(false); ShowPage(toyControlPage,toyControlNav); };
        shockerControlNav.Click += (_, _) => { toyControlPage.Session.SetPage(true); ShowPage(toyControlPage,shockerControlNav); };
        spsNav.Click += (_, _) => ShowPage(spsPage, spsNav);
        remoteNav.Click += (_, _) => ShowPage(remotePage, remoteNav);
        logsNav.Click += (_, _) => ShowPage(logsPage, logsNav);
        settingsNav.Click += (_, _) => ShowPage(settingsPage, settingsNav);
        updateNav.Click += (_, _) => ShowPage(updatePage, updateNav);
        navigation.Controls.Add(toyPanelNav);
        navigation.Controls.Add(toyControlNav);
        navigation.Controls.Add(shockerControlNav);
        navigation.Controls.Add(spsNav);
        navigation.Controls.Add(remoteNav);
        navigation.Controls.Add(logsNav);
        navigation.Controls.Add(settingsNav);
        navigation.Controls.Add(updateNav);
        var foot = Label("OpenShock · PiShock\nNative Windows overlay", 9, false, Faint);
        foot.Dock = DockStyle.Bottom;
        foot.Height = 58;
        foot.Padding = new Padding(22, 0, 0, 12);
        navigation.Controls.Add(foot);
        void ArrangeNavigation()
        {
            var buttons = new[] { toyPanelNav, toyControlNav, shockerControlNav, spsNav, remoteNav, logsNav, settingsNav, updateNav };
            var step = Math.Min(50, Math.Max(36, (navigation.ClientSize.Height - 118 - foot.Height) / buttons.Length));
            for (var index = 0; index < buttons.Length; index++) buttons[index].SetBounds(10, 118 + index * step, 192, step - 4);
        }
        navigation.Resize += (_, _) => ArrangeNavigation();
        ArrangeNavigation();

        var content = new Panel { Dock = DockStyle.Fill, BackColor = Background, Padding = new Padding(28), Margin = Padding.Empty };
        shell.Controls.Add(content, 1, 0);
        content.Controls.Add(settingsPage);
        content.Controls.Add(updatePage);
        content.Controls.Add(toyPanelPage);
        content.Controls.Add(toyControlPage);
        content.Controls.Add(spsPage);
        content.Controls.Add(remotePage);
        content.Controls.Add(logsPage);
    }

    private void ShowPage(Control page, Button navigation)
    {
        if(!ReferenceEquals(page,toyControlPage)) toyControlPage.StopRenderingTouch();
        foreach (var candidate in new Control[] { toyPanelPage, toyControlPage, spsPage, remotePage, logsPage, settingsPage, updatePage })
            candidate.Visible = ReferenceEquals(candidate, page);
        foreach (var candidate in new[] { toyPanelNav, toyControlNav, shockerControlNav, spsNav, remoteNav, logsNav, settingsNav, updateNav })
        {
            candidate.BackColor = ReferenceEquals(candidate, navigation) ? PanelColor : Sidebar;
            candidate.ForeColor = ReferenceEquals(candidate, navigation) ? TextColor : Muted;
        }
        page.BringToFront();
        if (ReferenceEquals(page, logsPage) && logGrid.RowCount > 0)
            TryScrollLog(logGrid.RowCount - 1);
    }

    private void BuildToyPanelPage()
    {
        toyPanelPage.AutoScroll = true;
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 9, Margin = Padding.Empty, Padding = Padding.Empty };
        activityLayout = layout;
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 103));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 103));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 194));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        toyPanelPage.Controls.Add(layout);

        var header = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var title = Label("Toy panel", 21, true);
        title.SetBounds(0, 0, 390, 35);
        var subtitle = Label("Toy controls and connection status.", 9, false, Muted);
        subtitle.SetBounds(0, 42, 560, 25);
        bridgeButton.FlatAppearance.BorderSize = 0;
        bridgeButton.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        bridgeButton.Click += (_, _) =>
        {
            if (bridge is { HasExited: false }) StopBridge();
            else StartBridge();
        };
        header.Controls.Add(title);
        header.Controls.Add(updateAvailable);
        header.Controls.Add(subtitle);
        header.Controls.Add(bridgeButton);
        header.Resize += (_, _) =>
        {
            bridgeButton.Location = new Point(Math.Max(0, header.Width - bridgeButton.Width), 4);
            title.Width = Math.Min(180, Math.Max(1, bridgeButton.Left - 12));
            updateAvailable.Location = new Point(Math.Min(title.Width, TextRenderer.MeasureText(title.Text, title.Font).Width) + 12, 11);
            subtitle.Width = header.Width;
        };
        layout.Controls.Add(header, 0, 0);

        var cards = BuildStatusRow();
        layout.Controls.Add(cards, 0, 1);
        layout.Controls.Add(BuildStatusRow(local: true), 0, 2);

        var toysHeader = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var toysTitle = Label("Shockers", 10, true);
        toysTitle.SetBounds(0, 5, 180, 24);
        var add = StyledButton("+ Add shocker", false, rounded: true);
        add.Font = new Font("Segoe UI Semibold", 9, FontStyle.Bold);
        add.Size = new Size(120, 28);
        add.FlatAppearance.BorderSize = 0;
        add.Click += (_, _) => AddShocker();
        toysHeader.Controls.AddRange([toysTitle, add]);
        toysHeader.Resize += (_, _) => add.Location = new Point(Math.Max(0, toysHeader.Width - add.Width), 0);
        layout.Controls.Add(toysHeader, 0, 3);
        layout.Controls.Add(shockerGrid, 0, 4);
        RebuildShockerCards();
        shockerGrid.Resize += (_, _) => ArrangeActivity();
        toyPanelPage.Resize += (_, _) => ArrangeActivity();

        modeBanner.Dock = DockStyle.Fill;
        modeBanner.Padding = new Padding(12, 10, 0, 0);
        modeBanner.Font = new Font("Segoe UI Semibold", 9, FontStyle.Bold);
        modeBanner.Margin = new Padding(0, 0, 0, 12);
        var localHeader = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var localTitle = Label("Bluetooth toys", 10, true);
        localTitle.SetBounds(0, 5, 230, 24);
        localHeader.Controls.Add(localTitle);
        layout.Controls.Add(localHeader, 0, 5);
        layout.Controls.Add(localToysPage, 0, 6);
        layout.Controls.Add(modeBanner, 0, 7);
        ArrangeActivity();
    }

    private Panel StatusCard(string name, Label status, string helpText)
    {
        var card = new RoundedPanel { Dock = DockStyle.Fill, BackColor = PanelColor, Margin = Padding.Empty, Padding = new Padding(14, 11, 12, 11) };
        var header = new TableLayoutPanel { Dock = DockStyle.Top, Height = 24, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty, Padding = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 20));
        header.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var heading = Label(name, 9, true, Muted);
        heading.Dock = DockStyle.Fill;
        heading.Margin = Padding.Empty;
        var icon = new HelpIcon(Accent, Hex("#18b991"))
        {
            AccessibleName = name + " help",
            AccessibleDescription = helpText,
            BackColor = PanelColor,
            Margin = Padding.Empty,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };
        statusHelp.SetToolTip(icon, helpText);
        icon.Click += (_, _) => statusHelp.Show(helpText, icon, new Point(-300, icon.Height + 5), 20000);
        header.Controls.Add(heading, 0, 0);
        header.Controls.Add(icon, 1, 0);
        card.Controls.Add(status);
        card.Controls.Add(header);
        return card;
    }

    private void SafeUi(Action action)
    {
        if (closing || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

}
