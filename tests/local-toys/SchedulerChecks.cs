using System.Diagnostics;
using System.Collections.Concurrent;
using ShockWave.Overlay;
using ShockWave.Overlay.Toys;

internal static partial class Program
{
    private static LovenseDiscoveryAdapter SchedulerAdapter(FakeBle api) => new(() => api,
        () => new LocalToyKeys(Enumerable.Repeat((byte)1, 32).ToArray()), commandsPerSecond: 10);

    private static async Task SchedulerFourToys()
    {
        var api = new FakeBle(); await using var adapter = SchedulerAdapter(api);
        await adapter.StartScanAsync();
        var symbols = new[] { "p", "u", "b", "a" }; // Edge, Lapis, Max/pump, Nora/rotation: nine independent functions.
        for (var i = 0; i < 4; i++) api.Found("scheduler-toy-" + i, $"AA:BB:CC:DD:EE:{i + 1:00}", symbols[i]);
        var devices = adapter.Snapshot.Devices.ToArray(); await adapter.StopScanAsync();
        foreach (var device in devices) await adapter.ConnectAsync(device.DeviceKey);
        await using var controller = new LocalToyController(adapter, new()); controller.SetTestMode(false); controller.SetUpdateRate(10);
        foreach (var device in devices) controller.Arm(device.DeviceKey);
        var started = Stopwatch.StartNew(); var tick = 0;
        while (started.Elapsed < TimeSpan.FromSeconds(2))
        {
            foreach (var device in devices)
                foreach (var cap in device.Capabilities)
                    controller.Request(device.DeviceKey, cap.ChannelId, cap.Type == ToyCapabilityType.Pump ? (tick % 3 + 1) * 33 + 1 : (tick % 18 + 1) * 5);
            tick++; await Task.Delay(10);
        }
        var writes = api.Commands.Where(c => c.Value > 0).ToArray();
        Assert(writes.Length is >= 20 and <= 41, "Adapter budget accumulated idle tokens, burst or exceeded 20 normal writes/sec.");
        for (var i = 0; i < writes.Length; i++)
            Assert(writes.Count(w => w.Time >= writes[i].Time && w.Time-writes[i].Time < Stopwatch.Frequency) <= 20,
                "Grouped output exceeded the rolling shared budget.");
        for (var i = 4; i < writes.Length; i++)
            Assert((writes[i].Time-writes[i-4].Time)/(double)Stopwatch.Frequency >= .19,
                "Grouped output accumulated idle credits or skipped its shared allowance.");
        foreach (var device in devices)
        {
            var id = "scheduler-toy-" + Array.IndexOf(symbols, device.Model switch { "Edge" => "p", "Lapis" => "u", "Max family" => "b", _ => "a" });
            var ownWrites = writes.Where(c => c.Id == id).ToArray();
            Assert(ownWrites.Length >= 4, "A continuously changing toy was starved.");
            for (var i = 1; i < ownWrites.Length; i++)
                Assert((ownWrites[i].Time - ownWrites[i - 1].Time) / (double)Stopwatch.Frequency >= .09, "Per-toy ceiling was exceeded.");
            foreach (var cap in device.Capabilities)
                Assert(ownWrites.Count(c => c.Command == LovenseModelCatalog.Command(cap.ChannelId)) >= 2, "One motor/pump was starved by its sibling functions.");
        }
        foreach (var device in devices) foreach (var cap in device.Capabilities) controller.Request(device.DeviceKey, cap.ChannelId, 100);
        await Until(() => devices.All(d => d.Capabilities.All(c => controller.State(d.DeviceKey, c.ChannelId).SentPercent == 100))
            && controller.SchedulerState.PendingFunctions==0 && controller.SchedulerState.SubmittingFunctions==0);
        var count = api.Commands.Count;
        for (var i = 0; i < 20; i++) foreach (var device in devices) foreach (var cap in device.Capabilities) controller.Request(device.DeviceKey, cap.ChannelId, 100);
        await Task.Delay(200);
        if(api.Commands.Count!=count) File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"scheduler-steady-failure.json"),
            System.Text.Json.JsonSerializer.Serialize(new {CountBefore=count,CountAfter=api.Commands.Count,Writes=api.Commands.Select(c=>new {c.Id,c.Command,c.Value,c.Time}).ToArray()}));
        Assert(api.Commands.Count == count && controller.SchedulerState.Replaced > 0, "Steady values were repeatedly written or replacements were untracked.");
        controller.Stop(); await Until(() => api.Commands.Count(c => c.Value == 0) == 9);
        await Task.Delay(200); Assert(api.Commands.Count(c => c.Value > 0) == count, "Four-toy Stop replayed pending input.");
    }

    private static async Task SchedulerReadyGroup()
    {
        var api=new FakeBle(); await using var adapter=SchedulerAdapter(api); await adapter.StartScanAsync();
        for(var i=0;i<4;i++) api.Found("group-toy-"+i,$"AA:BB:CC:DD:EE:{i+1:00}","r");
        var keys=Enumerable.Range(1,4).Select(i=>FixtureKey($"AABBCCDDEE{i:00}")).ToArray();
        await adapter.StopScanAsync(); foreach(var key in keys) await adapter.ConnectAsync(key);
        await using var controller=new LocalToyController(adapter,new(),automaticControls:true); controller.SetTestMode(false);
        using var entered=new ManualResetEventSlim(); using var release=new ManualResetEventSlim();
        var inFlight=0; var maximumInFlight=0;
        api.OnWrite=(id,_,value)=>
        {
            if(value==0) return;
            maximumInFlight=Math.Max(maximumInFlight,Interlocked.Increment(ref inFlight));
            try {if(id=="group-toy-0") {entered.Set(); release.Wait(TimeSpan.FromSeconds(3));}}
            finally {Interlocked.Decrement(ref inFlight);}
        };
        controller.Request(keys[0],"v",40); await Until(()=>entered.IsSet);
        try {foreach(var key in keys.Skip(1)) controller.Request(key,"v",60);}
        finally {release.Set();}
        await Until(()=>api.Commands.Count(c=>c.Value>0)==4 && controller.SchedulerState.Submitted==4);
        var ready=api.Commands.Where(c=>c.Value>0 && c.Id!="group-toy-0").ToArray();
        var spread=(ready.Last().Time-ready.First().Time)*1000d/Stopwatch.Frequency;
        var evidence=Path.Combine(AppContext.BaseDirectory,"scheduler-ready-group.json");
        File.WriteAllText(evidence,System.Text.Json.JsonSerializer.Serialize(new {ReadyToys=ready.Length,SpreadMilliseconds=spread,MaximumInFlight=maximumInFlight,PhysicalDevice=false}));
        Console.WriteLine($"Ready toy submission spread: {spread:0.0} ms; maximum concurrent calls: {maximumInFlight}");
        Assert(spread<45,"Three ready toys still wait separately behind the shared command pause.");
        Assert(maximumInFlight==1,"The scheduler made concurrent calls to the native library.");
        controller.Stop(); await Until(()=>api.Commands.Count(c=>c.Value==0)==4);
    }

    private static async Task SchedulerFreshness()
    {
        var api = new FakeBle(); await using var adapter = SchedulerAdapter(api);
        await adapter.StartScanAsync(); api.Found(); var key = One(adapter).DeviceKey; await adapter.ConnectAsync(key);
        await using var controller = new LocalToyController(adapter, new()); controller.SetTestMode(false); controller.Arm(key);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim(); var calls = 0;
        api.BeforeGuardedOutput = () => { if (Interlocked.Increment(ref calls) == 1) { entered.Set(); release.Wait(TimeSpan.FromSeconds(3)); } };
        controller.Request(key, "v1", 40); await Until(() => entered.IsSet);
        Assert(controller.State(key, "v1").SentPercent == 0 && controller.SchedulerState.SubmittingFunctions == 1,
            "Unsubmitted input was reported as Sent instead of an in-flight submission.");
        try { for (var value = 41; value <= 100; value++) controller.Request(key, "v1", value); }
        finally { release.Set(); }
        await Until(() => api.Commands.Any(c => c.Value > 0));
        Assert(api.Commands.Single(c => c.Value > 0).Value == 20 && controller.SchedulerState.CanceledBeforeSubmit == 1,
            "A captured old value crossed the delayed transport guard.");
        controller.Stop(); await Until(() => api.Commands.Last().Value == 0);
    }

    private static async Task SchedulerBatchBound()
    {
        var api=new FakeBle(); await using var adapter=SchedulerAdapter(api); await adapter.StartScanAsync();
        for(var i=0;i<5;i++) api.Found("bounded-toy-"+i,$"AA:BB:CC:DD:EE:{i+1:00}","r");
        var keys=Enumerable.Range(1,5).Select(i=>FixtureKey($"AABBCCDDEE{i:00}")).ToArray();
        await adapter.StopScanAsync(); foreach(var key in keys) await adapter.ConnectAsync(key);
        await using var controller=new LocalToyController(adapter,new(),automaticControls:true); controller.SetTestMode(false);
        using var entered=new ManualResetEventSlim(); using var release=new ManualResetEventSlim(); var calls=0;
        api.BeforeGuardedOutput=()=> {if(Interlocked.Increment(ref calls)==1) {entered.Set(); release.Wait(TimeSpan.FromSeconds(3));}};
        controller.Request(keys[0],"v",40); await Until(()=>entered.IsSet);
        try {foreach(var key in keys.Skip(1)) controller.Request(key,"v",40);}
        finally {release.Set();}
        await Until(()=>controller.SchedulerState.Submitted==5);
        var first=api.Commands.Where(c=>c.Value>0).ToArray();
        Assert(first.Length==5 && (first[3].Time-first[0].Time)*1000d/Stopwatch.Frequency<45
            && (first[4].Time-first[0].Time)*1000d/Stopwatch.Frequency>=190,
            "The fifth toy bypassed the four-toy group allowance or a ready toy was starved.");
        await Task.Delay(300);
        foreach(var key in keys) controller.Request(key,"v",80);
        await Until(()=>controller.SchedulerState.Submitted==10);
        var afterIdle=api.Commands.Where(c=>c.Value==16).ToArray();
        Assert(afterIdle.Length==5 && (afterIdle[4].Time-afterIdle[0].Time)*1000d/Stopwatch.Frequency>=190,
            "Idle time accumulated credits for an oversized output group.");
        controller.Stop(); await Until(()=>api.Commands.Count(c=>c.Value==0)==5);
    }

    private static async Task SchedulerExpiredTransportWait()
    {
        var api = new FakeBle(); await using var adapter = SchedulerAdapter(api);
        await adapter.StartScanAsync(); api.Found(); var key = One(adapter).DeviceKey; await adapter.ConnectAsync(key);
        await using var controller = new LocalToyController(adapter, new()); controller.SetTestMode(false); controller.Arm(key);
        controller.SetPreference(new(key, "v1", 100, 100, 250));
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        api.BeforeGuardedOutput = () => { entered.Set(); release.Wait(TimeSpan.FromSeconds(2)); };
        controller.Request(key, "v1", 100); await Until(() => entered.IsSet);
        try { await Task.Delay(350); } finally { release.Set(); }
        await Until(() => !controller.State(key, "v1").Active);
        Assert(!api.Commands.Any(c => c.Value > 0) && controller.SchedulerState.CanceledBeforeSubmit == 1,
            "Expired input started after a transport wait.");
    }

    private static async Task SchedulerStopBetweenToys()
    {
        var api = new FakeBle(); await using var adapter = SchedulerAdapter(api); await adapter.StartScanAsync();
        for (var i = 0; i < 3; i++) api.Found("priority-toy-" + i, $"AA:BB:CC:DD:EE:{i + 1:00}");
        var keys = Enumerable.Range(1, 3).Select(i => FixtureKey($"AABBCCDDEE{i:00}")).ToArray();
        foreach (var key in keys) await adapter.ConnectAsync(key);
        await using var controller = new LocalToyController(adapter, new()); controller.SetTestMode(false);
        foreach (var key in keys) controller.Arm(key);
        controller.Request(keys[0], "v1", 40); await Until(() => controller.SchedulerState.Submitted == 1);
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var zeroEntered = new ManualResetEventSlim(); using var zeroRelease = new ManualResetEventSlim();
        long zeroReturnedAt=0;
        api.OnWrite = (id, _, value) =>
        {
            if (id == "priority-toy-1" && value > 0) { entered.Set(); release.Wait(TimeSpan.FromSeconds(3)); }
            if (id == "priority-toy-0" && value == 0) { zeroEntered.Set(); zeroRelease.Wait(TimeSpan.FromSeconds(3)); zeroReturnedAt=Stopwatch.GetTimestamp(); }
        };
        controller.Request(keys[1], "v1", 50); await Until(() => entered.IsSet);
        try
        {
            controller.Request(keys[2], "v1", 60); controller.Stop(keys[0]); release.Set();
            await Until(() => zeroEntered.IsSet);
            try { controller.Arm(keys[0]); throw new Exception("Rearmed during a pending native zero."); } catch (InvalidOperationException) { }
        }
        finally { release.Set(); zeroRelease.Set(); }
        await Until(() => api.Commands.Any(c => c.Id == "priority-toy-2" && c.Value > 0));
        var writes = api.Commands.ToArray();
        Assert(Array.FindIndex(writes, c => c.Id == "priority-toy-0" && c.Value == 0) < Array.FindIndex(writes, c => c.Id == "priority-toy-2"),
            "Another toy's normal update overtook Stop.");
        Assert((writes.Single(c=>c.Id=="priority-toy-2" && c.Value>0).Time-zeroReturnedAt)*1000d/Stopwatch.Frequency<45,
            "A completed zero added a shared pause to an unrelated ready toy.");
        controller.Stop();
    }

    private static async Task SchedulerSlowCalls()
    {
        var api = new FakeBle(); await using var adapter = SchedulerAdapter(api);
        await adapter.StartScanAsync(); api.Found(); var key = One(adapter).DeviceKey; await adapter.ConnectAsync(key);
        await using var controller = new LocalToyController(adapter, new()); controller.SetTestMode(false); controller.SetUpdateRate(10); controller.Arm(key);
        api.OnOutput = value => { if (value > 0) Thread.Sleep(120); };
        controller.Request(key, "v1", 40); await Until(() => controller.SchedulerState.Submitted == 1);
        controller.Request(key, "v1", 80); await Until(() => controller.SchedulerState.Submitted == 2);
        var writes = api.Commands.Where(c => c.Value > 0).ToArray(); var status = controller.SchedulerState;
        var gap=(writes[1].Time-writes[0].Time)*1000d/Stopwatch.Frequency;
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"scheduler-slow-call.json"),System.Text.Json.JsonSerializer.Serialize(
            new {CallMilliseconds=status.LastSubmitMilliseconds,StartGapMilliseconds=gap,WaitMilliseconds=status.LastWaitMilliseconds,PhysicalDevice=false}));
        Console.WriteLine($"120-ms fake call: start gap {gap:0.0} ms, added input wait {status.LastWaitMilliseconds:0.0} ms");
        Assert(gap is >=110 and <200 && status.SharedTarget==20,
            "A slow call was followed by another shared pause instead of counting toward pacing.");
        Assert(status.LastSubmitMilliseconds >= 100 && status.LastWaitMilliseconds <70,
            "App waiting and submit time were not separated, or the call's delay was counted twice.");
        controller.Stop(); await Until(() => api.Commands.Last().Value == 0);
    }

    private static async Task SchedulerRateChoice()
    {
        var api = new FakeBle(); await using var adapter = SchedulerAdapter(api);
        await adapter.StartScanAsync(); api.Found(); var key = One(adapter).DeviceKey; await adapter.ConnectAsync(key);
        await using var controller = new LocalToyController(adapter, new()); controller.SetTestMode(false); controller.SetUpdateRate(1); controller.Arm(key);
        controller.SetPreference(new(key, "v1", 100, 30, 450));
        controller.Request(key, "v1", 20); await Until(() => controller.SchedulerState.Submitted == 1);
        controller.Request(key, "v1", 100); var elapsed = Stopwatch.StartNew();
        controller.SetUpdateRate(10);
        controller.SetPreference(new(key, "v1", 100, 30, 15000));
        await Until(() => controller.SchedulerState.Submitted == 2);
        Assert(elapsed.Elapsed < TimeSpan.FromMilliseconds(350) && api.Commands.Last().Value == 6,
            "Faster pacing retained an old one-second wait or bypassed the cap.");
        await Until(() => !controller.State(key, "v1").Active);
        Assert(elapsed.Elapsed < TimeSpan.FromMilliseconds(900), "Rate/preference edits extended the original expiry.");
        Assert(api.Commands.Where(c => c.Value > 0).All(c => c.Value <= 6), "Pace change bypassed the owner's maximum.");
        var positives = api.Commands.Count(c => c.Value > 0); controller.SetUpdateRate(5); await Task.Delay(120);
        Assert(api.Commands.Count(c => c.Value > 0) == positives, "Pace change restored expired output.");
        try { controller.SetUpdateRate(11); throw new Exception("Unbounded pace accepted."); } catch (ArgumentOutOfRangeException) { }
    }

    private static async Task WorkerFreshnessGuard()
    {
        using var worker = Worker("slow-battery"); string? id = null; var events = new ConcurrentQueue<int>();
        worker.ToyFound += (value, _, _) => id = value; worker.Notification += (code, _) => events.Enqueue(code);
        worker.StartScan(); worker.StopScan(); worker.Connect(id!);
        var management = Task.Run(() => worker.ReadBattery(id!)); await Until(() => events.Contains(301));
        var current = 1; var send = Task.Run(() => worker.TrySendLevel(id!, 1, 20, () => Volatile.Read(ref current) == 1));
        await Task.Delay(40); Volatile.Write(ref current, 0);
        await management; Assert(!await send && !events.Contains(220), "Worker submitted old input after waiting for management.");
        Assert(worker.TrySendLevel(id!, 1, 10, () => true), "A fresh request was rejected after canceling old input.");
        await Until(() => events.Contains(210));
    }

    private static void SchedulerUiCheck()
    {
        var apis = new List<FakeBle>();
        using var page = new LocalToysPage(_ =>
        {
            var api = new FakeBle(); apis.Add(api);
            return new RememberedToyAdapter(SchedulerAdapter(api), new MemoryToyStore(),
                TimeSpan.FromMilliseconds(80), TimeSpan.FromMilliseconds(10));
        });
        page.CreateControl(); foreach (var control in Descendants(page)) _ = control.Handle;
        var rate = page.UpdateRateInput; var source = page.SourceInput;
        page.StartRememberedConnections(); PumpUntil(() => source.Enabled && page.IsScanning);
        page.ToggleScan(); PumpUntil(() => page.CanToggleScan && !page.IsScanning);
        var stoppedStarts = apis[0].Starts; var pauseUntil = DateTime.UtcNow.AddMilliseconds(250);
        PumpUntil(() => DateTime.UtcNow >= pauseUntil);
        Assert(!page.IsScanning && apis[0].Starts == stoppedStarts, "Stop scan was undone by background recovery.");
        page.ToggleScan(); PumpUntil(() => page.IsScanning && !page.Busy);
        page.ToggleScan(); PumpUntil(() => !page.IsScanning && !page.Busy);
        rate.Value = 10; PumpUntil(() => page.StatusMessage.Contains("10/toy/sec") && page.StatusMessage.Contains("submitting"));
        source.SelectedIndex = 1;
        PumpUntil(() => source.Enabled && page.StatusMessage.Contains("10/toy/sec"));
        Assert(rate.Value == 10, "Source reset lost the session pace choice.");
        var shutdown = page.ShutdownAsync(); PumpUntil(() => shutdown.IsCompleted); shutdown.GetAwaiter().GetResult();
        Assert(uiExceptions.Count == 0, "Pace UI caused a WinForms exception.");
    }
}
