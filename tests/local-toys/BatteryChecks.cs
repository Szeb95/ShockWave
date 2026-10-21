using ShockWave.Overlay.Toys;

internal static partial class Program
{
    private static void BatteryChecks()
    {
        Run("battery reads once after connection and at five-minute boundaries", BatteryCadence);
        Run("battery reconnect uses a fresh request and cancels the offline schedule", BatteryReconnect);
        Run("identical multi-motor toys have independent battery schedules", BatteryPerToy);
        Run("automatic battery reads defer without stopping a scan", BatteryScanDeferral);
        Run("manual battery refresh resets the automatic interval", BatteryManualRefresh);
        Run("unknown and failed battery reads wait five minutes before retry", BatteryFailureCadence);
        Run("battery timeout and pending shutdown remain bounded", BatteryTimeoutAndShutdown);
        Run("battery refresh preserves active input and owner Stop", BatteryPreservesOutput);
    }

    private static RememberedToyAdapter BatteryAdapter(FakeBle api, BatteryFixtureTime clock) =>
        new(Adapter(api), new MemoryToyStore(), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(10), clock);

    private static bool OneConnectedBattery(ILocalToyAdapter adapter) => adapter.Snapshot.Devices.Any(device =>
        device.ConnectionState == ToyConnectionState.Connected && device.BatteryPercent == 72);

    private static async Task BatteryConnect(RememberedToyAdapter adapter, FakeBle api, string id = "battery-first")
    {
        await adapter.StartScanAsync(); api.Found(id); await adapter.StopScanAsync();
        await adapter.ConnectAsync(FixtureKey("AABBCCDDEE01"));
        await Until(() => api.BatteryReads >= 1);
    }

    private static async Task BatteryCadence()
    {
        var clock = new BatteryFixtureTime(); var api = new FakeBle();
        await using var adapter = BatteryAdapter(api, clock);
        await adapter.InitializeAsync(); await Task.Delay(40);
        Assert(api.BatteryReads == 0, "An offline/empty inventory triggered a battery read.");
        await BatteryConnect(adapter, api);
        await Until(() => OneConnectedBattery(adapter));
        api.Connection("battery-first", true); api.BatteryEvent("battery-first", "72");
        await Task.Delay(60);
        Assert(api.BatteryReads == 1, "A status/duplicate connected callback caused another connection battery read.");
        clock.Advance(TimeSpan.FromSeconds(299)); await Task.Delay(60);
        Assert(api.BatteryReads == 1, "Battery refreshed before five minutes.");
        clock.Advance(TimeSpan.FromSeconds(1)); await Until(() => api.BatteryReads == 2);
        clock.Advance(TimeSpan.FromMinutes(5)); await Until(() => api.BatteryReads == 3);
        await Task.Delay(60);
        Assert(api.BatteryReads == 3 && api.Commands.IsEmpty && One(adapter).Capabilities.Count == 2,
            "Periodic battery requests duplicated per motor or sent actuator output.");
    }

    private static async Task BatteryReconnect()
    {
        var clock = new BatteryFixtureTime(); var api = new FakeBle();
        await using var adapter = BatteryAdapter(api, clock); await BatteryConnect(adapter, api);
        var key = FixtureKey("AABBCCDDEE01");
        await adapter.DisconnectAsync(key);
        clock.Advance(TimeSpan.FromMinutes(10)); await Task.Delay(60);
        Assert(api.BatteryReads == 1 && One(adapter).BatteryPercent is null, "Offline battery state continued refreshing.");
        api.OnScan = () => api.Found("battery-fresh"); api.BatteryValue = "84";
        await adapter.ConnectAsync(key);
        await Until(() => api.BatteryReads == 2 && One(adapter).BatteryPercent == 84);
        Assert(api.BatteryReadIds.Last() == "battery-fresh", "Reconnect battery request used the old native identity.");
        clock.Advance(TimeSpan.FromSeconds(299)); await Task.Delay(60);
        Assert(api.BatteryReads == 2, "The old connection deadline survived reconnect.");
        clock.Advance(TimeSpan.FromSeconds(1)); await Until(() => api.BatteryReads == 3);
        Assert(api.Commands.IsEmpty, "Battery reconnect started an actuator.");
    }

    private static async Task BatteryPerToy()
    {
        var clock = new BatteryFixtureTime(); var api = new FakeBle();
        await using var adapter = BatteryAdapter(api, clock); await BatteryConnect(adapter, api);
        clock.Advance(TimeSpan.FromSeconds(30));
        await adapter.StartScanAsync(); api.Found("battery-second", "AA:BB:CC:DD:EE:02");
        await adapter.StopScanAsync(); await adapter.ConnectAsync(FixtureKey("AABBCCDDEE02"));
        await Until(() => api.BatteryReads == 2);
        Assert(adapter.Snapshot.Devices.Count == 2 && adapter.Snapshot.Devices.All(device => device.Capabilities.Count == 2),
            "Fixture did not retain two independent identical multi-motor toys.");
        clock.Advance(TimeSpan.FromSeconds(270)); await Until(() => api.BatteryReads == 3); await Task.Delay(60);
        Assert(api.BatteryReadIds.Count(id => id == "battery-first") == 2 &&
            api.BatteryReadIds.Count(id => id == "battery-second") == 1, "One toy reused another toy's battery deadline.");
        clock.Advance(TimeSpan.FromSeconds(30)); await Until(() => api.BatteryReads == 4);
        Assert(api.BatteryReadIds.Count(id => id == "battery-second") == 2 && api.Commands.IsEmpty,
            "Second toy's refresh duplicated a motor request or sent output.");
    }

