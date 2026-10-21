using ShockWave.Overlay.Toys;

namespace ShockWave.Overlay;

// Owns the existing local adapter/controller; its visible content is now the card grid in Toy panel.
internal sealed class LocalToysPage : UserControl
{
    private readonly ComboBox source = new SettingChoiceInput() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 232 };
    private readonly NumericUpDown updateRate = new SettingNumberInput() { ShowSpinner = false, BorderStyle = BorderStyle.None,
        Minimum = 1, Maximum = 10, Value = 5, Width = 52,
        AccessibleName = "Bluetooth updates per toy per second" };
    private readonly Button resetBluetooth = Button("Reset Bluetooth"), stopAll = Button("Stop all toys"),
        cancel = Button("Cancel operation");
    private readonly Label status = new() { ForeColor = Color.FromArgb(181, 186, 193) };
    private readonly Label empty = new() { Dock = DockStyle.Top, Height = 38, ForeColor = Color.FromArgb(181, 186, 193),
        Text = "Nearby toys connect automatically. Use Scan toys to discover more." };
    private readonly Dictionary<(string, string), ToyFunctionRow> functionRows = [];
    private LocalToyController? controller;
    private readonly LocalToyControlStore sessionControlStore = new();
    private bool testMode = true, stopScanRequested;
    private volatile bool scanPaused;
    private readonly CancellationTokenSource lifetime = new();
    private IRememberedToyAdapter? adapter;
    private readonly Func<int, IRememberedToyAdapter>? createAdapter;
    private bool busy, refreshing, shuttingDown, arranging;
    private string? operationError, loggedDiagnostic;
    private int refreshQueued, refreshDirty;
    private Task activeOperation = Task.CompletedTask;
    private Task? shutdownTask;
    private CancellationTokenSource? operationCancellation;
    private CancellationToken OperationToken => operationCancellation?.Token ?? lifetime.Token;
    private readonly int uiThread = Environment.CurrentManagedThreadId;

    public event Action? Changed, CardsLayoutChanged;
    public event Action<string, string>? Log;
    public ToyAdapterSnapshot? Snapshot => adapter?.Snapshot;
    internal LocalToyController? Controller => controller;
    public bool IsScanning => adapter?.Snapshot.IsScanning == true;
    public bool CanToggleScan => !shuttingDown && (!busy || IsScanning);
    public bool CanResetBluetooth => !busy && !shuttingDown;
    public bool Busy => busy;
    public bool Simulated => source.SelectedIndex is 1 or 2;
    public string StatusMessage => status.Text;
    public int GridHeight { get; private set; } = 38;
    public IReadOnlyList<ToyFunctionRow> Cards => functionRows.Values.ToArray();
    public ComboBox SourceInput => source;
    public NumericUpDown UpdateRateInput => updateRate;
    public Button ResetBluetoothButton => resetBluetooth;
    public Button StopAllButton => stopAll;
    public Button CancelButton => cancel;
    public Label OperationStatus => status;

    private Task OnUiAsync(Action action)
    {
        if (shuttingDown || IsDisposed) return Task.CompletedTask;
        if (Environment.CurrentManagedThreadId == uiThread) { action(); return Task.CompletedTask; }
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke((Action)(() =>
            {
                try { if (!shuttingDown && !IsDisposed) action(); completion.TrySetResult(true); }
                catch (Exception error) { completion.TrySetException(error); }
            }));
        }
        catch (InvalidOperationException) { completion.TrySetCanceled(); }
        return completion.Task;
    }

    public LocalToysPage(Func<int, IRememberedToyAdapter>? createAdapter = null)
    {
        this.createAdapter = createAdapter;
        Dock = DockStyle.Fill; Margin = Padding.Empty; BackColor = Color.FromArgb(30, 31, 34);
        ForeColor = Color.FromArgb(242, 243, 245);
        Controls.Add(empty);
        source.Items.AddRange(["Bluetooth toys (Buttplug)", "Simulated toys", "Simulated Bluetooth off", "Lovense Bluetooth (legacy)"]);
        source.SelectedIndex = 0;
        source.BackColor = updateRate.BackColor = Color.FromArgb(56, 58, 64);
        source.ForeColor = updateRate.ForeColor = ForeColor;
        source.DrawMode = DrawMode.OwnerDrawFixed; source.ItemHeight = 24;
        source.DrawItem += (_, args) =>
        {
            using var background = new SolidBrush(source.BackColor);
            args.Graphics.FillRectangle(background, args.Bounds);
            var text = args.Index >= 0 ? source.Items[args.Index]?.ToString() ?? "" : source.Text;
            TextRenderer.DrawText(args.Graphics, text, source.Font, args.Bounds, source.ForeColor,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            args.DrawFocusRectangle();
        };
        source.SelectedIndexChanged += (_, _) => StartOperation(RecreateAdapterAsync);
        resetBluetooth.Click += (_, _) => ResetBluetooth();
        stopAll.Click += (_, _) => ControlAction(() => controller?.Stop());
        cancel.Click += (_, _) =>
        {
            if (busy) operationCancellation?.Cancel();
            else if (adapter is not null) { scanPaused = true; StartOperation(() => adapter.StopScanAsync(OperationToken)); }
        };
        updateRate.ValueChanged += (_, _) => ControlAction(() => controller?.SetUpdateRate((double)updateRate.Value));
        SizeChanged += (_, _) => ArrangeCards();
        RefreshInventory();
    }

    private static Button Button(string text)
    {
        var button = new Button { Text = text, Size = new Size(142, 32), FlatStyle = FlatStyle.Flat,
            Cursor = Cursors.Hand, BackColor = Color.FromArgb(56, 58, 64), ForeColor = Color.FromArgb(242, 243, 245),
            Font = new Font("Segoe UI", 9) };
        button.FlatAppearance.BorderSize = 0; return button;
    }

    public void StartRememberedConnections() => StartOperation(EnsureAdapterAsync);
    public void ResetBluetooth() => StartOperation(RecreateAdapterAsync);
    public void SetTestMode(bool enabled) { testMode = enabled; controller?.SetTestMode(enabled); RefreshInventory(); }
    public void ToggleScan()
    {
        if (!CanToggleScan) return;
        if (IsScanning)
        {
            // An explicit stop also suppresses background recovery scans until the next user start.
            scanPaused = true;
            if (busy) { stopScanRequested = true; operationCancellation?.Cancel(); return; }
            StartOperation(() => adapter!.StopScanAsync(OperationToken));
        }
        else
        {
            scanPaused = false;
            StartOperation(async () =>
            {
                await EnsureAdapterAsync();
                OperationToken.ThrowIfCancellationRequested();
                await adapter!.StartScanAsync(OperationToken);
            });
        }
    }
    private async Task RecreateAdapterAsync()
    {
        var previous = adapter; adapter = null; scanPaused = false;
        if (previous is not null) previous.Changed -= AdapterChanged;
        if (controller is not null) { controller.Changed -= AdapterChanged; await controller.DisposeAsync(); }
        await OnUiAsync(() =>
        {
            // Inventory callbacks also read this field on the UI thread. Clear it
            // here so a queued refresh cannot lose it midway through rendering.
            controller = null;
            foreach (var row in functionRows.Values) row.Dispose();
            functionRows.Clear(); ArrangeCards();
        });
        if (previous is not null) await previous.DisposeAsync();
        await EnsureAdapterAsync();
    }
    private void ControlAction(Action action)
    {
        try { action(); RefreshInventory(); }
        catch (InvalidOperationException error) { operationError = error.Message; RefreshInventory(); }
    }

    private async Task EnsureAdapterAsync()
    {
        if (adapter is not null) { await adapter.InitializeAsync(lifetime.Token); return; }
        var selectedSource = 0;
        await OnUiAsync(() => selectedSource = source.SelectedIndex);
        lifetime.Token.ThrowIfCancellationRequested();
        var simulation = selectedSource is 1 or 2;
        var missingBluetooth = selectedSource == 2;
        var keyPath = Path.Combine(Path.GetDirectoryName(BridgeConfig.ConfigPath)!, "local-toy-key.bin");
        adapter = createAdapter?.Invoke(selectedSource) ?? new RememberedToyAdapter(selectedSource == 0
            ? new ButtplugToyAdapter(() => LocalToyKeys.LoadOrCreate(keyPath))
            : new LovenseDiscoveryAdapter(
            simulation ? () => new SimulatedLovenseBleApi(missingBluetooth ? 1 : 0) :
                () => new LovenseWorkerApi(),
            simulation ? () => new LocalToyKeys(new byte[32]) :
                () => LocalToyKeys.LoadOrCreate(keyPath),
            simulation, scanDuration: simulation ? TimeSpan.FromMilliseconds(600) : null, commandsPerSecond: 10), simulation ? new MemoryToyStore() : new RememberedToyStore(
                Path.Combine(Path.GetDirectoryName(BridgeConfig.ConfigPath)!, "remembered-toys.bin")));
        adapter.Changed += AdapterChanged;
        await adapter.EnableAutomaticConnectionsAsync(lifetime.Token, () => !scanPaused &&
            (adapter?.Snapshot.SupportsOutputWhileScanning == true || controller?.SchedulerState.ActiveFunctions is null or 0));
        lifetime.Token.ThrowIfCancellationRequested();
        await OnUiAsync(() =>
        {
            controller = new LocalToyController(adapter, simulation || createAdapter is not null ? sessionControlStore :
                new LocalToyControlStore(Path.Combine(Path.GetDirectoryName(BridgeConfig.ConfigPath)!, "local-toy-controls.json")), automaticControls: true);
            controller.SetTestMode(testMode); controller.SetUpdateRate((double)updateRate.Value); controller.Changed += AdapterChanged;
        });
    }

    private void StartOperation(Func<Task> action)
    {
        if (busy || shuttingDown) return;
        activeOperation = RunOperationAsync(action);
    }
    private async Task RunOperationAsync(Func<Task> action)
    {
        operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operationError = null; busy = true; RefreshInventory();
        string? errorMessage = null;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException error) { errorMessage = error.Message; }
        catch (Exception) { errorMessage = "Local discovery failed. Check Bluetooth and the Lovense library, then try again."; }
        finally
        {
            if (stopScanRequested && adapter is not null && !shuttingDown)
            {
                stopScanRequested = false;
                try { await adapter.StopScanAsync(lifetime.Token); }
                catch (InvalidOperationException error) { errorMessage = error.Message; }
                catch (OperationCanceledException) { }
            }
            await OnUiAsync(() =>
            {
                busy = false;
                operationCancellation?.Dispose(); operationCancellation = null;
                if (!shuttingDown && !IsDisposed) { operationError = errorMessage; RefreshInventory(); }
            });
        }
    }

    private void AdapterChanged() { Interlocked.Exchange(ref refreshDirty, 1); QueueInventoryRefresh(); }
    private void QueueInventoryRefresh()
    {
        if (shuttingDown || IsDisposed || !IsHandleCreated || Interlocked.Exchange(ref refreshQueued, 1) != 0) return;
        try
        {
            BeginInvoke((Action)(() =>
            {
                try { Interlocked.Exchange(ref refreshDirty, 0); if (!shuttingDown && !IsDisposed) RefreshInventory(); }
                finally
                {
                    Interlocked.Exchange(ref refreshQueued, 0);
                    if (Volatile.Read(ref refreshDirty) != 0) QueueInventoryRefresh();
                }
            }));
        }
        catch (InvalidOperationException) { Interlocked.Exchange(ref refreshQueued, 0); }
    }
    private void RefreshInventory()
    {
        if (refreshing) { AdapterChanged(); return; }
        refreshing = true;
        try
        {
            var snapshot = adapter?.Snapshot;
            status.Text = operationError ?? (snapshot?.LastError is string lastError ?
                lastError + "\n" + snapshot.Message : snapshot?.Message) ?? "Turn on Bluetooth and your toys. Nearby toys connect automatically.";
            if (controller is not null)
            {
                var pace = controller.SchedulerState;
                status.Text += $"\nUp to {pace.PerToyTarget:0.#}/toy/sec · {pace.PendingFunctions} waiting / {pace.SubmittingFunctions} submitting · " +
                    $"wait {pace.LastWaitMilliseconds:0} ms / submit {pace.LastSubmitMilliseconds:0} ms";
                if (controller.StorageWarning is string warning) status.Text += "\n" + warning;
            }
            var diagnostic = operationError ?? snapshot?.LastError ?? controller?.StorageWarning;
            if (diagnostic != loggedDiagnostic)
            {
                loggedDiagnostic = diagnostic;
                if (diagnostic is not null) Log?.Invoke("error", diagnostic);
            }
            source.Enabled = resetBluetooth.Enabled = !busy && !shuttingDown;
            cancel.Enabled = !shuttingDown && (busy || snapshot?.Devices.Any(device => device.ConnectionState == ToyConnectionState.Connecting) == true);
            stopAll.Enabled = controller is not null && !shuttingDown;
            RefreshFunctions(snapshot?.Devices ?? Array.Empty<LocalToyDescriptor>());
        }
        finally { refreshing = false; }
        Changed?.Invoke();
    }
    private void RefreshFunctions(IReadOnlyList<LocalToyDescriptor> devices)
    {
        var entries = devices.SelectMany(device => device.Capabilities.Count == 0
            ? new[] { (Device: device, Capability: (ToyCapability?)null) }
            : device.Capabilities.Select(capability => (Device: device, Capability: (ToyCapability?)capability))).ToArray();
        var keys = entries.Select(entry => (entry.Device.DeviceKey, entry.Capability?.ChannelId ?? "")).ToHashSet();
        var layoutChanged = false;
        foreach (var (key, row) in functionRows.ToArray())
            if (!keys.Contains(key)) { functionRows.Remove(key); Controls.Remove(row); row.Dispose(); layoutChanged = true; }
        if (controller is not null)
            foreach (var entry in entries)
            {
                var key = (entry.Device.DeviceKey, entry.Capability?.ChannelId ?? "");
                if (!functionRows.TryGetValue(key, out var row))
                {
                    row = new(controller, entry.Device, entry.Capability);
                    row.ReadBatteryRequested += deviceKey => StartOperation(() => adapter!.ReadBatteryAsync(deviceKey, OperationToken));
                    row.ForgetRequested += deviceKey => StartOperation(() => adapter!.ForgetAsync(deviceKey, OperationToken));
                    row.StopToyRequested += deviceKey => ControlAction(() => controller?.Stop(deviceKey));
                    row.ExpandedChanged += ArrangeCards;
                    functionRows.Add(key, row); Controls.Add(row); layoutChanged = true;
                }
                row.UpdateDevice(entry.Device, busy || shuttingDown);
            }
        empty.Visible = functionRows.Count == 0;
        if (layoutChanged) ArrangeCards();
    }
    public void ArrangeCards()
    {
        if (arranging) return;
        arranging = true;
        try
        {
            var available = Math.Max(3, ClientSize.Width - 24);
            var widths = new[] { (int)(available * .33333), (int)(available * .33333), 0 };
            widths[2] = available - widths[0] - widths[1];
            var cards = functionRows.Values.ToArray();
            var top = 0;
            for (var index = 0; index < cards.Length; index += 3)
            {
                var x = 0; var rowHeight = 0;
                for (var column = 0; column < 3 && index + column < cards.Length; column++)
                {
                    var card = cards[index + column];
                    card.SetBounds(x, top, widths[column], card.Expanded ? ToyFunctionRow.OptionsHeight : ToyFunctionRow.CompactHeight);
                    rowHeight = Math.Max(rowHeight, card.Height); x += widths[column] + 12;
                }
                top += rowHeight + 12;
            }
            GridHeight = Math.Max(38, top);
        }
        finally { arranging = false; }
        CardsLayoutChanged?.Invoke();
    }

    public Task ShutdownAsync() => shutdownTask ??= ShutdownCoreAsync();
    private async Task ShutdownCoreAsync()
    {
        shuttingDown = true; lifetime.Cancel();
        var previous = adapter;
        try
        {
            if (controller is not null) controller.Changed -= AdapterChanged;
            if (previous is not null) previous.Changed -= AdapterChanged;
            // Disarm synchronously before releasing a worker. Its quit path sends
            // zero and bounds native cleanup, interrupting ordinary call waits.
            var controllerCleanup = controller?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            var transportCleanup = previous is ILocalToyExit exit ? exit.CloseTransportForExitAsync() : Task.CompletedTask;
            var adapterCleanup = previous?.DisposeAsync().AsTask() ?? Task.CompletedTask;
            await Task.WhenAll(controllerCleanup, adapterCleanup, transportCleanup);
        }
        finally { await activeOperation; }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _ = ShutdownAsync();
            // Settings owns these visual controls, but standalone card-grid users also need cleanup.
            foreach (var control in new Control[] { source, updateRate, resetBluetooth, stopAll, cancel, status })
                if (control.Parent is null) control.Dispose();
        }
        base.Dispose(disposing);
    }
}
