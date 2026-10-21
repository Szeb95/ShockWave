using System.Diagnostics;
using ShockWave.Overlay;
using ShockWave.Overlay.Toys;

internal static partial class Program
{
    private static async Task ResponsiveRate()
    {
        var api = new FakeBle();
        await using var adapter = new LovenseDiscoveryAdapter(() => api, () => new LocalToyKeys(Enumerable.Repeat((byte)1, 32).ToArray()));
        await adapter.StartScanAsync(); api.Found(); var key = One(adapter).DeviceKey; await adapter.ConnectAsync(key);
        Assert(One(adapter).ConservativeCommandsPerSecond == 5, "BLE did not use its responsive update target.");
        await using var controller = new LocalToyController(adapter, new()); controller.SetTestMode(false); controller.Arm(key);
        controller.Request(key, "v1", 40); await Until(() => api.Commands.Count == 1);
        for (var value = 41; value <= 100; value++) controller.Request(key, "v1", value);
        await Until(() => api.Commands.Count == 2);
        var writes = api.Commands.ToArray(); var delay = (writes[1].Time - writes[0].Time) / (double)Stopwatch.Frequency;
        Assert(writes[1].Value == 20 && delay >= .18 && delay < .75, "Slider replayed old values, exceeded its rate, or retained the one-second delay.");
        controller.Stop(key); await Until(() => api.Commands.Last().Value == 0);
        await Task.Delay(250); Assert(api.Commands.Count == 3, "Superseded slider values replayed after Stop.");
    }

    private static async Task SlowWriteResponsiveness()
    {
        var api = new FakeBle(); await using var adapter = Adapter(api);
        await adapter.StartScanAsync(); api.Found(); var key = One(adapter).DeviceKey; await adapter.ConnectAsync(key);
        await using var controller = new LocalToyController(adapter, new()); controller.SetTestMode(false); controller.Arm(key);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        api.OnOutput = value => { if (value > 0) { entered.Set(); release.Wait(TimeSpan.FromSeconds(3)); } };
        controller.Request(key, "v1", 40);
        await Until(() => entered.IsSet);
        try
        {
            var interaction = Task.Run(() =>
            {
                Assert(One(adapter).ConnectionState == ToyConnectionState.Connected, "Inventory became unreadable during a write.");
                _ = controller.State(key, "v1"); controller.Request(key, "v1", 100); controller.Stop(key);
            });
            await interaction.WaitAsync(TimeSpan.FromMilliseconds(350));
            Assert(!controller.State(key, "v1").Armed, "Stop did not close the gate while a write was pending.");
        }
        finally { release.Set(); }
        await Until(() => api.Commands.Last().Value == 0);
        Assert(api.Commands.Count(write => write.Value > 0) == 1, "Pending input replayed behind a slow write and Stop.");
    }

    private static async Task OutputCallbackResponsiveness()
    {
        var api = new FakeBle(); await using var adapter = Adapter(api);
        await adapter.StartScanAsync(); api.Found(); var key = One(adapter).DeviceKey; await adapter.ConnectAsync(key);
        await using var controller = new LocalToyController(adapter, new()); controller.SetTestMode(false); controller.Arm(key);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        api.OnOutput = value =>
        {
            if (value == 0) return;
            var callback = Task.Run(() => api.Connection("private-test-id", false));
            completed.TrySetResult(callback.Wait(TimeSpan.FromMilliseconds(500)));
        };
        controller.Request(key, "v1", 40);
        Assert(await completed.Task.WaitAsync(TimeSpan.FromSeconds(2)), "Native callback was blocked by output state locks.");
        Assert(!controller.State(key, "v1").Armed && One(adapter).ConnectionState == ToyConnectionState.Disconnected,
            "A disconnect callback failed to close the output gate.");
    }

    private static async Task ManualOfflineReconnect()
    {
        var key = FixtureKey("AABBCCDDEE01"); var store = new MemoryToyStore();
        store.Save([new(key, "Edge", false, DateTimeOffset.UtcNow)]);
        var api = new FakeBle(); await using var adapter = Remembered(api, store); await adapter.InitializeAsync();
        try { await adapter.ConnectAsync(key); throw new Exception("Absent offline toy was reported connected."); }
        catch (InvalidOperationException error) { Assert(error.Message.Contains("not found"), "Missing offline toy had no actionable reconnect message."); }
        Assert(api.Connects == 0 && One(adapter).ConnectionState == ToyConnectionState.Unavailable, "An offline row used a stale native ID.");
        api.OnScan = () => api.Found("fresh-manual-id");
        await adapter.ConnectAsync(key);
        Assert(One(adapter).ConnectionState == ToyConnectionState.Connected && api.ConnectedIds.Single() == "fresh-manual-id", "Manual offline reconnect did not rediscover.");
        await adapter.DisconnectAsync(key); api.OnScan = () => api.Found("changed-manual-id");
        await adapter.ConnectAsync(key);
        Assert(api.ConnectedIds.Last() == "changed-manual-id" && !One(adapter).AutoConnectPaused, "Manual reconnect reused an old ID or failed to resume.");
    }