    private static async Task BatteryScanDeferral()
    {
        var clock = new BatteryFixtureTime(); var api = new FakeBle();
        await using var adapter = BatteryAdapter(api, clock); await BatteryConnect(adapter, api);
        await adapter.StartScanAsync(); var stops = api.Stops;
        clock.Advance(TimeSpan.FromMinutes(5)); await Task.Delay(90);
        Assert(adapter.Snapshot.IsScanning && api.Stops == stops && api.BatteryReads == 1,
            "Automatic battery polling stopped or competed with a user scan.");
        await adapter.StopScanAsync(); await Until(() => api.BatteryReads == 2);
        await Task.Delay(50);
        Assert(api.BatteryReads == 2 && api.Commands.IsEmpty, "Deferred read retried repeatedly or sent output.");
    }

    private static async Task BatteryManualRefresh()
    {
        var clock = new BatteryFixtureTime(); var api = new FakeBle();
        await using var adapter = BatteryAdapter(api, clock); await BatteryConnect(adapter, api);
        clock.Advance(TimeSpan.FromMinutes(2));
        await adapter.ReadBatteryAsync(FixtureKey("AABBCCDDEE01"));
        Assert(api.BatteryReads == 2, "Explicit Read battery stopped working.");
        clock.Advance(TimeSpan.FromSeconds(299)); await Task.Delay(60);
        Assert(api.BatteryReads == 2, "A manual refresh did not reset the next automatic battery deadline.");
        clock.Advance(TimeSpan.FromSeconds(1)); await Until(() => api.BatteryReads == 3);
    }

    private static async Task BatteryFailureCadence()
    {
        var clock = new BatteryFixtureTime(); var api = new FakeBle { BatteryValue = "unknown" };
        await using var adapter = BatteryAdapter(api, clock); await BatteryConnect(adapter, api);
        await Task.Delay(80);
        Assert(api.BatteryReads == 1 && One(adapter).BatteryPercent is null, "Unknown battery caused fast retries or a guessed value.");
        api.BatteryValue = "0"; clock.Advance(TimeSpan.FromMinutes(5));
        await Until(() => api.BatteryReads == 2 && One(adapter).BatteryPercent == 0);
        api.OnBatteryRead = _ => throw new InvalidOperationException("Fixture battery request failure");
        clock.Advance(TimeSpan.FromMinutes(5)); await Until(() => api.BatteryReads == 3); await Task.Delay(90);
        Assert(api.BatteryReads == 3, "A failed battery request caused a fast retry loop.");
        clock.Advance(TimeSpan.FromSeconds(299)); await Task.Delay(60);
        Assert(api.BatteryReads == 3, "Failed-request pacing was shorter than five minutes.");
        api.OnBatteryRead = null; api.BatteryValue = "63"; clock.Advance(TimeSpan.FromSeconds(1));
        await Until(() => api.BatteryReads == 4 && One(adapter).BatteryPercent == 63);
        Assert(api.Commands.IsEmpty, "A battery error/recovery submitted actuator commands.");
    }

    private static async Task BatteryTimeoutAndShutdown()
    {
        var clock = new BatteryFixtureTime(); var api = new FakeBle { ConfirmBattery = false };
        var adapter = BatteryAdapter(api, clock);
        try
        {
            await BatteryConnect(adapter, api);
            await Until(() => adapter.Snapshot.Message.Contains("Battery read timed out"));
            Assert(api.BatteryReads == 1 && One(adapter).BatteryPercent is null, "Timed-out battery read immediately retried.");
            clock.Advance(TimeSpan.FromMinutes(5)); await Until(() => api.BatteryReads == 2);
            var timer = System.Diagnostics.Stopwatch.StartNew(); await adapter.DisposeAsync();
            Assert(timer.Elapsed < TimeSpan.FromSeconds(1) && api.Disposed, "Shutdown waited for a pending battery deadline.");
            clock.Advance(TimeSpan.FromMinutes(10)); await Task.Delay(60);
            Assert(api.BatteryReads == 2 && api.Commands.IsEmpty, "Disposed battery polling continued reading or sending output.");
            await adapter.DisposeAsync(); Assert(api.Disposals == 1, "Battery cleanup made disposal non-idempotent.");
        }
        finally { await adapter.DisposeAsync(); }
    }

    private static async Task BatteryPreservesOutput()
    {
        var clock = new BatteryFixtureTime(); var api = new FakeBle();
        await using var adapter = BatteryAdapter(api, clock); await BatteryConnect(adapter, api);
        var key = FixtureKey("AABBCCDDEE01");
        await using var controller = new LocalToyController(adapter, new(), automaticControls: true);
        controller.SetTestMode(false); controller.Request(key, "v1", 40);
        await Until(() => controller.State(key, "v1").SentPercent == 40);
        var writes = api.Commands.Count;
        clock.Advance(TimeSpan.FromMinutes(5)); await Until(() => api.BatteryReads == 2); await Task.Delay(70);
        Assert(controller.State(key, "v1").Active && controller.State(key, "v1").RequestedPercent == 40 &&
            api.Commands.Count == writes, "Automatic battery read stopped, rearmed or repeated held actuator input.");
        controller.Stop(); await Until(() => api.Commands.Last().Value == 0 && !controller.State(key, "v1").Active);
        writes = api.Commands.Count; clock.Advance(TimeSpan.FromMinutes(5)); await Until(() => api.BatteryReads == 3);
        await Task.Delay(60);
        Assert(api.Commands.Count == writes && controller.State(key, "v1").RequestedPercent == 0,
            "Battery refresh replayed input after owner Stop.");
    }
}

internal sealed class BatteryFixtureTime : TimeProvider
{
    private long timestamp;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => Interlocked.Read(ref timestamp);
    public void Advance(TimeSpan interval) => Interlocked.Add(ref timestamp, interval.Ticks);
}
