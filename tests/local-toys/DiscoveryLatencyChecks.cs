using System.Diagnostics;
using System.Text.Json;
using ShockWave.Overlay.Toys;

internal static partial class Program
{
    private static void DiscoveryChecks()
    {
        Run("initial discovery connects four toys before the full scan deadline", FirstDiscoveryBatch);
        Run("offline remembered toys do not delay available toys", OfflineToyDoesNotBlock);
        Run("early connections continue discovery only while output is idle", DiscoveryContinuation);
        Run("empty-radio discovery retries promptly without a 30-second gap", EmptyRadioRetry);
        Run("repeated advertisements do not extend the first connection wait", DuplicateDiscoveryLatency);
        Run("continuing discovery does not monopolize connected toy controls", ConnectedDiscoveryWindow);
        Run("manual discoveries connect before searching for absent saved toys", ManualCandidatesBeforeMissing);
        Run("a late missing toy receives a longer uninterrupted recovery scan", SlowMissingToyRecovery);
        Run("extended recovery scans remain cancellable and obey explicit pause", ExtendedRecoveryCancellation);
    }

    private static RememberedToyAdapter LongDiscovery(FakeBle api, IRememberedToyStore? store = null,
        TimeSpan? duration = null) => new(Adapter(api, duration ?? TimeSpan.FromSeconds(15)),
            store ?? new MemoryToyStore(), pollInterval: TimeSpan.FromMilliseconds(10));

