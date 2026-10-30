using System.Text.Json;
using ShockWave.Overlay;
using ShockWave.Overlay.Toys;

internal static partial class Program
{
    private static int passed;
    private static readonly List<Exception> uiExceptions = [];
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.FirstOrDefault() == "--ble-worker-fixture")
                return LovenseWorkerHost.Run(() => new WorkerFixtureApi(args.ElementAtOrDefault(1) ?? "normal"));
            if (args.FirstOrDefault() == "--engine-owner-fixture") return EngineOwnerFixture(args[1]);
            ApplicationConfiguration.Initialize();
            if (args.Contains("--intiface-checks")) { IntifaceChecks(); return 0; }
            if (args.Contains("--discovery-checks"))
            {
                DiscoveryChecks();
                return 0;
            }
            if (args.Contains("--battery-checks")) { BatteryChecks(); return 0; }
            if (args.Contains("--buttplug-checks"))
            {
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (_, args) => uiExceptions.Add(args.Exception);
                ButtplugChecks(); return 0;
            }
            if (args.Contains("--engine-check"))
            { Run("real bundled engine with hardware managers disabled", () => ButtplugEngineCheck(args.Last())); return 0; }
            if (args.Contains("--scheduler-comparison"))
            {
                if (!args.Contains("--slow-only")) Run("ready toys submit together without an added per-command pause", SchedulerReadyGroup);
                Run("slow returns count toward pacing instead of adding another delay", SchedulerSlowCalls);
                return 0;
            }
            if (args.Contains("--scheduler-checks"))
            {
                Run("four multi-function toys share a paced fair Bluetooth budget", SchedulerFourToys);
                Run("ready toys submit together without an added per-command pause", SchedulerReadyGroup);
                Run("five toys retain bounded groups without idle credits", SchedulerBatchBound);
                Run("superseded input is canceled after transport waits", SchedulerFreshness);
                Run("expired input cannot cross a delayed submission guard", SchedulerExpiredTransportWait);
                Run("Stop preempts other toys and cannot rearm during zero", SchedulerStopBetweenToys);
                Run("slow returns count toward pacing and expose separate timings", SchedulerSlowCalls);
                Run("pace changes preserve limits, original expiry and activity gates", SchedulerRateChoice);
                return 0;
            }
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, args) => uiExceptions.Add(args.Exception);
            if (args.Contains("--ui-only")) { UiCheck(); RememberedUiCheck(); OfflineReconnectUiCheck(); ResetBluetoothUiCheck(); SchedulerUiCheck(); Console.WriteLine("PASS five offscreen page checks"); return 0; }
            Run("stable local keys and private identifier isolation", StableKeys);
            Run("independent channels and unknown metadata", Capabilities);
            Run("duplicate discovery and repeat start/stop", RepeatScans);
            Run("Bluetooth off/unavailable and missing DLL", MissingBluetooth);
            Run("scan cancellation and late callbacks", ScanCancellation);
            Run("completed scan reconciliation and connected toys", DisappearingToys);
            Run("connection, battery, disconnect and no output", ConnectAndBattery);
            BatteryChecks();
            ButtplugChecks();
            IntifaceChecks();
            Run("Bluetooth loss clears connection and battery", BluetoothLoss);
            Run("connection cancellation and timeout", ConnectionCancellation);
            Run("unconfirmed scan stop resets resources", StopTimeout);
            Run("cleanup during pending initialization and connection", Cleanup);
            Run("simulated discovery and missing Bluetooth", Simulation);
            Run("existing configuration migration", Configuration);
            Run("two identical models remembered separately and restored offline", RememberIdenticalToys);
            Run("encrypted remembered toy storage and corrupt-file protection", RememberedStorage);
            Run("startup auto-connect, individual flags and fresh native identities", AutomaticConnections);
            Run("auto-connect retries, manual disconnect pause and forgetting", AutomaticRecovery);
            Run("pending auto-connect cancellation and unsuccessful connections", AutomaticCancellation);
            Run("saved-file errors preserve manual connections and original data", RememberedErrors);
            Run("automatic connections respect manual scans and pending shutdown", AutomaticCleanup);
            Run("documented native function ranges and zero semantics", OutputRanges);
            Run("test mode previews but never invokes physical output", OutputTestMode);
            Run("independent motors, sensitivity, caps and expiry", OutputControl);
            Run("coalescing, priority stop and no stale restart", OutputScheduling);
            Run("responsive BLE target rate replaces old slider values", ResponsiveRate);
            Run("slow native writes keep state and Stop responsive", SlowWriteResponsiveness);
            Run("native callback threads can complete during output", OutputCallbackResponsiveness);
            Run("offline reconnect uses fresh discovery and reports missing toys", ManualOfflineReconnect);
            Run("connection loss starts rediscovery without waiting for retry delay", ImmediateLossRecovery);
            Run("lost native sessions are released before manual and automatic rediscovery", LostSessionCleanup);
            Run("failed connections refresh native resources without reusing old IDs", FailedSessionRefresh);
            Run("recovery preserves other connected toys", IsolatedSessionRecovery);
            Run("worker pipes carry callbacks and bounded scalar commands", WorkerCallbacks);
            Run("power cycles replace native process state without output replay", WorkerPowerCycles);
            Run("worker crashes close the owner gate and can recover", WorkerCrashRecovery);
            Run("worker heartbeat loss stops output and exits", WorkerHeartbeatExpiry);
            Run("parent pipe closure stops output and exits", WorkerPipeClosure);
            Run("hung native write is bounded and discards worker state", WorkerHungCall);
            Run("worker startup errors and slow cleanup are bounded", WorkerStartupAndCleanup);
            Run("worker startup diagnostics distinguish launch, exit and timeout", WorkerStartupDiagnostics);
            Run("four multi-function toys share a paced fair Bluetooth budget", SchedulerFourToys);
            Run("ready toys submit together without an added per-command pause", SchedulerReadyGroup);
            Run("five toys retain bounded groups without idle credits", SchedulerBatchBound);
            Run("superseded input is canceled after transport waits", SchedulerFreshness);
            Run("expired input cannot cross a delayed submission guard", SchedulerExpiredTransportWait);
            Run("Stop preempts other toys and cannot rearm during zero", SchedulerStopBetweenToys);
            Run("slow returns count toward pacing and expose separate timings", SchedulerSlowCalls);
            Run("pace changes preserve limits, original expiry and activity gates", SchedulerRateChoice);
            Run("worker rechecks freshness after management releases the pipe", WorkerFreshnessGuard);
            Run("all saved and discovered toys connect automatically without output", AlwaysConnectedToys);
            Run("empty radio retries and newly scanned toys connect without another scan", NewToysAndEmptyRadio);
            Run("automatic connections preserve damaged remembered storage", AutomaticConnectionStorageFault);
            Run("automatic readiness holds continuous input and never replays on recovery", ReadyContinuousInput);
            Run("native faults and damaged limits keep automatic output closed", ReadyFaultAndCorruptLimits);
            Run("background recovery waits for idle output before scanning", BackgroundDiscoveryWaitsForIdle);
            Run("connection cancellation permits callback threads to read inventory", CancellationPublishesWithoutInventoryLock);
            DiscoveryChecks();
            Run("reconnection and native failure disarm without replay", OutputFailure);
            Run("shutdown and scanning stop active native output", OutputCleanup);
            Run("motion stop clears queued position and never moves to zero", OutputMotion);
            Run("saved control preferences and corrupt-file protection", ControlStorage);
            UiCheck(); Console.WriteLine("PASS offscreen Local toys page, source switch, selection and cleanup"); passed++;
            RememberedUiCheck(); Console.WriteLine("PASS offscreen remembered rows and automatic startup without Scan toys"); passed++;
            OfflineReconnectUiCheck(); Console.WriteLine("PASS offline selection and cancellable automatic connection recovery"); passed++;
            ResetBluetoothUiCheck(); Console.WriteLine("PASS Bluetooth reset disposes transport and reconnects idle with saved limits"); passed++;
            SchedulerUiCheck(); Console.WriteLine("PASS offscreen pace control, timing footer and reset retention"); passed++;
            Run("app exit disarms and releases a blocked worker before the normal call timeout", WorkerExitDuringCall);
            Console.WriteLine($"{passed} checks passed; fixture commands only, no physical devices used.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Run(string name, Func<Task> check)
    { check().GetAwaiter().GetResult(); Console.WriteLine("PASS " + name); passed++; }
    private static void Assert(bool condition, string message)
    { if (!condition) throw new Exception(message); }
    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
    private static LovenseDiscoveryAdapter Adapter(FakeBle api, TimeSpan? duration = null, byte salt = 1) =>
        new(() => api, () => new LocalToyKeys(Enumerable.Repeat(salt, 32).ToArray()),
            scanDuration: duration ?? TimeSpan.FromSeconds(30), responseTimeout: TimeSpan.FromMilliseconds(120), commandsPerSecond: 1);
    private static LocalToyDescriptor One(ILocalToyAdapter adapter) => adapter.Snapshot.Devices.Single();

    private static string FixtureKey(string address) => new LocalToyKeys(Enumerable.Repeat((byte)1, 32).ToArray())
        .ForDevice("lovense-ble", "address:" + address);

    private static async Task OutputRanges()
    {
        foreach (var symbol in new[] { "p", "u", "a", "b", "ei", "ea", "q", "h", "ba", "ez" })
        {
            var api = new FakeBle(); await using var adapter = Adapter(api);
            await adapter.StartScanAsync(); api.Found(symbol: symbol);
            var device = One(adapter); await adapter.ConnectAsync(device.DeviceKey);
            foreach (var cap in device.Capabilities)
            {
                var maximum = cap.Type is ToyCapabilityType.Pump or ToyCapabilityType.Depth ? 3 : cap.Type == ToyCapabilityType.Position ? 100 : 20;
                Assert(cap.NativeMaximum == maximum, "Wrong native function range.");
                adapter.SendLevel(device.DeviceKey, cap.ChannelId, maximum);
                Assert(api.Commands.Last().Command == LovenseModelCatalog.Command(cap.ChannelId) && api.Commands.Last().Value == maximum, "Wrong native command.");
                adapter.SendLevel(device.DeviceKey, cap.ChannelId, 0);
                Assert(api.Commands.Last().Command == LovenseModelCatalog.Command(cap.IsSetting ? "t" : cap.ChannelId) && api.Commands.Last().Value == 0,
                    "Setting zero moved to an endpoint rather than stopping motion.");
                var count = api.Commands.Count;
                try { adapter.SendLevel(device.DeviceKey, cap.ChannelId, maximum + 1); throw new Exception("Invalid level accepted."); }
                catch (InvalidOperationException) { }
                Assert(api.Commands.Count == count, "Invalid value reached native API.");
            }
        }
    }

    private static async Task OutputTestMode()
    {
        var api = new FakeBle(); await using var adapter = Adapter(api);
        await adapter.StartScanAsync(); api.Found(); var key = One(adapter).DeviceKey; await adapter.ConnectAsync(key);
        await using var controller = new LocalToyController(adapter, new());
        try { controller.Request(key, "v1", 100); throw new Exception("Unarmed command accepted."); } catch (InvalidOperationException) { }
        controller.Arm(key); controller.Request(key, "v1", 65);
        await Until(() => controller.State(key, "v1").Detail.Contains("preview"));
        Assert(controller.State(key, "v1").SentPercent == 65 && api.Commands.IsEmpty, "Test mode sent a command or lost the preview.");
        controller.SetTestMode(false);
        Assert(!controller.State(key, "v1").Armed && !controller.State(key, "v1").Active, "Leaving test mode restored activity.");
        controller.Arm(key); controller.Request(key, "v1", 50); await Until(() => !api.Commands.IsEmpty);
        controller.SetTestMode(true); await Until(() => api.Commands.Last().Value == 0);
        var count = api.Commands.Count; controller.Arm(key); controller.Request(key, "v2", 100); await Task.Delay(150);
        Assert(api.Commands.Count == count, "Test mode allowed another output path.");
    }

    private static async Task OutputControl()
    {
        var api = new FakeBle(); await using var adapter = Adapter(api);
        await adapter.StartScanAsync(); api.Found(); var key = One(adapter).DeviceKey; await adapter.ConnectAsync(key);
        await using var controller = new LocalToyController(adapter, new()); controller.SetTestMode(false);
        controller.SetPreference(new(key, "v1", 50, 30, 300)); controller.SetPreference(new(key, "v2", 100, 100, 1500));
        controller.Arm(key); controller.Request(key, "v1", 100); controller.Request(key, "v2", 100);
        await Until(() => api.Commands.Any(c => c.Command == 1 && c.Value > 0));
        Assert(api.Commands.First(c => c.Value > 0).Value == 6 && controller.State(key, "v1").SentPercent == 30, "Sensitivity/max conversion exceeded cap.");
        await Until(() => api.Commands.Any(c => c.Command == 1 && c.Value == 0));
        await Until(() => api.Commands.Any(c => c.Command == 2 && c.Value == 20));
        Assert(!controller.State(key, "v1").Active && controller.State(key, "v2").Active, "One motor's expiry stopped another motor.");
        controller.SetPreference(new(key, "v2", 0, 100, 1500)); await Until(() => api.Commands.Last().Command == 2 && api.Commands.Last().Value == 0);
        Assert(controller.State(key, "v2").RequestedPercent == 0, "Zero sensitivity was not off.");
        controller.Request(key, "v2", 100); await Task.Delay(60);
        Assert(api.Commands.Last().Value == 0, "Zero sensitivity restarted output.");
    }

    private static async Task OutputScheduling()
    {
        var api = new FakeBle(); await using var adapter = Adapter(api);
        await adapter.StartScanAsync(); api.Found(); var key = One(adapter).DeviceKey; await adapter.ConnectAsync(key);
        await using var controller = new LocalToyController(adapter, new()); controller.SetTestMode(false); controller.Arm(key);
        controller.Request(key, "v1", 40); await Until(() => api.Commands.Any(c => c.Value > 0));
        for (var percent = 41; percent <= 100; percent++) controller.Request(key, "v1", percent);
        await Until(() => api.Commands.Count(c => c.Value > 0) == 2);
        var active = api.Commands.Where(c => c.Value > 0).ToArray();
        Assert(active[1].Value == 20 && active[1].Time - active[0].Time >= System.Diagnostics.Stopwatch.Frequency * .99,
            "Commands were replayed or native rate exceeded.");
        controller.SetPreference(new(key, "v2", 100, 100, 250)); controller.Request(key, "v2", 100);
        await Task.Delay(350); Assert(!api.Commands.Any(c => c.Command == 2 && c.Value > 0), "Expired pending input reached hardware.");
        controller.Request(key, "v1", 80); var before = DateTime.UtcNow; controller.Stop(key);
        await Until(() => api.Commands.Last().Value == 0);
        Assert(DateTime.UtcNow - before < TimeSpan.FromMilliseconds(250), "Stop waited for the normal command rate.");
        try { controller.Request(key, "v1", 100); throw new Exception("Stopped toy was still armed."); } catch (InvalidOperationException) { }
        await Task.Delay(1100); Assert(api.Commands.Count(c => c.Value > 0) == 2, "A stale pending command restarted output.");
    }

    private static async Task OutputFailure()
    {
        var api = new FakeBle(); await using var adapter = Adapter(api);
        await adapter.StartScanAsync(); api.Found(); var key = One(adapter).DeviceKey; await adapter.ConnectAsync(key);
        await using var controller = new LocalToyController(adapter, new()); controller.SetTestMode(false); controller.Arm(key);
        api.FailOutput = true; controller.Request(key, "v1", 100);
        await Until(() => !controller.State(key, "v1").Armed && api.Commands.Count >= 2);
        var count = api.Commands.Count; await Task.Delay(1100);
        Assert(api.Commands.Count == count && count == 2, "Failure retried or a failed stop looped.");
        api.FailOutput = false; controller.Arm(key); controller.Request(key, "v2", 100);
        await Until(() => api.Commands.Last().Value > 0); api.Connection("private-test-id", false); api.Connection("private-test-id", true);
        Assert(!controller.State(key, "v2").Armed && !controller.State(key, "v2").Active, "Reconnect restored output permission.");
        var positives = api.Commands.Count(c => c.Value > 0); await Task.Delay(1100);
        Assert(api.Commands.Count(c => c.Value > 0) == positives, "Reconnect replayed output.");
    }

    private static async Task OutputCleanup()
    {
        var api = new FakeBle(); await using var adapter = Adapter(api);
        await adapter.StartScanAsync(); api.Found(); var key = One(adapter).DeviceKey; await adapter.ConnectAsync(key);
        var controller = new LocalToyController(adapter, new()); controller.SetTestMode(false); controller.Arm(key);
        controller.Request(key, "v1", 100); await Until(() => api.Commands.Any(c => c.Value > 0));
        await adapter.StartScanAsync(); Assert(api.Commands.Last().Value == 0 && !controller.State(key, "v1").Armed, "Scanning did not stop/disarm native output first.");
        await adapter.StopScanAsync(); await Until(() => !controller.State(key, "v1").Active);
        await Task.Delay(60); // The priority stop is handled independently of the scan operation completion.
        controller.Arm(key); controller.Request(key, "v1", 100); await Until(() => api.Commands.Last().Value > 0);
        await controller.DisposeAsync(); Assert(api.Commands.Last().Value == 0, "Controller shutdown did not send zero before adapter cleanup.");
        await controller.DisposeAsync(); Assert(api.Commands.Last().Value == 0, "Repeated cleanup changed output.");
    }

    private static async Task OutputMotion()
    {
        var api = new FakeBle(); await using var adapter = Adapter(api);
        await adapter.StartScanAsync(); api.Found(symbol: "ba"); var key = One(adapter).DeviceKey; await adapter.ConnectAsync(key);
        await using var controller = new LocalToyController(adapter, new()); controller.SetTestMode(false); controller.Arm(key);
        controller.Request(key, "t", 100); await Until(() => api.Commands.Any(c => c.Command == 6 && c.Value > 0));
        controller.Request(key, "pos", 100); controller.Request(key, "t", 0);
        await Until(() => api.Commands.Last().Value == 0); await Task.Delay(1100);
        Assert(!api.Commands.Any(c => c.Command == 10), "Queued position moved after thrusting Stop.");
        Assert(controller.State(key, "pos").RequestedPercent == 0 && !controller.State(key, "pos").Active, "Motion stop retained stale settings.");
        controller.Request(key, "pos", 50); await Until(() => api.Commands.Last().Command == 10 && api.Commands.Last().Value == 50);
        controller.Request(key, "pos", 0); await Until(() => api.Commands.Last().Command == 6 && api.Commands.Last().Value == 0);
        Assert(!api.Commands.Any(c => c.Command == 10 && c.Value == 0), "Zero position moved to the endpoint instead of stopping motion.");
    }

    private static Task ControlStorage()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "controls-check-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new LocalToyControlStore(path); store.Load();
            var key = FixtureKey("AABBCCDDEE01"); store.Save(new(key, "v1", 42, 31, 1250)); store.Save(new(key, "v2", 99, 67, 15000));
            var restored = new LocalToyControlStore(path); restored.Load();
            Assert(restored.Get(key, "v1") == new ToyControlPreference(key, "v1", 42, 31, 1250) && restored.Get(key, "v2").Sensitivity == 99,
                "Independent settings were lost.");
            Assert(!File.ReadAllText(path).Contains("Requested") && !File.ReadAllText(path).Contains("Armed"), "Saved settings contained output/arming state.");
            File.WriteAllText(path, "invalid fixture"); var corrupt = new LocalToyControlStore(path); corrupt.Load();
            Assert(corrupt.Warning is not null, "Corrupt file had no visible warning.");
            try { corrupt.Save(new(key, "v1")); throw new Exception("Corrupt file was overwritten."); } catch (InvalidOperationException) { }
            Assert(File.ReadAllText(path) == "invalid fixture", "Corrupt settings were discarded.");
        }
        finally { File.Delete(path); }
        return Task.CompletedTask;
    }
    private static RememberedToyAdapter Remembered(FakeBle api, IRememberedToyStore store) => new(
        Adapter(api, TimeSpan.FromMilliseconds(70)), store, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(10));

    private static async Task RememberIdenticalToys()
    {
        var store = new MemoryToyStore(); var api = new FakeBle();
        await using (var adapter = Remembered(api, store))
        {
            await adapter.InitializeAsync();
            Assert(api.Checks == 0 && api.Starts == 0, "Empty remembered list initialized Bluetooth.");
            await adapter.StartScanAsync();
            api.Found("first-edge", "AA:BB:CC:DD:EE:01", "p");
            api.Found("second-edge", "AA:BB:CC:DD:EE:02", "p");
            Assert(adapter.Snapshot.Devices.Count == 2 && store.Load().Count == 0, "Same model merged, or discovery remembered an unconnected toy.");
            foreach (var toy in adapter.Snapshot.Devices) await adapter.ConnectAsync(toy.DeviceKey);
            Assert(store.Load().Count == 2 && store.Load().All(toy => !toy.AutoConnect), "Successful connections were not remembered with auto-connect off.");
            Assert(adapter.Snapshot.Devices.All(toy => toy.ConnectionState == ToyConnectionState.Connected && toy.IsRemembered), "Both identical toys did not remain connected.");
            await adapter.ReadBatteryAsync(FixtureKey("AABBCCDDEE01"));
        }
        var restartedApi = new FakeBle(); await using var restarted = Remembered(restartedApi, store);
        await restarted.InitializeAsync();
        Assert(restarted.Snapshot.Devices.Count == 2 && restarted.Snapshot.Devices.All(toy =>
            toy.IsRemembered && toy.ConnectionState == ToyConnectionState.Unavailable && toy.BatteryPercent is null), "Restart restored stale connection/battery.");
        await Task.Delay(180);
        Assert(restartedApi.Starts == 0 && restartedApi.Connects == 0, "Disabled remembered toys auto-connected.");
    }

    private static Task RememberedStorage()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "remembered-check-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            var store = new RememberedToyStore(path);
            Assert(store.Load().Count == 0, "Missing settings were not treated as first run.");
            RememberedToy[] toys = [new(FixtureKey("AABBCCDDEE01"), "Edge", true, DateTimeOffset.UtcNow),
                new(FixtureKey("AABBCCDDEE02"), "Edge", false, DateTimeOffset.UtcNow)];
            store.Save(toys);
            Assert(store.Load().SequenceEqual(toys), "Encrypted saved toys failed round trip.");
            Assert(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains(toys[0].DeviceKey), "Saved toy identity was plaintext.");
            store.Save([toys[1]]); Assert(store.Load().Single() == toys[1], "Atomic replacement failed.");
            File.WriteAllBytes(path, [1, 2, 3, 4]);
            try { store.Load(); throw new Exception("Corrupt settings were accepted."); }
            catch (System.Security.Cryptography.CryptographicException) { }
            Assert(File.ReadAllBytes(path).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "Loading corrupt data overwrote it.");
        }
        finally { File.Delete(path); }
        return Task.CompletedTask;
    }

    private static async Task AutomaticConnections()
    {
        var store = new MemoryToyStore();
        store.Save([new(FixtureKey("AABBCCDDEE01"), "Edge", true, DateTimeOffset.UtcNow),
            new(FixtureKey("AABBCCDDEE02"), "Edge", false, DateTimeOffset.UtcNow)]);
        var api = new FakeBle();
        api.OnScan = () =>
        {
            api.Found("new-native-id", "AA:BB:CC:DD:EE:01", "p");
            api.Found("disabled-edge", "AA:BB:CC:DD:EE:02", "p");
            api.Found("unknown-edge", "AA:BB:CC:DD:EE:03", "p");
        };
        await using var adapter = Remembered(api, store); await adapter.InitializeAsync();
        await Until(() => adapter.Snapshot.Devices.Any(toy => toy.ConnectionState == ToyConnectionState.Connected));
        Assert(api.ConnectedIds.SequenceEqual(["new-native-id"]), "Auto-connect targeted a disabled/unremembered toy or stale native ID.");
        await Until(() => api.BatteryReads == 1 && OneConnectedBattery(adapter));
        Assert(api.Stops >= 1, "Auto-connect failed to stop discovery before its connection/battery request.");
        var publicData = JsonSerializer.Serialize(adapter.Snapshot);
        Assert(!publicData.Contains("new-native-id") && !publicData.Contains("AA:BB"), "Remembered metadata leaked raw IDs.");
        await adapter.SetAutoConnectAsync(FixtureKey("AABBCCDDEE02"), true);
        await Until(() => adapter.Snapshot.Devices.Count(toy => toy.ConnectionState == ToyConnectionState.Connected) == 2);
        Assert(api.ConnectedIds.Count == 2 && store.Load().All(toy => toy.AutoConnect), "Two same-model auto-connect flags were not independent.");
        // After a real loss, old discovered entries must not substitute for a fresh advertisement.
        api.OnScan = null; api.Connection("new-native-id", false);
        await Task.Delay(380);
        Assert(api.Connects == 2, "A reconnect used a stale native entry without rediscovery.");
    }

    private static async Task AutomaticRecovery()
    {
        var key = FixtureKey("AABBCCDDEE01"); var store = new MemoryToyStore();
        store.Save([new(key, "Edge", true, DateTimeOffset.UtcNow)]);
        var api = new FakeBle { Status = 1 }; api.OnScan = () => api.Found();
        await using var adapter = Remembered(api, store); await adapter.InitializeAsync();
        await Until(() => api.Checks > 0); Assert(api.Connects == 0, "Auto-connect ignored disabled Bluetooth.");
        api.Status = 0; await Until(() => One(adapter).ConnectionState == ToyConnectionState.Connected);
        await adapter.DisconnectAsync(key); var count = api.Connects;
        await Task.Delay(380);
        Assert(api.Connects == count && One(adapter).AutoConnectPaused && store.Load().Single().AutoConnect, "Manual disconnect was undone or changed the saved toggle.");
        await adapter.SetAutoConnectAsync(key, true);
        await Until(() => One(adapter).ConnectionState == ToyConnectionState.Connected);
        Assert(!One(adapter).AutoConnectPaused, "Explicitly re-enabling did not resume this toy.");
        try { await adapter.ForgetAsync(key); throw new Exception("Connected toy could be forgotten."); } catch (InvalidOperationException) { }
        Assert(store.Load().Count == 1 && One(adapter).IsRemembered && !One(adapter).AutoConnectPaused,
            "Rejected connected Forget changed saved state or paused reconnection.");
        await adapter.DisconnectAsync(key);
        await adapter.ForgetAsync(key);
        Assert(store.Load().Count == 0 && adapter.Snapshot.Devices.Count == 0, "Forget did not remove saved toy settings and offline inventory.");
        await adapter.DisconnectAsync(key); count = api.Connects; await Task.Delay(220);
        Assert(api.Connects == count, "Forgotten toy reconnected.");
    }

    private static async Task AutomaticCancellation()
    {
        var key = FixtureKey("AABBCCDDEE01"); var store = new MemoryToyStore();
        store.Save([new(key, "Edge", true, DateTimeOffset.UtcNow)]);
        var api = new FakeBle { AutoConnect = false }; api.OnScan = () => api.Found();
        await using (var adapter = new RememberedToyAdapter(new LovenseDiscoveryAdapter(() => api,
            () => new LocalToyKeys(Enumerable.Repeat((byte)1, 32).ToArray()), scanDuration: TimeSpan.FromMilliseconds(70),
            responseTimeout: TimeSpan.FromSeconds(2)), store, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(10)))
        {
            await adapter.InitializeAsync(); await Until(() => api.Connects > 0);
            var timer = System.Diagnostics.Stopwatch.StartNew();
            await adapter.SetAutoConnectAsync(key, false);
            Assert(timer.Elapsed < TimeSpan.FromSeconds(1), "Disabling auto-connect waited for the pending connection timeout.");
            Assert(api.Disconnects > 0 && One(adapter).ConnectionState == ToyConnectionState.Disconnected && !One(adapter).AutoConnect, "Disabling auto-connect did not cancel a pending native connection.");
            var count = api.Connects; await Task.Delay(220); Assert(api.Connects == count, "Disabled toy retried connecting.");
        }
        var failedStore = new MemoryToyStore(); var failedApi = new FakeBle { AutoConnect = false };
        await using var failed = Remembered(failedApi, failedStore);
        await failed.StartScanAsync(); failedApi.Found();
        try { await failed.ConnectAsync(key); throw new Exception("Unconfirmed connection succeeded."); }
        catch (InvalidOperationException) { }
        Assert(failedStore.Load().Count == 0, "Unsuccessful connection was remembered.");
    }

    private static async Task RememberedErrors()
    {
        var store = new FailingToyStore { FailLoad = true }; var api = new FakeBle();
        await using var adapter = Remembered(api, store);
        await adapter.InitializeAsync(); await adapter.StartScanAsync(); api.Found();
        try { await adapter.ConnectAsync(One(adapter).DeviceKey); throw new Exception("Unavailable remembered storage was silently overwritten."); }
        catch (InvalidOperationException) { }
        Assert(One(adapter).ConnectionState == ToyConnectionState.Connected && store.Saves == 0 &&
            adapter.Snapshot.Message.Contains("saved file has been kept"), "Storage error hid a live connection or modified the corrupt file.");
        var saveStore = new FailingToyStore(); var saveApi = new FakeBle();
        await using var saveAdapter = Remembered(saveApi, saveStore);
        await saveAdapter.StartScanAsync(); saveApi.Found();
        try { await saveAdapter.ConnectAsync(One(saveAdapter).DeviceKey); throw new Exception("Failed save succeeded."); }
        catch (InvalidOperationException) { }
        Assert(!One(saveAdapter).IsRemembered && One(saveAdapter).ConnectionState == ToyConnectionState.Connected, "Failed save created a false remembered row or disconnected the toy.");
    }

    private static async Task AutomaticCleanup()
    {
        var key = FixtureKey("AABBCCDDEE01"); var store = new MemoryToyStore();
        store.Save([new(key, "Edge", true, DateTimeOffset.UtcNow)]);
        var api = new FakeBle { AutoConnect = false }; api.OnScan = () => api.Found();
        var adapter = new RememberedToyAdapter(new LovenseDiscoveryAdapter(() => api,
            () => new LocalToyKeys(Enumerable.Repeat((byte)1, 32).ToArray()), scanDuration: TimeSpan.FromMilliseconds(70),
            responseTimeout: TimeSpan.FromSeconds(2)), store, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(10));
        await adapter.InitializeAsync(); await Until(() => api.Connects > 0);
        var timer = System.Diagnostics.Stopwatch.StartNew(); await adapter.DisposeAsync(); await adapter.DisposeAsync();
        Assert(timer.Elapsed < TimeSpan.FromSeconds(1) && api.Disposals == 1 && api.Disconnects > 0,
            "Shutdown did not cancel/release an automatic pending connection exactly once.");
        api.Connection("private-test-id", true);
        Assert(One(adapter).ConnectionState != ToyConnectionState.Connected, "Late callback revived a closed remembered adapter.");

        var manualApi = new FakeBle { Status = 1 }; manualApi.OnScan = () => manualApi.Found();
        await using var manual = new RememberedToyAdapter(Adapter(manualApi, TimeSpan.FromMilliseconds(900)), store,
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10));
        await manual.InitializeAsync(); await Until(() => manualApi.Checks > 0);
        manualApi.Status = 0; await manual.StartScanAsync();
        await Task.Delay(260);
        Assert(manual.Snapshot.IsScanning && manualApi.Stops == 0 && manualApi.Connects == 0, "Automatic reconnect stopped/took over a user scan.");
        await manual.StopScanAsync();
    }

    private static async Task StableKeys()
    {
        var first = new FakeBle(); await using var a = Adapter(first);
        await a.StartScanAsync(); first.Found("private-test-id", "AA:BB:CC:DD:EE:01", "p");
        var key = One(a).DeviceKey;
        var serialized = JsonSerializer.Serialize(a.Snapshot);
        Assert(!serialized.Contains("private-test-id") && !serialized.Contains("AA:BB"), "Raw identifiers escaped into the snapshot.");
        var second = new FakeBle(); await using var b = Adapter(second);
        await b.StartScanAsync(); second.Found("different-private-test-id", "aa-bb-cc-dd-ee-01", "p");
        Assert(One(b).DeviceKey == key, "Same local address did not retain its key.");
        await using var c = Adapter(new FakeBle(), salt: 2);
        var other = new LocalToyKeys(Enumerable.Repeat((byte)2, 32).ToArray()).ForDevice("lovense-ble", "address:AABBCCDDEE01");
        Assert(other != key, "Different installations shared a hardware-derived key.");
        var path = Path.Combine(AppContext.BaseDirectory, "key-check-" + Guid.NewGuid().ToString("N") + ".bin");
        try
        {
            var storedA = LocalToyKeys.LoadOrCreate(path); var storedB = LocalToyKeys.LoadOrCreate(path);
            Assert(storedA.ForDevice("test", "private") == storedB.ForDevice("test", "private"), "Saved installation salt changed.");
        }
        finally { File.Delete(path); }
    }

    private static Task Capabilities()
    {
        Assert(LovenseModelCatalog.Identify("p0").Capabilities.Select(c => c.ChannelId).SequenceEqual(["v1", "v2"]), "Edge motors were collapsed or a master duplicated them.");
        Assert(LovenseModelCatalog.Identify("u").Capabilities.Count == 3, "Lapis did not have three independent motors.");
        var nora = LovenseModelCatalog.Identify("a");
        Assert(nora.Capabilities.Select(c => c.Type).SequenceEqual([ToyCapabilityType.Vibration, ToyCapabilityType.Rotation]), "Nora functions were collapsed.");
        Assert(nora.Capabilities.All(c => c.SupportsStop is null && c.SupportsStatus is null), "Unverified stop/status were advertised as supported.");
        Assert(nora.Capabilities[1].NativeMaximum == 20, "Documented rotation range is missing.");
        Assert(!LovenseModelCatalog.Identify("??").Known && LovenseModelCatalog.Identify("??").Capabilities.Count == 0, "Unknown model gained an actuator.");
        Assert(LovenseModelCatalog.Identify("s").Model == "Lush family", "Shared symbol incorrectly claimed a revision.");
        Assert(!LovenseModelCatalog.Identify("o").Capabilities.Any(c => c.Type == ToyCapabilityType.Oscillation), "Ambiguous Osci revision gained a function.");
        return Task.CompletedTask;
    }

    private static async Task RepeatScans()
    {
        var api = new FakeBle(); await using var adapter = Adapter(api);
        for (var i = 0; i < 3; i++)
        {
            await adapter.StartScanAsync(); await adapter.StartScanAsync();
            api.Found(); api.Found();
            Assert(adapter.Snapshot.Devices.Count == 1, "Duplicate discovery added extra devices.");
            await adapter.StopScanAsync(); await adapter.StopScanAsync();
            Assert(!adapter.Snapshot.IsScanning, "Stop did not end scanning.");
        }
        Assert(api.Starts == 3 && api.Stops == 3, "Repeat start/stop was not idempotent.");
    }

    private static async Task MissingBluetooth()
    {
        foreach (var status in new[] { 1, 2 })
        {
            var api = new FakeBle { Status = status }; await using var adapter = Adapter(api);
            await adapter.StartScanAsync();
            Assert(api.Starts == 0 && !adapter.Snapshot.IsScanning, "Scanning started without Bluetooth.");
            Assert(adapter.Snapshot.Availability == (status == 1 ? ToyAdapterAvailability.BluetoothOff : ToyAdapterAvailability.BluetoothUnavailable), "Wrong Bluetooth error.");
            api.Status = 0; await adapter.StartScanAsync(); Assert(api.Starts == 1, "Bluetooth recovery failed.");
        }
        await using var missing = new LovenseDiscoveryAdapter(() => throw new LovenseLibraryException("Library missing"), () => throw new Exception("Keys should not be created"));
        await missing.StartScanAsync(); Assert(missing.Snapshot.Availability == ToyAdapterAvailability.LibraryMissing, "Missing library not reported.");
    }

    private static async Task ScanCancellation()
    {
        var api = new FakeBle(); await using var adapter = Adapter(api); using var cancellation = new CancellationTokenSource();
        await adapter.StartScanAsync(cancellation.Token); api.Found(); cancellation.Cancel();
        await Until(() => !adapter.Snapshot.IsScanning);
        api.Found("late-private-test-id", "", "u"); Assert(adapter.Snapshot.Devices.Count == 1, "Canceled scan accepted a late toy.");
        await adapter.StartScanAsync(); api.Found(); Assert(api.Starts == 2, "Scan could not restart after cancellation.");
    }

    private static async Task DisappearingToys()
    {
        var api = new FakeBle(); await using var adapter = Adapter(api, TimeSpan.FromMilliseconds(60));
        await adapter.StartScanAsync(); api.Found(); var key = One(adapter).DeviceKey;
        await Until(() => !adapter.Snapshot.IsScanning);
        await adapter.StartScanAsync(); await Until(() => !adapter.Snapshot.IsScanning);
        Assert(One(adapter).ConnectionState == ToyConnectionState.Unavailable, "Missing toy stayed available after a completed scan.");
        await adapter.StartScanAsync(); api.Found(); await adapter.ConnectAsync(key);
        await adapter.StartScanAsync(); await Until(() => !adapter.Snapshot.IsScanning);
        Assert(One(adapter).ConnectionState == ToyConnectionState.Connected, "Connected toy was declared missing because it stopped advertising.");
        api.Connection("private-test-id", false); Assert(One(adapter).ConnectionState == ToyConnectionState.Disconnected, "Physical disconnect callback ignored.");
    }

    private static async Task ConnectAndBattery()
    {
        var api = new FakeBle(); await using var adapter = Adapter(api);
        await adapter.StartScanAsync(); api.Found(); var key = One(adapter).DeviceKey;
        Assert(One(adapter).BatteryPercent is null && api.Connects == 0 && api.BatteryReads == 0, "Discovery auto-connected or read a command.");
        await adapter.ConnectAsync(key);
        Assert(api.Stops == 1 && One(adapter).ConnectionState == ToyConnectionState.Connected, "Connect did not stop scanning first.");
        api.BatteryValue = "0"; await adapter.ReadBatteryAsync(key); Assert(One(adapter).BatteryPercent == 0, "Zero battery treated as unknown.");
        foreach (var invalid in new[] { "-1", "101", "unknown", "NaN", "0.5" })
        { api.BatteryValue = invalid; await adapter.ReadBatteryAsync(key); Assert(One(adapter).BatteryPercent is null, "Invalid battery accepted."); }
        api.BatteryValue = "87"; await adapter.ReadBatteryAsync(key); Assert(One(adapter).BatteryPercent == 87, "Valid battery not shown.");
        await adapter.DisconnectAsync(key); Assert(One(adapter).ConnectionState == ToyConnectionState.Disconnected && One(adapter).BatteryPercent is null, "Disconnect retained stale battery.");
        api.BatteryEvent("private-test-id", "90"); Assert(One(adapter).BatteryPercent is null, "Late battery callback revived disconnected data.");
        Assert(api.Commands.IsEmpty, "Discovery/connection/battery invoked an actuator.");
    }

    private static async Task ConnectionCancellation()
    {
        var api = new FakeBle { AutoConnect = false }; await using var adapter = Adapter(api);
        await adapter.StartScanAsync(); api.Found(); var key = One(adapter).DeviceKey;
        using var cancellation = new CancellationTokenSource();
        var pending = adapter.ConnectAsync(key, cancellation.Token); await Until(() => api.Connects == 1); cancellation.Cancel();
        try { await pending; throw new Exception("Canceled connection succeeded."); } catch (OperationCanceledException) { }
        Assert(api.Disconnects == 1 && One(adapter).ConnectionState == ToyConnectionState.Disconnected, "Canceled connection not cleaned up.");
        try { await adapter.ConnectAsync(key); throw new Exception("Unacknowledged connection succeeded."); } catch (InvalidOperationException) { }
        Assert(One(adapter).ConnectionState == ToyConnectionState.Disconnected, "Timed out connection stayed connecting.");
    }

    private static async Task BluetoothLoss()
    {
        var api = new FakeBle(); await using var adapter = Adapter(api);
        await adapter.StartScanAsync(); api.Found(); var key = One(adapter).DeviceKey;
        await adapter.ConnectAsync(key); await adapter.ReadBatteryAsync(key);
        api.Notify(10);
        Assert(One(adapter).ConnectionState == ToyConnectionState.Disconnected && One(adapter).BatteryPercent is null &&
            adapter.Snapshot.Availability == ToyAdapterAvailability.BluetoothUnavailable, "Bluetooth loss left stale connected status.");
        await adapter.StartScanAsync(); api.Found(); api.Notify(10);
        await Until(() => !adapter.Snapshot.IsScanning);
        Assert(api.Stops == 2, "Bluetooth loss did not stop active scanning.");
    }

    private static async Task StopTimeout()
    {
        var api = new FakeBle { ConfirmStop = false }; await using var adapter = Adapter(api);
        await adapter.StartScanAsync(); api.Found(); await adapter.StopScanAsync();
        Assert(api.Disposed && !adapter.Snapshot.IsScanning && adapter.Snapshot.Availability == ToyAdapterAvailability.Error, "Unconfirmed stop did not reset resources.");
    }

    private static async Task Cleanup()
    {
        var api = new FakeBle { ConfirmBluetooth = false }; var adapter = Adapter(api);
        var pending = adapter.StartScanAsync(); await Until(() => api.Checks == 1);
        await adapter.DisposeAsync(); try { await pending; } catch (OperationCanceledException) { }
        Assert(api.Disposed && adapter.Snapshot.Availability == ToyAdapterAvailability.Disposed, "Initialization cleanup failed.");
        await adapter.DisposeAsync(); Assert(api.Disposals == 1, "Cleanup was not idempotent.");
        api.Found(); Assert(adapter.Snapshot.Devices.Count == 0, "Disposed adapter accepted callbacks.");
        var otherApi = new FakeBle { AutoConnect = false }; var other = Adapter(otherApi);
        await other.StartScanAsync(); otherApi.Found(); pending = other.ConnectAsync(One(other).DeviceKey);
        await Until(() => otherApi.Connects == 1); await other.DisposeAsync();
        try { await pending; } catch (OperationCanceledException) { }
        Assert(otherApi.Disposed && !other.Snapshot.IsScanning, "Pending connection cleanup failed.");
    }

    private static async Task Simulation()
    {
        await using var adapter = new LovenseDiscoveryAdapter(() => new SimulatedLovenseBleApi(), () => new LocalToyKeys(new byte[32]), true,
            TimeSpan.FromMilliseconds(500), TimeSpan.FromMilliseconds(150));
        await adapter.StartScanAsync(); await Until(() => !adapter.Snapshot.IsScanning);
        Assert(adapter.Snapshot.Devices.Count == 4 && adapter.Snapshot.Devices.All(d => d.IsSimulated), "Simulated discovery incorrect.");
        await adapter.StartScanAsync(); await Until(() => !adapter.Snapshot.IsScanning);
        Assert(adapter.Snapshot.Devices.Single(d => !d.CapabilitiesKnown).ConnectionState == ToyConnectionState.Unavailable, "Simulated disappearance incorrect.");
        await using var off = new LovenseDiscoveryAdapter(() => new SimulatedLovenseBleApi(1), () => new LocalToyKeys(new byte[32]), true);
        await off.StartScanAsync(); Assert(off.Snapshot.Availability == ToyAdapterAvailability.BluetoothOff, "Simulated Bluetooth-off scenario incorrect.");
    }

    private static Task Configuration()
    {
        var legacy = BridgeConfig.Parse("""{"shockerId":"", "dryRun":true,"maxIntensity":37,"minCommandGapMs":1234}""");
        Assert(legacy.Shockers.Single().MaxIntensity == 37 && legacy.Shockers.Single().CooldownMs == 1234, "Legacy limits changed.");
        Assert(legacy.Clone().Shockers.Single().Key == "legacy-shocker" && legacy.Validate("") is null, "Legacy migration changed.");
        legacy.DryRun = false;
        Assert(legacy.Validate("") is null, "Local-only live mode required an OpenShock token.");
        legacy.Shockers.Single().Uuid = "11111111-1111-1111-1111-111111111111";
        Assert(legacy.Validate("") is not null && legacy.Validate("", requireToken: false) is null, "Backend-specific token validation was lost.");
        return Task.CompletedTask;
    }

    private static void UiCheck()
    {
        using var form = new Form { Size = new Size(790, 760), Font = new Font("Segoe UI", 10) };
        using var page = new LocalToysPage(); form.Controls.Add(page);
        _ = form.Handle; _ = page.Handle; form.PerformLayout();
        var source = page.SourceInput;
        source.SelectedIndex = 1;
        PumpUntil(() => source.Enabled && page.Snapshot?.Devices.Count == 4 &&
            page.Snapshot.Devices.All(device => device.ConnectionState == ToyConnectionState.Connected));
        PumpUntil(() => page.Cards.Count == 8);
        Assert(page.Cards.Count(card => card.ChannelId.Length > 0) == 7 && page.Cards.Count(card => card.ChannelId.Length == 0) == 1 &&
            !Descendants(page).OfType<DataGridView>().Any(),
            "Separate function cards are missing or the old inventory remains.");
        var card = page.Cards.First(candidate => candidate.ChannelId.Length > 0 &&
            page.Cards.Count(other => other.DeviceKey == candidate.DeviceKey) > 1);
        var sliders = Descendants(card).OfType<PercentSlider>().ToArray();
        PumpUntil(() => sliders[0].Enabled);
        Assert(sliders.Length == 3 && sliders[0].Enabled && sliders[0].Value == 0,
            "Input was unavailable or connection restored activity.");
        sliders[1].Value = 50;
        PumpUntil(() => Descendants(card).OfType<Label>().Any(label => label.Text.Contains("Preview 0%")));
        sliders[0].Value = 60;
        PumpUntil(() => Descendants(card).OfType<Label>().Any(label => label.Text.Contains("Preview 30%")));
        Assert(sliders[0].Value == 60 && sliders[1].Value == 50, "Input and sensitivity are not independent.");
        sliders[1].Value = 100; sliders[0].Value = 100;
        PumpUntil(() => Descendants(card).OfType<Label>().Any(label => label.Text.Contains("Preview 100%")));
        var maximum = sliders[2]; maximum.Value = 40;
        PumpUntil(() => Descendants(card).OfType<Label>().Any(label => label.Text.Contains("Preview 40%")));
        sliders[0].Value = 0;
        PumpUntil(() => Descendants(card).OfType<Label>().Any(label => label.Text.Contains("Preview 0%")));
        sliders[1].Value = 50; sliders[0].Value = 100;
        PumpUntil(() => Descendants(card).OfType<Label>().Any(label => label.Text.Contains("Preview 40%")));
        var sibling = page.Cards.First(other => other.DeviceKey == card.DeviceKey && other != card);
        var unrelated = page.Cards.First(other => other.DeviceKey != card.DeviceKey);
        var siblingInput = Descendants(sibling).OfType<PercentSlider>().First();
        var unrelatedInput = Descendants(unrelated).OfType<PercentSlider>().First();
        siblingInput.Value = 60; unrelatedInput.Value = 30;
        sliders[0].Value = 0;
        PumpUntil(() => sliders[0].Value == 0);
        Assert(siblingInput.Value == 60 && unrelatedInput.Value == 30, "Zero Input affected another function/toy.");
        sliders[0].Value = 100;
        ClickOffscreen(Descendants(card).OfType<Button>().Single(button => button.Text == "Stop toy"));
        PumpUntil(() => sliders[0].Value == 0 && siblingInput.Value == 0);
        Assert(unrelatedInput.Value == 30, "Stop toy stopped another toy.");
        ClickOffscreen(page.StopAllButton); PumpUntil(() => unrelatedInput.Value == 0);
        Assert(sliders[1].Value == 50 && maximum.Value == 40, "Stopping lost saved preferences.");
        card.Expanded = true;
        ClickOffscreen(Descendants(card).OfType<Button>().Single(button => button.Text == "Read battery"));
        PumpUntil(() => source.Enabled && page.Snapshot!.Devices.Single(device => device.DeviceKey == card.DeviceKey).BatteryPercent == 72);
        Assert(page.Cards.Where(other => other.DeviceKey == card.DeviceKey).All(other =>
            Descendants(other).OfType<Label>().Any(label => label.Text == "Battery 72%")),
            "Battery updates did not reach all sibling cards.");
        var forgetButton = Descendants(card).OfType<Button>().Single(button => button.Text == "Forget toy");
        Assert(!forgetButton.Enabled && page.Snapshot!.Devices.Single(device => device.DeviceKey == card.DeviceKey).IsRemembered,
            "Connected toy exposed Forget or had already lost its remembered state.");
        var rememberedAdapter = (IRememberedToyAdapter)typeof(LocalToysPage).GetField("adapter", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(page)!;
        var disconnect = rememberedAdapter.DisconnectAsync(card.DeviceKey);
        PumpUntil(() => disconnect.IsCompleted && forgetButton.Enabled); disconnect.GetAwaiter().GetResult();
        ClickOffscreen(forgetButton);
        PumpUntil(() => source.Enabled && !page.Snapshot!.Devices.Any(device => device.DeviceKey == card.DeviceKey));
        Assert(!page.Cards.Any(other => other.DeviceKey == card.DeviceKey), "Forget did not remove sibling cards.");
        form.Controls.Remove(page); page.Size = new Size(756, page.GridHeight); page.PerformLayout();
        using (var preview = new Bitmap(page.Width, page.Height))
        { page.DrawToBitmap(preview, page.ClientRectangle); preview.Save(Path.Combine(AppContext.BaseDirectory, "local-toys-preview.png")); }
        source.SelectedIndex = 2; PumpUntil(() => page.Cards.Count == 0 && source.Enabled);
        page.ToggleScan(); PumpUntil(() => source.Enabled && page.StatusMessage.Contains("Bluetooth is off"));
        var nestedRefreshes = 0;
        page.CardsLayoutChanged += () =>
        {
            if (nestedRefreshes++ > 0) return;
            typeof(LocalToysPage).GetMethod("RefreshInventory", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(page, null);
        };
        for (var cycle = 0; cycle < 8; cycle++)
        {
            source.SelectedIndex = 1;
            PumpUntil(() => source.Enabled && page.Snapshot?.Devices.Count == 4 &&
                page.Snapshot.Devices.All(device => device.ConnectionState == ToyConnectionState.Connected));
            page.ToggleScan(); PumpUntil(() => page.IsScanning && !page.Busy);
            page.ToggleScan(); PumpUntil(() => !page.IsScanning && !page.Busy);
            page.Size = new Size(cycle % 2 == 0 ? 506 : 756, page.GridHeight);
            source.SelectedIndex = 2; PumpUntil(() => source.Enabled && page.Cards.Count == 0);
        }
        var closing = page.ShutdownAsync(); PumpUntil(() => closing.IsCompleted); closing.GetAwaiter().GetResult();
        Assert(uiExceptions.Count == 0, "The WinForms UI reported an unhandled exception.");
    }
    private static void RememberedUiCheck()
    {
        var first = FixtureKey("AABBCCDDEE01"); var second = FixtureKey("AABBCCDDEE02");
        var store = new MemoryToyStore(); store.Save([new(first, "Edge", true, DateTimeOffset.UtcNow), new(second, "Edge", false, DateTimeOffset.UtcNow)]);
        var api = new FakeBle(); api.OnScan = () => { api.Found("first", "AA:BB:CC:DD:EE:01"); api.Found("second", "AA:BB:CC:DD:EE:02"); };
        using var page = new LocalToysPage(_ => Remembered(api, store)) { Size = new Size(756, 590) };
        page.CreateControl(); page.StartRememberedConnections();
        PumpUntil(() => page.Snapshot?.Devices.Count == 2 && page.Snapshot.Devices.All(device => device.ConnectionState == ToyConnectionState.Connected) && page.Cards.Count == 4);
        Assert(api.ConnectedIds.ToHashSet().SetEquals(["first", "second"]) && api.Commands.IsEmpty,
            "UI startup required saved flags or activated output.");
        Assert(page.Cards.Select(card => card.DeviceKey).Distinct().Count() == 2, "Identical toys were merged.");
        using (var preview = new Bitmap(page.Width, page.Height))
        { page.DrawToBitmap(preview, page.ClientRectangle); preview.Save(Path.Combine(AppContext.BaseDirectory, "remembered-toys-preview.png")); }
        var closing = page.ShutdownAsync(); PumpUntil(() => closing.IsCompleted); closing.GetAwaiter().GetResult();
        Assert(api.Disposed && uiExceptions.Count == 0, "Remembered toy UI leaked its adapter or raised a WinForms exception.");
    }
    private static void ClickOffscreen(Button button) => typeof(Button).GetMethod("OnClick",
        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(button, [EventArgs.Empty]);
    private static IEnumerable<Control> Descendants(Control control)
    { foreach (Control child in control.Controls) { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; } }
    private static void PumpUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (uiExceptions.Count > 0) throw new AggregateException("Unhandled WinForms exception", uiExceptions);
            if (DateTime.UtcNow > deadline) throw new Exception("Offscreen UI check timed out.");
            Application.DoEvents(); Thread.Sleep(5);
        }
        if (uiExceptions.Count > 0) throw new AggregateException("Unhandled WinForms exception", uiExceptions);
    }
}

