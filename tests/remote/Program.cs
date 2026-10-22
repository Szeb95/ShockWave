using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using ShockWave.Overlay;
using ShockWave.Overlay.Toys;
using ShockWave.Overlay.Sps;

internal static partial class Program
{
    private static int passed;
    private static string artifacts = "";
    [STAThread] private static int Main(string[] args)
    {
        artifacts = Path.GetFullPath("work/remote-checks"); Directory.CreateDirectory(artifacts);
        Application.EnableVisualStyles();
        try
        {
            if (args.Contains("--friends-internet")) FriendInternet();
            else if (args.Contains("--log-stress")) LogStress();
            else { Receiver(); LiveGestureReplacement(); Inventory(); SpsHandoff(); InternetTiming(); RemoteTimeoutSelection(); RemoteDiagnostics(); FriendStorage(); FriendPage(); Panel(); }
            if (args.Contains("--package-startup")) SharingCode();
            if (args.Contains("--internet")) Internet();
            File.WriteAllText(Path.Combine(artifacts, args.Contains("--friends-internet") ? "friends-internet-report.json" : args.Contains("--log-stress") ? "log-stress-report.json" : "native-report.json"), JsonSerializer.Serialize(new { passed, PhysicalOutput = false, PublicHelper = args.Contains("--internet") || args.Contains("--friends-internet"), PackagedStartup = args.Contains("--package-startup") }));
            Console.WriteLine($"Remote connection: {passed} checks passed"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); File.WriteAllText(Path.Combine(artifacts,"native-failure.txt"),e.ToString()); return 1; }
    }
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, RemoteSession.Json);
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); passed++; Console.WriteLine("PASS " + reason); }
    private static void Wait(Func<bool> condition, string reason, int timeout = 3000)
    { var until = Environment.TickCount64 + timeout; while (!condition()) { if (Environment.TickCount64 > until) throw new Exception(reason); Thread.Sleep(10); } }
    private static void Receiver()
    {
        using var f = new Fixture(); var r = f.Session; var c = f.Controller;
        c.SetPreference(c.State("fixture", "v1").Preference with { Sensitivity = 50, Maximum = 30 });
        var info = r.PublishInventory(); var t = info.Toys.Single(); var v = t.Functions[0];
        Check(t.Id != "fixture" && v.Id != "v1" && t.Id.Length == 32, "session aliases hide hardware/local keys");
        var frame = new RemoteLevel(t.Id,v.Id,80,1);
        r.Receive("state", Element(new[] { frame }));
        Wait(() => f.Adapter.Writes.Any(w => w.Value == 6), "receiver applies owner caps");
        Check(c.State("fixture","v1").RequestedPercent == 80 && c.State("fixture","v1").SentPercent == 30, "remote input passes actual owner sensitivity and maximum gate");
        c.Request("fixture","v1",14);
        r.Receive("state", Element(new[] { frame with { Percent = 100 } }));
        r.Receive("state", Element(new[] { frame with { Percent = 100 } }));
        Check(c.State("fixture","v1").RequestedPercent == 14, "owner edit rejects old peer claim without stopping owner input");
        r.Disconnect(); Check(c.State("fixture","v1").RequestedPercent == 14, "disconnect ends only peer-owned leases");
        c.Stop(); Wait(() => !c.State("fixture","v1").Stopping, "zero settles");
        info = r.PublishInventory(); t = info.Toys[0]; v = t.Functions[0]; frame = new(t.Id,v.Id,70,2);
        r.Receive("state", Element(new[] { frame }), 100);
        Wait(() => c.State("fixture","v1").RequestedPercent == 0, "remote lease expires without UI/network tick"); passed++;
        r.Receive("state", Element(new[] { frame })); Check(c.State("fixture","v1").RequestedPercent == 0, "expired claim cannot rearm itself");
        Wait(() => !c.State("fixture","v1").Stopping, "expiry zero settles");
        r.Receive("state", Element(new[] { frame with { Claim = 3 } })); Check(c.State("fixture","v1").RequestedPercent == 70, "fresh peer gesture can claim again without extra permission dialog");
        c.Stop(); r.Receive("state", Element(new[] { frame with { Claim = 3, Percent = 100 } }));
        Check(c.State("fixture","v1").RequestedPercent == 0, "owner Stop cannot be undone by old remote state");
        Wait(() => !c.State("fixture","v1").Stopping, "stop settles");
        r.Receive("state", Element(new[] { frame with { Claim = 4 }, new RemoteLevel(new string('a',32),new string('b',32),100,4) }));
        Check(c.State("fixture","v1").RequestedPercent == 0, "snapshot with unknown target is atomically rejected");
        bool invalid = false; try { r.Receive("state", Element(new[] { frame with { Percent = 101 } })); } catch (JsonException) { invalid = true; }
        Check(invalid && c.State("fixture","v1").RequestedPercent == 0, "out-of-range remote state has no output");
        f.Config.DryRun = true; c.SetTestMode(true); var writes = f.Adapter.Writes.Count;
        r.Receive("state", Element(new[] { frame with { Claim = 5 } })); Thread.Sleep(60);
        Check(f.Adapter.Writes.Count == writes, "receiver Test mode blocks physical submissions");
        r.Disconnect(); Check(c.State("fixture","v1").RequestedPercent == 0, "disconnect clears incoming activity");
    }
    private static void LiveGestureReplacement()
    {
        using var f=new Fixture(); var inventory=f.Session.PublishInventory(); var toy=inventory.Toys.Single();
        var frame=new RemoteLevel(toy.Id,toy.Functions[0].Id,60,1);
        f.Session.Receive("state",Element(new[]{frame}));
        Wait(()=>{f.Session.Receive("state",Element(new[]{frame}));return f.Controller.State("fixture","v1").SentPercent>0;},"initial remote drawing reaches the simulated motor");
        f.Adapter.BlockZero=true;
        try
        {
            frame=frame with {Claim=2,Percent=80}; f.Session.Receive("state",Element(new[]{frame}));
            Check(f.Controller.State("fixture","v1").RequestedPercent==80,
                "consecutive remote drawing does not stop and reject itself while a slow motor zero is pending");
            Check(!f.Adapter.ZeroEntered.IsSet,"replacing a live remote gesture introduces no unnecessary motor stop");
        }
        finally { f.Adapter.BlockZero=false; f.Adapter.AllowZero.Set(); }
    }
    private static void Inventory()
    {
        using var f = new Fixture(); var r = f.Session;
        var toy = new RemoteToyInfo(new string('a',32),"Friend toy",60,[new(new string('b',32),"Vibration","Vibration",45)]);
        r.Receive("inventory", Element(new RemoteInventory([toy], [new(new string('c',32),"Friend shocker","PiShock",30,500)])));
        Check(r.Toys.Snapshot.Devices.Single().DeviceKey.StartsWith("remote/") && r.Shockers.Single().Maximum == 30, "remote toy inventory and future shocker inventory remain separate");
        using var panel = new ToyControlSession(() => f.Adapter.Snapshot, () => f.Controller) { Remote = r.Toys };
        panel.Model.ChangeRoute(2); panel.Tick(ToyOscRouter.Clock);
        Check(panel.Model.FunctionCount == 3, "unified list displays local functions and friend's remote function");
        panel.Action("InRemote", false); panel.Tick(ToyOscRouter.Clock);
        Check(panel.Model.FunctionCount == 3 && panel.Model.Toys.Last().Name=="Remote Friend toy", "legacy destination controls preserve the unified list and Remote name prefix");
        panel.Action("InRow2",false); panel.Draw(new(600,300),false,true); panel.Tick(ToyOscRouter.Clock);
        Check(r.Toys.Levels().Length == 1 && f.Controller.State("fixture","v1").RequestedPercent == 0, "drawing a remote toy produces app state without touching local Bluetooth");
        r.Toys.Stop(); panel.Tick(ToyOscRouter.Clock);
        Check(panel.Model.Functions.All(row => row.Function.Level == 0), "remote rejection/Stop invalidates panel input");
        using(var noBluetooth = new ToyControlSession(()=>null,()=>null) { Remote = r.Toys })
        {
            noBluetooth.Model.ChangeRoute(1); noBluetooth.Tick(ToyOscRouter.Clock);
            noBluetooth.Action("InRow0",false); noBluetooth.Draw(new(600,300),false,true); noBluetooth.Tick(ToyOscRouter.Clock);
            Check(!noBluetooth.TestMode && r.Toys.Levels().Length==1,"controller without a Bluetooth backend can operate remote toys and shows the actual Test mode");
        }
        r.Disconnect(); panel.Tick(ToyOscRouter.Clock); Check(panel.Model.FunctionCount == 2, "disconnect removes remote functions while preserving local toy inventory");
        bool invalid = false; try { r.Receive("inventory",Element(new RemoteInventory([toy,toy],[]))); } catch (JsonException) { invalid=true; }
        Check(invalid && r.Toys.Snapshot.Devices.Count == 0, "duplicate inventory is rejected before partial replacement");
        foreach (var raw in new[] { "{\"toys\":[null],\"shockers\":[]}", "{\"toys\":[],\"shockers\":[null]}" })
        {
            bool rejected=false; using var doc=JsonDocument.Parse(raw);
            try { r.Receive("inventory",doc.RootElement); } catch(JsonException) { rejected=true; }
            Check(rejected,"null inventory elements are rejected without crashing or partial replacement");
        }
        using(var doc=JsonDocument.Parse("[null]"))
        {
            bool rejected=false; try { r.Receive("state",doc.RootElement); } catch(JsonException) { rejected=true; }
            Check(rejected,"null state elements are rejected before output");
        }
    }
    private static void SpsHandoff()
    {
        using var receiver = new Fixture(); using var sender = new Fixture();
        using var sps = new SpsSession(() => receiver.Adapter.Snapshot, () => receiver.Controller);
        const string address = "/avatar/parameters/OGB/Pen/Fixture/TouchOthers";
        var now = ToyOscRouter.Clock;
        sps.Configure([new SpsRule { Address=address, Targets=[new("fixture","v1")] }], now);
        sps.Tick(now, true);
        void Contact(float level)
        {
            Thread.Sleep(2); now = ToyOscRouter.Clock;
            sps.Apply(new(address, [level]), now); sps.Tick(now, true);
        }
        Contact(.6f);
        var advertised = receiver.Session.PublishInventory();
        sender.Session.Toys.Replace(advertised.Toys);
        using var graph = new ToyControlSession(() => null, () => null) { Remote=sender.Session.Toys };
        graph.Tick(ToyOscRouter.Clock); graph.Action("InRow0", false); graph.Draw(new(600,300), false, true); graph.Tick(ToyOscRouter.Clock);
        var frame = sender.Session.Toys.Levels().Single();
        receiver.Session.Receive("state", Element(new[] { frame }));
        var remoteSource = receiver.Controller.State("fixture","v1").InputSource;
        Contact(0); Contact(.9f);
        Check(receiver.Controller.State("fixture","v1").InputSource == remoteSource && receiver.Controller.State("fixture","v1").RequestedPercent == frame.Percent,
            "fresh SPS zero/rise cannot steal a function from an active remote drawing");
        receiver.Session.Receive("state", Element(new[] { frame with { Percent=42 } }));
        Check(receiver.Controller.State("fixture","v1").RequestedPercent == 42 && graph.Model.Functions.Single(f=>f.Function.Id==frame.Channel).Selected,
            "remote control remains selected and renewable while the owner's SPS stays enabled");
        receiver.Session.Receive("state", Element(Array.Empty<RemoteLevel>()));
        sps.Tick(ToyOscRouter.Clock, true);
        Check(sps.ActiveTargets == 0, "remote release does not replay a cached positive SPS contact");
        Wait(() => !receiver.Controller.State("fixture","v1").Stopping, "remote handoff zero completes");
        Contact(.4f);
        Check(receiver.Controller.State("fixture","v1").RequestedPercent == 40 && sps.ActiveTargets == 1,
            "a fresh SPS contact resumes after remote release without reconnecting or changing assignments");
        frame = frame with { Claim=frame.Claim+1, Percent=0 };
        receiver.Session.Receive("state", Element(new[] { frame }));
        Contact(.8f);
        Check(receiver.Controller.State("fixture","v1").RequestedPercent == 0 && sps.ActiveTargets == 0,
            "a remote curve's live zero keeps ownership instead of letting SPS steal the next rise");
        receiver.Session.Receive("state", Element(new[] { frame with { Percent=55 } }));
        Check(receiver.Controller.State("fixture","v1").RequestedPercent == 55,
            "the same remote curve can rise again after zero while SPS remains enabled");
        frame = frame with { Claim=frame.Claim+1, Percent=61 };
        receiver.Session.Receive("state",Element(new[] {frame}));
        Check(receiver.Controller.State("fixture","v1").RequestedPercent==61,
            "a fresh drawing replaces the same live remote source without stopping and rejecting itself");
        receiver.Session.Receive("state",Element(new[] {frame with {Claim=frame.Claim-1,Percent=99}}));
        Check(receiver.Controller.State("fixture","v1").RequestedPercent==61,"late activity from the previous drawing cannot replace a newer remote gesture");
        receiver.Session.Receive("state", Element(Array.Empty<RemoteLevel>()));
        sps.Tick(ToyOscRouter.Clock,true); Wait(() => !receiver.Controller.State("fixture","v1").Stopping, "second remote zero completes"); Contact(.4f);
        frame = frame with { Claim=frame.Claim+1, Percent=35 };
        receiver.Session.Receive("state",Element(new[] {frame}));
        receiver.Session.Receive("state",Element(Array.Empty<RemoteLevel>()));
        sps.Tick(ToyOscRouter.Clock,true);
        Check(sps.ActiveTargets==0,"a complete remote claim/release between SPS ticks cannot replay cached input");
        Wait(()=>!receiver.Controller.State("fixture","v1").Stopping,"short remote handoff zero completes"); Contact(.5f);
        Check(receiver.Controller.State("fixture","v1").RequestedPercent==50,"SPS recovers from a remote handoff it did not observe while active");
        frame = frame with { Claim=frame.Claim+1, Percent=65 };
        receiver.Session.Receive("state",Element(new[] {frame})); sps.Tick(ToyOscRouter.Clock,true); Contact(0);
        receiver.Controller.Stop();
        Wait(()=>!receiver.Controller.State("fixture","v1").Stopping,"owner Stop during remote handoff completes");
        frame = frame with {Claim=frame.Claim+1};
        receiver.Session.Receive("state",Element(new[] {frame}));
        receiver.Session.Receive("state",Element(Array.Empty<RemoteLevel>()));
        sps.Tick(ToyOscRouter.Clock,true); Contact(.9f);
        Check(receiver.Controller.State("fixture","v1").RequestedPercent==0 && sps.ActiveTargets==0,
            "owner Stop cannot be hidden by later remote claim/release or a pre-Stop neutral contact");
        Contact(0); Wait(()=>!receiver.Controller.State("fixture","v1").Stopping,"owner Stop rearm zero completes"); Contact(.3f);
        Check(receiver.Controller.State("fixture","v1").RequestedPercent==30,"a new neutral and rise still rearm SPS after an explicit owner Stop");
    }
    private static void InternetTiming()
    {
        // 152 ms RTT: about 80 ms one-way age, 81 ms clock uncertainty,
        // plus 35 ms in the receiving UI. Periodic 180 ms delivery gaps
        // revoke the old 250 ms claim even though the peer remains connected.
        var life = RemoteSession.RemainingLifetime("state",RemoteSession.StateLifetimeMs-80-81,35);
        using var owner = new Fixture();
        var toy = owner.Session.PublishInventory().Toys.Single();
        var frame = new RemoteLevel(toy.Id,toy.Functions[0].Id,65,1);
        for(var i=0;i<8;i++) { owner.Session.Receive("state",Element(new[]{frame}),life); Thread.Sleep(180); }
        Check(owner.Controller.State("fixture","v1").RequestedPercent==65,"152 ms RTT and bounded delivery gaps do not revoke a healthy remote claim");
        Wait(()=>owner.Controller.State("fixture","v1").RequestedPercent==0,"missing fresh updates still stop output independently of the UI",1000);
        Check(owner.Controller.State("fixture","v1").RequestedPercent==0,"Internet jitter allowance remains bounded and silence stops output");
        owner.Session.Receive("state",Element(new[]{frame}));
        Check(owner.Controller.State("fixture","v1").RequestedPercent==0,"an expired claim remains blocked after network recovery");
        Check(RemoteSession.RemainingLifetime("state",80,80)==0 && RemoteSession.RemainingLifetime("state",80,81)==0,
            "worker-to-UI expiry cannot be rounded up into a new lease");
        Check(RemoteSession.RemainingLifetime("state",500,30)==470 && RemoteSession.RemainingLifetime("shock",500,30)==220,
            "only continuous toy state gains jitter tolerance; shock commands retain 250 ms total expiry");
        Check(RemoteSession.RemainingLifetime("state",double.NaN,0)==0 && RemoteSession.RemainingLifetime("state",500,-11)==0,
            "invalid timing cannot mint a fresh output lease");
    }
    private static void RemoteTimeoutSelection()
    {
        using var receiver = new Fixture(); using var sender = new Fixture();
        sender.Session.Toys.Replace(receiver.Session.PublishInventory().Toys);
        using var graph = new ToyControlSession(() => null, () => null) { Remote=sender.Session.Toys };
        var graphLog = new List<string>(); graph.Log += graphLog.Add;
        graph.Tick(ToyOscRouter.Clock); graph.Action("InRow0",false);
        var row = graph.Model.Functions.First();
        row.Function.Pattern = new ShockWave.ShaderLab.PatternClip([new(0,.4f),new(1,.7f)]);
        graph.Action("InPlay",false); graph.Tick(ToyOscRouter.Clock);
        var frame = sender.Session.Toys.Levels().Single();
        receiver.Session.Receive("state",Element(new[]{frame}),30);
        Wait(()=>receiver.Controller.State("fixture","v1").RequestedPercent==0,"a network gap still expires the receiving motor lease");
        receiver.Session.Receive("state",Element(new[]{frame}));
        Check(receiver.Controller.State("fixture","v1").RequestedPercent==0,"a later packet cannot revive the expired drawing");
        sender.Session.Toys.Reject(frame.Device,frame.Channel,frame.Claim); graph.Tick(ToyOscRouter.Clock);
        row = graph.Model.Functions.First();
        Check(row.Selected && !row.Function.LoopRunning && row.Function.Level==0 && sender.Session.Toys.Levels().Length==0,
            "remote timeout keeps the user's selection but stops the loop and all outgoing activity");
        Check(graph.Model.Level==0,"graph percentage and new history samples fall to zero with the selected stopped toy");
        graph.Tick(ToyOscRouter.Clock);
        Check(sender.Session.Toys.Levels().Length==0,"keeping a row selected never rearms expired output on its own");
        graph.Action("InPlay",false); graph.Tick(ToyOscRouter.Clock);
        var fresh = sender.Session.Toys.Levels().Single();
        Check(fresh.Claim>frame.Claim,"fresh Play can restart the selected remote toy without selecting it again");
        sender.Session.Toys.Reject(frame.Device,frame.Channel,frame.Claim); graph.Tick(ToyOscRouter.Clock);
        Check(sender.Session.Toys.Levels().Single().Claim==fresh.Claim,"a delayed old rejection cannot stop a fresh drawing");
        graph.Draw(new(600,300),false,true); graph.Tick(ToyOscRouter.Clock);
        var held = sender.Session.Toys.Levels().Single();
        sender.Session.Toys.Reject(held.Device,held.Channel,held.Claim);
        // A mouse move may observe the rejection before the normal graph timer.
        graph.Draw(new(600,200),false); graph.Tick(ToyOscRouter.Clock);
        Check(graph.Model.Functions.First().Selected && sender.Session.Toys.Levels().Length==0,
            "a held mouse cannot silently reclaim a rejected remote toy");
        graph.Release(false); graph.Draw(new(600,300),false,true); graph.Tick(ToyOscRouter.Clock);
        Check(sender.Session.Toys.Levels().Single().Claim>held.Claim,"release and a fresh press restart control while retaining selection");
        graph.Release(false);
        void Vrc(string name, object value) => graph.Vrc.Apply(new("/avatar/parameters/SzebsToy_"+name,[value]),ToyOscRouter.Clock);
        Vrc("Visible",true); Vrc("InBeat",true); graph.Tick(ToyOscRouter.Clock);
        Vrc("InX",.5f); Vrc("InY",.6f); Vrc("InTouch",true); graph.Tick(ToyOscRouter.Clock);
        var vr = sender.Session.Toys.Levels().Single();
        sender.Session.Toys.Reject(vr.Device,vr.Channel,vr.Claim); graph.Tick(ToyOscRouter.Clock); graph.Tick(ToyOscRouter.Clock);
        Check(graph.Model.Functions.First().Selected && sender.Session.Toys.Levels().Length==0,
            "a held avatar touch keeps selection without reclaiming rejected output");
        Vrc("InTouch",false); graph.Tick(ToyOscRouter.Clock); Vrc("InTouch",true); graph.Tick(ToyOscRouter.Clock);
        Check(sender.Session.Toys.Levels().Single().Claim>vr.Claim,"fresh avatar touch resumes without selecting the row again");
        Vrc("InTouch",false); graph.Tick(ToyOscRouter.Clock); graph.Action("InInputMode",false);
        Vrc("InPointerLHit",true); Vrc("InPointerLX",.5f); Vrc("InPointerLY",.6f); Vrc("InPointerLPress",true); graph.Tick(ToyOscRouter.Clock);
        var pointer = sender.Session.Toys.Levels().Single();
        sender.Session.Toys.Reject(pointer.Device,pointer.Channel,pointer.Claim); graph.Tick(ToyOscRouter.Clock); graph.Tick(ToyOscRouter.Clock);
        Check(graph.Model.Functions.First().Selected && sender.Session.Toys.Levels().Length==0,
            "a held avatar pointer keeps selection without reclaiming rejected output");
        Vrc("InPointerLPress",false); graph.Tick(ToyOscRouter.Clock); Vrc("InPointerLPress",true); graph.Tick(ToyOscRouter.Clock);
        Check(sender.Session.Toys.Levels().Single().Claim>pointer.Claim,"fresh avatar pointer resumes without selecting the row again");
        graph.VrcLost();
        Check(graphLog.Any(s=>s.Contains("VRC panel link")),"avatar-link interruption is logged separately from peer/network loss");
        graph.StopAll(); Check(graph.Model.SelectedCount==0 && sender.Session.Toys.Levels().Length==0,"explicit Stop all still clears selection and activity");
        using var mixed = new ToyControlSession(()=>sender.Adapter.Snapshot,()=>sender.Controller) {Remote=sender.Session.Toys};
        mixed.Tick(ToyOscRouter.Clock);
        mixed.Action("InRow2",false); mixed.Draw(new(600,300),false,true); mixed.Release(false); mixed.Tick(ToyOscRouter.Clock);
        var background = sender.Session.Toys.Levels().Single();
        mixed.Action("InRow2",false); mixed.Action("InRow0",false); mixed.Action("InRecord",false);
        mixed.Draw(new(600,300),false,true); mixed.Tick(ToyOscRouter.Clock);
        sender.Session.Toys.Reject(background.Device,background.Channel,background.Claim); mixed.Tick(ToyOscRouter.Clock);
        Check(mixed.Model.Touching && mixed.Model.Recording && sender.Controller.State("fixture","v1").RequestedPercent>0,
            "a deselected remote toy timing out cannot interrupt recording on an unrelated local toy");
    }
    private static void RemoteDiagnostics()
    {
        using var owner = new Fixture(); using var sender = new Fixture();
        var receiverLog = new List<string>(); var senderLog = new List<string>();
        owner.Session.Log += receiverLog.Add; sender.Session.Log += senderLog.Add;
        var toy = owner.Session.PublishInventory().Toys.Single();
        var frame = new RemoteLevel(toy.Id,toy.Functions[0].Id,65,1);
        owner.Session.Receive("state",Element(new[]{frame}),25);
        Wait(()=>owner.Controller.State("fixture","v1").RequestedPercent==0,"diagnostic fixture expires independently of UI input");
        owner.Session.Receive("state",Element(new[]{frame}));
        Check(receiverLog.Any(s=>s.Contains("input freshness expired") && s.Contains("Last accepted") && s.Contains("previous lease")),
            "receiver logs expired claim timing rather than calling it a network disconnect");
        var count = receiverLog.Count;
        for(var i=0;i<50;i++) owner.Session.Receive("state",Element(new[]{frame}));
        Check(receiverLog.Count==count,"repeated rejected frames do not flood the diagnostic log");
        sender.Session.Toys.Replace([toy]);
        using var graph = new ToyControlSession(()=>null,()=>null) {Remote=sender.Session.Toys};
        graph.Tick(ToyOscRouter.Clock); graph.Action("InRow0",false); graph.Action("InRow1",false);
        graph.Draw(new(600,300),false,true); graph.Release(false); graph.Tick(ToyOscRouter.Clock);
        var levels = sender.Session.Toys.Levels();
        foreach(var value in levels) sender.Session.Receive("result",Element(value with {Reason="input-expired"}));
        graph.Tick(ToyOscRouter.Clock);
        Check(graph.Model.SelectedCount==2 && graph.Model.Level==0 && graph.Model.Functions.All(f=>f.Level==0),
            "two stopped selected motors agree with the zero-percent heading and history");
        Check(senderLog.Count(s=>s.Contains("Friend stopped toy input") && s.Contains("input freshness expired"))==2,
            "sender logs the receiving app's reason for each stopped input");
        sender.Session.ReportHealth(Element(new{sent=150,received=140,expired=3,future=0,blocked=1,maxAgeMs=510,maxGapMs=600,minRemainingMs=0,rttMs=420,uncertaintyMs=215,secret="private-fixture-marker",endpoint="private-fixture-marker"}));
        Check(senderLog.Any(s=>s.Contains("sent 150, received 140, expired 3") && s.Contains("clock uncertainty 215")),
            "bounded network summary exposes timing drops and clock uncertainty");
        Check(receiverLog.Concat(senderLog).All(s=>!s.Contains(toy.Id) && !s.Contains(toy.Functions[0].Id) && !s.Contains("private-fixture-marker")),
            "diagnostic logs exclude session aliases and unexpected private fields");
        graph.Draw(new(600,300),false,true); graph.Tick(ToyOscRouter.Clock);
        var fresh = sender.Session.Toys.Levels().First();
        sender.Session.Receive("result",Element(fresh with {Reason="private-fixture-marker"}));
        Check(senderLog.All(s=>!s.Contains("private-fixture-marker")),"peer-supplied rejection text is allowlisted rather than copied to logs");
    }
    private static void LogStress()
    {
        using var toys = new LocalToysPage(); using var form = new OverlayForm(new BridgeConfig(),initialSaveConfig:_=>{},initialLocalToys:toys);
        var flags = BindingFlags.NonPublic|BindingFlags.Instance;
        var append = typeof(OverlayForm).GetMethod("AppendLog",flags)!;
        var page = (Control)typeof(OverlayForm).GetField("logsPage",flags)!.GetValue(form)!;
        var grid = (DataGridView)typeof(OverlayForm).GetField("logGrid",flags)!.GetValue(form)!;
        const string line = "Network 5s: sent 109, received 108, expired 0, future 0, blocked 0, pipe expired 0, unknown targets 0; max state age 95 ms, gap 64 ms, min remaining 325 ms; max RTT 155 ms, clock uncertainty 83 ms; app gap 47 ms.";
        for(var i=0;i<500;i++) append.Invoke(form,["remote",line]);
        using var preview = new Form {Width=790,Height=600,BackColor=Color.FromArgb(30,31,34),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new(-32000,-32000)};
        preview.Controls.Add(page); page.Dock=DockStyle.Fill; page.Visible=true;
        using var owner = new Fixture(); var toy = owner.Session.PublishInventory().Toys.Single();
        var frame = new RemoteLevel(toy.Id,toy.Functions[0].Id,65,1);
        owner.Session.Receive("state",Element(new[]{frame}),330);
        var lastTick = Environment.TickCount64; long maximumGap=0; var expired=false; var ticks=0;
        using var timer = new System.Windows.Forms.Timer {Interval=33};
        timer.Tick += (_,_) => {
            var now=Environment.TickCount64; maximumGap=Math.Max(maximumGap,now-lastTick); lastTick=now; ticks++;
            owner.Session.Receive("state",Element(new[]{frame}),330);
            expired |= owner.Controller.State("fixture","v1").RequestedPercent==0;
        };
        timer.Start(); var showAt=Environment.TickCount64; preview.Show(); Application.DoEvents(); var showMs=Environment.TickCount64-showAt;
        var until=Environment.TickCount64+2500; var nextChange=Environment.TickCount64; var wide=false;
        while(Environment.TickCount64<until)
        {
            if(Environment.TickCount64>=nextChange) {
                wide=!wide; preview.Width=wide?1050:620; nextChange=Environment.TickCount64+250;
                append.Invoke(form,["remote",line]);
            }
            Application.DoEvents(); Thread.Sleep(5);
        }
        timer.Stop(); preview.Hide();
        File.WriteAllText(Path.Combine(artifacts,"log-performance.json"),JsonSerializer.Serialize(new{showMs,maximumGap,ticks,expired,rows=grid.RowCount,virtualMode=grid.VirtualMode,autoSize=grid.AutoSizeRowsMode.ToString(),PhysicalOutput=false}));
        Check(!expired && ticks>10 && owner.Controller.State("fixture","v1").RequestedPercent==65,
            "opening and resizing a full Log view preserves a 330 ms simulated remote input lease");
    }
    private static void Panel()
    {
        using var toys = new LocalToysPage(); using var form = new OverlayForm(new BridgeConfig(),initialSaveConfig:_=>{},initialLocalToys:toys);
        Button Nav(string name) => (Button)typeof(OverlayForm).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(form)!;
        Check(Nav("remoteNav").Top > Nav("spsNav").Top && Nav("remoteNav").Top < Nav("logsNav").Top, "Connect tab is between SPS and Log");
        var formRemote = (RemoteSession)typeof(OverlayForm).GetField("remoteSession",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(form)!;
        formRemote.ReportHealth(Element(new{sent=12,received=10}));
        var formLog = (List<string>)typeof(OverlayForm).GetField("recentLog",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(form)!;
        Check(formLog.Any(s=>s.Contains("REMOTE") && s.Contains("Network 5s: sent 12")),"normal app Log page receives remote diagnostics");
        var appendLog = typeof(OverlayForm).GetMethod("AppendLog",BindingFlags.NonPublic|BindingFlags.Instance)!;
        appendLog.Invoke(form,["other","unrelated-fixture-marker"]);
        Check(form.RemoteLogText().Contains("Network 5s: sent 12") && form.RemoteLogText().Contains("ShockWave") && !form.RemoteLogText().Contains("unrelated-fixture-marker"),
            "Copy remote log includes version/timing and remote events without unrelated activity");
        var logPage = (Control)typeof(OverlayForm).GetField("logsPage",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(form)!;
        IEnumerable<Control> Descendants(Control parent) => parent.Controls.Cast<Control>().SelectMany(child=>new[]{child}.Concat(Descendants(child)));
        var logButtons = Descendants(logPage).OfType<Button>().ToArray();
        Check(new[]{"Copy log","Copy remote log","Clear"}.All(text=>logButtons.Any(b=>b.Text==text)),"Log page exposes both copy actions and Clear");
        // Host only the page; showing the normal app form would start devices.
        using var logPreview = new Form {Width=620,Height=440,BackColor=Color.FromArgb(30,31,34),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new(-32000,-32000)};
        logPreview.Controls.Add(logPage); logPage.Dock=DockStyle.Fill; logPage.Visible=true; logPreview.Show(); Application.DoEvents();
        var logGrid = (DataGridView)typeof(OverlayForm).GetField("logGrid",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(form)!;
        Check(logGrid.Rows[0].Cells[1].Value?.ToString()=="REMOTE" && logGrid.Rows[0].Cells[2].Value?.ToString()?.Contains("Network 5s: sent 12")==true,
            "on-demand Log rows retain their source and full message values");
        Check(logButtons.All(b=>b.Visible && b.Left>=0 && b.Right<=b.Parent!.ClientSize.Width),"copy and clear buttons fit the minimum-width log page");
        using(var logImage=new Bitmap(logPreview.ClientSize.Width,logPreview.ClientSize.Height)) {logPreview.DrawToBitmap(logImage,logPreview.ClientRectangle); logImage.Save(Path.Combine(artifacts,"log-page.png"));}
        logButtons.Single(b=>b.Text=="Clear").PerformClick();
        Check(formLog.Count==0 && !form.RemoteLogText().Contains("Network 5s"),"Clear removes both visible history and the copy buffer");
        for(var i=0;i<505;i++) appendLog.Invoke(form,["remote","fixture entry "+i+"  full detail"]);
        Check(logGrid.RowCount==500 && formLog.Count==500 && logGrid.Rows[0].Cells[2].Value?.ToString()=="fixture entry 5  full detail" && logGrid.Rows[499].Cells[2].Value?.ToString()=="fixture entry 504  full detail",
            "Log rotation stays bounded and preserves full messages including double spaces");
        logButtons.Single(b=>b.Text=="Clear").PerformClick();
        logPreview.Hide();
        using var f = new Fixture(); using var page = new RemotePage(f.Session,()=>"node.exe",Path.GetFullPath("bridge"));
        using var preview = new Form { Width=1050,Height=870,ShowInTaskbar=false,Location=new(-32000,-32000),StartPosition=FormStartPosition.Manual };
        preview.Controls.Add(page); preview.Show(); Application.DoEvents();
        using var bitmap = new Bitmap(page.Width,page.Height); page.DrawToBitmap(bitmap,page.ClientRectangle); bitmap.Save(Path.Combine(artifacts,"connect-page.png")); preview.Hide();
        Check(!f.Session.Enabled && !f.Session.Connected, "opening Connect page starts no helper, account, Bluetooth or output");
    }
    private static void SharingCode()
    {
        var package = Environment.GetEnvironmentVariable("SHOCKWAVE_REMOTE_PACKAGE")
            ?? throw new Exception("Set SHOCKWAVE_REMOTE_PACKAGE for the published sharing-code check.");
        using var fixture = new Fixture();
        using var page = new RemotePage(fixture.Session, () => Path.Combine(package, "node.exe"), Path.Combine(package, "bridge"));
        using var preview = new Form { Width=1050,Height=870,ShowInTaskbar=false,Location=new(-32000,-32000),StartPosition=FormStartPosition.Manual };
        preview.Controls.Add(page); preview.Show(); Application.DoEvents();
        T Field<T>(string name) => (T)typeof(RemotePage).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
        Check(Field<TextBox>("own").Text.Length == 0 && !Field<Button>("copy").Enabled, "sharing code is initially empty without starting a connection");
        Field<Button>("connect").PerformClick();
        Wait(() => { Application.DoEvents(); return Field<TextBox>("own").Text.StartsWith("SW1."); }, "Connect button displays the generated sharing code", 15000);
        Check(fixture.Session.Enabled && Field<TextBox>("own").Text == fixture.Session.Invitation && Field<Button>("copy").Enabled,
            "actual Connect button fills the code field and enables Copy my code using packaged dependencies");
        Check(fixture.Adapter.Writes.IsEmpty, "generating a sharing code causes no toy output");
        Field<Button>("disconnect").PerformClick();
        Check(!fixture.Session.Enabled && !fixture.Session.Connected && Field<Button>("connect").Enabled,
            "Disconnect after sharing-code generation restores Connect without starting hardware");
        preview.Hide();
    }
    private static void Internet()
    {
        using var a = new Fixture(4); using var b = new Fixture(4);
        a.Session.Diagnostic += text => Console.WriteLine("A " + text); b.Session.Diagnostic += text => Console.WriteLine("B " + text);
        var package = Environment.GetEnvironmentVariable("SHOCKWAVE_REMOTE_PACKAGE");
        var node = package is null ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"nodejs","node.exe") : Path.Combine(package,"node.exe");
        var directory = package is null ? Path.GetFullPath("bridge") : Path.Combine(package,"bridge");
        a.Session.Connect("",node,directory); bool joined=false;
        Wait(() =>
        {
            a.Session.Tick(); b.Session.Tick();
            if (!joined && a.Session.Invitation.Length>0) { joined=true; b.Session.Connect(a.Session.Invitation,node,directory); }
            return a.Session.Connected && b.Session.Connected && a.Session.Toys.Snapshot.Devices.Count>0 && b.Session.Toys.Snapshot.Devices.Count>0;
        },"two native sessions connect through public helper",55000);
        Check(true,"two native app sessions automatically exchange inventory over direct WebRTC");
        var oldId = a.Session.Toys.Snapshot.Devices[0].DeviceKey;
        var outgoing = a.Session.Toys.Snapshot.Devices.Select(t => a.Session.Toys.BeginInput(t.DeviceKey,t.Capabilities[0].ChannelId,Guid.NewGuid(),65,TimeSpan.FromSeconds(2))).ToArray();
        var reverse = b.Session.Toys.Snapshot.Devices.Select(t => b.Session.Toys.BeginInput(t.DeviceKey,t.Capabilities[1].ChannelId,Guid.NewGuid(),45,TimeSpan.FromSeconds(2))).ToArray();
        Wait(() =>
        {
            a.Session.Tick(); b.Session.Tick();
            foreach(var lease in outgoing) a.Session.Toys.UpdateInput(lease,65,TimeSpan.FromSeconds(2));
            foreach(var lease in reverse) b.Session.Toys.UpdateInput(lease,45,TimeSpan.FromSeconds(2));
            return b.Adapter.Snapshot.Devices.All(t => b.Controller.State(t.DeviceKey,"v1").RequestedPercent==65) &&
                a.Adapter.Snapshot.Devices.All(t => a.Controller.State(t.DeviceKey,"pump").RequestedPercent==45);
        },"remote UI state reaches actual receiver gate",6000);
        Check(outgoing.Length==4 && reverse.Length==4,"four toys work simultaneously in both directions through simulated receiver gates");
        Check(a.Session.PublishInventory().Toys.Length==4,"remote inventory is never forwarded as locally owned toys");
        ContinuousSps(a,b);
        b.Session.Disconnect();
        Wait(() => { a.Session.Tick(); return !a.Session.Connected && b.Controller.State("fixture","v1").RequestedPercent==0; },"disconnect revokes both session directions",5000);
        Check(a.Session.Toys.Snapshot.Devices.Count==0,"either side disconnect clears peer inventory and incoming output");
        b.Session.Connect(a.Session.Invitation,node,directory);
        try { Wait(() => { a.Session.Tick(); b.Session.Tick(); return a.Session.Connected && b.Session.Connected && a.Session.Toys.Snapshot.Devices.Count==4; },"reconnect with both switches on",15000); }
        catch { Console.WriteLine("A status: " + a.Session.Status + "; B status: " + b.Session.Status); throw; }
        Check(a.Session.Toys.Snapshot.Devices[0].DeviceKey!=oldId && a.Session.Toys.Levels().Length==0 && b.Adapter.Snapshot.Devices.All(t=>b.Controller.State(t.DeviceKey,"v1").RequestedPercent==0),
            "reconnect automatically restores devices with fresh aliases and no old levels or loops");
    }
    private static void ContinuousSps(Fixture a, Fixture b)
    {
        const string contact="/avatar/parameters/OGB/Pen/Fixture/TouchOthers";
        using var aSps=new SpsSession(()=>a.Adapter.Snapshot,()=>a.Controller);
        using var bSps=new SpsSession(()=>b.Adapter.Snapshot,()=>b.Controller);
        SpsRule Mapping(Fixture owner)=>new() {Address=contact,Targets=owner.Adapter.Snapshot.Devices.SelectMany(d=>d.Capabilities.Select(c=>new SpsTarget(d.DeviceKey,c.ChannelId))).ToList()};
        aSps.Configure([Mapping(a)],ToyOscRouter.Clock); bSps.Configure([Mapping(b)],ToyOscRouter.Clock);
        using var aGraph=new ToyControlSession(()=>null,()=>null) {Remote=a.Session.Toys};
        using var bGraph=new ToyControlSession(()=>null,()=>null) {Remote=b.Session.Toys};
        aGraph.Tick(ToyOscRouter.Clock); bGraph.Tick(ToyOscRouter.Clock);
        foreach(var row in aGraph.Model.Functions.Where(f=>f.Function.Name=="Vibration")) row.Function.Selected=true;
        foreach(var row in bGraph.Model.Functions.Where(f=>f.Function.Name=="Pump")) row.Function.Selected=true;
        aGraph.Draw(new(600,300),false,true); bGraph.Draw(new(600,400),false,true);
        var aPercent=(int)Math.Round(aGraph.Model.Functions.First(f=>f.Selected).Function.Level*100);
        var bPercent=(int)Math.Round(bGraph.Model.Functions.First(f=>f.Selected).Function.Level*100);
        const int duration=30000;
        var until=Environment.TickCount64+duration; double contactAt=0; bool high=false, independent=false;
        while(Environment.TickCount64<until)
        {
            var now=ToyOscRouter.Clock;
            aGraph.Tick(now); bGraph.Tick(now); a.Session.Tick(); b.Session.Tick();
            if(now>=contactAt)
            {
                high=!high; contactAt=now+.12;
                aSps.Apply(new(contact,[high?.8f:0f]),now); bSps.Apply(new(contact,[high?.8f:0f]),now);
            }
            aSps.Tick(now,true); bSps.Tick(now,true);
            independent |= a.Adapter.Snapshot.Devices.All(d=>a.Controller.State(d.DeviceKey,"v1").RequestedPercent==80)
                && b.Adapter.Snapshot.Devices.All(d=>b.Controller.State(d.DeviceKey,"pump").RequestedPercent==80);
            if(!a.Session.Connected || !b.Session.Connected || aGraph.Model.Functions.Count(f=>f.Selected)!=4 || bGraph.Model.Functions.Count(f=>f.Selected)!=4)
                throw new Exception($"Remote selection lost after {duration-(until-Environment.TickCount64)} ms; A selected={aGraph.Model.Functions.Count(f=>f.Selected)}, B selected={bGraph.Model.Functions.Count(f=>f.Selected)}; A: {aGraph.Notice}; B: {bGraph.Notice}; connected={a.Session.Connected && b.Session.Connected}");
            Thread.Sleep(10);
        }
        Check(true,"four remote selections stay active in both directions for 30 seconds with changing all-function SPS contacts");
        Check(a.Adapter.Snapshot.Devices.All(d=>a.Controller.State(d.DeviceKey,"pump").RequestedPercent==bPercent)
            && b.Adapter.Snapshot.Devices.All(d=>b.Controller.State(d.DeviceKey,"v1").RequestedPercent==aPercent),
            "remote drawing keeps reaching the receiving owner gates throughout sustained SPS activity");
        Check(independent,"unrelated SPS functions continue routing while their siblings are controlled remotely");
    }
    private sealed class Fixture : IDisposable
    {
        public Adapter Adapter { get; } public BridgeConfig Config { get; } = new(); public LocalToyController Controller { get; }
        public RemoteSession Session { get; }
        public Fixture(int count=1, IRemoteFriendStore? friends=null) { Adapter=new(count); Controller=new(Adapter,new LocalToyControlStore(),true); Controller.SetTestMode(false); Session=new(()=>Adapter.Snapshot,()=>Controller,()=>Config,(_,_,_,_,_)=>{},friends); }
        public void Dispose() { Session.Dispose(); Controller.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
    private sealed class Adapter : ILocalToyAdapter, ILocalToyOutput
    {
        public event Action? Changed { add {} remove {} } public event Action<string>? OutputFailed { add {} remove {} }
        public ConcurrentQueue<(string Channel,int Value)> Writes { get; } = new();
        public bool BlockZero;
        public ManualResetEventSlim ZeroEntered { get; }=new(false);
        public ManualResetEventSlim AllowZero { get; }=new(false);
        public ToyAdapterSnapshot Snapshot { get; }
        public Adapter(int count) { Snapshot = new(ToyAdapterAvailability.Ready,false,"Fixture",Enumerable.Range(0,count).Select(i=>new LocalToyDescriptor(i==0 ? "fixture" : "fixture-"+i,"simulation","Fixture "+i,"Mock",ToyConnectionState.Connected,
            [new("v1",ToyCapabilityType.Vibration,"Vibration",0,20,1,null,null),new("pump",ToyCapabilityType.Pump,"Pump",0,3,1,null,null)],true,70,DateTimeOffset.UtcNow,10,true)).ToArray()); }
        public void SendLevel(string device,string channel,int value)
        {
            if(value==0 && BlockZero) {ZeroEntered.Set(); if(!AllowZero.Wait(3000)) throw new TimeoutException("Simulated zero remained blocked");}
            Writes.Enqueue((channel,value));
        }
        public Task StartScanAsync(CancellationToken c=default)=>Task.CompletedTask; public Task StopScanAsync(CancellationToken c=default)=>Task.CompletedTask;
        public Task ConnectAsync(string key,CancellationToken c=default)=>Task.CompletedTask; public Task DisconnectAsync(string key,CancellationToken c=default)=>Task.CompletedTask;
        public Task ReadBatteryAsync(string key,CancellationToken c=default)=>Task.CompletedTask; public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
