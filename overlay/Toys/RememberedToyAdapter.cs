using System.Diagnostics;

namespace ShockWave.Overlay.Toys;

// Connection automation never arms or restores actuator output.
internal sealed class RememberedToyAdapter : IRememberedToyAdapter, ILocalToyOutput, ILocalToyExit
{
    private readonly ILocalToyAdapter inner;
    private readonly IRememberedToyStore store;
    private readonly object sync = new();
    private readonly object storeWrites = new();
    private readonly SemaphoreSlim operations = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, RememberedToy> remembered = new(StringComparer.Ordinal);
    private HashSet<string> startupTargets = new(StringComparer.Ordinal);
    private readonly HashSet<string> paused = new(StringComparer.Ordinal);
    private readonly HashSet<string> forgotten = new(StringComparer.Ordinal);
    private readonly HashSet<string> freshCandidates = new(StringComparer.Ordinal);
    private HashSet<string> connectedKeys = new(StringComparer.Ordinal);
    private sealed record BatterySchedule(long Connection, long LastRead, bool HasRead);
    private readonly Dictionary<string, BatterySchedule> batterySchedules = new(StringComparer.Ordinal);
    private readonly TimeProvider batteryTime;
    private static readonly TimeSpan BatteryRefreshInterval = TimeSpan.FromMinutes(5);
    private long batteryConnection;
    private readonly TimeSpan retryInterval, automaticRetryInterval, pollInterval;
    private static readonly TimeSpan AdvertisementQuietTime = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaximumCollectionTime = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan ConnectedScanWindow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan MissingToyScanWindow = TimeSpan.FromSeconds(15);
    private TimeSpan initialScanRemaining = TimeSpan.FromSeconds(15);
    private bool initialized, storageAvailable = true, connectAll, initialDiscovery, wasScanning, extendRecoveryScan;
    private DateTimeOffset scanBegan;
    private Func<bool> automaticScanAllowed = () => true;
    private string? storageWarning;
    private DateTimeOffset nextAttempt;
    private int manualGeneration;
    private CancellationTokenSource? automaticAttempt;
    private Task autoLoop = Task.CompletedTask;
    private Task batteryLoop = Task.CompletedTask;
    private Task? disposeTask;
    public event Action? Changed;
    public event Action<string>? OutputFailed;
    public void SendLevel(string deviceKey, string channelId, int nativeValue) =>
        (inner as ILocalToyOutput ?? throw new InvalidOperationException("Toy output is unavailable."))
            .SendLevel(deviceKey, channelId, nativeValue);
    public bool TrySendLevel(string deviceKey, string channelId, int nativeValue, Func<bool> isCurrent) =>
        (inner as ILocalToyOutput ?? throw new InvalidOperationException("Toy output is unavailable."))
            .TrySendLevel(deviceKey, channelId, nativeValue, isCurrent);
    private void InnerOutputFailed(string key) => OutputFailed?.Invoke(key);

    public RememberedToyAdapter(ILocalToyAdapter inner, IRememberedToyStore store,
        TimeSpan? retryInterval = null, TimeSpan? pollInterval = null, TimeProvider? batteryTime = null)
    {
        this.inner = inner; this.store = store;
        this.retryInterval = retryInterval ?? TimeSpan.FromSeconds(30);
        automaticRetryInterval = retryInterval ?? TimeSpan.FromSeconds(3);
        this.pollInterval = pollInterval ?? TimeSpan.FromMilliseconds(100);
        this.batteryTime = batteryTime ?? TimeProvider.System;
        if (inner is ILocalToyConnectionFilter filter)
            filter.AllowConnection = key => { lock (sync) return !paused.Contains(key); };
        inner.Changed += InnerChanged;
        if (inner is ILocalToyOutput output) output.OutputFailed += InnerOutputFailed;
    }