    private static async Task FirstDiscoveryBatch()
    {
        var api = new FakeBle(); using var callbacks = new CancellationTokenSource();
        RememberedToyAdapter? owner = null;
        api.OnScan = () =>
        {
            api.Found("batch-1");
            _ = Task.Run(async () =>
            {
                try
                {
                    for (var i = 2; i <= 4; i++)
                    {
                        await Task.Delay(65, callbacks.Token);
                        if (owner?.Snapshot.IsScanning == true) api.Found("batch-" + i, "AA:BB:CC:DD:EE:0" + i, i == 4 ? "b" : "p");
                    }
                }
                catch (OperationCanceledException) { }
            });
        };
        await using var adapter = owner = LongDiscovery(api);
        var timer = Stopwatch.StartNew(); long firstConnectionTicks = 0, batchConnectionTicks = 0;
        adapter.Changed += () =>
        {
            var count = adapter.Snapshot.Devices.Count(d => d.ConnectionState == ToyConnectionState.Connected);
            if (count > 0) Interlocked.CompareExchange(ref firstConnectionTicks, timer.ElapsedTicks, 0);
            if (count == 4) Interlocked.CompareExchange(ref batchConnectionTicks, timer.ElapsedTicks, 0);
        };
        await adapter.EnableAutomaticConnectionsAsync();
        await Task.Delay(1000);
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "discovery-first-batch.json"), JsonSerializer.Serialize(new
        { elapsedMs = timer.Elapsed.TotalMilliseconds, firstConnectionMs = firstConnectionTicks * 1000d / Stopwatch.Frequency,
            fourConnectedMs = batchConnectionTicks * 1000d / Stopwatch.Frequency, scans = api.Starts, connections = api.Connects, physical = false }));
        callbacks.Cancel();
        Assert(api.Connects == 4, "Four advertised toys waited for the 15-second collection deadline instead of connecting within one second.");
        Assert(adapter.Snapshot.Devices.Count(d => d.ConnectionState == ToyConnectionState.Connected) == 4 && api.Commands.IsEmpty,
            "Fast first discovery lost an individual toy/function or activated output.");
    }

    private static async Task OfflineToyDoesNotBlock()
    {
        var store = new MemoryToyStore();
        store.Save(Enumerable.Range(1, 3).Select(i => new RememberedToy(FixtureKey("AABBCCDDEE0" + i),
            "Edge", true, DateTimeOffset.UtcNow)).ToArray());
        var api = new FakeBle(); api.OnScan = () => api.Found("available-now");
        await using var adapter = LongDiscovery(api, store);
        await adapter.EnableAutomaticConnectionsAsync(); await Task.Delay(900);
        Assert(api.ConnectedIds.SequenceEqual(["available-now"]), "Offline remembered toys held up the available toy.");
        Assert(store.Load().Count == 3 && adapter.Snapshot.Devices.Count(d => d.ConnectionState == ToyConnectionState.Connected) == 1 &&
            adapter.Snapshot.Devices.Where(d => d.ConnectionState != ToyConnectionState.Connected).All(d => d.BatteryPercent is null),
            "Fast discovery discarded remembered offline entries or invented connectivity/battery.");
    }

    private static async Task DiscoveryContinuation()
    {
        var api = new FakeBle(); var mayScan = 1;
        api.OnScan = () =>
        {
            api.Found("early");
            if (api.Starts >= 2)
                for (var i = 2; i <= 4; i++) api.Found("late-" + i, "AA:BB:CC:DD:EE:0" + i, "p");
        };
        await using var adapter = LongDiscovery(api);
        await adapter.EnableAutomaticConnectionsAsync(canScan: () => Volatile.Read(ref mayScan) != 0);
        await Until(() => api.Connects == 1);
        Volatile.Write(ref mayScan, 0); var scans = api.Starts;
        await Task.Delay(600);
        Assert(api.Starts == scans, "Continuing discovery ignored the active-output/explicit-pause gate.");
        Volatile.Write(ref mayScan, 1);
        await Until(() => adapter.Snapshot.Devices.Count(d => d.ConnectionState == ToyConnectionState.Connected) == 4);
        Assert(api.ConnectedIds.Distinct().Count() == 4 && api.Commands.IsEmpty, "Late toys were missed or an existing connection/output was replayed.");
    }

    private static async Task EmptyRadioRetry()
    {
        var api = new FakeBle(); api.OnScan = () => { if (api.Starts >= 2) api.Found("turned-on-later"); };
        await using var adapter = LongDiscovery(api, duration: TimeSpan.FromMilliseconds(80));
        await adapter.EnableAutomaticConnectionsAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (api.Connects == 0) await Task.Delay(10, timeout.Token);
        Assert(api.Starts == 2 && api.ConnectedIds.Single() == "turned-on-later" && api.Commands.IsEmpty,
            "An empty radio kept the old long retry delay, made excess scans or activated output.");
    }

    private static async Task DuplicateDiscoveryLatency()
    {
        var api = new FakeBle(); using var callbacks = new CancellationTokenSource();
        RememberedToyAdapter? owner = null;
        api.OnScan = () =>
        {
            api.Found("duplicate");
            _ = Task.Run(async () =>
            {
                try
                {
                    while (!callbacks.IsCancellationRequested)
                    {
                        await Task.Delay(30, callbacks.Token);
                        if (owner?.Snapshot.IsScanning == true) api.Found("duplicate");
                    }
                }
                catch (OperationCanceledException) { }
            });
        };
        await using var adapter = owner = LongDiscovery(api);
        await adapter.EnableAutomaticConnectionsAsync(); await Task.Delay(900); callbacks.Cancel();
        Assert(api.Connects == 1 && adapter.Snapshot.Devices.Count == 1 && api.Commands.IsEmpty,
            "Duplicate advertisements postponed connecting or created duplicate toys.");
    }

    private static async Task ConnectedDiscoveryWindow()
    {
        var api = new FakeBle(); api.OnScan = () => api.Found("already-connected");
        await using var adapter = LongDiscovery(api);
        await adapter.EnableAutomaticConnectionsAsync(); await Until(() => api.Connects == 1);
        await Until(() => api.Starts == 2); await Task.Delay(2200);
        Assert(!adapter.Snapshot.IsScanning && api.Starts == 2 && One(adapter).ConnectionState == ToyConnectionState.Connected &&
            api.Commands.IsEmpty, "Follow-up discovery held connected controls for the full 15-second window or replayed output.");
    }

    private static async Task SlowMissingToyRecovery()
    {
        var store = new MemoryToyStore();
        store.Save([new(FixtureKey("AABBCCDDEE01"), "Edge", true, DateTimeOffset.UtcNow),
            new(FixtureKey("AABBCCDDEE02"), "Hush", true, DateTimeOffset.UtcNow)]);
        var api = new FakeBle(); using var callbacks = new CancellationTokenSource();
        RememberedToyAdapter? owner = null;
        api.OnScan = () =>
        {
            api.Found("healthy-toy");
            var scanNumber = api.Starts;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(2800, callbacks.Token);
                    if (api.Starts == scanNumber && owner?.Snapshot.IsScanning == true)
                        api.Found("slow-hush", "AA:BB:CC:DD:EE:02", "z");
                }
                catch (OperationCanceledException) { }
            });
        };
        await using var adapter = owner = new RememberedToyAdapter(Adapter(api, TimeSpan.FromSeconds(15)), store,
            retryInterval: TimeSpan.FromMilliseconds(100), pollInterval: TimeSpan.FromMilliseconds(10));
        var timer = Stopwatch.StartNew();
        await adapter.EnableAutomaticConnectionsAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(9));
        try
        {
            while (!api.ConnectedIds.Contains("slow-hush")) await Task.Delay(10, timeout.Token);
        }
        catch (OperationCanceledException)
        {
            throw new Exception("Repeated short scans never found the late Hush advertisement while another toy stayed connected.");
        }
        finally { callbacks.Cancel(); }
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "discovery-missing-recovery.json"), JsonSerializer.Serialize(new
        { elapsedMs = timer.Elapsed.TotalMilliseconds, scans = api.Starts, connections = api.Connects, physical = false }));
        Assert(api.ConnectedIds.Count(id => id == "healthy-toy") == 1 && api.Disconnects == 0 && !api.Disposed && api.Commands.IsEmpty,
            "Missing-toy recovery reset/reconnected the healthy toy or invoked output.");
        Assert(adapter.Snapshot.Devices.Count(d => d.ConnectionState == ToyConnectionState.Connected) == 2,
            "Recovery did not preserve both independent connections.");
    }

    private static async Task ManualCandidatesBeforeMissing()
    {
        var store = new MemoryToyStore();
        store.Save([new(FixtureKey("AABBCCDDEE02"), "Hush", true, DateTimeOffset.UtcNow)]);
        var api = new FakeBle(); api.OnScan = () => { if (api.Starts == 1) api.Found("manual-only"); };
        var mayScan = 0;
        await using var adapter = LongDiscovery(api, store);
        await adapter.EnableAutomaticConnectionsAsync(canScan: () => Volatile.Read(ref mayScan) != 0);
        await adapter.StartScanAsync(); await adapter.StopScanAsync();
        await Task.Delay(600);
        Assert(api.ConnectedIds.SequenceEqual(["manual-only"]) && api.Starts == 1 && api.Commands.IsEmpty,
            "An absent remembered toy blocked a fresh manual candidate or discarded it through another scan.");
        Volatile.Write(ref mayScan, 1);
    }

    private static async Task ExtendedRecoveryCancellation()
    {
        var store = new MemoryToyStore();
        store.Save([new(FixtureKey("AABBCCDDEE01"), "Edge", true, DateTimeOffset.UtcNow),
            new(FixtureKey("AABBCCDDEE02"), "Hush", true, DateTimeOffset.UtcNow)]);
        var api = new FakeBle(); api.OnScan = () => api.Found("retained-during-search");
        var mayScan = 1;
        await using var adapter = new RememberedToyAdapter(Adapter(api, TimeSpan.FromSeconds(15)), store,
            retryInterval: TimeSpan.FromMilliseconds(100), pollInterval: TimeSpan.FromMilliseconds(10));
        await adapter.EnableAutomaticConnectionsAsync(canScan: () => Volatile.Read(ref mayScan) != 0);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(7));
        while (api.Starts < 3) await Task.Delay(10, timeout.Token);
        await Task.Delay(2200);
        Assert(adapter.Snapshot.IsScanning && api.Starts == 3, "Recovery never extended beyond the short search window.");
        Volatile.Write(ref mayScan, 0);
        var stopTime = Stopwatch.StartNew(); await adapter.StopScanAsync();
        Assert(stopTime.Elapsed < TimeSpan.FromSeconds(1) && !adapter.Snapshot.IsScanning,
            "Canceling extended discovery waited for the full recovery deadline.");
        await Task.Delay(1300);
        Assert(api.Starts == 3 && api.ConnectedIds.SequenceEqual(["retained-during-search"]) && api.Disconnects == 0 && api.Commands.IsEmpty,
            "Paused extended recovery restarted, reset a healthy connection or invoked output.");
    }
}