    private static async Task ImmediateLossRecovery()
    {
        var key = FixtureKey("AABBCCDDEE01"); var store = new MemoryToyStore();
        store.Save([new(key, "Edge", true, DateTimeOffset.UtcNow)]);
        var api = new FakeBle(); api.OnScan = () => api.Found("initial-id");
        await using var adapter = new RememberedToyAdapter(Adapter(api, TimeSpan.FromMilliseconds(70)), store,
            TimeSpan.FromSeconds(2), TimeSpan.FromMilliseconds(10));
        await adapter.InitializeAsync(); await Until(() => One(adapter).ConnectionState == ToyConnectionState.Connected);
        await Task.Delay(80); // Let the successful automatic cycle finish before simulating loss.
        api.OnScan = () => api.Found("recovered-id");
        var watch = Stopwatch.StartNew(); api.Connection("initial-id", false);
        await Until(() => api.ConnectedIds.Contains("recovered-id"));
        Assert(watch.Elapsed < TimeSpan.FromMilliseconds(700), "Connection loss waited for the normal retry interval.");
        await adapter.ReadBatteryAsync(key); Assert(One(adapter).BatteryPercent == 72, "Fixture battery did not populate.");
        api.OnScan = () => api.Found("notification-recovered-id");
        api.Notify(9, "recovered-id");
        Assert(One(adapter).ConnectionState != ToyConnectionState.Connected && One(adapter).BatteryPercent is null,
            "TOY_NO_CONNECT left a stale connected state or battery.");
        await Until(() => api.ConnectedIds.Contains("notification-recovered-id"));
        await adapter.DisconnectAsync(key); var count = api.Connects;
        await Task.Delay(150); Assert(api.Connects == count && One(adapter).AutoConnectPaused, "Manual disconnect was undone by immediate recovery.");
    }

    private static void OfflineReconnectUiCheck()
    {
        var first = FixtureKey("AABBCCDDEE01"); var second = FixtureKey("AABBCCDDEE02");
        var store = new MemoryToyStore(); store.Save([new(first, "Edge", false, DateTimeOffset.UtcNow), new(second, "Edge", false, DateTimeOffset.UtcNow)]);
        var api = new FakeBle { AutoConnect = false }; api.OnScan = () => api.Found("offline-ui-id");
        var fresh = new FakeBle(); fresh.OnScan = () => fresh.Found("fresh-ui-id"); var created = 0;
        using var page = new LocalToysPage(_ =>
        {
            var sessionApi = Interlocked.Increment(ref created) == 1 ? api : fresh;
            return new RememberedToyAdapter(new LovenseDiscoveryAdapter(() => sessionApi,
                () => new LocalToyKeys(Enumerable.Repeat((byte)1, 32).ToArray()), scanDuration: TimeSpan.FromMilliseconds(70),
                responseTimeout: TimeSpan.FromSeconds(2)), store, TimeSpan.FromMilliseconds(150), TimeSpan.FromMilliseconds(10));
        }) { Size = new Size(756, 590) };
        page.CreateControl(); foreach (var control in Descendants(page)) _ = control.Handle;
        page.StartRememberedConnections();
        var source = page.SourceInput;
        PumpUntil(() => page.Snapshot?.Devices.Count == 2 && source.Enabled && api.Connects > 0 && page.Cards.Count == 4);
        var offline = page.Cards.First(card => card.DeviceKey == second);
        offline.Expanded = true;
        Assert(offline.Enabled && Descendants(offline).OfType<Label>().Any(label => label.Text == "Offline"),
            "Offline card options became unavailable during automatic connection.");
        var cancel = page.CancelButton;
        PumpUntil(() => cancel.Enabled); ClickOffscreen(cancel);
        PumpUntil(() => source.Enabled); Assert(api.Disconnects > 0, "Cancel did not abort the pending native connection.");
        ClickOffscreen(page.ResetBluetoothButton);
        PumpUntil(() => source.Enabled && page.Snapshot?.Devices.Any(device => device.DeviceKey == first &&
            device.ConnectionState == ToyConnectionState.Connected) == true && fresh.ConnectedIds.Contains("fresh-ui-id"));
        Assert(fresh.Commands.IsEmpty, "Automatic recovery activated old output.");
        var closing = page.ShutdownAsync(); PumpUntil(() => closing.IsCompleted); closing.GetAwaiter().GetResult();
        Assert(api.Disposed && fresh.Disposed && uiExceptions.Count == 0, "Reconnect UI leaked its adapter or raised a thread exception.");
    }
}