    public ToyAdapterSnapshot Snapshot
    {
        get
        {
            var snapshot = inner.Snapshot;
            lock (sync)
            {
                // Engines retain disconnected entries for the session. Forget
                // must hide that inventory as well as delete the saved entry.
                var devices = snapshot.Devices.Where(device => !forgotten.Contains(device.DeviceKey))
                    .ToDictionary(device => device.DeviceKey, StringComparer.Ordinal);
                foreach (var toy in remembered.Values)
                {
                    if (!devices.TryGetValue(toy.DeviceKey, out var descriptor))
                    {
                        var (model, capabilities, known) = LovenseModelCatalog.IdentifyModel(toy.Model);
                        descriptor = new(toy.DeviceKey, "lovense-ble", model, model, ToyConnectionState.Unavailable,
                            capabilities, known, null, toy.LastSeenUtc, 1, false);
                    }
                    devices[toy.DeviceKey] = descriptor with
                    { IsRemembered = true, AutoConnect = connectAll || toy.AutoConnect, AutoConnectPaused = paused.Contains(toy.DeviceKey) };
                }
                if (connectAll)
                    foreach (var key in devices.Keys.ToArray()) devices[key] = devices[key] with
                        { AutoConnect = true, AutoConnectPaused = paused.Contains(key) };
                return snapshot with
                {
                    Message = storageWarning is null ? snapshot.Message : snapshot.Message + " " + storageWarning,
                    Devices = Array.AsReadOnly(devices.Values.OrderBy(device => device.DisplayName).ThenBy(device => device.DeviceKey).ToArray()),
                };
            }
        }
    }

