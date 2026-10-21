using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using ShockWave.Overlay.Toys;

internal static partial class Program
{
    private static void ButtplugChecks()
    {
        Run("Buttplug scan preserves active motors and accepts fresh input/Stop", ButtplugScanning);
        Run("Buttplug automatic recovery scans during another toy's held input", ButtplugRecovery);
        Run("Buttplug stable identities survive index changes and identical models", ButtplugIdentity);
        Run("Buttplug disconnect/reconnect clears input without disturbing siblings", ButtplugReconnect);
        Run("Buttplug pump/rotation mapping and native bounds are independent", ButtplugFunctions);
        Run("Buttplug unknown functions and missing identities remain unavailable", ButtplugUnknown);
        Run("Buttplug battery during scans, five-minute schedule and unknown values", ButtplugBattery);
        Run("Buttplug engine loss disarms and cleanup rejects replay", ButtplugLoss);
        Run("Buttplug actual cards remain usable and explicit Stop scan pauses recovery", ButtplugPage);
        Run("Buttplug Forget prevents reconnect from exposing fresh controls", ButtplugForget);
        Run("Buttplug connection saves cannot overwrite a concurrent Forget", ButtplugStorageRace);
        Run("Buttplug scanning retains Test mode, owner caps and fresh-input requirements", ButtplugLimits);
    }
    private static ButtplugToyAdapter ButtAdapter(FakeButtplug session) => new(
        () => new LocalToyKeys(Enumerable.Repeat((byte)1, 32).ToArray()), () => session, TimeSpan.FromSeconds(30));
    private static async Task ButtUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        while (!condition()) await Task.Delay(5, timeout.Token);
    }
    private static async Task ButtplugScanning()
    {
        var fake = new FakeButtplug(); await using var adapter = ButtAdapter(fake);
        await adapter.StartScanAsync(); fake.Add(0, "AA:BB:CC:DD:EE:01"); await Until(() => adapter.Snapshot.Devices.Count == 1);
        var key = One(adapter).DeviceKey; await using var controller = new LocalToyController(adapter, new(), automaticControls: true);
        controller.SetTestMode(false); controller.Request(key, "v1", 40); controller.Request(key, "v2", 65);
        await Until(() => fake.Levels.Count == 2);
        Assert(adapter.Snapshot.IsScanning && controller.State(key, "v1").Armed && fake.Levels.Any(level => level.Index == 1 && level.Value == .65), "Discovery blocked connected independent motors.");
        await adapter.StopScanAsync(); await adapter.StartScanAsync(); await Task.Delay(80);
        Assert(controller.State(key, "v1").Active && controller.State(key, "v1").RequestedPercent == 40 && fake.Levels.Count == 2, "Rescan stopped/repeated a held motor.");
        controller.Request(key, "v1", 70); await Until(() => fake.Levels.Count == 3);
        controller.Request(key, "v1", 0); await Until(() => fake.Levels.Any(level => level.Index == 0 && level.Value == 0));
        Assert(controller.State(key, "v2").Active && adapter.Snapshot.IsScanning, "Stop affected a sibling motor or stopped discovery.");
    }
    private static async Task ButtplugRecovery()
    {
        var fake = new FakeButtplug(); var store = new MemoryToyStore();
        store.Save([new(FixtureKey("AABBCCDDEE02"), "Hush", true, DateTimeOffset.UtcNow)]);
        fake.OnScan = () => { if (fake.Scans == 1) fake.Add(0, "AA:BB:CC:DD:EE:01"); else fake.Add(1, "AA:BB:CC:DD:EE:02", "Lovense Hush", ["Vibrate"]); };
        await using var adapter = new RememberedToyAdapter(ButtAdapter(fake), store, TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(10));
        await adapter.EnableAutomaticConnectionsAsync(canScan: () => adapter.Snapshot.SupportsOutputWhileScanning);
        await ButtUntil(() => adapter.Snapshot.Devices.Any(device => device.DeviceKey == FixtureKey("AABBCCDDEE01") && device.ConnectionState == ToyConnectionState.Connected));
        await using var controller = new LocalToyController(adapter, new(), automaticControls: true); controller.SetTestMode(false);
        controller.Request(FixtureKey("AABBCCDDEE01"), "v1", 55); await Until(() => fake.Levels.Count == 1);
        await ButtUntil(() => fake.Scans >= 2 && adapter.Snapshot.Devices.Count(device => device.ConnectionState == ToyConnectionState.Connected) == 2);
        Assert(controller.State(FixtureKey("AABBCCDDEE01"), "v1").Active && fake.Levels.Count == 1, "Automatic recovery canceled live input or replayed it.");
        Assert(store.Load().Count == 2 && adapter.Snapshot.Devices.All(device => device.IsRemembered), "Engine-connected toys were not remembered independently.");
    }
    private static async Task ButtplugIdentity()
    {
        string first;
        var firstSession = new FakeButtplug();
        await using (var adapter = ButtAdapter(firstSession))
        {
            await adapter.StartScanAsync();
            firstSession.Add(7, "PeripheralId(\"BluetoothLE#BluetoothLE11:22:33:44:55:66-AA:BB:CC:DD:EE:01\")");
            await Until(() => adapter.Snapshot.Devices.Count == 1); first = One(adapter).DeviceKey;
        }
        var second = new FakeButtplug(); await using var restarted = ButtAdapter(second); await restarted.StartScanAsync();
        second.Add(91, "AA:BB:CC:DD:EE:01"); second.Add(92, "AA:BB:CC:DD:EE:02"); await Until(() => restarted.Snapshot.Devices.Count == 2);
        Assert(first == FixtureKey("AABBCCDDEE01") && restarted.Snapshot.Devices.Any(device => device.DeviceKey == first) && restarted.Snapshot.Devices.Select(device => device.DeviceKey).Distinct().Count() == 2,
            "Identity used a session index/model or the adapter's MAC instead of the target MAC.");
        Assert(!JsonSerializer.Serialize(restarted.Snapshot).Contains("AA:BB"), "Private engine address leaked into public inventory.");
        Assert(restarted.Snapshot.Devices.All(device=>device.DisplayName==device.Model && !device.DisplayName.Contains(" · ")),"Generated four-character suffix remained in the visible name.");
    }
    private static async Task ButtplugReconnect()
    {
        var fake = new FakeButtplug(); await using var adapter = ButtAdapter(fake); await adapter.StartScanAsync();
        fake.Add(0, "AA:BB:CC:DD:EE:01"); fake.Add(1, "AA:BB:CC:DD:EE:02"); await Until(() => adapter.Snapshot.Devices.Count == 2);
        await using var controller = new LocalToyController(adapter, new(), automaticControls: true); controller.SetTestMode(false);
        var first = FixtureKey("AABBCCDDEE01"); var second = FixtureKey("AABBCCDDEE02");
        controller.Request(first, "v1", 40); controller.Request(second, "v1", 60); await Until(() => fake.Levels.Count == 2);
        fake.Remove(0); await Until(() => !controller.State(first, "v1").Armed); fake.Add(9, "AA:BB:CC:DD:EE:01");
        try { await Until(() => controller.State(first, "v1").Armed); }
        catch (OperationCanceledException) { throw new Exception("Reconnect fixture: " + controller.State(first, "v1").Detail + " / " + adapter.Snapshot.Devices.Single(device => device.DeviceKey == first).ConnectionState); }
        await Task.Delay(100);
        Assert(controller.State(first, "v1").RequestedPercent == 0 && controller.State(second, "v1").Active && fake.Levels.Count(level => level.Value > 0) == 2,
            "Reconnect replayed input or stopped an unrelated toy.");
    }
    private static async Task ButtplugFunctions()
    {
        var fake = new FakeButtplug(); await using var adapter = ButtAdapter(fake); await adapter.StartScanAsync();
        fake.Add(0, "AA:BB:CC:DD:EE:01", "Lovense Max", ["Vibrate", "Constrict"]);
        fake.Add(1, "AA:BB:CC:DD:EE:02", "Lovense Nora", ["Vibrate"], rotation: true); await Until(() => adapter.Snapshot.Devices.Count == 2);
        var max = adapter.Snapshot.Devices.Single(device => device.Model == "Max"); var nora = adapter.Snapshot.Devices.Single(device => device.Model == "Nora");
        adapter.SendLevel(max.DeviceKey, "p", 2); adapter.SendLevel(nora.DeviceKey, "r", 10); adapter.SendLevel(max.DeviceKey, "v", 10);
        Assert(max.Capabilities.Single(cap => cap.ChannelId == "p").NativeMaximum == 3 && fake.Levels.Any(level => level.Actuator == "Constrict" && level.Value == 2 / 3d) &&
            fake.Levels.Any(level => level.Kind == "RotateCmd" && level.Value == .5), "Pump/rotation were mapped to vibration or the wrong scale.");
        try { adapter.SendLevel(max.DeviceKey, "p", 4); throw new Exception("Out-of-range pump accepted."); } catch (ArgumentOutOfRangeException) { }
        Assert(!adapter.TrySendLevel(max.DeviceKey, "v", 20, () => false) && fake.Levels.Count == 3, "Superseded input crossed the final guard.");
    }
    private static async Task ButtplugUnknown()
    {
        var fake = new FakeButtplug(); await using var adapter = ButtAdapter(fake); await adapter.StartScanAsync();
        fake.Add(0, "AA:BB:CC:DD:EE:01", "Lovense Unknown", ["Position"]); fake.Add(1, null); fake.Add(2, "AA:BB:CC:DD:EE:02", "Another brand");
        await Until(() => adapter.Snapshot.Devices.Count == 1); await Task.Delay(80);
        Assert(!One(adapter).CapabilitiesKnown && One(adapter).Capabilities.Count == 0 && fake.Levels.IsEmpty,
            "Unknown motion, missing private identity or an unrelated brand gained actuator controls.");
    }
    private static async Task ButtplugBattery()
    {
        var fake = new FakeButtplug(); var clock = new BatteryFixtureTime();
        var store = new MemoryToyStore(); var key = FixtureKey("AABBCCDDEE01");
        store.Save([new(FixtureKey("AABBCCDDEE02"), "Hush", true, DateTimeOffset.UtcNow)]);
        fake.OnScan = () => fake.Add(0, "AA:BB:CC:DD:EE:01");
        await using var adapter = new RememberedToyAdapter(ButtAdapter(fake), store, pollInterval: TimeSpan.FromMilliseconds(10), batteryTime: clock);
        await adapter.EnableAutomaticConnectionsAsync();
        await Until(() => adapter.Snapshot.Devices.Any(device => device.DeviceKey == key && device.ConnectionState == ToyConnectionState.Connected));
        async Task ReadDuringDiscovery(int expected, int? reading)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { while (fake.BatteryReads != expected || adapter.Snapshot.Devices.Single(device => device.DeviceKey == key).BatteryPercent != reading)
                    await Task.Delay(10, deadline.Token); }
            catch (OperationCanceledException) { throw new Exception("Automatic battery waited behind a missing-toy scan instead of reading during discovery."); }
        }
        await ReadDuringDiscovery(1, 72);
        Assert(adapter.Snapshot.Devices.Single(device => device.DeviceKey == key).BatteryPercent == 72, "Automatic connection reading did not publish battery.");
        Assert(adapter.Snapshot.IsScanning && fake.Levels.IsEmpty, "Connection battery read blocked scanning or activated a motor.");
        fake.BatteryValue = 0; clock.Advance(TimeSpan.FromMinutes(5)); await ReadDuringDiscovery(2, 0);
        Assert(adapter.Snapshot.Devices.Single(device => device.DeviceKey == key).BatteryPercent == 0 && adapter.Snapshot.IsScanning,
            "Five-minute battery refresh waited for automatic discovery to finish or lost a genuine zero reading.");
        fake.BatteryValue = 105; clock.Advance(TimeSpan.FromMinutes(5)); await ReadDuringDiscovery(3, null);
        Assert(adapter.Snapshot.Devices.Single(device => device.DeviceKey == key).BatteryPercent is null, "Invalid battery fabricated a percentage.");
        await Task.Delay(70); Assert(fake.BatteryReads == 3, "Unknown battery caused rapid retries or a fabricated value.");
    }
    private static async Task ButtplugLoss()
    {
        var fake = new FakeButtplug(); var adapter = ButtAdapter(fake); await adapter.StartScanAsync(); fake.Add(0, "AA:BB:CC:DD:EE:01"); await Until(() => adapter.Snapshot.Devices.Count == 1);
        await using var controller = new LocalToyController(adapter, new(), automaticControls: true); controller.SetTestMode(false); var key = One(adapter).DeviceKey;
        controller.Request(key, "v1", 40); await Until(() => fake.Levels.Count == 1); fake.Lose();
        Assert(!controller.State(key, "v1").Armed && controller.State(key, "v1").RequestedPercent == 0 && !adapter.Snapshot.IsScanning && One(adapter).BatteryPercent is null,
            "Engine loss preserved active intent or stale battery.");
        await adapter.CloseTransportForExitAsync(); await adapter.DisposeAsync(); await adapter.DisposeAsync();
        Assert(fake.Disposals == 1, "Engine cleanup was not idempotent.");
        try { adapter.SendLevel(key, "v1", 20); throw new Exception("Closed engine accepted replay."); } catch (Exception error) when (error is OperationCanceledException or ObjectDisposedException or InvalidOperationException) { }
        Assert(fake.Levels.Count(level => level.Value > 0) == 1, "Cleanup resubmitted positive input.");
    }
    private static async Task ButtplugEngineCheck(string executable)
    {
        var session = new ButtplugEngineSession(bluetooth: false, executable: executable); var id = 0;
        try
        {
            using var bounded = new CancellationTokenSource(TimeSpan.FromSeconds(12)); await session.InitializeAsync(bounded.Token); id = session.ProcessId;
            var devices = await session.RequestAsync("RequestDeviceList", new { }, bounded.Token);
            Assert(devices.GetProperty("Devices").GetArrayLength() == 0, "Disabled hardware smoke check discovered a real device.");
            await session.RequestAsync("StartScanning", new { }, bounded.Token); await session.RequestAsync("StopScanning", new { }, bounded.Token);
            await session.RequestAsync("Ping", new { }, bounded.Token); await Task.Delay(1100, bounded.Token);
            await session.RequestAsync("Ping", new { }, bounded.Token);
            var canceled = await session.RequestAsync("ScalarCmd", new { DeviceIndex = 999, Scalars = new[] { new { Index = 0, Scalar = 1d, ActuatorType = "Vibrate" } } }, bounded.Token, () => false);
            Assert(canceled.ValueKind == JsonValueKind.Undefined, "Real transport sent a superseded output command.");
        }
        finally { var elapsed = Stopwatch.StartNew(); await session.DisposeAsync(); Assert(elapsed.Elapsed < TimeSpan.FromSeconds(2) && (id == 0 || Exited(id)), "Owned engine did not exit within its cleanup bound."); }
        await session.DisposeAsync();
        var info = LovenseWorkerApi.StartInfo("--engine-owner-fixture"); info.ArgumentList.Add(executable);
        using var owner = new Process { StartInfo = info }; owner.Start();
        try
        {
            var engineId = int.Parse((await owner.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)))!);
            owner.Kill(); await owner.WaitForExitAsync(); await Until(() => Exited(engineId));
        }
        finally { if (!owner.HasExited) owner.Kill(); }
    }
    private static int EngineOwnerFixture(string executable)
    {
        var session = new ButtplugEngineSession(false, executable); session.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
        using var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true }; writer.WriteLine(session.ProcessId);
        Thread.Sleep(Timeout.Infinite); return 0;
    }
    private static async Task ButtplugForget()
    {
        var fake = new FakeButtplug(); var store = new MemoryToyStore();
        var oldKey = FixtureKey("AABBCCDDEE03"); var key = FixtureKey("AABBCCDDEE01"); var sibling = FixtureKey("AABBCCDDEE02");
        store.Save([new(oldKey, "Diamo", true, DateTimeOffset.UtcNow)]);
        await using var adapter = new RememberedToyAdapter(ButtAdapter(fake), store);
        await adapter.StartScanAsync(); fake.Add(0, "AA:BB:CC:DD:EE:01", "Lovense Hush", ["Vibrate"]);
        fake.Add(1, "AA:BB:CC:DD:EE:02", "Lovense Hush", ["Vibrate"]);
        await Until(() => adapter.Snapshot.Devices.Count == 3 && store.Load().Count == 3);
        try { await adapter.ForgetAsync(key); throw new Exception("Connected Buttplug toy could be forgotten."); } catch (InvalidOperationException) { }
        Assert(adapter.Snapshot.Devices.Single(device => device.DeviceKey == key).ConnectionState == ToyConnectionState.Connected &&
            store.Load().Count == 3 && adapter.Snapshot.Devices.Single(device => device.DeviceKey == key).IsRemembered,
            "Rejected connected Forget changed the saved toy or connection.");
        await adapter.ForgetAsync(oldKey);
        Assert(!adapter.Snapshot.Devices.Any(device => device.DeviceKey == oldKey), "Old offline toy did not disappear after Forget.");
        fake.Remove(0); await adapter.ForgetAsync(key);
        Assert(!adapter.Snapshot.Devices.Any(device => device.DeviceKey == key) && store.Load().Single().DeviceKey == sibling,
            "A newly connected Hush remained in inventory after offline Forget, or another identical toy was removed.");
        fake.Add(9, "AA:BB:CC:DD:EE:01"); await Task.Delay(150);
        Assert(adapter.Snapshot.Devices.Count == 1 && adapter.Snapshot.Devices.Single().DeviceKey == sibling &&
            adapter.Snapshot.Devices.Single().ConnectionState == ToyConnectionState.Connected && store.Load().Single().DeviceKey == sibling && fake.Levels.IsEmpty,
            "Engine rediscovery restored a forgotten toy's app controls.");
    }
    private static Task ButtplugPage()
    {
        var fake = new FakeButtplug(); fake.OnScan = () => fake.Add(0, "AA:BB:CC:DD:EE:01");
        var store = new MemoryToyStore();
        var remembered = new RememberedToyAdapter(ButtAdapter(fake), store, TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(10));
        using var page = new ShockWave.Overlay.LocalToysPage(_ => remembered) { Size = new Size(756, 590) }; _ = page.Handle;
        page.StartRememberedConnections(); PumpUntil(() => !page.Busy && page.IsScanning && page.Cards.Count == 2);
        page.SetTestMode(false);
        var input = (ShockWave.Overlay.PercentSlider)typeof(ShockWave.Overlay.ToyFunctionRow).GetField("input", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(page.Cards[0])!;
        Assert(input.Enabled, "Actual connected card was disabled by a Buttplug scan."); input.Value = 40;
        Assert(page.Cards.All(card => !Descendants(card).OfType<Button>().Single(button => button.Text == "Forget toy").Enabled),
            "A connected Buttplug motor card exposed Forget.");
        var card = page.Cards[0]; var key = card.DeviceKey; card.Expanded = true;
        using (var image = new Bitmap(page.Width, page.Height))
        { page.DrawToBitmap(image, page.ClientRectangle); image.Save(Path.Combine(AppContext.BaseDirectory, "buttplug-forget-disabled.png")); }
        PumpUntil(() => fake.Levels.Any(level => level.Value == .4));
        page.ToggleScan(); PumpUntil(() => !page.Busy && !page.IsScanning); var scans = fake.Scans;
        var until = DateTime.UtcNow.AddMilliseconds(1200); PumpUntil(() => DateTime.UtcNow >= until);
        Assert(fake.Scans == scans && page.Controller!.State(page.Cards[0].DeviceKey, page.Cards[0].ChannelId).Active,
            "Explicit Stop scan was undone or canceled active output.");
        fake.Add(1, "AA:BB:CC:DD:EE:02"); PumpUntil(() => !page.Busy && page.Cards.Count == 4 && store.Load().Count == 2);
        fake.Remove(0);
        var forget = Descendants(card).OfType<Button>().Single(button => button.Text == "Forget toy");
        PumpUntil(() => forget.Enabled);
        using (var image = new Bitmap(page.Width, page.Height))
        { page.DrawToBitmap(image, page.ClientRectangle); image.Save(Path.Combine(AppContext.BaseDirectory, "buttplug-forget-enabled.png")); }
        ClickOffscreen(forget);
        PumpUntil(() => !page.Busy && page.Cards.Count == 2 && page.Cards.All(other => other.DeviceKey != key));
        Assert(store.Load().Count == 1 && page.Cards.All(other => other.DeviceKey != key), "Forget retained a motor card or deleted another toy.");
        fake.Add(9, "AA:BB:CC:DD:EE:01");
        var ignoredUntil = DateTime.UtcNow.AddMilliseconds(200); PumpUntil(() => DateTime.UtcNow >= ignoredUntil);
        Assert(page.Cards.Count == 2 && page.Cards.All(other => other.DeviceKey != key), "A rediscovered forgotten toy returned to the page.");
        var shutdown = page.ShutdownAsync(); PumpUntil(() => shutdown.IsCompleted); shutdown.GetAwaiter().GetResult();
        Assert(uiExceptions.Count == 0 && fake.Disposals == 1, "New backend page cleanup caused an exception or leaked its engine.");
        return Task.CompletedTask;
    }
    private static async Task ButtplugStorageRace()
    {
        var fake = new FakeButtplug(); var store = new BlockingRememberedStore();
        await using var adapter = new RememberedToyAdapter(ButtAdapter(fake), store);
        await adapter.StartScanAsync(); fake.Add(0, "AA:BB:CC:DD:EE:01"); await Until(() => store.Load().Count == 1);
        fake.Remove(0);
        fake.Add(1, "AA:BB:CC:DD:EE:02"); await Until(() => store.Entered.IsSet);
        var forgetting = adapter.ForgetAsync(FixtureKey("AABBCCDDEE01"));
        try { await Task.Delay(60); Assert(!forgetting.IsCompleted, "Concurrent persistence skipped its transaction boundary."); }
        finally { store.Release.Set(); }
        await forgetting;
        Assert(store.Load().Count == 1 && store.Load()[0].DeviceKey == FixtureKey("AABBCCDDEE02"), "A connection save restored a forgotten toy or lost the other connection.");
    }
    private static async Task ButtplugLimits()
    {
        var fake = new FakeButtplug(); await using var adapter = ButtAdapter(fake); await adapter.StartScanAsync(); fake.Add(0, "AA:BB:CC:DD:EE:01"); await Until(() => adapter.Snapshot.Devices.Count == 1);
        var key = One(adapter).DeviceKey; await using var controller = new LocalToyController(adapter, new(), automaticControls: true);
        controller.SetPreference(new(key, "v1", 50, 30)); controller.Request(key, "v1", 100); await Task.Delay(60);
        Assert(fake.Levels.IsEmpty && controller.State(key, "v1").SentPercent == 30, "Concurrent scan bypassed Test mode or its preview cap.");
        controller.SetTestMode(false); await Task.Delay(40);
        Assert(controller.State(key, "v1").RequestedPercent == 0 && fake.Levels.IsEmpty, "Test-mode transition replayed preview input.");
        controller.Request(key, "v1", 100); await Until(() => fake.Levels.Count == 1);
        Assert(fake.Levels.Single().Value == .3, "New backend bypassed sensitivity/maximum or native quantization.");
        controller.Stop(); await Until(() => fake.Levels.Last().Value == 0);
    }
}