internal sealed class FakeBle : ILovenseBleApi
{
    public int Status, Starts, Stops, Checks, Connects, Disconnects, BatteryReads, Disposals;
    public bool AutoConnect = true, ConfirmStop = true, ConfirmBluetooth = true, Disposed;
    public string BatteryValue = "72";
    public bool ConfirmBattery = true;
    public Action<string>? OnBatteryRead;
    public readonly System.Collections.Concurrent.ConcurrentQueue<string> BatteryReadIds = new();
    public Action? OnScan;
    public bool FailOutput;
    public bool RequireLostSessionRelease;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> liveSessions = new(), staleSessions = new();
    public Action<int>? OnOutput;
    public Action<string, int, int>? OnWrite;
    public Action? BeforeGuardedOutput;
    public readonly System.Collections.Concurrent.ConcurrentQueue<(string Id, int Command, int Value, long Time)> Commands = new();
    public readonly System.Collections.Concurrent.ConcurrentQueue<string> ConnectedIds = new();
    public event Action<int>? BluetoothStatus;
    public event Action<string, string, string>? ToyFound;
    public event Action<string, bool>? ConnectionChanged;
    public event Action<string, string>? Battery;
    public event Action<int, string>? Notification;
    public void CheckBluetooth() { Checks++; if (ConfirmBluetooth) BluetoothStatus?.Invoke(Status); }
    public void StartScan() { Starts++; Notification?.Invoke(0, ""); if (!RequireLostSessionRelease || staleSessions.IsEmpty) OnScan?.Invoke(); }
    public void StopScan() { Stops++; if (ConfirmStop) Notification?.Invoke(1, ""); }
    public void Connect(string id) { Connects++; ConnectedIds.Enqueue(id); if (RequireLostSessionRelease && staleSessions.ContainsKey(id)) Notification?.Invoke(8, id); else if (AutoConnect) Connection(id, true); }
    public void Disconnect(string id) { Disconnects++; Connection(id, false); staleSessions.TryRemove(id, out _); }
    public void ReadBattery(string id)
    {
        Interlocked.Increment(ref BatteryReads); BatteryReadIds.Enqueue(id);
        OnBatteryRead?.Invoke(id);
        if (ConfirmBattery) BatteryEvent(id, BatteryValue);
    }
    public void SendLevel(string id, int command, int value)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        Commands.Enqueue((id, command, value, System.Diagnostics.Stopwatch.GetTimestamp()));
        OnWrite?.Invoke(id, command, value);
        OnOutput?.Invoke(value);
        if (FailOutput) Notification?.Invoke(3, id);
    }
    public bool TrySendLevel(string id, int command, int value, Func<bool> isCurrent)
    {
        BeforeGuardedOutput?.Invoke();
        if (!isCurrent()) return false;
        SendLevel(id, command, value);
        return true;
    }
    public void Found(string id = "private-test-id", string address = "AA:BB:CC:DD:EE:01", string symbol = "p") => ToyFound?.Invoke(id, address, symbol);
    public void Connection(string id, bool connected)
    {
        if (connected) liveSessions[id] = true;
        else if (liveSessions.TryRemove(id, out _) && RequireLostSessionRelease) staleSessions[id] = true;
        ConnectionChanged?.Invoke(id, connected);
    }
    public void BatteryEvent(string id, string value) => Battery?.Invoke(id, value);
    public void Notify(int code, string id = "") => Notification?.Invoke(code, id);
    public void Dispose() { Disposed = true; Disposals++; }
}

internal sealed class FailingToyStore : IRememberedToyStore
{
    public bool FailLoad;
    public int Saves;
    public IReadOnlyList<RememberedToy> Load() => FailLoad ? throw new InvalidDataException("fixture corrupt file") : Array.Empty<RememberedToy>();
    public void Save(IReadOnlyList<RememberedToy> toys) { Saves++; throw new IOException("fixture write denied"); }
}
