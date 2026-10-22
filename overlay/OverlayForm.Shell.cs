using System.Diagnostics;
using System.Drawing;
using System.Text.RegularExpressions;
using System.Text.Json;
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
        toyControlPage = new(localToysPage, ForwardOscToBridge);
        spsPage = new(localToysPage, () => toyControlPage.Listening, initialSpsStore, () => toyControlPage.AvatarPort);
        remoteSession = new(() => localToysPage.Snapshot, () => localToysPage.Controller, () => config, SendRemoteShocker,
            initialRemoteFriends ?? (initialConfig is null ? new RemoteFriendStore(Path.Combine(Path.GetDirectoryName(BridgeConfig.ConfigPath)!, "remote-friends.bin")) : new MemoryRemoteFriendStore()));
        remotePage = new(remoteSession, NodePath, bridgeDirectory);
        toyControlPage.Session.Remote = remoteSession.Toys;
        toyControlPage.Session.Shockers.Inventory = ShockerControlInventory;
        toyControlPage.Session.Shockers.Send = SendPanelShocker;
        toyControlPage.Session.PageChanged += () => { if(toyControlPage.Visible) ShowPage(toyControlPage,toyControlPage.Session.ShockerPage ? shockerControlNav : toyControlNav); };
        remoteSession.Changed += UpdateRemoteGate;
        remoteSession.Log += message => AppendLog("remote", message);
        remoteSession.Toys.Log += message => AppendLog("remote", message);
        toyControlPage.Session.Log += message => AppendLog("remote", message);
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

    private void BuildLogsPage()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        logsPage.Controls.Add(layout);

        var logHeader = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var logTitle = Label("Log", 21, true);
        logTitle.SetBounds(0, 0, 220, 35);
        var logSubtitle = Label("Recent activity from this session", 9, false, Muted);
        logSubtitle.SetBounds(0, 42, 310, 25);
        var clear = StyledButton("Clear", false);
        var copy = StyledButton("Copy log", false);
        var copyRemote = StyledButton("Copy remote log", false);
        copyRemote.Width = 150; copyRemote.FlatAppearance.BorderSize = 0;
        copyRemote.Click += (_, _) =>
        {
            try { Clipboard.SetText(RemoteLogText()); }
            catch (Exception) { AppendLog("error", "Could not copy the remote log."); }
        };
        copy.Width = 100;
        copy.FlatAppearance.BorderSize = 0;
        copy.Click += (_, _) =>
        {
            try { if (recentLog.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, recentLog)); }
            catch (Exception error) { AppendLog("error", "Could not copy the log: " + error.Message); }
        };
        clear.FlatAppearance.BorderSize = 0;
        clear.Click += (_, _) => { recentLog.Clear(); logGrid.RowCount = 0; };
        logHeader.Controls.Add(logTitle);
        logHeader.Controls.Add(logSubtitle);
        logHeader.Controls.Add(clear);
        logHeader.Controls.Add(copy);
        logHeader.Controls.Add(copyRemote);
        logHeader.Resize += (_, _) =>
        {
            clear.Location = new Point(Math.Max(0, logHeader.Width - clear.Width), 4);
            copy.Location = new Point(Math.Max(0, clear.Left - copy.Width - 12), 4);
            copyRemote.Location = new Point(Math.Max(0, copy.Left - copyRemote.Width - 12), 4);
            logTitle.Width = Math.Max(1, copyRemote.Left - 12);
            logSubtitle.Width = logHeader.Width;
        };
        layout.Controls.Add(logHeader, 0, 0);

        logGrid.Dock = DockStyle.Fill;
        logGrid.Margin = Padding.Empty;
        logGrid.BackgroundColor = PanelColor;
        logGrid.BorderStyle = BorderStyle.None;
        logGrid.GridColor = InputColor;
        logGrid.RowHeadersVisible = false;
        logGrid.AllowUserToAddRows = false;
        logGrid.AllowUserToDeleteRows = false;
        logGrid.AllowUserToResizeRows = false;
        logGrid.ReadOnly = true;
        logGrid.MultiSelect = false;
        logGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        logGrid.EnableHeadersVisualStyles = false;
        logGrid.ColumnHeadersHeight = 34;
        logGrid.RowTemplate.Height = 30;
        // Do not measure every stored message on show/resize or each append:
        // this shares the UI thread with the expiring control timers.
        logGrid.VirtualMode = true;
        logGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        logGrid.CellValueNeeded += (_, args) => args.Value = LogValue(args.RowIndex,args.ColumnIndex);
        logGrid.CellToolTipTextNeeded += (_, args) => args.ToolTipText = LogValue(args.RowIndex,2);
        logGrid.CellFormatting += (_, args) =>
        {
            if (args.CellStyle is not { } style) return;
            var error = LogValue(args.RowIndex,1) == "ERROR";
            if (args.ColumnIndex == 1) style.ForeColor = error ? Warning : Accent;
            else if (args.ColumnIndex == 2) style.ForeColor = error ? Warning : TextColor;
        };
        logGrid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = PanelHover, ForeColor = Muted, SelectionBackColor = PanelHover,
            SelectionForeColor = Muted, Font = new Font("Segoe UI Semibold", 9, FontStyle.Bold),
        };
        logGrid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = PanelColor, ForeColor = TextColor, SelectionBackColor = PanelHover,
            SelectionForeColor = TextColor, Font = new Font("Segoe UI", 9),
        };
        logGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Time", Width = 92, SortMode = DataGridViewColumnSortMode.NotSortable });
        logGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Source", Width = 78, SortMode = DataGridViewColumnSortMode.NotSortable });
        logGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Event", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
        logGrid.Columns[2].DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        layout.Controls.Add(logGrid, 0, 1);
    }

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

    private void BuildSettingsPage()
    {
        var flow = new ThemedFlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = Background,
            Margin = Padding.Empty,
            Padding = Padding.Empty,
        };
        settingsPage.Controls.Add(flow);
        var heading = new Panel { Height = 82, Width = 720, Margin = Padding.Empty };
        var title = Label("Settings", 21, true);
        title.SetBounds(0, 0, 350, 35);
        var subtitle = Label("Connections and debug options for this PC.", 9, false, Muted);
        subtitle.SetBounds(0, 42, 400, 24);
        heading.Controls.Add(title);
        heading.Controls.Add(subtitle);
        flow.Controls.Add(heading);

        BuildProviderSettings(flow);
        var apiCard = Card(291);
        openShockSettingsCard = apiCard;
        AddCardHeading(apiCard, "OpenShock", "Connect your account. Set each UUID on its shocker card.");
        AddField(apiCard, "API URL", apiUrlInput, 88);
        tokenInput.UseSystemPasswordChar = true;
        tokenInput.PlaceholderText = "Paste the generated API token value";
        statusHelp.SetToolTip(tokenInput, "Enter the actual token value generated by OpenShock. Copy ID on the API Tokens page gives an identifier. Your shocker UUID belongs under Options on its card. Remember token encrypts it for this Windows account.");
        AddField(apiCard, "API token", tokenInput, 151);
        rememberTokenInput.Text = "Remember token on this PC";
        rememberTokenInput.ForeColor = TextColor;
        rememberTokenInput.BackColor = PanelColor;
        rememberTokenInput.AutoSize = true;
        rememberTokenInput.SetBounds(18, 211, 300, 28);
        statusHelp.SetToolTip(rememberTokenInput, "Save encrypts your token for this Windows account. Uncheck and Save to remove the remembered copy; the current session keeps working.");
        apiCard.Controls.Add(rememberTokenInput);
        statusHelp.SetToolTip(pollIntervalInput, "Minimum delay before reconnecting to live logs. Valid shockers are checked once; failed setup checks wait at least one minute between retries. Changing sliders only saves limits.");
        tokenHint.Text = "Remember token saves an encrypted copy for this Windows account.";
        tokenHint.ForeColor = Faint;
        tokenHint.BackColor = Color.Transparent;
        tokenHint.Font = new Font("Segoe UI", 8);
        tokenHint.SetBounds(18, 249, 670, 32);
        tokenHint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        apiCard.Controls.Add(tokenHint);
        flow.Controls.Add(apiCard);

        var oscCard = Card(158);
        AddCardHeading(oscCard, "VRChat OSC", "Default ports are 9001 in and 9000 out.");
        AddField(oscCard, "Listen port", listenPortInput, 87, 2, 0);
        AddField(oscCard, "Send port", sendPortInput, 87, 2, 1);
        flow.Controls.Add(oscCard);

        var bluetoothCard = Card(338);
        AddCardHeading(bluetoothCard, "Bluetooth toys", "Discovery, recovery and toy update rate for this PC.");
        AddField(bluetoothCard, "Toy source", localToysPage.SourceInput, 87);
        AddField(bluetoothCard, "Updates/toy/sec", localToysPage.UpdateRateInput, 150);
        statusHelp.SetToolTip(localToysPage.UpdateRateInput, "Shared by all functions of each toy. Defaults to 5; accepts 1–10 for this session. All toys share a 20/sec normal-update ceiling. This is an app limit, not measured Bluetooth capacity.");
        var bluetoothActions = new FlowLayoutPanel { Location = new Point(18, 218), Height = 76, Width = 670,
            Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right, WrapContents = true };
        bluetoothActions.Controls.AddRange([localToysPage.ResetBluetoothButton, localToysPage.StopAllButton, localToysPage.CancelButton]);
        bluetoothCard.Controls.Add(bluetoothActions);
        localToysPage.OperationStatus.SetBounds(18, 295, 670, 38);
        localToysPage.OperationStatus.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        localToysPage.OperationStatus.Font = new Font("Segoe UI", 8);
        bluetoothCard.Controls.Add(localToysPage.OperationStatus);
        flow.Controls.Add(bluetoothCard);

        var debugCard = Card(378);
        AddCardHeading(debugCard, "Debug", "Test your own devices with the OSC output from your avatar.");
        testModeInput.Text = "Test mode · device commands disabled";
        testModeInput.ForeColor = TextColor;
        testModeInput.BackColor = PanelColor;
        testModeInput.AutoSize = true;
        testModeInput.SetBounds(18, 81, 390, 30);
        debugCard.Controls.Add(testModeInput);
        selfTestInput.Text = "Control my own devices from VRChat";
        selfTestInput.ForeColor = TextColor;
        selfTestInput.BackColor = PanelColor;
        selfTestInput.AutoSize = true;
        selfTestInput.SetBounds(18, 115, 390, 30);
        statusHelp.SetToolTip(selfTestInput, "While enabled, avatar OSC actions go only to the test devices selected below, through the selected shocker service. Each device keeps its own limits, pause, and cooldown. Test mode still disables all real device commands. Manual card buttons keep targeting their own device.");
        debugCard.Controls.Add(selfTestInput);
        var devicesLabel = Label("My test devices · select your own devices", 9, true);
        devicesLabel.SetBounds(18, 155, 660, 22);
        devicesLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        debugCard.Controls.Add(devicesLabel);
        selfTestDevicesInput.BackColor = InputColor;
        selfTestDevicesInput.ForeColor = TextColor;
        selfTestDevicesInput.BorderStyle = BorderStyle.FixedSingle;
        selfTestDevicesInput.CheckOnClick = true;
        selfTestDevicesInput.IntegralHeight = false;
        selfTestDevicesInput.HorizontalScrollbar = true;
        selfTestDevicesInput.SetBounds(18, 181, 660, 80);
        selfTestDevicesInput.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        selfTestDevicesInput.AccessibleName = "My test devices";
        statusHelp.SetToolTip(selfTestDevicesInput, "Add devices on Toy panel for the selected service. OpenShock uses UUIDs; PiShock loads your available devices. Select only your own devices here. Self test sends the same avatar action to every checked device.");
        debugCard.Controls.Add(selfTestDevicesInput);
        AddField(debugCard, "VRChat test group", selfTestGroupInput, 272);
        statusHelp.SetToolTip(selfTestGroupInput, "Use the parameter group your avatar sends, usually PiCtrl. It reads <group>_Intensity, <group>_Duration, and <group>_Action. In self test this group is routed to your checked devices instead of the usual card assignments.");
        var selfTestHint = Label("Select your devices, then Save settings. Turn Test mode off to activate them.", 9, false, Muted);
        selfTestHint.SetBounds(18, 339, 660, 31);
        selfTestHint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        debugCard.Controls.Add(selfTestHint);
        flow.Controls.Add(debugCard);

        var actions = new Panel { Height = 68, Width = 720, Margin = Padding.Empty };
        saveMessage.ForeColor = Muted;
        saveMessage.BackColor = Color.Transparent;
        saveMessage.SetBounds(0, 17, 490, 42);
        var saveButton = StyledButton("Save settings", true);
        saveButton.FlatAppearance.BorderSize = 0;
        saveButton.SetBounds(552, 8, 150, 40);
        saveButton.Click += (_, _) => SaveSettings();
        actions.Controls.Add(saveMessage);
        actions.Controls.Add(saveButton);
        actions.Resize += (_, _) =>
        {
            saveButton.Left = Math.Max(0, actions.Width - saveButton.Width);
            saveMessage.Width = Math.Max(1, saveButton.Left - 12);
        };
        flow.Controls.Add(actions);

        var resizingSettings = false;
        void FitSettings()
        {
            if (resizingSettings) return;
            resizingSettings = true;
            try
            {
                var width = Math.Max(1, flow.ClientSize.Width);
                foreach (Control control in flow.Controls) control.Width = width;
            }
            finally { resizingSettings = false; }
        }
        flow.Resize += (_, _) => FitSettings();
        flow.Layout += (_, _) => FitSettings();
        FitSettings();
    }

    private static void AddCardHeading(Panel card, string title, string subtitle)
    {
        var heading = Label(title, 12, true);
        heading.SetBounds(18, 15, 600, 28);
        heading.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var hint = Label(subtitle, 9, false, Muted);
        hint.SetBounds(18, 45, 660, 23);
        hint.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        card.Controls.Add(heading);
        card.Controls.Add(hint);
    }

    private static void AddField(Panel card, string title, Control input, int y, int columns = 1, int column = 0)
    {
        var label = Label(title, 9, true);
        card.Controls.Add(label);
        card.Controls.Add(input);
        void Arrange()
        {
            var width = Math.Max(1, (card.ClientSize.Width - 36 - 24 * (columns - 1)) / columns);
            var x = 18 + column * (width + 24);
            label.SetBounds(x, y, width, 22);
            input.SetBounds(x, y + 24, width, 30);
        }
        card.Resize += (_, _) => Arrange();
        Arrange();
    }

    private void FillSettings()
    {
        FillProviderSettings();
        apiUrlInput.Text = config.ApiBaseUrl;
        testModeInput.Checked = config.DryRun;
        selfTestInput.Checked = config.ActiveSelfTestEnabled;
        selfTestGroupInput.Text = config.SelfTestAvatarGroup;
        FillSelfTestDevices(useSavedSelection: true);
        pollIntervalInput.Value = Math.Clamp(config.PollIntervalMs, 500, 60000);
        listenPortInput.Value = Math.Clamp(config.Osc.ListenPort, 1, 65535);
        sendPortInput.Value = Math.Clamp(config.Osc.SendPort, 1, 65535);
        tokenInput.Text = apiToken;
        rememberTokenInput.Checked = config.RememberApiToken;
        tokenHint.Text = apiToken.Length > 0
            ? tokenRemembered ? "Token remembered with Windows encryption; loaded automatically next time."
                : "Token available for this session. Save with Remember token checked to keep it."
            : "Paste the generated token value; each shocker UUID goes on its own card.";
        RefreshApiSetupStatus();
    }

    private sealed record SelfTestDevice(string Key, string Name)
    {
        public override string ToString() => Name;
    }

    private void FillSelfTestDevices(bool useSavedSelection = false)
    {
        var selected = useSavedSelection ? config.ActiveSelfTestShockerKeys.ToHashSet()
            : selfTestDevicesInput.CheckedItems.Cast<SelfTestDevice>().Select(item => item.Key).ToHashSet();
        selfTestDevicesInput.BeginUpdate();
        try
        {
            selfTestDevicesInput.Items.Clear();
            foreach (var device in config.ActiveShockers)
                selfTestDevicesInput.Items.Add(new SelfTestDevice(device.Key, device.Name), selected.Contains(device.Key));
        }
        finally { selfTestDevicesInput.EndUpdate(); }
    }

    private void SaveSettings()
    {
        FlushShockerChanges();
        var next = config.Clone();
        next.ApiBaseUrl = apiUrlInput.Text.Trim();
        next.DryRun = testModeInput.Checked;
        next.ActiveSelfTestEnabled = selfTestInput.Checked;
        next.ActiveSelfTestShockerKeys = selfTestDevicesInput.CheckedItems.Cast<SelfTestDevice>().Select(item => item.Key).ToList();
        next.SelfTestAvatarGroup = selfTestGroupInput.Text.Trim();
        next.RememberApiToken = rememberTokenInput.Checked;
        next.PollIntervalMs = (int)pollIntervalInput.Value;
        next.Osc.ListenPort = (int)listenPortInput.Value;
        next.Osc.SendPort = (int)sendPortInput.Value;
        var nextToken = tokenInput.Text.Trim().Length > 0 ? tokenInput.Text.Trim() : apiToken;
        var nextPiShockKey = piShockKeyInput.Text.Trim();
        next.PiShockUsername = piShockUsernameInput.Text.Trim();
        next.RememberPiShockApiKey = rememberPiShockInput.Checked;
        next.Provider = providerInput.SelectedItem as string ?? "OpenShock";
        var providerChanged = next.Provider != config.Provider;
        var error = next.Validate(next.IsPiShock ? nextPiShockKey : nextToken, requireToken: false);
        if (error is not null)
        {
            saveMessage.Text = error;
            return;
        }
        if (!next.DryRun && config.DryRun &&
            MessageBox.Show("Enable live device commands, including local toys?", "Enable live mode",
                MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
            return;
        try
        {
            tokenStore?.Save(nextToken, next.RememberApiToken);
            piShockKeyStore?.Save(nextPiShockKey, next.RememberPiShockApiKey);
            saveConfig(next);
            if (providerChanged) { piShockDevices.Clear(); apiHealth = null; }
            var oscChanged=config.Osc.ListenPort!=next.Osc.ListenPort || config.Osc.SendPort!=next.Osc.SendPort || config.Osc.ListenHost!=next.Osc.ListenHost;
            config = next;
            if (providerChanged) UpdateProviderStatusCards();
            if(oscChanged) toyControlPage.ConfigureOsc(config.Osc.ListenPort,config.Osc.SendPort,config.Osc.ListenHost);
            localToysPage.SetTestMode(config.DryRun);
            apiToken = nextToken;
            piShockApiKey = nextPiShockKey;
            tokenRemembered = tokenStore is not null && next.RememberApiToken && nextToken.Length > 0;
            FillSettings();
            SetModeBanner();
            RebuildShockerCards();
            saveMessage.Text = "Saved. The bridge will use these settings.";
            AppendLog("overlay", "Settings saved");
            if (bridge is { HasExited: false })
            {
                restartPending = true;
                StopBridge();
            }
            else if (config.DryRun) StartBridge();
        }
        catch (Exception exception)
        {
            saveMessage.Text = exception.Message;
            AppendLog("error", "Could not save settings: " + exception.Message);
        }
    }

    private void SetModeBanner()
    {
        modeBanner.Text = config.ActiveSelfTestEnabled
            ? config.DryRun ? "SELF TEST · Selected devices · Commands disabled" : "SELF TEST · Selected devices · Commands enabled"
            : config.DryRun ? "TEST MODE · Device commands disabled" : "LIVE MODE · Device commands enabled";
        modeBanner.BackColor = config.DryRun ? Hex("#17382f") : Hex("#4b3025");
        modeBanner.ForeColor = config.DryRun ? Hex("#7ce0c1") : Hex("#ffbd86");
    }

    private static string NodePath()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "node.exe");
        if (File.Exists(bundled)) return bundled;
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        return File.Exists(installed) ? installed : "node.exe";
    }

    private void StartBridge()
    {
        if (bridge is { HasExited: false }) return;
        var script = Path.Combine(bridgeDirectory, "bridge.mjs");
        if (!File.Exists(script))
        {
            AppendLog("error", "The packaged bridge files are missing.");
            SetBridgeStatus("Error", ErrorColor);
            return;
        }
        var start = new ProcessStartInfo(NodePath())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = bridgeDirectory,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(BridgeConfig.ConfigPath);
        start.Environment["OPENSHOCK_TOKEN"] = config.IsPiShock ? "" : apiToken;
        start.Environment["PISHOCK_API_KEY"] = config.IsPiShock ? piShockApiKey : "";
        start.Environment["SHOCKWAVE_EXTERNAL_OSC"] = "1";
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null) SafeUi(() => { if (bridge == process) HandleBridgeLine("bridge", args.Data); });
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null) SafeUi(() =>
            {
                if (bridge == process) HandleBridgeLine("error", args.Data);
                else if (lastStartedBridge == process) AppendLog("error", args.Data);
            });
        };
        process.Exited += (_, _) => SafeUi(() => BridgeExited(process));
        try
        {
            stopRequested = false;
            apiHealth = null;
            nameRequests.Clear(); failedNames.Clear(); deferredNames.Clear();
            foreach (var card in shockerCards) card.SetApiState(null);
            bridge = process;
            lastStartedBridge = process;
            SetBridgeStatus("Starting", Warning);
            RefreshToyControlStatus();
            process.Start();
            UpdateRemoteGate();
            RefreshApiSetupStatus(connecting: true);
            process.StandardInput.AutoFlush = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            SetBridgeStatus("Running", Accent);
            AppendLog("overlay", "PiCtrl bridge started");
            foreach (var card in shockerCards) SyncName(card);
        }
        catch (Exception error)
        {
            bridge = null;
            process.Dispose();
            SetBridgeStatus("Error", ErrorColor);
            RefreshToyControlStatus();
            AppendLog("error", "Could not start bridge: " + error.Message);
        }
    }

    private void StopBridge()
    {
        if (bridge is not { HasExited: false } process) return;
        stopRequested = true;
        foreach (var card in shockerCards) card.SetRuntime(false, config.DryRun);
        SetBridgeStatus("Stopping", Faint);
        apiHealth = null;
        ApplyHubHealth();
        bridgeButton.Enabled = false;
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { BridgeExited(process); }
        catch (Exception error)
        {
            bridgeButton.Enabled = true;
            AppendLog("error", "Could not stop bridge: " + error.Message);
        }
    }

    private void BridgeExited(Process process)
    {
        if (closing || bridge != process) return;
        var expected = stopRequested;
        bridge = null;
        apiHealth = null;
        nameRequests.Clear(); deferredNames.Clear();
        stopRequested = false;
        bridgeButton.Enabled = true;
        SetBridgeStatus(expected ? "Stopped" : "Error", expected ? Faint : ErrorColor);
        RefreshApiSetupStatus();
        RefreshToyControlStatus();
        AppendLog(expected ? "overlay" : "error", expected ? "Bridge stopped" : "Bridge exited unexpectedly");
        process.Dispose();
        if (restartPending)
        {
            restartPending = false;
            BeginInvoke(StartBridge);
        }
    }

    private void SafeUi(Action action)
    {
        if (closing || IsDisposed || !IsHandleCreated) return;
        try { BeginInvoke(action); }
        catch (InvalidOperationException) { }
    }

    private void HandleBridgeLine(string source, string line)
    {
        line = Regex.Replace(line, @"^\[\d{1,2}:\d{2}:\d{2}[^\]]*\]\s*", "");
        if (HandlePiShockDevices(line)) return;
        if (line.StartsWith("@activity ", StringComparison.Ordinal))
        {
            if (!closing) shockerActivity.Receive(line[10..], config.ActiveShockers.Select(device => device.Key));
            return;
        }
        if (HandleShockerUpdate(line)) return;
        if (line.StartsWith("@api ", StringComparison.Ordinal))
        {
            if (closing || stopRequested || restartPending) return;
            try
            {
                var snapshot = JsonSerializer.Deserialize<ApiStatusSnapshot>(line[5..],
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (snapshot is not { Kind: "api-status" } || snapshot.Shockers is null)
                    throw new JsonException("Missing API status details");
                apiHealth = snapshot;
                ApplyApiHealth();
            }
            catch (JsonException error) { AppendLog("error", "Could not read API status: " + error.Message); }
            return;
        }
        if (apiHealth is null)
        {
        if (line.Contains("OpenShock logs connected", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("OpenShock logs reconnected", StringComparison.OrdinalIgnoreCase))
            SetApiStatus("Connected", Accent);
        else if (line.Contains("OpenShock HTTP 401", StringComparison.OrdinalIgnoreCase))
            SetApiStatus("Token rejected", ErrorColor);
        else if (line.Contains("OpenShock HTTP 403", StringComparison.OrdinalIgnoreCase))
            SetApiStatus("No access", ErrorColor);
        else if (line.Contains("OpenShock HTTP 404", StringComparison.OrdinalIgnoreCase))
            SetApiStatus("UUID not found", ErrorColor);
        else if (line.Contains("OpenShock log poll failed", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("OpenShock command failed", StringComparison.OrdinalIgnoreCase))
            SetApiStatus("Error", ErrorColor);
        else if (line.Contains("API setup: token missing", StringComparison.OrdinalIgnoreCase))
            SetApiStatus("Token needed", Warning);
        else if (line.Contains("API setup: shocker UUID missing", StringComparison.OrdinalIgnoreCase))
            SetApiStatus("UUID needed", Warning);
        else if (line.Contains("API setup: checking connection", StringComparison.OrdinalIgnoreCase))
            SetApiStatus("Connecting", Warning);
        }
        if (line.Contains("Listening for VRChat OSC", StringComparison.OrdinalIgnoreCase))
            RefreshToyControlStatus();
        else if (line.Contains("OSC listener error", StringComparison.OrdinalIgnoreCase))
            RefreshToyControlStatus();
        var category = source == "error" || line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("error:", StringComparison.OrdinalIgnoreCase) ? "error"
            : line.Contains("OpenShock", StringComparison.OrdinalIgnoreCase) || line.Contains("PiShock", StringComparison.OrdinalIgnoreCase) ||
              line.Contains("API log", StringComparison.OrdinalIgnoreCase) ? "api"
            : line.Contains("OSC", StringComparison.OrdinalIgnoreCase) ||
              line.Contains("TEST ONLY", StringComparison.OrdinalIgnoreCase) ? "osc"
            : "bridge";
        AppendLog(category, line);
    }

    private void AppendLog(string source, string message)
    {
        if (apiToken.Length > 0) message = message.Replace(apiToken, "[redacted]", StringComparison.Ordinal);
        if (piShockApiKey.Length > 0) message = message.Replace(piShockApiKey, "[redacted]", StringComparison.Ordinal);
        foreach (var device in config.Shockers.Concat(config.PiShockShockers))
            if (device.Uuid.Length > 0) message = message.Replace(device.Uuid, "[shocker]", StringComparison.OrdinalIgnoreCase);
        var time = DateTime.Now.ToString("HH:mm:ss");
        var entry = time + "  " + source.ToUpperInvariant() + "  " + message;
        recentLog.Add(entry);
        while (recentLog.Count > 500) recentLog.RemoveAt(0);
        var fileError = sessionLog?.Append(entry);
        var visible = logGrid.Visible;
        var nearBottom = visible && logGrid.RowCount == 0;
        if (visible) try
        {
            nearBottom = nearBottom || logGrid.FirstDisplayedScrollingRowIndex < 0 ||
                logGrid.FirstDisplayedScrollingRowIndex + logGrid.DisplayedRowCount(false) >= logGrid.RowCount - 2;
        }
        catch (InvalidOperationException) { nearBottom = true; }
        if (logGrid.RowCount != recentLog.Count) logGrid.RowCount = recentLog.Count;
        if (visible)
        {
            logGrid.Invalidate();
            if (nearBottom && logGrid.RowCount > 0) TryScrollLog(logGrid.RowCount - 1);
        }
        if (fileError is not null) AppendLog("error", fileError);
    }

    private string LogValue(int row, int column)
    {
        if (row < 0 || row >= recentLog.Count || column is < 0 or > 2) return "";
        var parts = recentLog[row].Split(["  "],3,StringSplitOptions.None);
        return column < parts.Length ? parts[column] : "";
    }

    private void TryScrollLog(int index)
    {
        if (!logGrid.IsHandleCreated || logGrid.Height <= logGrid.ColumnHeadersHeight) return;
        try { logGrid.FirstDisplayedScrollingRowIndex = index; }
        catch (InvalidOperationException) { }
    }

    private void ApplyApiHealth()
    {
        ApplyHubHealth();
        if (apiHealth is null) return;
        foreach (var card in shockerCards)
            card.SetApiState(apiHealth.Shockers.FirstOrDefault(status => status.Key == card.Device.Key));
        if (config.IsPiShock && !config.ActiveShockers.Any(s => s.Uuid.Length > 0))
        {
            var connection = apiHealth.Connection;
            SetApiStatus(connection?.State == "Connected" ? "Connected" : connection?.State == "Error" ? connection.Label.Replace("API ", "") : ActiveApiToken.Length == 0 ? "Key needed" : "Connecting",
                connection?.State == "Connected" ? Accent : Warning);
            statusHelp.SetToolTip(apiStatus, connection?.Detail ?? "Enter PiShock credentials, save Settings, and start the bridge.");
            return;
        }
        var statuses = config.ActiveShockers.Where(device => device.Uuid.Length > 0)
            .Select(device => apiHealth.Shockers.FirstOrDefault(status => status.Key == device.Key)
                ?? new ShockerApiStatus { Key = device.Key }).ToList();
        var details = config.ActiveShockers.Select(device =>
            device.Name + ": " + (apiHealth.Shockers.FirstOrDefault(status => status.Key == device.Key)?.Detail
                ?? (device.Uuid.Length > 0 ? "Checking connection" : "Enter UUID under Options")));
        statusHelp.SetToolTip(apiStatus, string.Join("\n\n", details) + "\n\nClick to show errors on the Log page.");
        if (ActiveApiToken.Length == 0) { SetApiStatus(config.IsPiShock ? "Key needed" : "Token needed", Warning); return; }
        if (statuses.Count == 0) { SetApiStatus(config.IsPiShock ? "Add shocker" : "UUID needed", Warning); return; }
        var connected = statuses.Count(status => status.State == "Connected");
        var errors = statuses.Where(status => status.State is "Error" or "RateLimited").ToList();
        if (apiHealth.RetryAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            SetApiStatus("Waiting to retry", Warning);
        else if (errors.Count > 0 && connected > 0)
            SetApiStatus($"{connected}/{statuses.Count} connected", Warning);
        else if (errors.Count > 0)
            SetApiStatus(errors.Select(status => status.Label).Distinct().Count() == 1
                ? errors[0].Label.Replace("API ", "", StringComparison.Ordinal) : "Error", ErrorColor);
        else if (connected < statuses.Count)
            SetApiStatus("Connecting", Warning);
        else
            SetApiStatus(statuses.Count == 1 ? "Connected" : $"Connected ({statuses.Count})", Accent);
    }

    private void SetBridgeStatus(string value, Color color)
    {
        bridgeStatus.Text = "●  " + value;
        bridgeStatus.ForeColor = color;
        bridgeButton.Text = value is "Running" or "Starting" ? "Stop bridge" : "Start bridge";
        foreach (var card in shockerCards) card.SetRuntime(value == "Running", config.DryRun);
    }

    private void RefreshApiSetupStatus(bool connecting = false)
    {
        ApplyHubHealth();
        if (ActiveApiToken.Length == 0) SetApiStatus(config.IsPiShock ? "Key needed" : "Token needed", Warning);
        else if (!config.IsPiShock && !config.ActiveShockers.Any(s => s.Uuid.Length > 0)) SetApiStatus("UUID needed", Warning);
        else if (bridge is not { HasExited: false }) SetApiStatus("Bridge stopped", Faint);
        else if (connecting) SetApiStatus("Connecting", Warning);
        else ApplyApiHealth();
    }

    private void SetApiStatus(string value, Color color)
    {
        apiStatus.Text = "●  " + value;
        apiStatus.ForeColor = color;
    }

    private void SetOscStatus(string value, Color color)
    {
        oscStatus.Text = "●  " + value;
        oscStatus.ForeColor = color;
    }
}
