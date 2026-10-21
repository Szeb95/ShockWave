using ShockWave.Overlay;
using ShockWave.Overlay.Toys;

internal static partial class Program
{
    private static async Task LostSessionCleanup()
    {
        var api = new FakeBle { RequireLostSessionRelease = true }; var store = new MemoryToyStore();
        api.OnScan = () => api.Found();
        await using var adapter = Remembered(api, store);
        await adapter.StartScanAsync(); var key = One(adapter).DeviceKey; await adapter.ConnectAsync(key);
        await using var controller = new LocalToyController(adapter, new()); controller.Arm(key);
        api.Connection("private-test-id", false);
        await adapter.ConnectAsync(key);
        Assert(api.Disconnects == 1 && One(adapter).ConnectionState == ToyConnectionState.Connected,
            "Manual reconnect scanned a retained native session instead of releasing it.");
        Assert(!controller.State(key, "v1").Armed && api.Commands.IsEmpty, "Connection recovery restored arming or issued output.");
        await adapter.SetAutoConnectAsync(key, true);
        api.Connection("private-test-id", false);
        await Until(() => api.Connects == 3 && One(adapter).ConnectionState == ToyConnectionState.Connected);
        Assert(api.Disconnects == 2 && !controller.State(key, "v1").Armed, "Automatic recovery retained the old session or armed output.");
    }

    private static async Task FailedSessionRefresh()
    {
        var oldApi = new FakeBle { AutoConnect = false }; oldApi.OnScan = () => oldApi.Found("old-session-id");
        var freshApi = new FakeBle(); freshApi.OnScan = () => freshApi.Found("fresh-session-id");
        var created = 0;
        await using var inner = new LovenseDiscoveryAdapter(() => Interlocked.Increment(ref created) == 1 ? oldApi : freshApi,
            () => new LocalToyKeys(Enumerable.Repeat((byte)1, 32).ToArray()), scanDuration: TimeSpan.FromMilliseconds(70), responseTimeout: TimeSpan.FromMilliseconds(100));
        await using var adapter = new RememberedToyAdapter(inner, new MemoryToyStore(), TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(10));
        await adapter.StartScanAsync(); var key = One(adapter).DeviceKey;
        try { await adapter.ConnectAsync(key); throw new Exception("Unacknowledged native connection succeeded."); } catch (InvalidOperationException) { }
        Assert(adapter.Snapshot.LastError?.Contains("not confirmed") == true, "Automatic connection error was not retained in status.");
        await adapter.StartScanAsync();
        Assert(oldApi.Disposals == 1 && created == 2 && adapter.Snapshot.LastError is not null,
            "Next scan did not refresh failed native resources or hid the error before recovery.");
        oldApi.Connection("old-session-id", true); oldApi.BatteryEvent("old-session-id", "99");
        Assert(One(adapter).ConnectionState != ToyConnectionState.Connected && One(adapter).BatteryPercent is null, "Old transport callback revived a reset connection.");
        await adapter.ConnectAsync(key);
        Assert(freshApi.ConnectedIds.Single() == "fresh-session-id" && One(adapter).ConnectionState == ToyConnectionState.Connected && adapter.Snapshot.LastError is null,
            "Recovery used a stale ID or did not clear the confirmed failure.");
        Assert(oldApi.Commands.IsEmpty && freshApi.Commands.IsEmpty, "Resource recovery invoked output.");
    }

