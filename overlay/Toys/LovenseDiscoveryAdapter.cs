using System.Globalization;
using System.Text.RegularExpressions;

namespace ShockWave.Overlay.Toys;

internal sealed class LovenseDiscoveryAdapter : ILocalToyAdapter, ILocalToyOutput, ILocalToyExit
{
    private sealed record Entry(string PrivateId, LocalToyDescriptor Descriptor);
    private readonly object sync = new();
    private readonly object outputSync = new();
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Func<ILovenseBleApi> createApi;
    private readonly Func<LocalToyKeys> createKeys;
    private readonly bool simulated;
    private readonly TimeSpan scanDuration, responseTimeout;
    private readonly double commandsPerSecond;
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> privateIds = new(StringComparer.Ordinal);
    private readonly HashSet<string> seen = new(StringComparer.Ordinal);
    private readonly HashSet<string> lostConnections = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Device, string Channel), int> activeOutputs = [];
    private LocalToyKeys? keys;
    private ILovenseBleApi? api;
    private ToyAdapterAvailability availability = ToyAdapterAvailability.Ready;
    private string message;
    private string? lastError;
    private bool nativeRecoveryRequired;
    private bool scanning, closing, outputSuspended;
    private int scanGeneration;
    private CancellationTokenSource? scanLifetime;
    private TaskCompletionSource<int>? bluetoothResult;
    private TaskCompletionSource<bool>? scanStarted, scanStopped;
    private (string Id, bool Connecting, TaskCompletionSource<bool> Result)? connectionResult;
    private (string Id, TaskCompletionSource<int?> Result)? batteryResult;
    private Task? disposeTask;
    public event Action? Changed;
    public event Action<string>? OutputFailed;

    public void SendLevel(string deviceKey, string channelId, int nativeValue)
        => TrySendLevel(deviceKey, channelId, nativeValue, () => true);

    public bool TrySendLevel(string deviceKey, string channelId, int nativeValue, Func<bool> isCurrent)
    {
        lock (outputSync)
        {
            if (!isCurrent()) return false;
            ILovenseBleApi transport; string id; int command;
            int previous;
            lock (sync)
            {
                var device = Device(deviceKey);
                var capability = device.Descriptor.Capabilities.FirstOrDefault(c => c.ChannelId == channelId)
                    ?? throw new InvalidOperationException("This toy does not have that function.");
                if (api is null || closing || device.Descriptor.ConnectionState != ToyConnectionState.Connected)
                    throw new InvalidOperationException("Connect the toy before using its controls.");
                if (scanning) throw new InvalidOperationException("Stop scanning before using toy controls.");
                if (nativeValue > 0 && outputSuspended) throw new InvalidOperationException("Toy output is paused while scanning initializes.");
                if (capability.NativeMaximum is not int maximum || nativeValue < 0 || nativeValue > maximum)
                    throw new InvalidOperationException("This function's command range is unavailable.");
                previous = activeOutputs.GetValueOrDefault((deviceKey, channelId));
                activeOutputs[(deviceKey, channelId)] = nativeValue;
                transport = api; id = device.PrivateId;
                // Zero on a setting stops motion, never moves to an endpoint or changes depth.
                command = LovenseModelCatalog.Command(nativeValue == 0 && capability.IsSetting ? "t" : channelId);
            }
            // Native BLE writes may wait for a callback on another thread. Inventory stays readable.
            var submitted = transport.TrySendLevel(id, command, nativeValue, isCurrent);
            if (!submitted) lock (sync) activeOutputs[(deviceKey, channelId)] = previous;
            return submitted;
        }
    }

    private void StopNativeOutputs(string? deviceKey = null)
    {
        lock (outputSync)
        {
            List<(ILovenseBleApi Api, string Id, int Command)> stops = [];
            lock (sync)
                foreach (var (key, value) in activeOutputs.ToArray())
                {
                    if (value == 0 || deviceKey is not null && key.Device != deviceKey) continue;
                    activeOutputs[key] = 0;
                    if (api is not null && entries.TryGetValue(key.Device, out var entry) && entry.Descriptor.ConnectionState == ToyConnectionState.Connected)
                        stops.Add((api, entry.PrivateId, LovenseModelCatalog.Command(key.Channel is "d" or "pos" ? "t" : key.Channel)));
                }
            var failed = false;
            foreach (var stop in stops)
            {
                try { stop.Api.SendLevel(stop.Id, stop.Command, 0); }
                catch (Exception) { failed = true; }
            }
            if (failed) throw new InvalidOperationException("A toy stop request failed. Turn off the toy if it is still active.");
        }
    }

    public LovenseDiscoveryAdapter(Func<ILovenseBleApi> createApi, Func<LocalToyKeys> createKeys,
        bool simulated = false, TimeSpan? scanDuration = null, TimeSpan? responseTimeout = null, double commandsPerSecond = 5)
    {
        if (!double.IsFinite(commandsPerSecond) || commandsPerSecond is <= 0 or > 10) throw new ArgumentOutOfRangeException(nameof(commandsPerSecond));
        this.createApi = createApi; this.createKeys = createKeys; this.simulated = simulated;
        this.commandsPerSecond = commandsPerSecond;
        this.scanDuration = scanDuration ?? TimeSpan.FromSeconds(simulated ? 2 : 15);
        this.responseTimeout = responseTimeout ?? TimeSpan.FromSeconds(10);
        message = simulated ? "Simulated toys. No hardware is used." : "Ready to scan. Turn on Bluetooth and your toys.";
    }

    public ToyAdapterSnapshot Snapshot
    {
        get
        {
            lock (sync) return new(availability, scanning, message,
                Array.AsReadOnly(entries.Values.Select(e => e.Descriptor).OrderBy(d => d.DisplayName).ThenBy(d => d.DeviceKey).ToArray()))
                { LastError = lastError };
        }
    }

    private void Publish()
    {
        if (Changed is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
            try { handler(); } catch (Exception) { /* Callback threads must remain alive. */ }
    }

    private void Status(ToyAdapterAvailability state, string text)
    {
        lock (sync)
        {
            if (closing && state != ToyAdapterAvailability.Disposed) return;
            availability = state; message = text;
            if (state is ToyAdapterAvailability.Error or ToyAdapterAvailability.LibraryMissing) lastError = text;
        }
        Publish();
    }

    private Task Run(Func<CancellationToken, Task> action, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await operations.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            await action(linked.Token).ConfigureAwait(false);
        }
        finally { operations.Release(); }
    });

    private async Task<bool> EnsureBluetoothAsync(CancellationToken token)
    {
        try
        {
            if (api is null)
            {
                api = createApi();
                api.BluetoothStatus += OnBluetooth;
                api.ToyFound += OnToy;
                api.ConnectionChanged += OnConnection;
                api.Battery += OnBattery;
                api.Notification += OnNotification;
            }
            keys ??= createKeys();
            var result = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (sync) bluetoothResult = result;
            api.CheckBluetooth();
            var status = await result.Task.WaitAsync(responseTimeout, token).ConfigureAwait(false);
            return status == 0;
        }
        catch (LovenseLibraryException error) { Status(ToyAdapterAvailability.LibraryMissing, error.Message); return false; }
        catch (TimeoutException) { Status(ToyAdapterAvailability.Error, "Bluetooth check timed out. Try scanning again."); return false; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { Status(ToyAdapterAvailability.Error, "Local discovery could not initialize. Check the installation and local settings access."); return false; }
        catch (InvalidOperationException)
        { Status(ToyAdapterAvailability.Error, "Bluetooth worker needs a reset. Select Reset Bluetooth and scan again."); return false; }
        finally { lock (sync) bluetoothResult = null; }
    }

    private void OnBluetooth(int status)
    {
        lock (sync)
        {
            if (closing) return;
            bluetoothResult?.TrySetResult(status);
            if (status != 0)
            {
                foreach (var (key, entry) in entries.ToArray())
                    entries[key] = entry with { Descriptor = entry.Descriptor with { ConnectionState = ToyConnectionState.Disconnected, BatteryPercent = null } };
                connectionResult?.Result.TrySetResult(false);
                batteryResult?.Result.TrySetResult(null);
                scanStarted?.TrySetResult(false);
                scanLifetime?.Cancel();
            }
        }
        Status(status == 0 ? ToyAdapterAvailability.Ready : status == 1 ? ToyAdapterAvailability.BluetoothOff : ToyAdapterAvailability.BluetoothUnavailable,
            status == 0 ? (simulated ? "Simulated Bluetooth ready." : "Bluetooth ready.") :
            status == 1 ? "Bluetooth is off. Turn it on, then scan again." : "Bluetooth is unavailable. Connect a compatible Bluetooth adapter.");
    }

    public Task StartScanAsync(CancellationToken cancellationToken = default) => Run(async token =>
    {
        lock (sync) if (scanning) return;
        lock (sync) outputSuspended = true;
        StopNativeOutputs();
        bool reset;
        lock (sync) reset = nativeRecoveryRequired && !entries.Values.Any(entry => entry.Descriptor.ConnectionState == ToyConnectionState.Connected);
        if (reset)
        {
            ResetApi();
            lock (sync) nativeRecoveryRequired = false;
            Publish();
        }
        if (!await EnsureBluetoothAsync(token).ConfigureAwait(false)) return;
        ReleaseLostConnections();
        var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int generation;
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            scanning = true; seen.Clear(); generation = ++scanGeneration; scanStarted = started;
            scanLifetime?.Dispose();
            scanLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
            message = simulated ? "Scanning simulated toys…" : "Scanning nearby toys…";
        }
        Publish();
        try
        {
            api!.StartScan();
            if (!await started.Task.WaitAsync(responseTimeout, token).ConfigureAwait(false))
            { await StopScanCoreAsync(false, CancellationToken.None).ConfigureAwait(false); return; }
            CancellationToken scanToken;
            lock (sync) scanToken = scanLifetime!.Token;
            _ = FinishAfterDeadlineAsync(generation, scanToken);
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException)
        {
            await StopScanCoreAsync(false, CancellationToken.None).ConfigureAwait(false);
            if (error is OperationCanceledException) throw;
            Status(ToyAdapterAvailability.Error, "Scan start timed out. Try scanning again.");
        }
        finally { lock (sync) scanStarted = null; }
    }, cancellationToken);

    private async Task FinishAfterDeadlineAsync(int generation, CancellationToken token)
    {
        var completed = true;
        try { await Task.Delay(scanDuration, token).ConfigureAwait(false); }
        catch (OperationCanceledException) { completed = false; }
        try
        {
            await Run(async _ =>
            {
                lock (sync) if (!scanning || scanGeneration != generation) return;
                await StopScanCoreAsync(completed, CancellationToken.None).ConfigureAwait(false);
            }, lifetime.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            FinishScan(false);
            Status(ToyAdapterAvailability.Error, "Discovery stopped unexpectedly. Close and reopen the app before scanning again.");
        }
    }

    public Task StopScanAsync(CancellationToken cancellationToken = default) => Run(
        _ => StopScanCoreAsync(false, CancellationToken.None), cancellationToken);

    private async Task StopScanCoreAsync(bool reconcile, CancellationToken token)
    {
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync)
        {
            if (!scanning) return;
            if (closing) { api?.StopScan(); scanning = false; return; }
            scanStopped = stopped;
        }
        try
        {
            api!.StopScan();
            await stopped.Task.WaitAsync(responseTimeout, token).ConfigureAwait(false);
            FinishScan(reconcile);
        }
        catch (TimeoutException)
        {
            // Reset Bluetooth resources before allowing another scan/connection; never claim an unconfirmed stop.
            ResetApi(); FinishScan(false);
            Status(ToyAdapterAvailability.Error, "Scan stop was not confirmed. Bluetooth resources were reset; scan again.");
        }
        finally { lock (sync) scanStopped = null; }
    }

    private void FinishScan(bool reconcile)
    {
        lock (sync)
        {
            scanning = false; outputSuspended = false; scanLifetime?.Cancel();
            if (reconcile)
                foreach (var (key, entry) in entries.ToArray())
                    if (!seen.Contains(key) && entry.Descriptor.ConnectionState is not ToyConnectionState.Connected)
                        entries[key] = entry with { Descriptor = entry.Descriptor with { ConnectionState = ToyConnectionState.Unavailable, BatteryPercent = null } };
            if (availability == ToyAdapterAvailability.Ready)
                message = reconcile ? "Scan complete. Choose a toy to connect." : "Scan stopped. Discovery does not activate toys.";
        }
        Publish();
    }

    private void OnToy(string id, string address, string symbol)
    {
        lock (sync)
        {
            if (closing || !scanning || string.IsNullOrWhiteSpace(id) || id.Length > 4096 || address.Length > 128 || symbol.Length > 64 || entries.Count >= 256 && !privateIds.ContainsKey(id)) return;
            var normalizedAddress = Regex.Replace(address, "[^0-9A-Fa-f]", "");
            var validAddress = normalizedAddress.Length == 12 && normalizedAddress != "000000000000" && !normalizedAddress.Equals("FFFFFFFFFFFF", StringComparison.OrdinalIgnoreCase);
            var identity = validAddress ? "address:" + normalizedAddress : "id:" + id;
            var key = privateIds.TryGetValue(id, out var existingKey) ? existingKey : keys!.ForDevice(simulated ? "simulated-lovense" : "lovense-ble", identity);
            var (model, capabilities, known) = LovenseModelCatalog.Identify(symbol);
            entries.TryGetValue(key, out var previous);
            var state = previous?.Descriptor.ConnectionState ?? ToyConnectionState.Discovered;
            if (state == ToyConnectionState.Unavailable) state = ToyConnectionState.Discovered;
            var descriptor = new LocalToyDescriptor(key, simulated ? "simulated-lovense" : "lovense-ble",
                (simulated ? "Simulated " : "") + model, model, state, capabilities, known,
                previous?.Descriptor.BatteryPercent, DateTimeOffset.UtcNow, commandsPerSecond, simulated);
            if (previous is not null && previous.PrivateId != id) privateIds.Remove(previous.PrivateId);
            entries[key] = new Entry(id, descriptor); privateIds[id] = key; seen.Add(key);
        }
        Publish();
    }

    private void OnConnection(string id, bool connected)
    {
        lock (sync)
        {
            if (closing || !privateIds.TryGetValue(id, out var key)) return;
            var entry = entries[key];
            if (!connected && entry.Descriptor.ConnectionState is ToyConnectionState.Connected or ToyConnectionState.Connecting)
            {
                lostConnections.Add(id);
                nativeRecoveryRequired |= api is LovenseWorkerApi;
            }
            if (connected) lostConnections.Remove(id);
            entries[key] = entry with { Descriptor = entry.Descriptor with
            { ConnectionState = connected ? ToyConnectionState.Connected : ToyConnectionState.Disconnected,
                BatteryPercent = connected ? entry.Descriptor.BatteryPercent : null } };
            if (connectionResult is { } pending && pending.Id == id)
                pending.Result.TrySetResult(connected == pending.Connecting);
            if (!connected && batteryResult is { } batteryPending && batteryPending.Id == id) batteryPending.Result.TrySetResult(null);
        }
        Publish();
    }

    private void ReleaseLostConnections()
    {
        string[] ids;
        lock (sync)
        {
            ids = lostConnections.Where(id => !privateIds.TryGetValue(id, out var key) ||
                entries[key].Descriptor.ConnectionState != ToyConnectionState.Connected).ToArray();
            foreach (var id in ids) lostConnections.Remove(id);
        }
        // A false status callback does not release native GATT/device resources.
        // Run cleanup on the serialized worker before rediscovery, never on the callback stack.
        foreach (var id in ids)
            try { api?.Disconnect(id); }
            catch (Exception)
            {
                lock (sync) nativeRecoveryRequired = true;
                Status(ToyAdapterAvailability.Error, "Bluetooth session cleanup failed. Select Reset Bluetooth, then scan again.");
                throw new InvalidOperationException("Bluetooth session cleanup failed. Select Reset Bluetooth, then scan again.");
            }
    }

    private void OnBattery(string id, string value)
    {
        int? percent = int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed is >= 0 and <= 100 ? parsed : null;
        lock (sync)
        {
            if (closing || !privateIds.TryGetValue(id, out var key) || entries[key].Descriptor.ConnectionState != ToyConnectionState.Connected) return;
            var entry = entries[key];
            entries[key] = entry with { Descriptor = entry.Descriptor with { BatteryPercent = percent } };
            if (batteryResult is { } pending && pending.Id == id) pending.Result.TrySetResult(percent);
        }
        Publish();
    }

    private void OnNotification(int code, string id)
    {
        if (code == -1)
        {
            OnBluetooth(2);
            lock (sync) nativeRecoveryRequired = true;
            Status(ToyAdapterAvailability.Error, "Bluetooth worker stopped. Connection will refresh automatically when idle; select Reset Bluetooth if recovery fails.");
            return;
        }
        string? failedKey = null;
        lock (sync)
        {
            if (closing) return;
            if (code == 0) { scanStarted?.TrySetResult(true); return; }
            if (code == 1 && scanStopped is not null) { scanStopped.TrySetResult(true); return; }
            if (code is 3 or 4 or 6 or 8 or 9 && connectionResult is { } pending && (pending.Id == id || id.Length == 0)) pending.Result.TrySetResult(false);
            if (code == 3 && batteryResult is { } batteryPending && (batteryPending.Id == id || id.Length == 0)) batteryPending.Result.TrySetResult(null);
            if (code == 3) scanStarted?.TrySetResult(false);
            if (code == 10) scanStarted?.TrySetResult(false);
            if (code is 3 or 4 or 9 && privateIds.TryGetValue(id, out var key)) failedKey = key;
        }
        if (failedKey is not null)
            _ = Task.Run(() =>
            {
                // The DLL can notify synchronously inside SendLevel while the native lock is held.
                // Never take the controller lock from that callback stack.
                try { OutputFailed?.Invoke(failedKey); } catch (Exception) { }
            });
        // Some native failures report TOY_NO_CONNECT without a separate connection callback.
        // Treat that as lost connection so Auto-connect can rediscover; never retry the output.
        if (code == 9) OnConnection(id, false);
        if (code == 1) FinishScan(false);
        if (code == 10)
        {
            OnBluetooth(2);
            Status(ToyAdapterAvailability.BluetoothUnavailable, "Bluetooth became unavailable. Turn it on and scan again.");
            // Let the serialized deadline/stop path release the scan resources.
            lock (sync) scanLifetime?.Cancel();
        }
    }

    private Entry Device(string key)
    {
        lock (sync) return entries.TryGetValue(key, out var entry) ? entry : throw new InvalidOperationException("Select a discovered toy first.");
    }

    private void SetConnection(string key, ToyConnectionState state)
    {
        lock (sync)
        {
            var entry = entries[key];
            entries[key] = entry with { Descriptor = entry.Descriptor with { ConnectionState = state, BatteryPercent = null } };
        }
        Publish();
    }

    public Task ConnectAsync(string deviceKey, CancellationToken cancellationToken = default) => Run(async token =>
    {
        await StopScanCoreAsync(false, CancellationToken.None).ConfigureAwait(false);
        if (!await EnsureBluetoothAsync(token).ConfigureAwait(false)) return;
        var device = Device(deviceKey);
        if (device.Descriptor.ConnectionState == ToyConnectionState.Connected) return;
        if (device.Descriptor.ConnectionState == ToyConnectionState.Unavailable) throw new InvalidOperationException("This toy was not found. Turn it on and scan again.");
        await ChangeConnectionAsync(deviceKey, device.PrivateId, true, token).ConfigureAwait(false);
    }, cancellationToken);

    public Task DisconnectAsync(string deviceKey, CancellationToken cancellationToken = default) => Run(async token =>
    {
        await StopScanCoreAsync(false, CancellationToken.None).ConfigureAwait(false);
        StopNativeOutputs(deviceKey);
        var device = Device(deviceKey);
        if (api is null || device.Descriptor.ConnectionState is not (ToyConnectionState.Connected or ToyConnectionState.Connecting)) return;
        await ChangeConnectionAsync(deviceKey, device.PrivateId, false, token).ConfigureAwait(false);
    }, cancellationToken);

    private async Task ChangeConnectionAsync(string key, string id, bool connecting, CancellationToken token)
    {
        var result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync) connectionResult = (id, connecting, result);
        SetConnection(key, connecting ? ToyConnectionState.Connecting : ToyConnectionState.Disconnecting);
        try
        {
            if (connecting) api!.Connect(id); else api!.Disconnect(id);
            if (!await result.Task.WaitAsync(responseTimeout, token).ConfigureAwait(false))
                throw new InvalidOperationException(connecting ? "Toy connection failed. Turn on the toy and try again." : "Toy disconnect failed. Try again.");
            token.ThrowIfCancellationRequested();
            lock (sync) { lostConnections.Remove(id); lastError = null; nativeRecoveryRequired |= !connecting && api is LovenseWorkerApi; }
            Status(ToyAdapterAvailability.Ready, connecting ? "Toy connected." : "Toy disconnected.");
        }
        catch (Exception error) when (error is TimeoutException or OperationCanceledException or InvalidOperationException)
        {
            if (connecting)
            {
                try { api!.Disconnect(id); } // Release a failed/pending native connection, never activate output.
                finally
                {
                    lock (sync) { lostConnections.Remove(id); nativeRecoveryRequired |= error is not OperationCanceledException; }
                    SetConnection(key, ToyConnectionState.Disconnected);
                }
            }
            else SetConnection(key, ToyConnectionState.Connected); // A missing acknowledgement is not a confirmed disconnect.
            if (error is OperationCanceledException) throw;
            var detail = connecting ? "Toy connection was not confirmed. Turn it on and keep it nearby; select Reset Bluetooth if automatic recovery fails." : "Toy disconnect was not confirmed. Try again or select Reset Bluetooth.";
            Status(ToyAdapterAvailability.Error, detail);
            throw new InvalidOperationException(detail);
        }
        finally { lock (sync) connectionResult = null; }
    }

    public Task ReadBatteryAsync(string deviceKey, CancellationToken cancellationToken = default) => Run(async token =>
    {
        await StopScanCoreAsync(false, CancellationToken.None).ConfigureAwait(false);
        var device = Device(deviceKey);
        if (api is null || device.Descriptor.ConnectionState != ToyConnectionState.Connected)
            throw new InvalidOperationException("Connect this toy before reading its battery.");
        var result = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (sync) batteryResult = (device.PrivateId, result);
        try
        {
            api.ReadBattery(device.PrivateId);
            var percent = await result.Task.WaitAsync(responseTimeout, token).ConfigureAwait(false);
            Status(ToyAdapterAvailability.Ready, percent.HasValue ? "Battery updated." : "This toy's battery level is unknown.");
        }
        catch (TimeoutException) { OnBattery(device.PrivateId, "unknown"); Status(ToyAdapterAvailability.Ready, "Battery read timed out. Charge level is unknown."); }
        finally { lock (sync) batteryResult = null; }
    }, cancellationToken);

    private void ResetApi()
    {
        if (api is null) return;
        try { StopNativeOutputs(); } catch (InvalidOperationException) { /* Cleanup still releases the transport. */ }
        api.BluetoothStatus -= OnBluetooth; api.ToyFound -= OnToy; api.ConnectionChanged -= OnConnection;
        api.Battery -= OnBattery; api.Notification -= OnNotification;
        api.Dispose(); api = null;
        lock (sync)
        {
            privateIds.Clear(); lostConnections.Clear(); activeOutputs.Clear();
            foreach (var (key, entry) in entries.ToArray())
                entries[key] = entry with { PrivateId = "", Descriptor = entry.Descriptor with { ConnectionState = ToyConnectionState.Unavailable, BatteryPercent = null } };
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (sync)
        {
            if (disposeTask is not null) return new ValueTask(disposeTask);
            closing = true; lifetime.Cancel(); scanLifetime?.Cancel();
            disposeTask = Task.Run(async () =>
            {
                await operations.WaitAsync().ConfigureAwait(false);
                try
                {
                    try { if (scanning) api?.StopScan(); }
                    finally { ResetApi(); FinishScan(false); Status(ToyAdapterAvailability.Disposed, "Local toy adapter closed."); }
                    scanLifetime?.Dispose();
                }
                finally { operations.Release(); }
            });
            return new ValueTask(disposeTask);
        }
    }

    public Task CloseTransportForExitAsync() => api is LovenseWorkerApi worker
        ? Task.Run(worker.Dispose) : Task.CompletedTask;
}
