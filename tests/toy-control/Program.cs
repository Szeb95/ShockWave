using System.Net;
using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using ShockWave.ShaderLab;
using ShockWave.Overlay;
using ShockWave.Overlay.Toys;
using ShockWave.ToyControl.Shared;

internal static class Program
{
    private static int passed;
    private static string artifacts="";
    private static void Check(bool condition,string label) { if(!condition) throw new Exception(label); passed++; Console.WriteLine("PASS "+label); }
    private static void Wait(Func<bool> condition,string label,int milliseconds=3500)
    {
        var until=DateTime.UtcNow.AddMilliseconds(milliseconds);
        while(!condition()) { if(DateTime.UtcNow>until) throw new Exception("Timed out: "+label); Thread.Sleep(5); }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); artifacts=args.FirstOrDefault(a=>!a.StartsWith("--")) ?? Path.Combine(AppContext.BaseDirectory,"checks"); Directory.CreateDirectory(artifacts);
        try
        {
            LatchedRecord(); Leases(); HeldRenewals(); ZeroCrossings(); OwnerEditDuringZero(); FailedZero(); SixFunctions(); Session(); RowContacts(); LateStartup(); Pointer(); Udp(); Heartbeats(); ClearFeedback(); Graphics(args.Contains("--warp")); Design(args.Contains("--warp")); Navigation();
            ShockerControlChecks.Run(Check,artifacts,args.Contains("--warp"));
            File.WriteAllText(Path.Combine(artifacts,"report.json"),JsonSerializer.Serialize(new { Passed=passed, PhysicalOutput=false, NativeLibraryLoaded=false, VrChatClient=false }));
            Console.WriteLine($"Toy control: {passed} checks passed"); return 0;
        }
        catch(Exception e) { File.WriteAllText(Path.Combine(artifacts,"failure.txt"),e.ToString()); Console.Error.WriteLine(e); return 1; }
    }
    private static void Leases()
    {
        using var fixture=new Fixture(); var c=fixture.Controller;
        c.SetPreference(c.State("fixture","v1").Preference with {Sensitivity=50,Maximum=30});
        c.SetTestMode(false);
        var lease=c.BeginInput("fixture","v1",Guid.NewGuid(),80,TimeSpan.FromSeconds(2));
        Wait(()=>fixture.Adapter.Writes.Any(w=>w.Channel=="v1" && w.Value==6),"capped scalar");
        Check(c.State("fixture","v1").RequestedPercent==80 && c.State("fixture","v1").SentPercent==30,"curve request uses owner sensitivity and maximum before native quantization");
        c.Request("fixture","v1",14);
        Check(!c.UpdateInput(lease,100,TimeSpan.FromSeconds(2)) && c.State("fixture","v1").RequestedPercent==14,"owner input immediately invalidates old curve generation");
        c.Stop(); Wait(()=>!c.SchedulerState.ActiveFunctions.Equals(1),"stop");
        Check(!c.UpdateInput(lease,90,TimeSpan.FromSeconds(2)) && c.State("fixture","v1").RequestedPercent==0,"Stop rejects stale renewals without replay");
        Wait(()=>c.State("fixture","v1").Armed,"stop settles");
        var expire=c.BeginInput("fixture","v1",Guid.NewGuid(),70,TimeSpan.FromMilliseconds(120));
        Wait(()=>c.State("fixture","v1").RequestedPercent==0,"independent source watchdog");
        Check(!c.UpdateInput(expire,80,TimeSpan.FromSeconds(2)),"hardware gate expires a silent host independently of UI ticking");
        Wait(()=>c.State("fixture","v1").Armed,"watchdog stop settles");
        c.SetTestMode(true); var before=fixture.Adapter.Writes.Count;
        var test=c.BeginInput("fixture","v1",Guid.NewGuid(),100,TimeSpan.FromSeconds(2)); Thread.Sleep(40);
        Check(c.State("fixture","v1").SentPercent==30 && fixture.Adapter.Writes.Count==before,"Test mode curve input previews capped state without hardware submission");
        c.SetTestMode(false);
        Check(!c.UpdateInput(test,100,TimeSpan.FromSeconds(2)) && c.State("fixture","v1").RequestedPercent==0,"changing Test mode requires fresh input");
    }
    private static void LatchedRecord()
    {
        using var f=new Fixture(); using var s=new ToyControlSession(()=>f.Adapter.Snapshot,()=>f.Controller);
        s.Tick(ToyOscRouter.Clock); s.Action("InRow0",false); s.Action("InRecord",false);
        s.Draw(new(600,300),false); s.Model.AdvanceTo(s.Model.Now+.3); s.Release();
        var first=s.Model.Functions.First().Function.Pattern;
        Check(s.Model.RecordArmed && s.Model.LoopCount==1,"Record remains enabled after completing a desktop/VR shared capture");
        s.Draw(new(600,400),false); s.Model.AdvanceTo(s.Model.Now+.4); s.Release();
        Check(s.Model.RecordArmed && s.Model.LoopCount==1 && s.Model.Functions.First().Function.Pattern!=first,"next touch records a replacement without pressing Record again");
        s.StopAll(); Check(s.Model.RecordArmed && s.Model.LoopCount==0,"Stop all clears output without changing the latched Record setting");
        s.Action("InRecord",false); Check(!s.Model.RecordArmed,"only switching Record off clears the Record setting");
    }
    private static void HeldRenewals()
    {
        using var f=new Fixture(); var c=f.Controller; c.SetTestMode(false); f.Adapter.BlockGuard=true;
        var lease=c.BeginInput("fixture","v1",Guid.NewGuid(),60,TimeSpan.FromSeconds(2));
        Wait(()=>f.Adapter.GuardEntered.IsSet,"pending guarded submission");
        try { for(var i=0;i<10;i++) c.UpdateInput(lease,60,TimeSpan.FromSeconds(2)); }
        finally { f.Adapter.BlockGuard=false; f.Adapter.AllowGuard.Set(); }
        Wait(()=>f.Adapter.Writes.Any(w=>w.Value==12),"held value submission");
        Check(c.SchedulerState.CanceledBeforeSubmit==0,"unchanged lease renewals do not cancel a current write waiting for the transport");
    }
    private static void ZeroCrossings()
    {
        using var f=new Fixture(); var c=f.Controller; c.SetTestMode(false);
        var lease=c.BeginInput("fixture","v1",Guid.NewGuid(),60,TimeSpan.FromSeconds(2));
        Wait(()=>f.Adapter.Writes.Any(w=>w.Value==12),"initial nonzero submission");
        f.Adapter.BlockZero=true;
        Check(c.UpdateInput(lease,0,TimeSpan.FromSeconds(2)),"a live pattern can request zero");
        Wait(()=>f.Adapter.ZeroEntered.IsSet,"zero submission in flight");
        Check(c.UpdateInput(lease,70,TimeSpan.FromSeconds(2)) && c.UpdateInput(lease,90,TimeSpan.FromSeconds(2)),"rising from zero keeps the live lease and replaces pending levels");
        Check(!f.Adapter.Writes.Any(w=>w.Value is 14 or 18),"the required zero completes before the next positive level");
        f.Adapter.AllowZero.Set();
        Wait(()=>f.Adapter.Writes.Any(w=>w.Value==18),"latest level after zero");
        Check(!f.Adapter.Writes.Any(w=>w.Value==14),"no superseded value is replayed after zero");
        f.Adapter.ZeroEntered.Reset(); f.Adapter.AllowZero.Reset();
        c.UpdateInput(lease,0,TimeSpan.FromSeconds(2)); Wait(()=>f.Adapter.ZeroEntered.IsSet,"second zero");
        c.UpdateInput(lease,80,TimeSpan.FromSeconds(2)); c.Stop(); f.Adapter.AllowZero.Set();
        Wait(()=>c.State("fixture","v1").Armed,"owner Stop settles");
        Check(!c.UpdateInput(lease,80,TimeSpan.FromSeconds(2)) && c.State("fixture","v1").RequestedPercent==0 && !f.Adapter.Writes.Any(w=>w.Value==16),"owner Stop invalidates a queued rise and its lease");
    }
    private static void RowContacts()
    {
        using var f=new Fixture(); using var s=new ToyControlSession(()=>f.Adapter.Snapshot,()=>f.Controller);
        var now=ToyOscRouter.Clock; s.Tick(now);
        void Put(string name,object value)=>s.Vrc.Apply(new("/avatar/parameters/"+ToyControlContract.Prefix+name,[value]),now);
        Put("Visible",true); Put("InBeat",true); s.Tick(now);
        Put("InRow0",true); s.Tick(now); now+=.04; Put("InRow0",false); s.Tick(now);
        now+=.03; Put("InRow0",true); Put("InY",.63f); Put("InTouch",true); s.Tick(now);
        Check(s.Model.Functions.First().Selected && f.Controller.State("fixture","v1").RequestedPercent==63,"brief row contact bounce while moving to draw cannot deselect the function");
        Put("InTouch",false); Put("InRow0",false); s.Tick(now); now+=.3; Put("InRow0",true); s.Tick(now);
        Check(!s.Model.Functions.First().Selected && f.Controller.State("fixture","v1").RequestedPercent==63,"leaving the row then touching it again deliberately deselects while preserving the held value");
    }
    private static void Pointer()
    {
        using var f=new Fixture(); using var s=new ToyControlSession(()=>f.Adapter.Snapshot,()=>f.Controller);
        var now=ToyOscRouter.Clock; s.Tick(now);
        void Put(string name,object value)=>s.Vrc.Apply(new("/avatar/parameters/"+ToyControlContract.Prefix+name,[value]),now);
        void Tick() { now+=.01; s.Tick(now); }
        void Aim(string hand,float x,float y,bool hit=true)
        { Put("InPointer"+hand+"X",x/1240); Put("InPointer"+hand+"Y",1-(y-100)/608); Put("InPointer"+hand+"Hit",hit); }
        void Press(string hand,bool value) { Put("InPointer"+hand+"Press",value); Tick(); }
        Put("Visible",true); Put("InBeat",true); Tick();
        Check(!s.PointerMode && ToyControlLayout.Hit(new(720,148))==28,"Touch is the default and mode button sits beside Play loop");
        Put("InInputMode",true); Tick(); Put("InInputMode",false); Tick();
        Check(s.PointerMode,"physical mode-button edge switches to Pointer");
        Aim("L",100,270); Tick();
        Check(s.Model.SelectedCount==0 && f.Controller.State("fixture","v1").RequestedPercent==0,"left-hand aiming alone selects nothing and sends no input");
        Press("L",true); Tick();
        Check(s.Model.SelectedCount==1,"left-hand click selects exactly once while held"); Press("L",false);
        Aim("L",600,300); Aim("R",600,530); Press("L",true);
        var left=f.Controller.State("fixture","v1").RequestedPercent;
        Check(s.TouchFromVrc && left==80,"left pointer draws at its own normalized board coordinates");
        Press("R",true); Tick();
        Check(f.Controller.State("fixture","v1").RequestedPercent==left,"right-hand click cannot move a drawing held by the left hand");
        Press("L",false); Tick();
        Check(!s.Model.Touching && f.Controller.State("fixture","v1").RequestedPercent==left,"releasing the drawing hand holds its final input without transferring to an already-held other hand");
        Press("R",false); Press("R",true);
        Check(s.TouchFromVrc && f.Controller.State("fixture","v1").RequestedPercent==15,"a fresh right-hand press can draw independently");
        Aim("R",600,530,false); Tick(); Aim("R",600,300); Tick();
        Check(!s.Model.Touching && f.Controller.State("fixture","v1").RequestedPercent==15,"missing the panel cancels a drag and held re-entry does not restart it");
        Press("R",false); Press("R",true);
        Check(s.Model.Touching && f.Controller.State("fixture","v1").RequestedPercent==80,"a new press after a miss can start drawing again");
        s.Action("InRecord",false); // Desktop takeover must block the held pointer.
        Tick(); Check(!s.Model.Touching && s.Model.RecordArmed,"desktop pattern action cancels and blocks a held pointer");
        Press("R",false); Press("R",true); now+=.4; Tick();
        Aim("R",600,300,false); Press("R",false);
        Check(s.Model.LoopCount==0 && !s.Model.Touching,"simultaneous ray loss and gesture release cancels capture instead of committing a loop");
        Aim("R",600,300); Press("R",true); now+=.4; Tick();
        s.Action("InInputMode",false); Tick();
        Check(!s.PointerMode && !s.Model.Touching && s.Model.LoopCount==0 && s.Model.RecordArmed,"mode switch cancels unfinished capture without committing a loop or disabling Record");
        s.Action("InInputMode",false); Tick();
        Check(!s.Model.Touching,"switching back cannot replay a held pointer press");
        Press("R",false); Press("R",true); Press("R",false);
        Aim("R",720,148); Press("R",true);
        Check(!s.PointerMode,"right-hand pointer can click its own mode button back to Touch");
        Press("R",false); Put("InY",.43f); Put("InTouch",true); Tick();
        Check(s.Model.Touching && f.Controller.State("fixture","v1").RequestedPercent==43,"fresh finger touch still works after returning to Touch");
        Put("InTouch",false); Tick(); s.Action("InInputMode",false); Tick();
        Put("InY",.9f); Put("InTouch",true); Tick();
        Check(!s.Model.Touching && f.Controller.State("fixture","v1").RequestedPercent==43,"finger graph input is ignored in Pointer mode");
        Put("InStop",true); Tick();
        Check(f.Controller.State("fixture","v1").RequestedPercent==0,"expression-menu Stop remains usable in Pointer mode");
        Check(!s.Vrc.Apply(new("/avatar/parameters/"+ToyControlContract.Prefix+"InPointerLX",[float.NaN]),now),"invalid pointer coordinates are rejected");
        s.Vrc.Reset("Avatar changed"); Press("L",true);
        Check(f.Controller.State("fixture","v1").RequestedPercent==0,"pointer input cannot act after avatar link reset");
        using var peer=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        using var router=new ToyOscRouter(0,((IPEndPoint)peer.Client.LocalEndPoint!).Port);
        router.Send(s); Thread.Sleep(20); var feedback=new List<OscMessage>(); IPEndPoint remote=new(IPAddress.Any,0);
        while(peer.Available>0) feedback.AddRange(OscCodec.Decode(peer.Receive(ref remote)));
        Check(feedback.Any(m=>m.Address.EndsWith("Rx_Pointer") && (float)m.Arguments[0]==1),"pointer mode reaches the avatar through the actual display-feedback UDP path");
    }
    private static void FailedZero()
    {
        using var f=new Fixture(); var c=f.Controller; c.SetTestMode(false);
        var lease=c.BeginInput("fixture","v1",Guid.NewGuid(),60,TimeSpan.FromSeconds(2));
        Wait(()=>f.Adapter.Writes.Any(w=>w.Value>0),"positive before failed zero");
        f.Adapter.BlockZero=true; f.Adapter.FailZero=true;
        c.UpdateInput(lease,0,TimeSpan.FromSeconds(2)); Wait(()=>f.Adapter.ZeroEntered.IsSet,"failing zero in flight");
        c.UpdateInput(lease,90,TimeSpan.FromSeconds(2)); f.Adapter.AllowZero.Set();
        Wait(()=>c.State("fixture","v1").InputSource==Guid.Empty,"failed zero invalidates source");
        Check(!c.UpdateInput(lease,90,TimeSpan.FromSeconds(2)) && !c.State("fixture","v1").Armed && c.State("fixture","v1").RequestedPercent==0 && !f.Adapter.Writes.Any(w=>w.Value==18),"failed zero discards the queued rise and closes the toy gate without replay");
    }
    private static void OwnerEditDuringZero()
    {
        using var f=new Fixture(); var c=f.Controller; c.SetTestMode(false);
        var lease=c.BeginInput("fixture","v1",Guid.NewGuid(),60,TimeSpan.FromSeconds(2));
        Wait(()=>f.Adapter.Writes.Any(w=>w.Value==12),"output before owner takeover");
        f.Adapter.BlockZero=true; c.UpdateInput(lease,0,TimeSpan.FromSeconds(2));
        Wait(()=>f.Adapter.ZeroEntered.IsSet,"zero before owner takeover"); c.UpdateInput(lease,90,TimeSpan.FromSeconds(2));
        try { c.Request("fixture","v1",25); }
        finally { f.Adapter.AllowZero.Set(); }
        Wait(()=>f.Adapter.Writes.Any(w=>w.Value==5),"fresh owner level after zero");
        Check(!c.UpdateInput(lease,90,TimeSpan.FromSeconds(2)) && c.State("fixture","v1").RequestedPercent==25 && !f.Adapter.Writes.Any(w=>w.Value==18),"owner edit during a required zero replaces the queued VR rise and invalidates its lease");
    }
    private static void SixFunctions()
    {
        using var f=new Fixture(six:true); using var s=new ToyControlSession(()=>f.Adapter.Snapshot,()=>f.Controller);
        f.Controller.SetTestMode(false); s.Tick(ToyOscRouter.Clock);
        for(var i=0;i<6;i++) s.Action("InRow"+i,false);
        var elapsed=Stopwatch.StartNew(); var tick=0;
        while(elapsed.Elapsed.TotalSeconds<2)
        {
            var level=(tick++%18+1)*.05f;
            s.Draw(new(600,PanelLayout.Graph.Y+PanelLayout.Graph.Height*(1-level)),false);
            s.Tick(ToyOscRouter.Clock); Thread.Sleep(33);
        }
        s.Release(); var writes=f.Adapter.Writes.Where(w=>w.Value>0).ToArray();
        Check(s.Model.Functions.Count()==6 && s.Model.Functions.All(r=>r.Selected) && writes.Select(w=>(w.Device,w.Channel)).Distinct().Count()==6,"six changing functions across four toys retain selection and all receive output");
        Check(writes.GroupBy(w=>(w.Device,w.Channel)).All(g=>g.Count()>=3),"the default scheduler does not starve a motor sharing its toy");
        Check(writes.GroupBy(w=>w.Device).All(g=>g.Zip(g.Skip(1),(a,b)=>(b.Time-a.Time)/(double)Stopwatch.Frequency).All(gap=>gap>=.19)),"default five updates per second apply to each toy across its motors");
        var cadence=writes.GroupBy(w=>(w.Device,w.Channel)).Select(g=>new {FunctionsOnToy=f.Adapter.Snapshot.Devices.Single(d=>d.DeviceKey==g.Key.Device).Capabilities.Count,
            Writes=g.Count(),MeanIntervalMs=g.Zip(g.Skip(1),(a,b)=>(b.Time-a.Time)*1000d/Stopwatch.Frequency).Average()}).ToArray();
        File.WriteAllText(Path.Combine(artifacts,"six-functions-cadence.json"),JsonSerializer.Serialize(new {Mock=true,PhysicalOutput=false,PerToyTarget=f.Controller.SchedulerState.PerToyTarget,Cadence=cadence}));
    }
    private static void LateStartup()
    {
        using var f=new Fixture(); using var s=new ToyControlSession(()=>f.Adapter.Snapshot,()=>f.Controller);
        var now=ToyOscRouter.Clock; s.Tick(now);
        void Put(string name,object value)=>s.Vrc.Apply(new("/avatar/parameters/"+ToyControlContract.Prefix+name,[value]),now);
        Put("InBeat",false); Put("InTouch",true); Put("InY",.9f); Put("InRow0",true); s.Tick(now);
        Check(!s.Vrc.IsLive,"one heartbeat value cannot infer an already-visible panel");
        now+=.25; Put("InBeat",true); s.Tick(now);
        Check(s.Vrc.IsLive && f.Controller.State("fixture","v1").RequestedPercent==0 && !s.Model.Touching,"alternating visible-only pulses connect an already-open panel without replaying held input");
        Put("InTouch",false); Put("InRow0",false); s.Tick(now); now+=.3;
        Put("InRow0",true); Put("InTouch",true); Put("InY",.6f); s.Tick(now);
        Check(f.Controller.State("fixture","v1").RequestedPercent==60,"fresh selection and touch work after late-start connection");
        Put("Visible",false); s.Tick(now); now+=.25; Put("InBeat",false); s.Tick(now); now+=.25; Put("InBeat",true); s.Tick(now);
        Check(!s.Vrc.IsLive && f.Controller.State("fixture","v1").RequestedPercent==0,"an explicit hidden panel cannot be reopened by heartbeat inference");
    }
    private static void Session()
    {
        using var f=new Fixture(); using var session=new ToyControlSession(()=>f.Adapter.Snapshot,()=>f.Controller);
        var now=ToyOscRouter.Clock; session.Tick(now);
        Check(session.Model.FunctionCount==2 && session.Model.Functions.All(r=>r.Level==0),"connected inventory supplies independent functions without initial activity");
        void Put(string name,object value) => session.Vrc.Apply(new("/avatar/parameters/"+ToyControlContract.Prefix+name,[value]),now);
        bool beat=true;
        void Beat(double seconds) { now+=seconds; Put("InBeat",!beat); beat=!beat; session.Tick(now); }
        void Rows(params string[] names) { foreach(var name in names) Put(name,false); now+=.21; foreach(var name in names) Put(name,true); }
        Put("Visible",true); Put("InBeat",true); session.Tick(now);
        Check(session.Vrc.IsLive,"visible local avatar heartbeat establishes the control session");
        Put("InRow0",true); Put("InRow1",true); Put("InRecord",true); Put("InY",.8f); Put("InX",.2f); Put("InTouch",true); session.Tick(now);
        Check(session.Model.Recording && f.Controller.State("fixture","v1").RequestedPercent==80 && f.Controller.State("fixture","v2").RequestedPercent==80,"VR row group and Record/touch route through the actual controller");
        Beat(.3); Put("InY",.3f); session.Tick(now); Beat(.3);
        Put("InTouch",false); Put("InY",0f); session.Tick(now);
        Check(session.Model.LoopCount==2 && session.Model.Functions.All(r=>r.Function.Pattern is not null),"release commits a shared RAM pattern without receiver-reset zero");
        Rows("InRow0","InRow1"); session.Tick(now); Beat(.17);
        Check(session.Model.Functions.All(r=>!r.Selected && r.Function.LoopRunning),"deselection preserves independently timed running functions");
        f.Adapter.Rename("Fixture renamed",64); Beat(.17);
        Check(session.Model.LoopCount==2 && session.Model.Toys[0].Name=="Fixture renamed","battery/name refresh preserves loops and bindings");
        f.Controller.Request("fixture","v1",14); Beat(.1);
        Check(!session.Model.Functions.First().Function.LoopRunning && session.Model.Functions.First().Level==.14f && session.Model.Functions.Last().Function.LoopRunning,"desktop Input replaces only the affected loop and value");
        var frame=new VrFeedback().Frame(session.Model,true);
        Check(frame["S00_State"]>0 && frame.Keys.Count==541 && frame.Values.All(v=>float.IsFinite(v) && v>=0 && v<=1),"bounded numeric feedback includes active deselected patterns without identifiers");
        // An owner edit during a held touch cannot be taken back by that touch.
        Rows("InRow0"); Put("InTouch",true); Put("InY",.9f); Beat(.1);
        f.Controller.Request("fixture","v1",23); Beat(.1);
        Check(f.Controller.State("fixture","v1").RequestedPercent==23 && !session.Model.Functions.First().Selected,"held VR touch cannot undo a desktop edit");
        Put("InTouch",false); session.Tick(now);
        Rows("InRow0"); Put("InTouch",true); Put("InY",.6f); Beat(.1);
        Check(f.Controller.State("fixture","v1").RequestedPercent==60,"fresh selection/touch can take control again");
        session.Draw(new PointF(600,PanelLayout.Graph.Y+PanelLayout.Graph.Height*.6f),false); session.Tick(now);
        Check(f.Controller.State("fixture","v1").RequestedPercent==40,"desktop curve takes over a currently held VR touch");
        Put("Visible",false); session.Tick(now);
        Check(f.Controller.State("fixture","v1").RequestedPercent==40 && f.Controller.State("fixture","v2").RequestedPercent==0,"avatar hide stops VR sources while preserving desktop-owned input");
        session.Release(false); session.Action("InRemote",false); session.Tick(now);
        Check(f.Controller.State("fixture","v1").RequestedPercent==40 && session.Model.Route==0,"legacy destination inputs leave the unified local/remote list and current intent intact");
        f.Adapter.Connect(false); session.Tick(now+.1); f.Adapter.Connect(true); session.Tick(now+.2);
        Check(session.Model.LoopCount==0 && session.Model.Functions.All(r=>r.Level==0 && r.Function.Pattern is null),"disconnect/reconnect clears clips and current intent without replay");
    }
    private static void Udp()
    {
        using var f=new Fixture(); using var session=new ToyControlSession(()=>f.Adapter.Snapshot,()=>f.Controller); session.Tick(ToyOscRouter.Clock);
        using var peer=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)); var send=((IPEndPoint)peer.Client.LocalEndPoint!).Port;
        using var router=new ToyOscRouter(0,send); using var input=new UdpClient(); var received=new List<OscMessage>();
        void Send(params byte[][] messages) { var p=ToyOscRouter.Bundle(messages); input.Send(p,p.Length,new IPEndPoint(IPAddress.Loopback,router.ListenPort)); }
        byte[] In(string n,object value)=>OscCodec.Encode("/avatar/parameters/"+ToyControlContract.Prefix+n,value);
        Send(In("Visible",true),In("InBeat",true),In("InRow0",true),In("InY",.7f),In("InTouch",true));
        Wait(()=> { router.Tick(session,(_,m)=>received.AddRange(m)); session.Tick(ToyOscRouter.Clock); return f.Controller.State("fixture","v1").RequestedPercent==70; },"real loopback VR input");
        Check(received.Count==5 && session.Vrc.IsLive,"one real UDP socket feeds typed control packets");
        Send(OscCodec.Encode("/avatar/parameters/"+ToyControlContract.ReceivePrefix+"Level",.99f),In("InY",float.NaN));
        Thread.Sleep(20); router.Tick(session,(_,_)=>{}); session.Tick(ToyOscRouter.Clock);
        Check(f.Controller.State("fixture","v1").RequestedPercent==70,"feedback echo and invalid coordinates cannot alter output");
        router.Send(session); peer.Client.ReceiveTimeout=2000;
        IPEndPoint remote=new(IPAddress.Any,0); var replies=new List<OscMessage>();
        replies.AddRange(OscCodec.Decode(peer.Receive(ref remote)));
        Check(replies.All(m=>m.Address.StartsWith("/avatar/parameters/"+ToyControlContract.ReceivePrefix) || m.Address.EndsWith("RxBeat")),"feedback sends only display/heartbeat addresses");
        Send(In("InTouch",false),In("InY",0f)); Thread.Sleep(20); router.Tick(session,(_,_)=>{}); session.Tick(ToyOscRouter.Clock);
        Check(f.Controller.State("fixture","v1").RequestedPercent==70,"ordinary touch release holds input despite reset Y=0");
        Send(In("InTouch",true),In("InY",.9f)); Thread.Sleep(310); router.Tick(session,(_,_)=>{}); session.Tick(ToyOscRouter.Clock);
        Check(router.Dropped>0 && f.Controller.State("fixture","v1").RequestedPercent==70,"late queued packets are discarded rather than replayed");
        session.Tick(ToyOscRouter.Clock+2.1);
        Check(!session.Vrc.IsLive && f.Controller.State("fixture","v1").RequestedPercent==0,"avatar heartbeat expiry stops held hardware intent");
    }
    private static void ClearFeedback()
    {
        using var f=new Fixture(); using var session=new ToyControlSession(()=>f.Adapter.Snapshot,()=>f.Controller);
        using var peer=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        using var router=new ToyOscRouter(0,((IPEndPoint)peer.Client.LocalEndPoint!).Port);
        session.Tick(ToyOscRouter.Clock); session.Action("InRow0",false); session.Draw(new(600,PanelLayout.Graph.Y+PanelLayout.Graph.Height*.3f),false); session.Release();
        float start=1;
        void ReadFeedback() { IPEndPoint remote=new(IPAddress.Any,0); while(peer.Available>0) foreach(var m in OscCodec.Decode(peer.Receive(ref remote))) if(m.Address.EndsWith("Rx_CurveStart")) start=(float)m.Arguments[0]; }
        for(var i=0;i<12;i++) { session.Model.AdvanceTo(session.Model.Now+.5); router.Send(session); ReadFeedback(); Thread.Sleep(60); }
        var before=start; session.Action("InClear",false); session.Model.AdvanceTo(session.Model.Now+.125); router.Send(session); ReadFeedback();
        Check(before<.5 && start>.9 && f.Controller.State("fixture","v1").RequestedPercent==70,"Clear removes avatar feedback history while desktop rendering is hidden without stopping output");
    }
    private static void Heartbeats()
    {
        using var f=new Fixture(); using var session=new ToyControlSession(()=>f.Adapter.Snapshot,()=>f.Controller);
        var now=ToyOscRouter.Clock; session.Tick(now);
        void Put(string name,object value)=>session.Vrc.Apply(new("/avatar/parameters/"+ToyControlContract.Prefix+name,[value]),now);
        Put("Visible",true); Put("InBeat",true); session.Tick(now); Put("InRow0",true); Put("InY",.6f); Put("InTouch",true); session.Tick(now);
        Put("InTouch",false); session.Tick(now);
        now+=1.75; session.Tick(now);
        Check(session.Vrc.IsLive && session.Vrc.Interruptions==0 && f.Controller.State("fixture","v1").RequestedPercent==60,"a short pulse interruption preserves the established two-second lease window");
        now+=.26; session.Tick(now);
        Check(!session.Vrc.IsLive && session.Vrc.Interruptions==1 && session.Vrc.WaitingReason=="Panel heartbeat interrupted" && f.Controller.State("fixture","v1").RequestedPercent==0,"heartbeat expiry still stops output and records the precise interruption reason");
        Put("InBeat",false); session.Tick(now);
        Check(session.Vrc.IsLive && f.Controller.State("fixture","v1").RequestedPercent==0,"fresh heartbeat restores the link without restarting previous toy activity");
        Put("Visible",false);
        Check(session.Vrc.WaitingReason=="Panel hidden" && session.Vrc.Interruptions==2,"hiding the panel is distinguished from lost heartbeat");
        using var peer=new UdpClient(new IPEndPoint(IPAddress.Loopback,0)); using var router=new ToyOscRouter(0,((IPEndPoint)peer.Client.LocalEndPoint!).Port);
        router.Send(session); peer.Client.ReceiveTimeout=2000; IPEndPoint remote=new(IPAddress.Any,0);
        var first=OscCodec.Decode(peer.Receive(ref remote)).ToArray();
        Check(first.Length==1 && first[0].Address.EndsWith("RxBeat"),"freshness pulse is a separate first datagram before the full display batch");
    }
    private static void Graphics(bool warp)
    {
        using var f=new Fixture(); using var session=new ToyControlSession(()=>f.Adapter.Snapshot,()=>f.Controller); session.Tick(ToyOscRouter.Clock);
        session.Action("InRow0",false); session.Action("InRecord",false); session.Draw(new(600,300),false);
        session.Model.AdvanceTo(session.Model.Now+.3); session.Draw(new(600,500),false); session.Model.AdvanceTo(session.Model.Now+.3); session.Release();
        session.Action("InRow0",false);
        using var renderer=new GpuRenderer(Path.Combine(AppContext.BaseDirectory,"ToyControl","Shaders","Panel.hlsl"),1240,608,forceWarp:warp);
        using var labels=ToyControlLabels.Create(session); renderer.UploadLabels(labels); renderer.UploadLoopPatterns(session.Model);
        renderer.Render(ShaderState.From(session.Model,1240,608,0,0,0),false);
        using var image=renderer.Capture(); image.Save(Path.Combine(artifacts,"toy-control.png"));
        Check(image.GetPixel(0,0).A==0 && image.GetPixel(1239,0).A==0 && image.GetPixel(0,607).A==0 && image.GetPixel(1239,607).A==0,"desktop HLSL gives all four corners transparent alpha");
        Check(labels.GetPixel(580,650).A==0 && labels.GetPixel(400,50).A==0,"removed Zero/header labels leave no ghost text");
        var ink=0; for(var y=632;y<674;y++) for(var x=960;x<1175;x++) if(labels.GetPixel(x,y).A>100) ink++;
        Check(ink>250,"connected status text is rendered at the bottom right");
        Check(renderer.CompileError is null && session.Model.LoopCount==1,"accepted pattern layout compiles and draws through production HLSL");
        session.Action("InInputMode",false);
        using(var pointerLabels=ToyControlLabels.Create(session)) renderer.UploadLabels(pointerLabels);
        renderer.Render(ShaderState.From(session.Model,1240,608,0,28,0),false);
        using(var pointerImage=renderer.Capture()) pointerImage.Save(Path.Combine(artifacts,"toy-control-pointer.png"));
        session.Action("InInputMode",false);
        Check(ToyControlLayout.Hit(new(95,204))==11 && ToyControlLayout.Hit(new(245,204))==13 && ToyControlLayout.Hit(new(400,146))==25 && ToyControlLayout.Hit(new(400,196))==0,"relocated Clear/Stop/Record click targets match the visible controls, leaving the title inactive");
        Check(ToyControlLayout.Hit(new(100,275))==1 && ToyControlLayout.Hit(new(100,590))==6 && ToyControlLayout.Hit(new(750,650))==0,"all six toy rows remain selectable and the former Stop location is inactive");
        session.Model.Functions.Last().Function.Selected=true;
        var frame=new VrFeedback().Frame(session.Model,true);
        foreach(var name in ToyControlContract.FeedbackFields()) frame.TryAdd(name,0);
        frame["Pointer"]=session.PointerMode ? 1 : 0;
        File.WriteAllText(Path.Combine(artifacts,"feedback.json"),JsonSerializer.Serialize(new {version=ToyControlContract.Version,values=frame.Select(v=>new {name=v.Key,value=v.Value}).ToArray()}));
    }
    private static void Navigation()
    {
        using var toys=new LocalToysPage(); using var form=new OverlayForm(new BridgeConfig(),initialSaveConfig:_=>{},initialLocalToys:toys);
        Button Field(string name)=>(Button)typeof(OverlayForm).GetField(name,BindingFlags.Instance|BindingFlags.NonPublic)!.GetValue(form)!;
        Check(Field("toyControlNav").Text.Contains("Toy control") && Field("toyControlNav").Top>Field("toyPanelNav").Top && Field("toyControlNav").Top<Field("logsNav").Top,"Toy control navigation sits directly below Toy panel");
        Check(ToyControlContract.BoolInputs.Length+ToyControlContract.FloatInputs.Length+1+ToyControlContract.FeedbackFields().Length==592 && ToyControlContract.Rows==7 && ToyControlContract.Version==5,"v5 contract retains seven rows and adds page/slider input and shocker feedback without synced parameters");
        Check(Field("shockerControlNav").Top>Field("toyControlNav").Top && Field("shockerControlNav").Top<Field("spsNav").Top,"new Shocker control tab sits below Toy control");
    }
    private static void Design(bool warp)
    {
        using var f=new Fixture(); using var session=new ToyControlSession(()=>f.Adapter.Snapshot,()=>f.Controller);
        session.Tick(ToyOscRouter.Clock); var model=session.Model;
        ToyUpdate Inventory(int count) => PanelModel.Validate("design","Design toy",true,67,null,
            Enumerable.Range(1,count).Select(i=>new FunctionUpdate("f"+i,"Function "+i)).ToArray());
        model.ReplaceInventory([Inventory(7)]);
        Check(model.PageCount==1 && model.Visible.Count()==7 && ToyControlLayout.Hit(new(100,654),model)==7,
            "seven functions fit on one page and the seventh row replaces the pager");
        model.ToggleVisible(6);
        var frame=new VrFeedback().Frame(model,true);
        Check(model.SelectedCount==1 && frame[ToyControlContract.Row(6,"State")]>0 && frame["Selected"]==1/256f,
            "seventh-row selection and selected-count feedback reach the avatar");
        model.ReplaceInventory([Inventory(8)]); model.ChangePage(1);
        Check(model.PageCount==2 && model.Visible.First().Function.Id=="f7" && model.Visible.Count()==2,
            "adding an eighth function moves function seven to page two");
        frame=new VrFeedback().Frame(model,true);
        Check(frame[ToyControlContract.Row(6,"State")]==0 && ToyControlLayout.Hit(new(66,667),model)==15,
            "paged feedback clears the hidden seventh slot and restores page controls");
        model.ReplaceInventory([Inventory(7)]);
        Check(model.Page==0 && model.PageCount==1 && model.Visible.Count()==7,
            "removing the eighth function returns to a single seven-row page");
        model.ToggleVisible(6);
        Check(model.SelectedCount==0 && ToyControlLayout.Hit(new(700,400),model)==0,
            "no selected function leaves the graph unavailable to drawing input");
        model.DrawAt(new(700,400)); Check(!model.Touching && model.Functions.All(row=>row.Level==0),"drawing without selection starts no input or recording");
        using var renderer=new GpuRenderer(Path.Combine(AppContext.BaseDirectory,"ToyControl","Shaders","Panel.hlsl"),1240,608,forceWarp:warp);
        using(var labels=ToyControlLabels.Create(session)) renderer.UploadLabels(labels);
        renderer.Render(ShaderState.From(model,1240,608,0,0,0),false);
        using(var image=renderer.Capture())
        { image.Save(Path.Combine(artifacts,"toy-control-seven-empty.png")); var p=image.GetPixel(600,400); Check(p.R==43 && p.G==45 && p.B==49,"unselected graph has no grid or waveform surface"); }
        model.ToggleVisible(0); model.DrawAt(new(800,400));
        using(var labels=ToyControlLabels.Create(session)) renderer.UploadLabels(labels);
        renderer.Render(ShaderState.From(model,1240,608,0,0,0),false);
        using(var image=renderer.Capture())
        { image.Save(Path.Combine(artifacts,"toy-control-seven-selected.png")); var p=image.GetPixel(800,300); Check(p.G>p.R && p.G>p.B,"the touch emblem uses the theme green"); }
        model.Release(false); model.ReplaceInventory([Inventory(8)]); model.ChangePage(1);
        using(var labels=ToyControlLabels.Create(session)) renderer.UploadLabels(labels);
        renderer.Render(ShaderState.From(model,1240,608,0,0,0),false);
        using(var image=renderer.Capture()) image.Save(Path.Combine(artifacts,"toy-control-page-two.png"));
    }
    private sealed class Fixture : IDisposable
    {
        public FakeAdapter Adapter { get; }=new();
        public LocalToyController Controller { get; }
        public Fixture(bool six=false) { if(six) Adapter.SixFunctions=true; Controller=new(Adapter,new LocalToyControlStore(),automaticControls:true); }
        public void Dispose() { Controller.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
    private sealed class FakeAdapter : ILocalToyAdapter,ILocalToyOutput
    {
        private LocalToyDescriptor device=new("fixture","fake","Fixture toy","Fixture",ToyConnectionState.Connected,
            [new("v1",ToyCapabilityType.Vibration,"Vibration 1",0,20,1,true,null),new("v2",ToyCapabilityType.Vibration,"Vibration 2",0,20,1,true,null)],true,67,DateTimeOffset.UtcNow,10,true);
        public event Action? Changed;
        public event Action<string>? OutputFailed { add { } remove { } }
        public System.Collections.Concurrent.ConcurrentQueue<(string Device,string Channel,int Value,long Time)> Writes { get; }=new();
        public bool BlockZero, FailZero, SixFunctions, BlockGuard;
        public ManualResetEventSlim ZeroEntered { get; }=new(false);
        public ManualResetEventSlim AllowZero { get; }=new(false);
        public ManualResetEventSlim GuardEntered { get; }=new(false);
        public ManualResetEventSlim AllowGuard { get; }=new(false);
        public ToyAdapterSnapshot Snapshot=>new(ToyAdapterAvailability.Ready,false,"Fake adapter",SixFunctions ? Enumerable.Range(0,4).Select(i=>device with {DeviceKey="fixture"+i,Capabilities=i<2 ? device.Capabilities : device.Capabilities.Take(1).ToArray()}).ToArray() : [device]);
        public void Rename(string name,int battery) { device=device with {DisplayName=name,BatteryPercent=battery}; Changed?.Invoke(); }
        public void Connect(bool connected) { device=device with {ConnectionState=connected ? ToyConnectionState.Connected : ToyConnectionState.Disconnected}; Changed?.Invoke(); }
        public void SendLevel(string key,string channel,int value)
        {
            if(value==0 && BlockZero) { ZeroEntered.Set(); if(!AllowZero.Wait(3000)) throw new TimeoutException("Mock zero release"); }
            if(value==0 && FailZero) throw new IOException("Mock zero failure");
            Writes.Enqueue((key,channel,value,Stopwatch.GetTimestamp()));
        }
        public bool TrySendLevel(string key,string channel,int value,Func<bool> isCurrent)
        {
            if(BlockGuard) { GuardEntered.Set(); if(!AllowGuard.Wait(3000)) throw new TimeoutException("Mock transport wait"); }
            if(!isCurrent()) return false;
            SendLevel(key,channel,value); return true;
        }
        public Task StartScanAsync(CancellationToken c=default)=>Task.CompletedTask;
        public Task StopScanAsync(CancellationToken c=default)=>Task.CompletedTask;
        public Task ConnectAsync(string key,CancellationToken c=default)=>Task.CompletedTask;
        public Task DisconnectAsync(string key,CancellationToken c=default)=>Task.CompletedTask;
        public Task ReadBatteryAsync(string key,CancellationToken c=default)=>Task.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