    private static async Task IsolatedSessionRecovery()
    {
        var oldApi = new FakeBle();
        oldApi.OnScan = () => { oldApi.Found("retained-live", "AA:BB:CC:DD:EE:01"); oldApi.Found("lost-toy", "AA:BB:CC:DD:EE:02"); };
        var created = 0;
        await using var inner = new LovenseDiscoveryAdapter(() => { Interlocked.Increment(ref created); return oldApi; },
            () => new LocalToyKeys(Enumerable.Repeat((byte)1, 32).ToArray()), scanDuration: TimeSpan.FromMilliseconds(70), responseTimeout: TimeSpan.FromMilliseconds(100));
        await using var adapter = new RememberedToyAdapter(inner, new MemoryToyStore());
        await adapter.StartScanAsync(); var first = FixtureKey("AABBCCDDEE01"); var second = FixtureKey("AABBCCDDEE02");
        await adapter.ConnectAsync(first); await adapter.ConnectAsync(second);
        oldApi.RequireLostSessionRelease = true; oldApi.Connection("lost-toy", false); oldApi.AutoConnect = false;
        try { await adapter.ConnectAsync(second); throw new Exception("Unacknowledged reconnect succeeded."); } catch (InvalidOperationException) { }
        await adapter.StartScanAsync();
        Assert(created == 1 && !oldApi.Disposed && adapter.Snapshot.Devices.Single(device => device.DeviceKey == first).ConnectionState == ToyConnectionState.Connected,
            "Failure recovery reset an unrelated connected toy.");
        Assert(oldApi.ConnectedIds.Count(id => id == "retained-live") == 1, "An unrelated connection was replayed.");
    }

    private static void ResetBluetoothUiCheck()
    {
        var key = FixtureKey("AABBCCDDEE01"); var store = new MemoryToyStore();
        store.Save([new(key, "Edge", false, DateTimeOffset.UtcNow)]);
        var oldApi = new FakeBle(); oldApi.OnScan = () => oldApi.Found("reset-ui-old-id");
        var freshApi = new FakeBle(); freshApi.OnScan = () => freshApi.Found("reset-ui-fresh-id");
        var created = 0;
        using var page = new LocalToysPage(_ => Remembered(Interlocked.Increment(ref created) == 1 ? oldApi : freshApi, store)) { Size = new Size(756, 590) };
        page.CreateControl(); foreach (var control in Descendants(page)) _ = control.Handle;
        page.StartRememberedConnections();
        var source = page.SourceInput;
        PumpUntil(() => page.Snapshot?.Devices.Count == 1 && source.Enabled && page.Cards.Count == 2 &&
            page.Snapshot.Devices[0].ConnectionState == ToyConnectionState.Connected);
        var functionRow = Descendants(page).OfType<ToyFunctionRow>().First();
        var sliders = Descendants(functionRow).OfType<PercentSlider>().ToArray();
        sliders[1].Value = 60;
        sliders[0].Value = 50;
        PumpUntil(() => Descendants(functionRow).OfType<Label>().Any(label => label.Text.Contains("Preview 30%")));
        ClickOffscreen(page.ResetBluetoothButton);
        PumpUntil(() => source.Enabled && freshApi.Starts == 1 && page.Cards.Count == 2 && page.Snapshot?.Devices.Count == 1 &&
            page.Snapshot.Devices[0].ConnectionState == ToyConnectionState.Connected);
        Assert(oldApi.Disposals == 1 && created == 2 && store.Load().Single().DeviceKey == key,
            "Bluetooth reset retained the old transport or removed settings.");
        Assert(freshApi.Connects == 1 && freshApi.Commands.IsEmpty, "Reset did not reconnect idle.");
        var restoredSliders = Descendants(Descendants(page).OfType<ToyFunctionRow>().First()).OfType<PercentSlider>().ToArray();
        Assert(restoredSliders[0].Value == 0 && restoredSliders[1].Value == 60,
            "Reset restored old input or lost the function sensitivity.");
        Assert(freshApi.ConnectedIds.Single() == "reset-ui-fresh-id", "Reset UI connected with a stale native ID.");
        var closing = page.ShutdownAsync(); PumpUntil(() => closing.IsCompleted); closing.GetAwaiter().GetResult();
        Assert(freshApi.Disposed && uiExceptions.Count == 0, "Reset UI leaked resources or raised a thread exception.");
    }
}
