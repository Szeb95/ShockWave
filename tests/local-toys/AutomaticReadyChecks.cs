using ShockWave.Overlay.Toys;

internal static partial class Program
{
    private static async Task AlwaysConnectedToys()
    {
        var store = new MemoryToyStore();
        store.Save([new(FixtureKey("AABBCCDDEE01"), "Edge", false, DateTimeOffset.UtcNow),
            new(FixtureKey("AABBCCDDEE02"), "Edge", false, DateTimeOffset.UtcNow)]);
        var api = new FakeBle();
        api.OnScan = () => { api.Found("auto-first"); api.Found("auto-second", "AA:BB:CC:DD:EE:02"); api.Found("auto-new", "AA:BB:CC:DD:EE:03", "u"); };
        await using var adapter = Remembered(api, store);
        await adapter.EnableAutomaticConnectionsAsync();
        await Until(() => adapter.Snapshot.Devices.Count(d => d.ConnectionState == ToyConnectionState.Connected) == 3 && store.Load().Count == 3);
        Assert(store.Load().All(t => t.AutoConnect) && api.Commands.IsEmpty, "Automatic connection required an old flag or activated output.");
        var scans = api.Starts; await Task.Delay(350);
        Assert(api.Starts == scans, "Connected toys were interrupted by unnecessary background scans.");
        api.OnScan = () => { api.Found("auto-fresh"); api.Found("auto-second", "AA:BB:CC:DD:EE:02"); api.Found("auto-new", "AA:BB:CC:DD:EE:03", "u"); };
        api.Connection("auto-first", false);
        await Until(() => api.ConnectedIds.Contains("auto-fresh") && adapter.Snapshot.Devices.All(d => d.ConnectionState == ToyConnectionState.Connected));
        Assert(api.Commands.IsEmpty, "Recovery replayed output.");
    }

    private static async Task NewToysAndEmptyRadio()
    {
        var api = new FakeBle();
        api.OnScan = () => { if (api.Starts >= 2) api.Found("appeared-later"); };
        await using var adapter = Remembered(api, new MemoryToyStore());
        await adapter.EnableAutomaticConnectionsAsync();
        await Until(() => api.ConnectedIds.Contains("appeared-later"));
        api.OnScan = () =>
        {
            api.Found("appeared-later");
            _ = Task.Run(async () => { await Task.Delay(35); api.Found("second-new", "AA:BB:CC:DD:EE:02", "u"); });
        };
        var scans = api.Starts;
        await adapter.StartScanAsync(); await Until(() => !adapter.Snapshot.IsScanning);
        await Until(() => api.ConnectedIds.Contains("second-new"));
        Assert(api.Starts == scans + 1 && api.Commands.IsEmpty, "Manual discovery required another scan/connect button or activated output.");
    }

    private static async Task AutomaticConnectionStorageFault()
    {
        var store = new FailingToyStore { FailLoad = true }; var api = new FakeBle(); api.OnScan = () => api.Found();
        await using var adapter = Remembered(api, store);
        await adapter.EnableAutomaticConnectionsAsync();
        await Until(() => adapter.Snapshot.Devices.Any(d => d.ConnectionState == ToyConnectionState.Connected));
        Assert(store.Saves == 0 && adapter.Snapshot.Message.Contains("saved file has been kept") && api.Commands.IsEmpty,
            "A damaged remembered file was overwritten or prevented runtime discovery.");
    }

    private static async Task ReadyContinuousInput()
    {
        var api = new FakeBle(); await using var adapter = SchedulerAdapter(api);
        await adapter.StartScanAsync(); api.Found(); await adapter.StopScanAsync(); var key = One(adapter).DeviceKey;
        await adapter.ConnectAsync(key);
        await using var controller = new LocalToyController(adapter, new(), automaticControls: true);
        Assert(controller.State(key, "v1").Armed && !controller.State(key, "v1").Active && api.Commands.IsEmpty, "Readiness started output.");
        controller.SetPreference(new(key, "v1", 100, 100, 250));
        controller.SetTestMode(false); controller.Request(key, "v1", 40);
        await Until(() => controller.State(key, "v1").SentPercent == 40); await Task.Delay(450);
        Assert(controller.State(key, "v1").Active && api.Commands.Count == 1 && api.Commands.Last().Value == 8, "Continuous input expired or repeated steady traffic.");
        controller.Request(key, "v1", 80); await Until(() => controller.State(key, "v1").SentPercent == 80);
        controller.SetPreference(new(key, "v1", 50, 30, 250)); await Until(() => controller.State(key, "v1").SentPercent == 30);
        controller.Stop(); await Until(() => !controller.State(key, "v1").Active && controller.State(key, "v1").Armed && api.Commands.Last().Value == 0);
        var count = api.Commands.Count; await Task.Delay(270);
        Assert(api.Commands.Count == count && controller.State(key, "v1").RequestedPercent == 0, "Stop restarted old input.");
        controller.Request(key, "v1", 50); await Until(() => api.Commands.Last().Value == 5);
        await adapter.DisconnectAsync(key); await Until(() => !controller.State(key, "v1").Active);
        await adapter.StartScanAsync(); api.Found("continuous-fresh"); await adapter.StopScanAsync(); await adapter.ConnectAsync(key);
        await Until(() => controller.State(key, "v1").Armed);
        count = api.Commands.Count; await Task.Delay(260);
        Assert(api.Commands.Count == count && controller.State(key, "v1").RequestedPercent == 0, "Reconnect restored prior activity.");
        controller.Request(key, "v1", 100); await Until(() => api.Commands.LastOrDefault().Id == "continuous-fresh" && api.Commands.LastOrDefault().Value == 6);
        controller.SetTestMode(true); await Until(() => api.Commands.Last().Value == 0);
        count = api.Commands.Count; controller.Request(key, "v1", 100); await Task.Delay(100);
        Assert(api.Commands.Count == count && controller.State(key, "v1").SentPercent == 30, "Test mode submitted a continuous input.");
        controller.SetTestMode(false); await Task.Delay(100);
        Assert(api.Commands.Count == count && !controller.State(key, "v1").Active, "Leaving test mode replayed preview input.");
    }