internal sealed class BlockingRememberedStore : IRememberedToyStore
{
    private readonly MemoryToyStore inner = new();
    public readonly ManualResetEventSlim Entered = new(false), Release = new(false);
    public IReadOnlyList<RememberedToy> Load() => inner.Load();
    public void Save(IReadOnlyList<RememberedToy> value)
    {
        if (value.Count == 2 && !Entered.IsSet) { Entered.Set(); if (!Release.Wait(3000)) throw new IOException("Fixture save deadline."); }
        inner.Save(value);
    }
}

internal sealed class FakeButtplug : IButtplugSession
{
    public event Action<string, JsonElement>? Message;
    public event Action? Lost;
    public readonly ConcurrentQueue<(string Kind, uint Device, int Index, string Actuator, double Value)> Levels = new();
    private readonly ConcurrentDictionary<uint, string> identities = new();
    public int Scans, BatteryReads, Disposals, BatteryValue = 72;
    public Action? OnScan;
    private int disposed;
    public string? Identity(uint index) => identities.GetValueOrDefault(index);
    public Task InitializeAsync(CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
    public void Add(uint index, string? address, string name = "Lovense Edge", string[]? actuators = null, bool rotation = false)
    {
        if (address is not null) identities[index] = address;
        var messages = new Dictionary<string, object>
        {
            ["ScalarCmd"] = (actuators ?? ["Vibrate", "Vibrate"]).Select(type => new { ActuatorType = type, StepCount = type == "Constrict" ? 3 : 20 }).ToArray(),
            ["SensorReadCmd"] = new[] { new { SensorType = "Battery", SensorRange = new[] { new[] { 0, 100 } } } },
        };
        if (rotation) messages["RotateCmd"] = new[] { new { ActuatorType = "Rotate", StepCount = 20 } };
        Message?.Invoke("DeviceAdded", JsonSerializer.SerializeToElement(new { Id = 0, DeviceIndex = index, DeviceName = name, DeviceMessages = messages }));
    }
    public void Remove(uint index) => Message?.Invoke("DeviceRemoved", JsonSerializer.SerializeToElement(new { Id = 0, DeviceIndex = index }));
    public void Lose() => Lost?.Invoke();
    public Task<JsonElement> RequestAsync(string kind, object fields, CancellationToken token, Func<bool>? isCurrent = null)
    {
        token.ThrowIfCancellationRequested(); ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (isCurrent is not null && !isCurrent()) return Task.FromResult(default(JsonElement));
        var payload = JsonSerializer.SerializeToElement(fields);
        if (kind == "StartScanning") { Interlocked.Increment(ref Scans); OnScan?.Invoke(); }
        if (kind is "ScalarCmd" or "RotateCmd")
            foreach (var value in payload.GetProperty(kind == "ScalarCmd" ? "Scalars" : "Rotations").EnumerateArray())
                Levels.Enqueue((kind, payload.GetProperty("DeviceIndex").GetUInt32(), value.GetProperty("Index").GetInt32(),
                    kind == "ScalarCmd" ? value.GetProperty("ActuatorType").GetString()! : "Rotate", value.GetProperty(kind == "ScalarCmd" ? "Scalar" : "Speed").GetDouble()));
        if (kind == "SensorReadCmd")
        { Interlocked.Increment(ref BatteryReads); return Task.FromResult(JsonSerializer.SerializeToElement(new { DeviceIndex = payload.GetProperty("DeviceIndex").GetUInt32(), SensorIndex = payload.GetProperty("SensorIndex").GetInt32(), SensorType = "Battery", Data = new[] { BatteryValue } })); }
        return Task.FromResult(kind == "RequestDeviceList" ? JsonSerializer.SerializeToElement(new { Devices = Array.Empty<object>() }) : JsonSerializer.SerializeToElement(new { Id = 1 }));
    }
    public ValueTask DisposeAsync() { if (Interlocked.Exchange(ref disposed, 1) == 0) Interlocked.Increment(ref Disposals); return ValueTask.CompletedTask; }
}