    private void InnerChanged()
    {
        var snapshot = inner.Snapshot;
        var connected = snapshot.Devices.Where(device => device.ConnectionState == ToyConnectionState.Connected)
            .Select(device => device.DeviceKey).ToHashSet(StringComparer.Ordinal);
        string[] newlyConnected;
        lock (sync)
        {
            newlyConnected = connected.Except(connectedKeys).ToArray();
            foreach (var key in connectedKeys.Except(connected)) batterySchedules.Remove(key);
            foreach (var key in connected.Except(connectedKeys))
                batterySchedules[key] = new(++batteryConnection, 0, false);
            foreach (var key in connectedKeys.Except(connected)) freshCandidates.Remove(key);
            if (connectedKeys.Except(connected).Any(key => !paused.Contains(key) &&
                (connectAll || remembered.TryGetValue(key, out var toy) && toy.AutoConnect)))
                nextAttempt = DateTimeOffset.MinValue;
            if (snapshot.IsScanning && !wasScanning) { scanBegan = DateTimeOffset.UtcNow; freshCandidates.Clear(); }
            if (snapshot.IsScanning)
                foreach (var device in snapshot.Devices.Where(device => device.LastSeenUtc >= scanBegan &&
                    device.ConnectionState is ToyConnectionState.Discovered or ToyConnectionState.Disconnected))
                    freshCandidates.Add(device.DeviceKey);
            wasScanning = snapshot.IsScanning;
            connectedKeys = connected;
        }
        // Buttplug establishes connections as discoveries arrive. Save those
        // confirmed identities even though no explicit Connect call was needed.
        if (snapshot.SupportsOutputWhileScanning && initialized)
            foreach (var key in newlyConnected)
                try { RememberConnected(key); } catch (InvalidOperationException) { /* Existing storage warning; connection remains usable. */ }
        Publish();
    }
    private void Publish() => Changed?.Invoke();
    private Task Run(Func<CancellationToken, Task> action, CancellationToken cancellationToken) => Task.Run(async () =>
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime.Token);
        await operations.WaitAsync(linked.Token).ConfigureAwait(false);
        try { linked.Token.ThrowIfCancellationRequested(); await action(linked.Token).ConfigureAwait(false); }
        finally { operations.Release(); }
    });

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        lock (sync) if (initialized) return Task.CompletedTask;
        return Run(InitializeCoreAsync, cancellationToken);
    }

    public Task EnableAutomaticConnectionsAsync(CancellationToken cancellationToken = default, Func<bool>? canScan = null)
    {
        lock (sync)
        {
            if (canScan is not null) automaticScanAllowed = canScan;
            if (!connectAll) { connectAll = true; initialDiscovery = true; paused.Clear(); forgotten.Clear(); nextAttempt = DateTimeOffset.MinValue; }
        }
        return InitializeAsync(cancellationToken);
    }

    private Task InitializeCoreAsync(CancellationToken token)
    {
        if (initialized) return Task.CompletedTask;
        try
        {
            var saved = store.Load().ToDictionary(toy => toy.DeviceKey, StringComparer.Ordinal);
            lock (sync)
            {
                foreach (var (key, toy) in saved) remembered.Add(key, toy);
                startupTargets = saved.Keys.ToHashSet(StringComparer.Ordinal);
            }
        }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or
            System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or ArgumentException)
        {
            lock (sync)
            {
                storageAvailable = false;
                storageWarning = "Remembered toys could not be loaded. The saved file has been kept; new connections cannot be remembered.";
            }
        }
        InnerChanged();
        autoLoop = Task.Run(AutoConnectLoopAsync);
        batteryLoop = Task.Run(BatteryLoopAsync);
        lock (sync) initialized = true;
        Publish();
        return Task.CompletedTask;
    }

    private void CancelAutomaticAttempt()
    {
        CancellationTokenSource? attempt;
        lock (sync)
        {
            manualGeneration++;
            nextAttempt = DateTimeOffset.UtcNow + retryInterval;
            attempt = automaticAttempt;
        }
        // Cancellation can synchronously publish connection changes. Never hold inventory state here.
        try { attempt?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private async Task ManualAsync(Func<CancellationToken, Task> action, CancellationToken token)
    {
        CancelAutomaticAttempt();
        await InitializeAsync(token).ConfigureAwait(false);
        CancelAutomaticAttempt();
        try { await Run(action, token).ConfigureAwait(false); }
        finally { lock (sync) if (connectAll) nextAttempt = DateTimeOffset.MinValue; }
    }

    public Task StartScanAsync(CancellationToken cancellationToken = default) => ManualAsync(async token =>
    {
        await inner.StartScanAsync(token).ConfigureAwait(false);
        lock (sync) if (connectAll) initialDiscovery = false;
    }, cancellationToken);
    public Task StopScanAsync(CancellationToken cancellationToken = default) => ManualAsync(
        token => inner.StopScanAsync(token), cancellationToken);
    public Task ReadBatteryAsync(string deviceKey, CancellationToken cancellationToken = default) => ManualAsync(
        async token =>
        {
            // An explicit refresh also starts a new five-minute interval.
            lock (sync)
                if (batterySchedules.TryGetValue(deviceKey, out var schedule))
                    batterySchedules[deviceKey] = schedule with { LastRead = batteryTime.GetTimestamp(), HasRead = true };
            await inner.ReadBatteryAsync(deviceKey, token).ConfigureAwait(false);
        }, cancellationToken);

    private bool BatteryIsDue(BatterySchedule schedule) => !schedule.HasRead ||
        batteryTime.GetElapsedTime(schedule.LastRead, batteryTime.GetTimestamp()) >= BatteryRefreshInterval;

    private async Task BatteryLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                bool due;
                lock (sync) due = batterySchedules.Values.Any(BatteryIsDue);
                if (due)
                {
                    // The bundled engine serializes each management request
                    // itself. Do not queue a sensor read behind the coordinator's
                    // entire missing-toy search; output and scans stay available.
                    if (inner.Snapshot.SupportsOutputWhileScanning)
                        await PollBatteriesAsync(lifetime.Token).ConfigureAwait(false);
                    else
                        await Run(PollBatteriesAsync, lifetime.Token).ConfigureAwait(false);
                }
                await Task.Delay(pollInterval, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task PollBatteriesAsync(CancellationToken token)
    {
        KeyValuePair<string, BatterySchedule>[] due;
        lock (sync) due = batterySchedules.Where(pair => BatteryIsDue(pair.Value)).ToArray();
        foreach (var (key, expected) in due)
        {
            token.ThrowIfCancellationRequested();
            var snapshot = inner.Snapshot;
            // Battery reads use the existing serialized management path. Never
            // stop/take over a scan, mark the page busy, or alter actuator input.
            if (snapshot.IsScanning && !snapshot.SupportsOutputWhileScanning) return;
            if (!snapshot.Devices.Any(device => device.DeviceKey == key &&
                device.ConnectionState == ToyConnectionState.Connected)) continue;
            lock (sync)
            {
                if (!batterySchedules.TryGetValue(key, out var current) ||
                    current.Connection != expected.Connection || !BatteryIsDue(current)) continue;
                // Reserve before the request/callback so errors and unknown
                // replies cannot cause retries on every background tick.
                batterySchedules[key] = current with { LastRead = batteryTime.GetTimestamp(), HasRead = true };
            }
            try { await inner.ReadBatteryAsync(key, token).ConfigureAwait(false); }
            catch (Exception) when (!token.IsCancellationRequested)
            { /* Existing adapter diagnostics apply; next read waits five minutes or a new connection. */ }
        }
    }

    public Task ConnectAsync(string deviceKey, CancellationToken cancellationToken = default) => ManualAsync(async token =>
    {
        var device = inner.Snapshot.Devices.FirstOrDefault(device => device.DeviceKey == deviceKey);
        if (device is null || device.ConnectionState is ToyConnectionState.Unavailable or ToyConnectionState.Disconnected)
        {
            // An offline row has no usable native ID. Rediscover before an explicit reconnect.
            await inner.StopScanAsync(token).ConfigureAwait(false);
            var scanStartedAt = DateTimeOffset.UtcNow;
            try
            {
                await inner.StartScanAsync(token).ConfigureAwait(false);
                while (inner.Snapshot.IsScanning)
                {
                    device = inner.Snapshot.Devices.FirstOrDefault(device => device.DeviceKey == deviceKey);
                    if (device is not null && device.LastSeenUtc >= scanStartedAt &&
                        device.ConnectionState is ToyConnectionState.Discovered or ToyConnectionState.Disconnected) break;
                    await Task.Delay(TimeSpan.FromMilliseconds(50), token).ConfigureAwait(false);
                }
            }
            finally { await inner.StopScanAsync(lifetime.Token).ConfigureAwait(false); }
            device = inner.Snapshot.Devices.FirstOrDefault(device => device.DeviceKey == deviceKey);
            if (device is null || device.LastSeenUtc < scanStartedAt || device.ConnectionState == ToyConnectionState.Unavailable)
                throw new InvalidOperationException("Toy not found. Turn it on and keep it nearby, then select Reconnect.");
        }
        await inner.ConnectAsync(deviceKey, token).ConfigureAwait(false);
        if (inner.Snapshot.Devices.FirstOrDefault(device => device.DeviceKey == deviceKey)?.ConnectionState == ToyConnectionState.Connected)
        {
            RememberConnected(deviceKey, explicitConnect: true);
            lock (sync) paused.Remove(deviceKey);
            Publish();
        }
    }, cancellationToken);

    public Task DisconnectAsync(string deviceKey, CancellationToken cancellationToken = default)
    {
        // Set this before waiting for a pending automatic attempt to finish/cancel.
        lock (sync) paused.Add(deviceKey);
        Publish();
        return ManualAsync(token => inner.DisconnectAsync(deviceKey, token), cancellationToken);
    }

    public Task SetAutoConnectAsync(string deviceKey, bool enabled, CancellationToken cancellationToken = default) =>
        ManualAsync(_ =>
        {
            RememberedToy toy;
            lock (sync) toy = remembered.TryGetValue(deviceKey, out var saved) ? saved :
                throw new InvalidOperationException("Connect this toy once before enabling Auto-connect.");
            SaveChange(toy with { AutoConnect = enabled }, deviceKey);
            lock (sync) { paused.Remove(deviceKey); if (enabled) nextAttempt = DateTimeOffset.MinValue; }
            Publish();
            return Task.CompletedTask;
        }, cancellationToken);

    private void RequireOfflineForForget(string deviceKey)
    {
        if (inner.Snapshot.Devices.FirstOrDefault(device => device.DeviceKey == deviceKey)?.ConnectionState is
            ToyConnectionState.Connected or ToyConnectionState.Connecting or ToyConnectionState.Disconnecting)
            throw new InvalidOperationException("Turn this toy off and wait until it is offline before forgetting it.");
    }
    public Task ForgetAsync(string deviceKey, CancellationToken cancellationToken = default)
    {
        // Reject an in-use toy before canceling connection automation. Check
        // again after queued work/store writes, since it may connect meanwhile.
        try { RequireOfflineForForget(deviceKey); }
        catch (InvalidOperationException error) { return Task.FromException(error); }
        return ManualAsync(_ =>
        {
            lock (storeWrites)
            {
                RequireOfflineForForget(deviceKey);
                SaveChangeCore(null, deviceKey);
                lock (sync) { paused.Add(deviceKey); forgotten.Add(deviceKey); }
            }
            Publish();
            return Task.CompletedTask;
        }, cancellationToken);
    }

    private void RememberConnected(string key, bool explicitConnect = false)
    {
        lock (storeWrites)
        {
            bool enabled;
            lock (sync)
            {
                // A delayed engine notification must not save a forgotten toy again.
                if (forgotten.Contains(key) && !explicitConnect) return;
                enabled = connectAll || remembered.TryGetValue(key, out var previous) && previous.AutoConnect;
            }
            var descriptor = inner.Snapshot.Devices.Single(device => device.DeviceKey == key);
            if (!descriptor.PersistentIdentity) return;
            SaveChangeCore(new(key, descriptor.Model, enabled, descriptor.LastSeenUtc), key);
            if (explicitConnect) lock (sync) forgotten.Remove(key);
        }
        Publish();
    }

    private void SaveChange(RememberedToy? toy, string key)
    {
        // Native discovery may confirm a connection while an owner edits saved
        // metadata. Serialize the whole read/save/update transaction.
        lock (storeWrites) SaveChangeCore(toy, key);
    }
    private void SaveChangeCore(RememberedToy? toy, string key)
    {
        RememberedToy[] saved;
        lock (sync)
        {
            if (!storageAvailable) throw new InvalidOperationException("Toy settings cannot be saved because remembered toys could not be loaded. The existing file has been kept.");
            saved = remembered.Values.Where(value => value.DeviceKey != key).Concat(toy is null ? [] : new[] { toy }).ToArray();
        }
        try { store.Save(saved); }
        catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            lock (sync) storageWarning = "Remembered toy settings could not be saved. Check access to local settings and try again.";
            Publish();
            throw new InvalidOperationException("Remembered toy settings could not be saved. Check access to local settings and try again. A connected toy stays connected.");
        }
        lock (sync)
        {
            storageWarning = null;
            if (toy is null) remembered.Remove(key); else remembered[key] = toy;
        }
    }

    private string[] EligibleKeys()
    {
        var connected = inner.Snapshot.Devices.Where(device => device.ConnectionState == ToyConnectionState.Connected)
            .Select(device => device.DeviceKey).ToHashSet(StringComparer.Ordinal);
        var discovered = inner.Snapshot.Devices.Select(device => device.DeviceKey).ToArray();
        lock (sync) return (connectAll ? remembered.Keys.Concat(discovered).Distinct(StringComparer.Ordinal) :
                remembered.Values.Where(toy => toy.AutoConnect).Select(toy => toy.DeviceKey))
            .Where(key => !paused.Contains(key) && !connected.Contains(key)).ToArray();
    }

    private async Task AutoConnectLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                bool due, discover; int generation;
                lock (sync) { due = DateTimeOffset.UtcNow >= nextAttempt; generation = manualGeneration;
                    discover = connectAll && (initialDiscovery || inner.Snapshot.Devices.Count == 0); }
                if (due && (discover || EligibleKeys().Length > 0))
                {
                    try { await Run(token => AutomaticCycleAsync(generation, token), lifetime.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { }
                    catch (Exception) when (!lifetime.IsCancellationRequested)
                    {
                        lock (sync) nextAttempt = DateTimeOffset.UtcNow + NextAttemptDelay;
                    }
                }
                await Task.Delay(pollInterval, lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
    }

    private async Task AutomaticCycleAsync(int generation, CancellationToken token)
    {
        // Do not take over a scan that the user started.
        bool all, initial;
        lock (sync) { all = connectAll; initial = initialDiscovery; }
        if ((!all && EligibleKeys().Length == 0) || inner.Snapshot.IsScanning) return;
        bool scanNeeded, extendedRecovery;
        var eligibleBeforeScan = EligibleKeys();
        lock (sync)
        {
            // Consume fresh manual/previous discoveries before another scan clears them.
            var available = inner.Snapshot.Devices.Where(device =>
                device.ConnectionState is ToyConnectionState.Discovered or ToyConnectionState.Disconnected)
                .Select(device => device.DeviceKey).ToHashSet(StringComparer.Ordinal);
            scanNeeded = !all || !eligibleBeforeScan.Any(key => freshCandidates.Contains(key) && available.Contains(key));
            extendedRecovery = all && extendRecoveryScan && eligibleBeforeScan.Length > 0;
        }
        // Invoke the owner predicate outside adapter locks. Background scans must not cancel live output.
        if (all && scanNeeded && !automaticScanAllowed()) return;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
        lock (sync)
        {
            if (generation != manualGeneration) return;
            automaticAttempt = attempt; nextAttempt = DateTimeOffset.UtcNow + retryInterval;
        }
        try
        {
            var scanStartedAt = DateTimeOffset.UtcNow;
            if (scanNeeded)
            {
                await inner.StartScanAsync(attempt.Token).ConfigureAwait(false);
                var scanClock = Stopwatch.StartNew();
                var candidates = new HashSet<string>(StringComparer.Ordinal);
                TimeSpan? firstFound = null, lastFound = null;
                while (inner.Snapshot.IsScanning)
                {
                    var found = inner.Snapshot.Devices.Where(device => device.LastSeenUtc >= scanStartedAt &&
                            device.ConnectionState is ToyConnectionState.Discovered or ToyConnectionState.Disconnected)
                        .Select(device => device.DeviceKey).ToHashSet(StringComparer.Ordinal);
                    var eligible = EligibleKeys();
                    if (!all && eligible.All(found.Contains)) break;
                    if (all)
                    {
                        // Collect nearby advertisements briefly; absent saved toys never hold up found ones.
                        foreach (var key in eligible.Where(found.Contains))
                            if (candidates.Add(key)) { firstFound ??= scanClock.Elapsed; lastFound = scanClock.Elapsed; }
                        if (firstFound is TimeSpan first && lastFound is TimeSpan last &&
                            (scanClock.Elapsed - last >= AdvertisementQuietTime || scanClock.Elapsed - first >= MaximumCollectionTime)) break;
                        TimeSpan remaining;
                        lock (sync) remaining = initialScanRemaining;
                        // A missing known toy needs an uninterrupted search after a short miss,
                        // even when the remaining all-new startup budget is smaller.
                        if (initial && !extendedRecovery && scanClock.Elapsed >= remaining) break;
                        if (scanClock.Elapsed >= (extendedRecovery ? MissingToyScanWindow : ConnectedScanWindow) && inner.Snapshot.Devices.Any(device =>
                            device.ConnectionState == ToyConnectionState.Connected)) break;
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(50), attempt.Token).ConfigureAwait(false);
                }
                var scanCompleted = !inner.Snapshot.IsScanning;
                await inner.StopScanAsync(attempt.Token).ConfigureAwait(false);
                if (all && candidates.Count == 0 && EligibleKeys().Length > 0 &&
                    inner.Snapshot.Devices.Any(device => device.ConnectionState == ToyConnectionState.Connected) &&
                    (scanCompleted || scanClock.Elapsed >= ConnectedScanWindow))
                    lock (sync) extendRecoveryScan = true;
                if (all && initial)
                {
                    var ready = inner.Snapshot.Devices.Where(device => device.ConnectionState == ToyConnectionState.Connected ||
                            device.LastSeenUtc >= scanStartedAt && device.ConnectionState is ToyConnectionState.Discovered or ToyConnectionState.Disconnected)
                        .Select(device => device.DeviceKey).ToHashSet(StringComparer.Ordinal);
                    lock (sync)
                    {
                        initialScanRemaining = TimeSpan.FromTicks(Math.Max(0, initialScanRemaining.Ticks - scanClock.Elapsed.Ticks));
                        // Resume the unused startup window so late toys aren't lost after the first connection.
                        initialDiscovery = !scanCompleted && initialScanRemaining > TimeSpan.Zero &&
                            !(startupTargets.Count > 0 && startupTargets.All(ready.Contains));
                    }
                }
            }
            foreach (var key in EligibleKeys())
            {
                attempt.Token.ThrowIfCancellationRequested();
                var device = inner.Snapshot.Devices.FirstOrDefault(device => device.DeviceKey == key);
                bool fresh; lock (sync) fresh = all ? freshCandidates.Remove(key) : device?.LastSeenUtc >= scanStartedAt;
                if (device is null || !fresh ||
                    device.ConnectionState is not (ToyConnectionState.Discovered or ToyConnectionState.Disconnected)) continue;
                try
                {
                    await inner.ConnectAsync(key, attempt.Token).ConfigureAwait(false);
                    if (inner.Snapshot.Devices.First(device => device.DeviceKey == key).ConnectionState == ToyConnectionState.Connected)
                        RememberConnected(key);
                }
                catch (InvalidOperationException) { /* Other enabled toys still get their own connection attempt. */ }
            }
            if (EligibleKeys().Length == 0) lock (sync) extendRecoveryScan = false;
        }
        finally
        {
            lock (sync) { automaticAttempt = null; nextAttempt = DateTimeOffset.UtcNow + NextAttemptDelay; }
            if (!lifetime.IsCancellationRequested) await inner.StopScanAsync(lifetime.Token).ConfigureAwait(false);
        }
    }

    // Called under sync. Legacy per-toy flags retain their delay; the page uses automatic-all mode.
    private TimeSpan NextAttemptDelay => !connectAll ? retryInterval : initialDiscovery
        ? TimeSpan.FromSeconds(1) : automaticRetryInterval;

    public ValueTask DisposeAsync()
    {
        lock (sync) return new ValueTask(disposeTask ??= Task.Run(DisposeCoreAsync));
    }

    public Task CloseTransportForExitAsync() => inner is ILocalToyExit exit
        ? exit.CloseTransportForExitAsync() : Task.CompletedTask;

    private async Task DisposeCoreAsync()
    {
        lifetime.Cancel(); CancelAutomaticAttempt();
        try { await Task.WhenAll(autoLoop, batteryLoop).ConfigureAwait(false); } catch (OperationCanceledException) { }
        await operations.WaitAsync().ConfigureAwait(false);
        try
        {
            inner.Changed -= InnerChanged;
            if (inner is ILocalToyOutput output) output.OutputFailed -= InnerOutputFailed;
            await inner.DisposeAsync().ConfigureAwait(false);
        }
        finally { operations.Release(); }
    }
}