    private static async Task ReadyFaultAndCorruptLimits()
    {
        var api = new FakeBle(); await using var adapter = SchedulerAdapter(api);
        await adapter.StartScanAsync(); api.Found(); await adapter.StopScanAsync(); var key = One(adapter).DeviceKey; await adapter.ConnectAsync(key);
        await using (var controller = new LocalToyController(adapter, new(), automaticControls: true))
        {
            controller.SetTestMode(false); controller.Request(key, "v1", 40); await Until(() => api.Commands.Any(c => c.Value > 0));
            api.Notify(3, "private-test-id");
            await Until(() => !controller.State(key, "v1").Armed);
            await Task.Delay(100); Assert(!controller.State(key, "v1").Armed && !controller.State(key, "v1").Active, "Native failure automatically reopened output.");
            try { controller.Request(key, "v1", 70); throw new Exception("A failed toy accepted fresh input without recovery."); } catch (InvalidOperationException) { }
        }
        var path = Path.Combine(AppContext.BaseDirectory, "damaged-limits-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            File.WriteAllText(path, "damaged fixture limits");
            await using var controller = new LocalToyController(adapter, new(path), automaticControls: true);
            Assert(!controller.State(key, "v1").Armed && controller.StorageWarning is not null && File.ReadAllText(path) == "damaged fixture limits",
                "Corrupt saved limits silently enabled default output or were overwritten.");
        }
        finally { File.Delete(path); }
    }

    private static async Task BackgroundDiscoveryWaitsForIdle()
    {
        var api = new FakeBle(); api.OnScan = () => { api.Found("idle-first"); api.Found("idle-second", "AA:BB:CC:DD:EE:02"); };
        var mayScan = 1;
        await using var adapter = Remembered(api, new MemoryToyStore());
        await adapter.EnableAutomaticConnectionsAsync(canScan: () => Volatile.Read(ref mayScan) != 0);
        await Until(() => adapter.Snapshot.Devices.Count(d => d.ConnectionState == ToyConnectionState.Connected) == 2);
        Volatile.Write(ref mayScan, 0); var scans = api.Starts;
        api.OnScan = () => { api.Found("idle-fresh"); api.Found("idle-second", "AA:BB:CC:DD:EE:02"); };
        api.Connection("idle-first", false); await Task.Delay(250);
        Assert(api.Starts == scans && adapter.Snapshot.Devices.Any(d => d.DeviceKey == FixtureKey("AABBCCDDEE02") &&
            d.ConnectionState == ToyConnectionState.Connected), "Background recovery interrupted a running toy.");
        Volatile.Write(ref mayScan, 1); await Until(() => api.ConnectedIds.Contains("idle-fresh"));
        Assert(api.Commands.IsEmpty, "Idle recovery sent actuator output.");
    }

    private static async Task CancellationPublishesWithoutInventoryLock()
    {
        var api = new FakeBle { AutoConnect = false }; api.OnScan = () => api.Found();
        var store = new MemoryToyStore(); store.Save([new(FixtureKey("AABBCCDDEE01"), "Edge", true, DateTimeOffset.UtcNow)]);
        await using var adapter = new RememberedToyAdapter(new LovenseDiscoveryAdapter(() => api,
            () => new LocalToyKeys(Enumerable.Repeat((byte)1, 32).ToArray()), scanDuration: TimeSpan.FromMilliseconds(70),
            responseTimeout: TimeSpan.FromSeconds(2)), store, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(10));
        await adapter.InitializeAsync(); await Until(() => api.Connects > 0);
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed()
        {
            if (!adapter.Snapshot.Devices.Any(d => d.ConnectionState == ToyConnectionState.Disconnected)) return;
            var read = Task.Run(() => adapter.Snapshot);
            observed.TrySetResult(read.Wait(TimeSpan.FromMilliseconds(250)));
        }
        adapter.Changed += Changed;
        try
        {
            await adapter.StopScanAsync();
            Assert(await observed.Task.WaitAsync(TimeSpan.FromSeconds(2)), "Cancellation held inventory while callback threads needed it.");
        }
        finally { adapter.Changed -= Changed; }
    }
}
