using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using ShockWave.ShaderLab;
using ShockWave.Overlay;
using ShockWave.Overlay.Sps;
using ShockWave.Overlay.Toys;

internal static class Program
{
    private static int passed;
    private static readonly string artifacts = Path.Combine(AppContext.BaseDirectory, "sps-checks");
    private const string A = "/avatar/parameters/OGB/Pen/Fixture/TouchOthers", B = "/avatar/parameters/VFH/Zone/Touch/Chest/Self";
    private const string Handjob = "/avatar/parameters/OGB/Orf/Handjob_Handjob_Right/PenSelfNewTip";
    private static SpsRule Rule(string address = A, string channel = "v1") => new() { Name = "Fixture mapping", Address = address, Targets = [new("fixture", channel)] };
    private static void Check(bool condition, string label) { if (!condition) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
    private static void Wait(Func<bool> condition, string label)
    { var end = DateTime.UtcNow.AddSeconds(3); while (!condition()) { if (DateTime.UtcNow > end) throw new Exception("Timed out: " + label); Thread.Sleep(5); } }
    private static void Put(SpsSession session, string address, object value, double now) => session.Apply(new(address, [value]), now);
    [STAThread]
    private static int Main(string[] args)
    {
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false); Directory.CreateDirectory(artifacts);
        try
        {
            if(args.SequenceEqual(new[] { "--read-vrchat" })) return LiveQuery();
            if(args.SequenceEqual(new[] { "--reproduce-pump" })) return PumpReproduction();
            Defaults(); AutomaticLayout(); GlobalSources(); ActivationStates(); ScanRouting(); ReleaseOrdering(); RecordRecovery(); AllRouting(); ReceivedInputs(); DiscoveryWithoutListener(); Discovery(); Parsing(); Mapping(); Automatic(); Ownership(); Faults(); Timing(); ZeroCrossings(); Storage(); Socket(); BoardModel(); Depth(); Query(); LiveRows(); Ui();
            File.WriteAllText(Path.Combine(artifacts, "report.json"), JsonSerializer.Serialize(new { Passed = passed, PhysicalDevice = false, NativeDll = false, VrChat = false }));
            Console.WriteLine($"SPS: {passed} checks passed"); return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); File.WriteAllText(Path.Combine(artifacts, "failure.txt"), error.ToString()); return 1; }
    }
    private static void ActivationStates()
    {
        const string stem="/avatar/parameters/OGB/Orf/Mouth";
        OscMessage[] contacts=[new(stem+"/PenSelfNewTip",[0f])];
        var on=SpsActivation.Resolve(contacts,[new("/avatar/parameters/VF123_Mouth",[true])]);
        Check(on.TryGetValue(stem,out var enabled) && enabled,"zero-contact socket resolves its unique VRCFury On switch independently of penetration");
        var state=new SpsActivation(); state.Apply(on,10);
        Check(state.Get(stem,10.1)==true && state.Get(stem,12.01) is null,"socket On display expires without a fresh query");
        state.Apply(new Dictionary<string,bool> {{stem,false}},9);
        Check(state.Get(stem,10.2)==true,"older enable-state frames cannot replace a newer state");
        state.Apply(new Dictionary<string,bool> {{stem,false}},11);
        Check(state.Get(stem,11.1)==false,"explicit socket Off is separate from unknown state");
        state.Reset(); Check(state.Get(stem,11.2) is null,"reset clears enabled indicators");
        Check(SpsActivation.Resolve(contacts,[new("/avatar/parameters/VF1_Mouth",[true]),new("/avatar/parameters/VF2_Mouth",[false])]).Count==0,
            "ambiguous same-name menu switches do not claim socket state");
        Check(SpsActivation.Resolve(contacts,[new("/avatar/parameters/MouthOpen",[true]),new("/avatar/parameters/VF1_Mouth",[1f])]).Count==0,
            "unrelated face inputs and floats cannot become socket On indicators");
        var nodes=new Dictionary<string,object> {
            ["tip"]=new {FULL_PATH=stem+"/PenSelfNewTip",TYPE="f",ACCESS=3,VALUE=new[]{0f}},
            ["enable"]=new {FULL_PATH="/avatar/parameters/VF10_Mouth",TYPE="T",ACCESS=3,VALUE=new[]{true}}
        };
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(new { CONTENTS=new { change=new {VALUE=new[]{"activation-fixture"}},parameters=new {CONTENTS=nodes} } }));
        var frame=SpsOscQuery.Parse(json.RootElement,10,10);
        Check(frame.Messages.Length==1 && frame.SocketEnabled.GetValueOrDefault(stem) && frame.Messages.All(m=>m.Address!= "/avatar/parameters/VF10_Mouth"),
            "query enable switches remain display metadata and never become routing inputs");
    }
    private static void AutomaticLayout()
    {
        var storePath=Path.Combine(artifacts,"automatic-layout.json"); var boardPath=Path.ChangeExtension(storePath,".board.json");
        File.Delete(storePath); File.Delete(boardPath); var boardStore=new SpsBoardStore(boardPath);
        Check(boardStore.Load([]).AllToysToAllHoles,"a fresh SPS setup defaults All toys to all holes on");
        Check(!boardStore.Load([],legacyFileExists:true).AllToysToAllHoles,"an existing legacy setup retains manual routing");
        const string root="/avatar/parameters/OGB/Orf/Mouth/PenOthersNewRoot",tip="/avatar/parameters/OGB/Orf/Mouth/PenOthersNewTip";
        var graph=SpsBoardModel.FromRules([Rule(tip) with { RootAddress=root }]);
        graph.Holes[0].Inputs.Add(root);
        SpsBoardModel.Discover(graph,SpsSignals.Describe("/avatar/parameters/OGB/Orf/Anal/TouchOthers")!);
        SpsBoardModel.Discover(graph,SpsSignals.Describe("/avatar/parameters/OGB/Orf/Anal/TouchSelf")!);
        SpsBoardModel.Discover(graph,SpsSignals.Describe("/avatar/parameters/OGB/Orf/Pussy/TouchOthers")!);
        boardStore.Save(graph); Check(!boardStore.Load([]).AllToysToAllHoles,"a saved explicit manual setting is preserved");
        graph=graph with { AllToysToAllHoles=true }; boardStore.Save(graph);
        var manual=JsonSerializer.Serialize(graph.Wires);
        using var toys=new LocalToysPage(_=>new FakeAdapter());
        using var page=new SpsPage(toys,()=>true,new SpsStore(storePath)); page.Size=new(1240,608); page.PerformLayout(); _=page.Handle;
        toys.StartRememberedConnections(); while(toys.Busy) {Application.DoEvents();Thread.Sleep(5);}
        page.Tick(); var board=page.Board;
        Check(board.AutomaticPanel.Visible && board.AutomaticPanel.Cards.Count==3,"automatic mode replaces the human with the added hole settings list");
        Check(board.AutomaticPanel.SourceOptions.Switches.Count==6 && Descendants(board.AutomaticPanel).OfType<ThemeToggle>().Count()==6,
            "automatic mode presents exactly one shared set of six source switches for all holes");
        var now=ToyOscRouter.Clock;
        page.ReceiveQuery(QueryFrame("automatic-fixture",0,0,now) with { SocketEnabled=new Dictionary<string,bool> {
            [SpsBoardModel.Stem(tip)]=true,[graph.Holes[1].Stem]=false } });
        Thread.Sleep(35); page.Tick();
        Check(board.HoleEnabled!(page.Document.Holes[0])==true && !board.HoleActive!(page.Document.Holes[0]) && page.Session.ActiveTargets==0,
            "socket On is green with no penetrator and cannot create output");
        var manualHoles=JsonSerializer.Serialize(graph.Holes);
        board.AutomaticPanel.SourceOptions.Switches[SpsSources.MyPlugs].Checked=false;
        Check(page.Document.AutomaticSources== (SpsSources.All & ~SpsSources.MyPlugs) && SpsBoardStore.Read(boardPath).AutomaticSources==page.Document.AutomaticSources
            && JsonSerializer.Serialize(page.Document.Holes)==manualHoles,"the shared switches persist globally without changing manual hole settings or positions");
        page.Receive(new("/avatar/parameters/OGB/Orf/Anal/TouchSelf",[.5f]),ToyOscRouter.Clock);
        Check(board.HoleActive!(page.Document.Holes[1]),"automatic contact indicators use the shared switches rather than saved manual filters");
        board.AutomaticPanel.SourceOptions.Switches[SpsSources.MyBody].Checked=false;
        Check(!board.HoleActive!(page.Document.Holes[1]) && page.Session.Rules.All(r=>!r.Address.EndsWith("/TouchSelf")),
            "disabling one shared source removes both its contact display and automatic routes");
        board.AutomaticPanel.SourceOptions.Switches[SpsSources.MyBody].Checked=true;
        page.Receive(new("/avatar/parameters/OGB/Orf/Anal/TouchSelf",[0f]),ToyOscRouter.Clock);
        var toy=board.Rows.First().Target; var gear=board.ToyGearBox(0);
        Check(gear.Left>board.LeftBox.Right && board.RightBox.Contains(gear) && !board.AutomaticPanel.Bounds.IntersectsWith(Rectangle.Round(gear)),
            "automatic toy gear sits inside the right panel beside its toy, outside the toy list and hole cards");
        page.OptionsPresenter=options=> { Check(options is SpsWireOptions && options.Text=="Toy SPS options","automatic gear opens that function's SPS settings");
            Field<RadioButton>(options,"motion").Checked=true; Click(Descendants(options).OfType<Button>().Single(b=>b.Text=="Apply")); };
        Tap(board,Center(gear));
        Check(page.Document.AutomaticToyOptions.Single().Target==toy && page.Document.AutomaticToyOptions[0].Response==SpsResponse.Motion
            && page.Session.Rules.Where(r=>r.Targets.Contains(toy)).All(r=>r.Response==SpsResponse.Motion),
            "automatic response settings apply only to the selected toy function");
        Check(page.Session.Rules.Where(r=>!r.Targets.Contains(toy)).All(r=>r.Response==SpsResponse.Level) && JsonSerializer.Serialize(page.Document.Wires)==manual,
            "other automatic functions and saved manual connections keep their settings");
        var settingsBefore=File.ReadAllText(boardPath); page.OptionsPresenter=options=>options.DialogResult=DialogResult.Cancel; Tap(board,Center(gear));
        Check(File.ReadAllText(boardPath)==settingsBefore,"Cancel on automatic toy options preserves saved routing");
        foreach(var width in new[]{1240,1040,790})
        {
            page.Size=new(width,608); page.PerformLayout(); Thread.Sleep(35); page.Tick();
            Check(board.RightBox.Contains(board.ToyGearBox(0)) && board.AutomaticPanel.Width>200 && board.AutomaticPanel.Cards.All(c=>c.Width>150),
                "automatic gears and visible hole settings fit at "+width+"px");
            using var image=Capture(board); image.Save(Path.Combine(artifacts,"sps-automatic-"+width+".png"));
        }
        Tap(board,Center(board.AllToysBox));
        Check(!page.Document.AllToysToAllHoles && !board.AutomaticPanel.Visible && board.ManualHintBox.Right<=board.StatusBox.Left
            && JsonSerializer.Serialize(page.Document.Wires)==manual,"manual mode restores the map/lines with a hint immediately left of SPS connection status");
        using(var image=Capture(board)) image.Save(Path.Combine(artifacts,"sps-manual-hint.png"));
        page.Shutdown(); File.Delete(boardPath);
    }
    private static int PumpReproduction()
    {
        var cases=new List<object>();
        foreach(var allHoles in new[]{false,true})
        foreach(var mode in new[]{"Desktop","Touch","Pointer"})
        foreach(var movingMotor in new[]{false,true})
        {
            using var f=new Fixture(); f.Adapter.UseMotorAndPump(); f.Controller.SetTestMode(false);
            using var sps=f.Session(); using var graph=new ToyControlSession(()=>f.Adapter.Snapshot,()=>f.Controller);
            var document=SpsBoardModel.FromRules([Rule()]) with { AllToysToAllHoles=allHoles };
            sps.Configure(SpsBoardModel.Compile(document,[new("fixture","v1"),new("fixture","p")]),ToyOscRouter.Clock);
            graph.Tick(ToyOscRouter.Clock); sps.Tick(ToyOscRouter.Clock,true);
            var beat=false; var step=0;
            void Input(string name,object value) => graph.Vrc.Apply(new("/avatar/parameters/SzebsToy_"+name,[value]),ToyOscRouter.Clock);
            void Tick()
            {
                var now=ToyOscRouter.Clock; var level=movingMotor ? .4f+(step++%6)*.1f : .6f;
                Put(sps,A,level,now); sps.Tick(now,true);
                Input("InBeat",beat=!beat); graph.Tick(now);
            }
            void PumpFor(int milliseconds)
            { var until=Environment.TickCount64+milliseconds; do { Tick(); Thread.Sleep(10); } while(Environment.TickCount64<until); }
            void Aim(float x,float y)
            { Input("InPointerLX",x/1240); Input("InPointerLY",1-(y-100)/608); Input("InPointerLHit",true); }
            void Draw(float level)
            {
                if(mode=="Desktop") graph.Draw(new(600,PanelLayout.Graph.Y+PanelLayout.Graph.Height*(1-level)),false);
                else if(mode=="Touch") { Input("InY",level); Input("InX",.4f); Input("InTouch",true); }
                else { Aim(600,PanelLayout.Graph.Y+PanelLayout.Graph.Height*(1-level)); Input("InPointerLPress",true); }
                Tick();
            }
            Input("Visible",true); Tick();
            if(mode=="Desktop") graph.Action("InRow1",false);
            else if(mode=="Touch") { Input("InRow1",true); Tick(); Input("InRow1",false); Tick(); }
            else
            {
                Input("InInputMode",true); Tick(); Input("InInputMode",false); Tick();
                var row=ToyControlLayout.Row(1); Aim(row.Left+30,row.Top+20);
                Input("InPointerLPress",true); Tick(); Input("InPointerLPress",false); Tick();
            }
            var selectedImmediately=graph.Model.Functions.Single(r=>r.Function.Id=="p").Selected;
            // A user needs time to move from the function row to the graph.
            // Keep both app timers running through that ordinary delay.
            PumpFor(300);
            var selected=graph.Model.Functions.Single(r=>r.Function.Id=="p").Selected;
            Draw(.9f); PumpFor(450);
            var pumpState=f.Controller.State("fixture","p"); var draw=pumpState.RequestedPercent;
            var curveOwnsPump=pumpState.InputSource==Field<Guid>(graph,"source");
            // Start a fresh recording after releasing the first draw.
            if(mode=="Desktop") graph.Release(); else if(mode=="Touch") Input("InTouch",false); else Input("InPointerLPress",false);
            Tick();
            if(!graph.Model.RecordArmed) graph.Action("InRecord",mode!="Desktop");
            Draw(.9f); PumpFor(240); Draw(.4f); PumpFor(240);
            if(mode=="Desktop") graph.Release(); else if(mode=="Touch") Input("InTouch",false); else Input("InPointerLPress",false);
            Tick(); PumpFor(650);
            var loops=graph.Model.Functions.Single(r=>r.Function.Id=="p").Function.LoopRunning;
            var pumpWrites=f.Adapter.Writes.Where(w=>w.Channel=="p").Select(w=>w.Value).ToArray();
            var motor=f.Controller.State("fixture","v1");
            var spsOwnsMotor=motor.InputSource==Field<Guid>(sps,"source");
            var broken=!selected || draw<85 || !curveOwnsPump || !loops || !pumpWrites.Contains(2) || !spsOwnsMotor;
            cases.Add(new { AllToysToAllHoles=allHoles,Mode=mode,MovingSpsMotor=movingMotor,PumpIncludedBySps=sps.Rules.Any(r=>r.Targets.Any(t=>t.ChannelId=="p")),
                PumpSelectedImmediately=selectedImmediately,PumpSelectedAfterMovingToGraph=selected,PumpDrawRequest=draw,CurveOwnsPump=curveOwnsPump,PumpLoopRunning=loops,SpsOwnsMotor=spsOwnsMotor,
                PumpNativeWrites=pumpWrites,MotorRequest=motor.RequestedPercent,MotorNativeWrites=f.Adapter.Writes.Count(w=>w.Channel=="v1" && w.Value>0),Reproduced=broken });
            Console.WriteLine($"{(broken?"REPRODUCED":"NOT REPRODUCED")} {(allHoles?"all holes":"manual links")}, {mode}, {(movingMotor?"moving":"steady")} SPS motor: pump selected={selectedImmediately}->{selected}, curve owns pump={curveOwnsPump}, looping={loops}, motor stays SPS={spsOwnsMotor}");
        }
        File.WriteAllText(Path.Combine(artifacts,"pump-reproduction.json"),JsonSerializer.Serialize(new { Source="current working source",PhysicalOutput=false,Cases=cases },new JsonSerializerOptions { WriteIndented=true }));
        return 0;
    }
    private static void Parsing()
    {
        var pen = SpsSignals.Describe(A); var touch = SpsSignals.Describe(B);
        Check(pen is { Kind: "Plug", Zone: "Fixture", Interaction: "Others" } && touch is { Kind: "Touch", Zone: "Chest", Interaction: "Self" }, "verified plug and touch namespaces expose zone and self/other metadata");
        Check(SpsSignals.Describe("/avatar/parameters/VF12_OGB/Orf/Socket/PenSelfNewRoot") is { Kind: "Socket", Measurement: "Root proximity (1 m)" }, "socket root contact is labelled as proximity rather than invented depth");
        Check(SpsSignals.Describe("/avatar/parameters/OGB/Orf/Socket/PenOthersNewTip")?.Measurement == "Tip proximity (1 m)", "tip contact is separate from root and calibrated depth");
        Check(SpsSignals.Describe("/avatar/parameters/OGB/Pen/P/FrotOthersClose")?.Measurement.Contains("Rubbing") == true, "close rubbing contact is discovered");
        Check(SpsSignals.Describe("/avatar/parameters/OGB/Pen/P/Invented") is null && SpsSignals.Describe("/other/OGB/Pen/P/TouchSelf") is null, "unknown fields and non-avatar namespaces do not auto-discover");
        Check(!SpsSignals.SafeAddress("/avatar/parameters/VF12_SzebsToy_Rx_Online") && !SpsSignals.SafeAddress("/avatar/parameters/PiCtrl_FeedbackEvent") && !SpsSignals.SafeAddress("/avatar/parameters/OGB_ENABLED") && !SpsSignals.SafeAddress("/avatar/parameters/OGB/*"), "avatar feedback, presence and wildcard addresses are forbidden as mapping input");
        using var f = new Fixture(); using var session = f.Session(); var now = ToyOscRouter.Clock;
        Check(session.Apply(new(A, [.5f]), now) && session.Apply(new(B, [true]), now), "finite scalar and bool signals are monitored without output permission");
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 0 && session.Rules.Count == 0, "automatic discovery cannot route an input without a toy assignment");
        Check(SpsSignals.Describe("/avatar/parameters/OGB/Orf/Left Hand/TouchOthers") is { Zone: "Left Hand" } && SpsSignals.Describe("/avatar/parameters/VF3_OGB/Orf/CustomSocket2/PenOthersNewTip") is { Zone: "CustomSocket2" }, "generated names, spaces, custom socket IDs and duplicate suffixes need no manual naming");
        var rejected = session.Rejected;
        foreach (var bad in new object[] { float.NaN, float.PositiveInfinity, 1.1f, -.1f, "0.5" }) session.Apply(new(A, [bad]), now);
        Check(session.Rejected == rejected + 5 && session.Signals.All(s => s.Address != A), "invalid known input types and ranges invalidate cached values");
        var custom = Rule("/avatar/parameters/InjectedDepth") with { InputMin = -.2, InputMax = .4 }; session.Configure([custom], now);
        Check(session.Apply(new(custom.Address, [-.1f]), now) && Math.Abs(session.Level(custom, now) - 1d / 6) < .00001, "explicit custom depth input uses calibrated scalar range without guessing its meaning");
        for (var i = 0; i < 270; i++) session.Apply(new($"/avatar/parameters/VFH/Zone/Touch/Zone{i}/Others", [.1f]), now);
        Check(session.Signals.Count == 256 && session.Rejected > rejected + 5, "monitor capacity is bounded without unbounded new-zone allocation");
    }
    private static void DiscoveryWithoutListener()
    {
        Check(SpsSignals.Describe(Handjob) is { Kind:"Socket",Zone:"Handjob_Handjob_Right",Interaction:"Self" }
            && SpsDiscovery.Describe(Handjob) is { Kind:"Socket",Name:"Handjob_Handjob_Right" },
            "the owner's exact Handjob self-tip parameter is a recognized discovery socket");
        using var handler=new QueryHandler { Avatar=JsonSerializer.Serialize(new { CONTENTS=new {
            change=new { VALUE=new[]{"blocked-port-fixture-avatar"} }, parameters=new { CONTENTS=new Dictionary<string,object> {
                ["tip"]=new { FULL_PATH=Handjob,TYPE="f",ACCESS=3,VALUE=new[]{0f} }
            } } } }) };
        using var client=new System.Net.Http.HttpClient(handler); using var reader=new SpsOscQuery(client,()=>[12345]);
        using var toys=new LocalToysPage(_=>new FakeAdapter());
        var store=new SpsStore(Path.Combine(artifacts,"blocked-port-discovery.json")); var boardFile=Path.ChangeExtension(store.PathName,".board.json");
        File.Delete(store.PathName); File.Delete(boardFile);
        var listening=false; using var page=new SpsPage(toys,()=>listening,store);
        typeof(SpsPage).GetField("query",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(page,reader);
        page.Board.SetDiscoveryMode(true);
        var deadline=DateTime.UtcNow.AddSeconds(2);
        do { page.Tick(); Thread.Sleep(5); } while(page.DiscoveryCandidates.Count==0 && DateTime.UtcNow<deadline);
        Thread.Sleep(35); page.Tick();
        Check(page.DiscoveryCandidates.SingleOrDefault() is { Name:"Handjob_Handjob_Right",SupportedInputs.Length:1 },
            "read-only query discovery finds an unchanged zero Handjob socket while the UDP listener is unavailable");
        Check(page.Session.Signals.Count==0 && page.Session.ActiveTargets==0 && !File.Exists(boardFile),
            "blocked-port query discovery neither feeds routing signals nor saves a marker or creates output");
        Check(page.Board.Discoveries.Single().Name=="Handjob_Handjob_Right" && page.Board.Status=="OSC UNAVAILABLE"
            && page.Board.StatusDetail.Contains("paused",StringComparison.OrdinalIgnoreCase),
            "discovery displays the socket while clearly reporting paused toy output");
        page.ReceiveQuery(new("blocked-port-fixture-avatar",[new(Handjob,[.9f])],ToyOscRouter.Clock,ToyOscRouter.Clock)); page.Tick();
        Check(page.Session.Signals.Count==0 && page.Session.ActiveTargets==0,
            "positive query contacts remain metadata while the shared listener is blocked");
        page.AddDiscovered(page.DiscoveryCandidates.Single().Id);
        Check(page.Document.Holes.Single() is { DiscoveryOnly:false,Inputs.Count:1 }
            && page.Document.Wires.Count==0 && !page.Document.Holes[0].Sources.HasFlag(SpsSources.MyPlugs),
            "Add during a port conflict preserves assignment and self-contact defaults");
        reader.Reset(); typeof(SpsPage).GetField("query",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(page,null);
        listening=true; page.Tick();
        Check(page.Session.Signals.Count==0 && page.Session.ActiveTargets==0,
            "restoring the listener cannot replay discovery-only contact values");
        Thread.Sleep(5); page.ReceiveQuery(new("blocked-port-fixture-avatar",[new(Handjob,[.5f])],ToyOscRouter.Clock,ToyOscRouter.Clock));
        Check(page.Session.Signals.Single().Address==Handjob && page.Session.ActiveTargets==0,
            "only a fresh post-recovery query enters routing and still needs permitted assignment");
        var stale=reader.ReadOnceAsync(9000).GetAwaiter().GetResult()!; Thread.Sleep(5); listening=false; page.ResetInput(); page.ReceiveQuery(stale);
        Check(page.DiscoveryCandidates.Count==0 && page.Session.Signals.Count==0,
            "port-independent discovery retains reset freshness barriers");
        page.Shutdown(); reader.Tick(true,9000,_=>throw new Exception("Closed query delivered discovery."));
        Check(page.DiscoveryCandidates.Count==0,"closing still cancels port-independent query discovery");
        File.Delete(boardFile);
    }
    private static void Discovery()
    {
        const string root="/avatar/parameters/OGB/Orf/Mouth/PenOthersNewRoot",tip="/avatar/parameters/OGB/Orf/Mouth/PenOthersNewTip";
        const string plug="/avatar/parameters/VF7_OGB/Pen/Lollipop/PenOthers",legacy="/avatar/parameters/TPS_Internal/Orf/Legacy/Depth_In";
        var catalog=new SpsDiscovery(); double at=10;
        foreach(var input in new[] { new OscMessage(root,[0f]),new(tip,[0f]),new(plug,[false]),new(legacy,[2f]) }) catalog.Observe(input,at);
        var candidates=catalog.Candidates(at);
        Check(candidates.Count==3 && candidates.Single(c=>c.Name=="Mouth").Inputs.Length==2,"discovery groups root/tip and finds zero-valued sockets and plugs by full stem");
        Check(candidates.Single(c=>c.Name=="Legacy") is { Kind:"Socket",SupportedInputs.Length:0 },"unfamiliar TPS socket is visible as metadata without inventing motor semantics");
        foreach(var address in new[] { "/avatar/parameters/v2/MouthOpen","/avatar/parameters/FT/SocketOpen","/avatar/parameters/VRCFaceTracking/JawOpen",
            "/avatar/parameters/PiCtrl_Action","/avatar/parameters/PiCtrl_Intensity","/avatar/parameters/PiCtrl_Duration","/avatar/parameters/PiCtrl_FeedbackActive",
            "/avatar/parameters/ShockWaveToy_Rx_Level","/avatar/parameters/OGB_ENABLED","/avatar/parameters/MySocket","/avatar/parameters/MouthSocket",
            "/avatar/parameters/VFH/Zone/Touch/Face/Others" }) catalog.Observe(new(address,[.5f]),at);
        Check(catalog.Candidates(at).Count==3,"face tracking, shock controls, feedback, anatomical names and body-only touch are absent from plug/socket discovery");
        catalog.Observe(new("/avatar/parameters/OGB/Orf/Invalid/TouchOthers",[float.NaN]),at);
        catalog.Observe(new("/avatar/parameters/OGB/Orf/Invalid/TouchOthers",[1.5f]),at);
        catalog.Observe(new("/avatar/parameters/TPS_Internal/Pen/Text/Depth_In",["private"]),at);
        Check(catalog.Candidates(at).Count==3,"invalid, out-of-range known and text candidates are rejected");
        catalog.Observe(new("/avatar/parameters/OGB/Pen/Mouth/PenOthers",[0f]),at);
        Check(catalog.Candidates(at).Count(c=>c.Name=="Mouth")==2,"same-named socket and plug remain distinct candidates");
        catalog.Observe(new(root,[.8f]),at-1);
        Check(catalog.Candidates(at).Single(c=>c.Kind=="Socket" && c.Name=="Mouth").SeenAt==at,"older discovery packets cannot overwrite a newer receipt");
        Check(catalog.Candidates(at+121).Count==0,"unrefreshed discovery entries expire after two minutes");
        for(var i=0;i<70;i++) catalog.Observe(new($"/avatar/parameters/OGB/Orf/Zone{i}/TouchOthers",[0f]),at+122);
        Check(catalog.Candidates(at+122).Count==64,"discovery is bounded to 64 grouped zones");
        catalog.Observe(new("/avatar/parameters/OGB/Orf/AfterExpiry/TouchOthers",[0f]),at+243);
        Check(catalog.Candidates(at+243).Single().Name=="AfterExpiry","stale capacity is reclaimed before a new one-shot contact arrives");
        catalog.Reset(); for(var i=0;i<20;i++) catalog.Observe(new($"/avatar/parameters/TPS_Internal/Orf/Many/Field{i}",[0f]),at);
        Check(catalog.Candidates(at).Single().Inputs.Length==16,"discovery input grouping has a per-zone bound");
        catalog.Reset(); for(var i=0;i<16;i++) catalog.Observe(new($"/avatar/parameters/OGB/Orf/Priority/Unknown{i}",[0f]),at);
        catalog.Observe(new("/avatar/parameters/OGB/Orf/Priority/TouchOthers",[0f]),at);
        Check(catalog.Candidates(at).Single() is { Inputs.Length:16,SupportedInputs.Length:1 },"known contact fields take priority over unverified metadata at the input bound");
        using(var listBoard=new SpsBoard { Size=new(1240,608) })
        {
            listBoard.Discoveries=Enumerable.Range(0,15).Select(i=>new SpsDiscoveryRow("row-"+i,"Fixture "+i,"Socket","/avatar/parameters/OGB/Orf/Fixture"+i,2,false,true)).ToArray();
            listBoard.SetDiscoveryMode(true); var listAdded=new List<string>(); listBoard.DiscoveryAddRequested+=id=>listAdded.Add(id);
            var wheelAt=Center(listBoard.BodyBox);
            Mouse(listBoard,"OnMouseWheel",wheelAt); // Zero wheel does not change selection.
            typeof(Control).GetMethod("OnMouseWheel",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(listBoard,[new HandledMouseEventArgs(MouseButtons.None,0,wheelAt.X,wheelAt.Y,-120)]);
            Tap(listBoard,Center(listBoard.DiscoveryAddBox(0)));
            Check(listBoard.DiscoveryPage==1 && listAdded.SequenceEqual(["row-"+listBoard.DiscoveryPageSize]),"discovery wheel paging targets the displayed candidate without editing settings");
            listBoard.Discoveries=listBoard.Discoveries.Take(1).ToArray(); Tap(listBoard,Center(listBoard.DiscoveryAddBox(0)));
            Check(listBoard.DiscoveryPage==0 && listAdded.Last()=="row-0","a shrinking or reset inventory safely clamps its discovery page");
            listBoard.SetDiscoveryMode(false);
            Check(!listBoard.DiscoveryMode && !listBoard.Dragging,"leaving discovery restores the board without a pending drag");
        }

        var parameters=new Dictionary<string,object>();
        void Node(string path,string type,object value,int access=1) => parameters[path]=new { FULL_PATH=path,TYPE=type,VALUE=new[]{value},ACCESS=access };
        Node(root,"f",0f); Node(tip,"f",0f); Node(legacy,"f",2f); Node("/avatar/parameters/SPS/Plug/Extra/Depth","i",0);
        Node("/avatar/parameters/v2/MouthOpen","f",.8f); Node("/avatar/parameters/PiCtrl_Action","i",1);
        Node("/avatar/parameters/TPS_Internal/Pen/WriteOnly/Depth_In","f",.5f,2);
        Node("/avatar/parameters/TPS_Internal/Pen/Bad/Depth_In","s","ignored");
        var tree=JsonSerializer.Serialize(new { CONTENTS=new { change=new { VALUE=new[]{"discovery-fixture-avatar"} },parameters=new { CONTENTS=parameters } } });
        using var json=JsonDocument.Parse(tree); var receipt=ToyOscRouter.Clock;
        var frame=SpsOscQuery.Parse(json.RootElement,receipt,receipt);
        Check(frame.Messages.Length==2 && frame.DiscoveryMessages.Length==2 && frame.DiscoveryMessages.Any(m=>m.Address==legacy),
            "query inventory exposes only plausible readable numeric SPS metadata while preserving the strict routing frame");
        using var toys=new LocalToysPage(_=>new FakeAdapter()); var store=new SpsStore(Path.Combine(artifacts,"discovery-fixture.json"));
        var boardFile=Path.ChangeExtension(store.PathName,".board.json"); File.Delete(store.PathName); File.Delete(boardFile);
        using var page=new SpsPage(toys,()=>true,store); page.ReceiveQuery(frame);
        Check(page.DiscoveryCandidates.Count==3 && page.Document.Holes.Count==0 && !File.Exists(boardFile),"query discovery collects unchanged zero contacts without saving or adding markers");
        var known=page.DiscoveryCandidates.Single(c=>c.Name=="Mouth"); page.AddDiscovered(known.Id);
        Check(page.Document.Holes.Single().Inputs.Count==2 && !page.Document.Holes[0].DiscoveryOnly && page.Document.Wires.Count==0,
            "explicit Add groups supported socket inputs into one persistent marker");
        var unknown=page.DiscoveryCandidates.Single(c=>c.Name=="Legacy"); page.AddDiscovered(unknown.Id);
        var saved=SpsBoardStore.Read(boardFile);
        Check(saved.Holes.Single(h=>h.Id==unknown.Id).DiscoveryOnly && saved.Holes.Count==2,"unsupported discovery icon round-trips as explicitly nonrouting metadata in graph v1");
        saved.Wires.Add(new() { HoleId=unknown.Id,Target=new("fixture","v1") }); saved=saved with { AllToysToAllHoles=true };
        Check(SpsBoardModel.Compile(saved).Count==0 && SpsBoardModel.Compile(saved,[new("fixture","v1")]).All(r=>r.Name!="Legacy"),
            "unsupported discovery icons cannot become normal or all-holes output rules");
        var before=File.ReadAllText(boardFile); page.AddDiscovered(known.Id); page.AddDiscovered("missing");
        Check(File.ReadAllText(boardFile)==before && page.Session.ActiveTargets==0,"duplicate/stale Add changes neither saved connections nor output");
        page.Receive(new(root,[.7f]),ToyOscRouter.Clock);
        Check(page.Document.Holes.Count==2,"a new value refreshes an added marker without creating another icon");
        var unsupportedBefore=page.Document.Holes.Single(h=>h.Id==unknown.Id);
        var invalidMetadata=page.Document.Copy(); invalidMetadata.Holes[1]=unsupportedBefore with { Inputs=["/avatar/parameters/PiCtrl_Intensity"] };
        var rejectedMetadata=false; try { SpsBoardStore.Validate(invalidMetadata); } catch(InvalidDataException) { rejectedMetadata=true; }
        Check(rejectedMetadata,"discovery-only saved metadata cannot smuggle a shock signal into its SPS stem");
        var staleFrame=frame; Thread.Sleep(5); page.ResetInput(); page.ReceiveQuery(staleFrame); page.Receive(new(legacy,[0f]),receipt);
        Check(page.DiscoveryCandidates.Count==0 && page.Document.Holes.Count==2,"reset rejects old query/UDP discovery while preserving the saved body map");
        Thread.Sleep(5); page.ReceiveQuery(frame with { Avatar="next-fixture-avatar",RequestedAt=ToyOscRouter.Clock });
        page.Receive(new("/avatar/change",["changed-fixture-avatar"]),ToyOscRouter.Clock);
        Check(page.DiscoveryCandidates.Count==0 && page.Document.Holes.Count==2,"avatar change clears the discovery inventory without deleting saved markers");
        var closedBefore=File.ReadAllText(boardFile); page.Shutdown(); page.Receive(new(root,[0f]),ToyOscRouter.Clock); page.AddDiscovered(known.Id);
        Check(page.DiscoveryCandidates.Count==0,"closed discovery cannot collect or add late input");
        Check(File.ReadAllText(boardFile)==closedBefore,"late Add after shutdown cannot mutate the saved layout");
        File.Delete(boardFile);
    }
    private static void ScanRouting()
    {
        using var fixture = new Fixture(); using var session = fixture.Session(); var now = ToyOscRouter.Clock;
        session.Configure([Rule()], now); session.Tick(now, true); Put(session, A, .4f, now + .01); session.Tick(now + .01, true);
        fixture.Adapter.Scan(true);
        session.Tick(now + .02, true);
        Check(fixture.Controller.State("fixture", "v1").RequestedPercent == 40, "concurrent-scan backend preserves an existing SPS lease");
        Put(session, A, .7f, now + .03); session.Tick(now + .03, true);
        Check(fixture.Controller.State("fixture", "v1").RequestedPercent == 70, "fresh SPS input routes while concurrent discovery runs");
        session.Tick(now + 2.1, true);
        Check(fixture.Controller.State("fixture", "v1").RequestedPercent == 0, "SPS freshness still expires during concurrent discovery");
        using var legacy = new Fixture(); using var blocked = legacy.Session(); now = ToyOscRouter.Clock;
        blocked.Configure([Rule()], now); legacy.Adapter.Scan(false); Put(blocked, A, .8f, now + .01); blocked.Tick(now + .01, true);
        Check(legacy.Controller.State("fixture", "v1").RequestedPercent == 0, "legacy scanning retains its SPS output gate");
    }
    private static void RecordRecovery()
    {
        using var f = new Fixture(); using var sps = f.Session();
        using var graph = new ToyControlSession(() => f.Adapter.Snapshot, () => f.Controller);
        sps.Configure([Rule(), Rule(B, "v2")], ToyOscRouter.Clock);
        graph.Tick(ToyOscRouter.Clock); sps.Tick(ToyOscRouter.Clock, true);
        Put(sps, A, .6f, ToyOscRouter.Clock); Put(sps, B, .4f, ToyOscRouter.Clock); sps.Tick(ToyOscRouter.Clock, true);
        graph.Tick(ToyOscRouter.Clock); graph.Action("InRow0", false); graph.Action("InRecord", false);
        graph.Draw(new(600, PanelLayout.Graph.Y + PanelLayout.Graph.Height * .2f), false);
        sps.Tick(ToyOscRouter.Clock,true);
        Check(f.Controller.State("fixture","v2").RequestedPercent==40,"recording one function leaves an unrelated SPS motor routing");
        Thread.Sleep(220); graph.Tick(ToyOscRouter.Clock); graph.Release();
        Check(graph.Model.LoopCount == 1, "recording one SPS-owned function commits a loop on that function");
        graph.StopAll(); Thread.Sleep(5);
        // VRChat exports changes only: release can arrive before the SPS timer
        // notices that Toy control replaced/stopped its old lease.
        Put(sps, A, 0f, ToyOscRouter.Clock); Put(sps, B, 0f, ToyOscRouter.Clock);
        Thread.Sleep(5); sps.Tick(ToyOscRouter.Clock, true);
        Thread.Sleep(5); Put(sps, A, .7f, ToyOscRouter.Clock); Put(sps, B, .5f, ToyOscRouter.Clock); sps.Tick(ToyOscRouter.Clock, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 70 && f.Controller.State("fixture", "v2").RequestedPercent == 50,
            "a single fresh OSC release after recorded-loop Stop rearms SPS even before its timer observes the override");
    }
    private static void Defaults()
    {
        Check(!new BridgeConfig().DryRun && !BridgeConfig.Parse("""{"shockers":[]} """).DryRun,"fresh and missing-field settings default Test mode off");
        Check(BridgeConfig.Parse("""{"shockers":[],"dryRun":true}""").Clone().DryRun
            && !BridgeConfig.Parse("""{"shockers":[],"dryRun":false}""").DryRun,"existing explicit Test mode choices survive parsing and cloning");
    }
    private static void ReleaseOrdering()
    {
        using var f=new Fixture(); using var s=f.Session();
        s.Configure([Rule()],ToyOscRouter.Clock); s.Tick(ToyOscRouter.Clock,true);
        Put(s,A,.6f,ToyOscRouter.Clock); s.Tick(ToyOscRouter.Clock,true);
        Thread.Sleep(5); Put(s,A,0f,ToyOscRouter.Clock); Thread.Sleep(5); f.Controller.Stop(); s.Tick(ToyOscRouter.Clock,true);
        Put(s,A,.9f,ToyOscRouter.Clock); s.Tick(ToyOscRouter.Clock,true);
        Check(s.ActiveTargets==0 && f.Controller.State("fixture","v1").RequestedPercent==0,"a neutral received before owner Stop cannot rearm later held positive input");
        Thread.Sleep(5); Put(s,A,0f,ToyOscRouter.Clock); s.Tick(ToyOscRouter.Clock,true);
        Thread.Sleep(5); Put(s,A,.5f,ToyOscRouter.Clock); s.Tick(ToyOscRouter.Clock,true);
        Check(f.Controller.State("fixture","v1").RequestedPercent==50,"neutral after Stop followed by fresh positive input restores SPS");
        f.Controller.Stop(); Thread.Sleep(5); Put(s,A,0f,ToyOscRouter.Clock); s.Tick(ToyOscRouter.Clock,true);
        Thread.Sleep(5); f.Controller.Stop(); Thread.Sleep(5); Put(s,A,.8f,ToyOscRouter.Clock); s.Tick(ToyOscRouter.Clock,true);
        Check(s.ActiveTargets==0,"a second owner Stop invalidates a release already consumed while waiting for new positive input");
        Thread.Sleep(5); Put(s,A,0f,ToyOscRouter.Clock); s.Tick(ToyOscRouter.Clock,true);
        Thread.Sleep(5); Put(s,A,.5f,ToyOscRouter.Clock); s.Tick(ToyOscRouter.Clock,true);
        Check(f.Controller.State("fixture","v1").RequestedPercent==50,"single release after the later Stop rearms normally");
        f.Controller.SetTestMode(false); s.Tick(ToyOscRouter.Clock,true);
        Put(s,A,0f,ToyOscRouter.Clock); s.Tick(ToyOscRouter.Clock,true); Thread.Sleep(5);
        Put(s,A,.7f,ToyOscRouter.Clock); s.Tick(ToyOscRouter.Clock,true); Wait(()=>f.Adapter.Writes.Any(w=>w.Value==14),"output before in-flight Stop");
        f.Adapter.BlockZero=true; f.Controller.Stop(); Wait(()=>f.Adapter.ZeroEntered.IsSet,"required Stop zero");
        try
        {
            Thread.Sleep(5); Put(s,A,0f,ToyOscRouter.Clock); s.Tick(ToyOscRouter.Clock,true);
            Thread.Sleep(5); Put(s,A,.9f,ToyOscRouter.Clock); s.Tick(ToyOscRouter.Clock,true);
            Check(f.Controller.State("fixture","v1").RequestedPercent==0 && !f.Adapter.Writes.Any(w=>w.Value==18),"rearmed SPS rise waits for owner Stop's required zero to finish");
        }
        finally { f.Adapter.AllowZero.Set(); }
        Wait(()=>f.Controller.State("fixture","v1").Armed,"Stop completed"); s.Tick(ToyOscRouter.Clock,true);
        Wait(()=>f.Adapter.Writes.Any(w=>w.Value==18),"fresh rise after required zero");
        Check(f.Controller.State("fixture","v1").RequestedPercent==90,"an in-flight Stop cannot permanently consume the single fresh release");
    }
    private static void AllRouting()
    {
        using var f=new Fixture(); using var s=f.Session(); var now=ToyOscRouter.Clock;
        var saved=SpsBoardModel.FromRules([Rule(),Rule(B,"v2")]); var manual=JsonSerializer.Serialize(saved.Wires);
        var board=saved with { AllToysToAllHoles=true }; SpsTarget[] targets=[new("fixture","v1"),new("fixture","v2")];
        var all=SpsBoardModel.Compile(board,targets);
        Check(all.Count==2 && all.All(r=>r.Targets.Count==2) && JsonSerializer.Serialize(board.Wires)==manual,"all-holes rules share contacts across every scalar target without replacing saved wires");
        f.Controller.SetPreference(f.Controller.State("fixture","v2").Preference with {Maximum=40});
        s.Configure(all,now); s.Tick(now,true); Put(s,A,.7f,now+.01); Put(s,B,.3f,now+.01); s.Tick(now+.02,true);
        Check(targets.All(t=>f.Controller.State(t.DeviceKey,t.ChannelId).RequestedPercent==70) && f.Controller.State("fixture","v2").SentPercent==40,"all toys follow the strongest fresh hole with per-function caps still applied");
        s.Tick(now+2.1,true); Check(s.ActiveTargets==0,"all-holes output expires when its contacts expire");
        s.Configure(SpsBoardModel.Compile(saved),now+2.2); s.Tick(now+2.2,true);
        Put(s,A,.6f,now+2.21); Put(s,B,.2f,now+2.21); s.Tick(now+2.22,true);
        Check(f.Controller.State("fixture","v1").RequestedPercent==60 && f.Controller.State("fixture","v2").RequestedPercent==20,"switching off automatic routing restores independent manual mappings on fresh input");
        var file=Path.Combine(artifacts,"all-routing.json"); SpsBoardStore.Export(file,board); var loaded=SpsBoardStore.Read(file);
        Check(loaded.AllToysToAllHoles && JsonSerializer.Serialize(loaded.Wires)==manual,"all-holes preference persists separately from unchanged manual assignments");
        var many=Enumerable.Range(0,256).Select(i=>new SpsTarget("synthetic-"+i,"v1")).ToArray();
        Check(SpsBoardModel.Compile(board,many).Count==2,"256 automatic target functions use bounded shared contact rules");
        File.Delete(file);
    }
    private static void GlobalSources()
    {
        var graph=new SpsBoardDocument(1,false,[],[]) { AllToysToAllHoles=true };
        foreach(var kind in new[]{"Pen","Orf"}) foreach(var zone in new[]{"One","Two"})
        {
            foreach(var field in new[]{"TouchOthers","TouchSelf","PenOthers","PenSelf","FrotOthers"})
                SpsBoardModel.Discover(graph,SpsSignals.Describe($"/avatar/parameters/OGB/{kind}/{zone}/{field}")!);
            graph.Holes[^1]=graph.Holes[^1] with { Sources=zone=="One"?0:SpsSources.All };
        }
        SpsTarget[] targets=[new("fixture","v1"),new("fixture","v2")];
        var original=JsonSerializer.Serialize(graph.Holes);
        foreach(var source in new[]{SpsSources.OtherSockets,SpsSources.OtherBody,SpsSources.OtherPlugs,SpsSources.MySockets,SpsSources.MyBody,SpsSources.MyPlugs})
        {
            var rules=SpsBoardModel.Compile(graph with { AutomaticSources=source },targets);
            var expected=graph.Holes.SelectMany(h=>h.Inputs).Where(a=>SpsBoardModel.Source(SpsSignals.Describe(a)!)==source).Order().ToArray();
            Check(expected.Length>=2 && rules.Select(r=>r.Address).Order().SequenceEqual(expected) && rules.All(r=>r.Targets.Count==2),
                source+" applies to every applicable hole and toy, independently of manual filters");
        }
        Check(SpsBoardModel.Compile(graph with {AutomaticSources=0},targets).Count==0,"all six shared switches off excludes every recognized contact");
        var expanded=graph.Copy() with {AutomaticSources=SpsSources.MyPlugs};
        const string added="/avatar/parameters/OGB/Orf/NewSocket/PenSelf";
        SpsBoardModel.Discover(expanded,SpsSignals.Describe(added)!);
        Check(SpsBoardModel.Compile(expanded,targets).Any(r=>r.Address==added),"newly added holes immediately inherit the same global interaction choices");
        var manual=graph with {AllToysToAllHoles=false,AutomaticSources=SpsSources.MyPlugs};
        foreach(var hole in manual.Holes) manual.Wires.Add(new() { HoleId=hole.Id,Target=targets[0] });
        Check(SpsBoardModel.Compile(manual).All(r=>r.Address.Contains("/Two/")) && JsonSerializer.Serialize(graph.Holes)==original,
            "manual routing keeps its per-hole filters despite the saved shared switches");
        var file=Path.Combine(artifacts,"global-sources.json"); var stored=graph with {AutomaticSources=0};
        SpsBoardStore.Export(file,stored); stored=SpsBoardStore.Read(file);
        Check(stored.AutomaticSources==0 && SpsBoardModel.Compile(stored,targets).Count==0,"saved all-off is preserved instead of inheriting old manual filters");
        var rejected=false; try {SpsBoardStore.Validate(stored with {AutomaticSources=(SpsSources)64});} catch(InvalidDataException) {rejected=true;}
        Check(rejected,"unknown global source bits are rejected"); File.Delete(file);
    }
    private static void ReceivedInputs()
    {
        using var toys=new LocalToysPage(_=>new FakeAdapter());
        var store=new SpsStore(Path.Combine(artifacts,"received-inputs.json"));
        File.Delete(store.PathName); File.Delete(Path.ChangeExtension(store.PathName,".board.json"));
        using var page=new SpsPage(toys,()=>true,store); var now=ToyOscRouter.Clock;
        const string standard="/avatar/parameters/VF7_OGB/Pen/Lollipop/PenOthers", custom="/avatar/parameters/CustomLollipopContact";
        page.Receive(new(standard,[.4f]),now);
        Check(page.Document.Holes.Count==0 && page.DiscoveryCandidates.Single().Name=="Lollipop","standard generated Lollipop contact is a discovery candidate until explicitly added");
        page.AddDiscovered(page.DiscoveryCandidates.Single().Id);
        Check(page.Document.Holes.Single().Name=="Lollipop" && page.Document.Wires.Count==0,"Add creates an unassigned Lollipop zone without an automatic toy connection");
        page.Receive(new(custom,[.6f]),now);
        foreach(var rejected in new[] { new OscMessage("/avatar/parameters/SzebsToy_Rx_Level",[.8f]),new("/avatar/parameters/Bad",[float.NaN]),new("/avatar/parameters/Text",["secret"]) }) page.Receive(rejected,now);
        Check(page.UnrecognizedInputs.SequenceEqual([custom]) && page.Session.ActiveTargets==0,"other finite scalar OSC inputs are observable without routing feedback, text or invalid values");
        page.OptionsPresenter=form=>form.DialogResult=DialogResult.OK;
        typeof(SpsPage).GetMethod("CustomInput",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(page,[custom]);
        Check(page.Document.Holes.Any(h=>h.Inputs.Contains(custom)) && page.Document.Wires.Count==0 && page.Session.ActiveTargets==0,"received custom contact can be added explicitly without inventing its name or automatically assigning a toy");
        for(var i=0;i<70;i++) page.Receive(new($"/avatar/parameters/Received{i}",[.1f]),now+i*.001);
        Check(page.UnrecognizedInputs.Count==64,"unrecognized input observation remains bounded to 64 recent scalar addresses");
        page.ResetInput(); Check(page.UnrecognizedInputs.Count==0,"avatar/listener reset clears received-input diagnostics");
        page.Shutdown(); File.Delete(Path.ChangeExtension(store.PathName,".board.json"));
    }
    private static void Mapping()
    {
        var rule = Rule() with { DeadZone = 20, Gain = 2 };
        Check(SpsSignals.Map(rule, .2) == 0 && Math.Abs(SpsSignals.Map(rule, .4) - .5) < .00001 && SpsSignals.Map(rule, 1) == 1, "dead zone rescales input and scale clips normalized request");
        Check(SpsSignals.Map(rule, 0) == 0 && SpsSignals.Map(rule with { Gain = 0 }, 1) == 0, "zero input or scale is always off");
        using var f = new Fixture(); using var session = f.Session(); var now = ToyOscRouter.Clock;
        var one = Rule() with { Targets = [new("fixture", "v1"), new("fixture", "v2")] }; var two = Rule(B);
        Put(session, A, .9f, now); session.Configure([one, two], now + .01); session.Tick(now + .02, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 0, "assigning a toy ignores previously cached values without a manual Start step");
        Put(session, A, .4f, now + .03); Put(session, B, .8f, now + .03); session.Tick(now + .04, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 80 && f.Controller.State("fixture", "v2").RequestedPercent == 40, "fresh signals automatically route multiple targets and combine shared-target mappings by maximum");
        Put(session, B, 0f, now + .05); session.Tick(now + .06, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 40, "one neutral mapping leaves another valid mapping's request intact");
        session.Reset(now + .065, "Fixture reset"); Check(f.Controller.State("fixture", "v1").RequestedPercent == 0 && f.Controller.State("fixture", "v2").RequestedPercent == 0, "reset clears every SPS-owned target");
        session.Configure([Rule() with { SmoothingMs = 500 }], now + .07); Put(session, A, 1f, now + .08); session.Tick(now + .1, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent is > 0 and < 100, "positive requests use configured smoothing");
        Put(session, A, 0f, now + .11); session.Tick(now + .12, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 0, "neutral input bypasses smoothing immediately");
        var motion = Rule() with { Response = SpsResponse.Motion, Gain = .2 }; session.Configure([motion], now + .13);
        Put(session, A, .4f, now + .14); session.Tick(now + .15, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 0, "first motion sample produces no derivative burst");
        Put(session, A, .6f, now + .24); session.Tick(now + .25, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent is >= 39 and <= 40, "motion response uses absolute normalized change per second");
        session.Tick(now + .43, true); Check(f.Controller.State("fixture", "v1").RequestedPercent == 0, "motion decays to off without new movement rather than holding a velocity");
    }
    private static void Automatic()
    {
        using var f = new Fixture(); using var session = f.Session(); var now = ToyOscRouter.Clock;
        var one = Rule(); var two = Rule(B, "v2"); session.Configure([one, two], now);
        Put(session, A, .8f, now + .01); Put(session, B, .6f, now + .01); session.Tick(now + .02, true);
        one = one with { Gain = .5 }; session.Configure([one, two], now + .03); session.Tick(now + .04, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 0 && f.Controller.State("fixture", "v2").RequestedPercent == 60, "editing one assignment waits for its fresh input while unrelated toy functions keep routing");
        Put(session, A, .4f, now + .05); session.Tick(now + .06, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 20, "edited assignments resume automatically on new input");
        session.Apply(new("/avatar/change", ["new-fixture-avatar"]), now + .07); Put(session, A, .8f, now + .08); session.Tick(now + .09, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 40 && f.Controller.State("fixture", "v2").RequestedPercent == 0, "avatar change drops cached output and accepts a fresh positive signal automatically");
        session.Tick(now + .1, false); session.Tick(now + .11, true);
        Check(session.ActiveTargets == 0, "listener recovery cannot replay old avatar values");
        Put(session, A, .6f, now + .12); session.Tick(now + .13, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 30, "listener recovery routes fresh signals without a Start click");
        session.Tick(now + 2.2, true); Put(session, A, .9f, now + 2.21); session.Tick(now + 2.22, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 45, "new input after exact-signal expiry automatically creates a new request");
    }
    private static void Ownership()
    {
        using var f = new Fixture(); using var session = f.Session(); var now = ToyOscRouter.Clock; var rule = Rule();
        session.Configure([rule], now); session.Tick(now, true); Put(session, A, .8f, now + .01); session.Tick(now + .02, true);
        f.Controller.Request("fixture", "v1", 23); Put(session, A, .9f, now + .03); session.Tick(now + .04, true); session.Tick(now + .05, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 23 && session.ActiveTargets == 0, "owner Input invalidates SPS and continued positive input cannot take it back");
        Put(session, A, 0f, now + .06); session.Tick(now + .07, true); Put(session, A, .6f, now + .08); session.Tick(now + .09, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 60, "a fresh release followed by positive input explicitly rearms SPS");
        f.Controller.Request("fixture", "v2", 19); session.Stop();
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 0 && f.Controller.State("fixture", "v2").RequestedPercent == 19, "stopping SPS preserves an unrelated desktop-owned sibling");
        Put(session, A, .7f, now + .11); session.Tick(now + .12, true); f.Controller.Stop("fixture");
        Put(session, A, .9f, now + .14); session.Tick(now + .15, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 0 && session.Signals.Count == 1, "existing toy Stop blocks held SPS input while automatic monitoring continues");
        Put(session, A, 0f, now + .16); session.Tick(now + .17, true); Put(session, A, .8f, now + .18); session.Tick(now + .19, true);
        f.Controller.SetTestMode(false); session.Tick(now + .2, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 0, "changing Test mode invalidates SPS without replay into hardware mode");
        session.Tick(now + .21, true); Check(f.Adapter.Writes.All(w => w.Value == 0), "old positive intent cannot escape during a Test mode transition");
    }
    private static void Faults()
    {
        using var f = new Fixture(); using var session = f.Session(); var now = ToyOscRouter.Clock; session.Configure([Rule()], now); session.Tick(now, true);
        Put(session, A, .8f, now + .01); session.Tick(now + .02, true); f.Adapter.Connect(false); session.Tick(now + .03, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 0, "disconnect discards SPS activity");
        Put(session, A, .9f, now + .04); session.Tick(now + .05, true); f.Adapter.Connect(true); session.Tick(now + .06, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 0, "reconnect cannot restore held positive SPS activity");
        Put(session, A, .7f, now + .08); session.Tick(now + .09, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 70, "a fresh positive signal after reconnect routes automatically without Start or a neutral prerequisite");
        session.Apply(new(A, [float.NaN]), now + .1); session.Tick(now + .11, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 0 && session.RuleState(session.Rules[0], now + .11) == "No signal", "invalid mapped signal immediately ends its lease");
        Put(session, A, .9f, now + .12); session.Tick(now + .13, true); Check(f.Controller.State("fixture", "v1").RequestedPercent == 0, "invalid-signal recovery cannot auto-replay positive output");
        Put(session, A, 0f, now + .14); session.Tick(now + .15, true); Put(session, A, .7f, now + .16); session.Tick(now + .17, true);
        session.Apply(new("/avatar/change", ["fixture-avatar"]), now + .18); session.Tick(now + .19, true);
        Check(session.Signals.Count == 0 && f.Controller.State("fixture", "v1").RequestedPercent == 0, "avatar changes clear learned live values and stop only SPS activity");
        Put(session, A, 0f, now + .2); session.Tick(now + .21, true); Put(session, A, .7f, now + .22); session.Tick(now + .23, true);
        session.Tick(now + .24, false); Check(session.ActiveTargets == 0 && session.Signals.Count == 0, "lost shared listener clears SPS leases and input cache");
        session.Configure([Rule(channel: "pos")], now + .25); Put(session, A, .9f, now + .26); session.Tick(now + .27, true);
        Check(f.Controller.State("fixture", "pos").RequestedPercent == 0, "position settings are rejected by continuous scalar routing even if imported");
    }
    private static void Timing()
    {
        using var f = new Fixture(); using var session = f.Session(); var now = ToyOscRouter.Clock;
        f.Controller.SetPreference(f.Controller.State("fixture", "v1").Preference with { Sensitivity = 50, Maximum = 30 });
        session.Configure([Rule()], now); session.Tick(now, true); Put(session, A, .8f, now + .01); session.Tick(now + .02, true);
        Check(f.Controller.State("fixture", "v1").SentPercent == 30 && f.Adapter.Writes.Count == 0, "Test mode reports scaled capped preview with no native actuator writes");
        session.Reset(now + .03, "Fixture reset"); f.Controller.SetTestMode(false); Put(session, A, .8f, now + .04); session.Tick(now + .05, true);
        Wait(() => f.Adapter.Writes.Any(w => w.Channel == "v1" && w.Value == 6), "scaled output");
        Check(f.Controller.State("fixture", "v1").SentPercent == 30, "live fake output passes existing sensitivity, maximum and native floor quantization");
        session.Tick(now + 2.1, true); Wait(() => f.Adapter.Writes.Any(w => w.Channel == "v1" && w.Value == 0), "stale stop");
        Check(session.ActiveTargets == 0 && f.Controller.State("fixture", "v1").RequestedPercent == 0, "stale exact signal stops output and issues priority zero");
        f.Controller.SetTestMode(true); var shortRule = Rule() with { TimeoutMs = 250 }; var weak = Rule(B) with { TimeoutMs = 1500 };
        now = ToyOscRouter.Clock; session.Configure([shortRule, weak], now); Put(session, A, .9f, now + .001); Put(session, B, .2f, now + .001); session.Tick(now + .002, true);
        Thread.Sleep(330);
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 0, "strongest aggregated input retains its own expiry during a stalled UI");
        session.Tick(ToyOscRouter.Clock, true); Check(session.ActiveTargets == 0, "independently expired lease cannot be renewed by a weaker cached input");
    }
    private static void ZeroCrossings()
    {
        using var f=new Fixture(); using var session=f.Session(); var now=ToyOscRouter.Clock;
        f.Controller.SetTestMode(false); session.Configure([Rule()],now); session.Tick(now,true);
        Put(session,A,0f,now+.001); session.Tick(now+.002,true);
        Check(f.Controller.State("fixture","v1").InputSource==Guid.Empty,"initial neutral SPS does not allocate input ownership");
        Put(session,A,.6f,now+.01); session.Tick(now+.02,true);
        Wait(()=>f.Adapter.Writes.Any(w=>w.Value==12),"SPS initial write");
        f.Adapter.BlockZero=true;
        Put(session,A,0f,now+.03); session.Tick(now+.04,true);
        Wait(()=>f.Adapter.ZeroEntered.IsSet,"SPS zero in flight");
        try
        {
            Check(session.ActiveTargets==0 && f.Controller.State("fixture","v1").RequestedPercent==0,"neutral SPS immediately turns output off without reporting an active function");
            Put(session,A,.9f,now+.05); session.Tick(now+.06,true);
            Check(f.Controller.State("fixture","v1").RequestedPercent==90 && session.ActiveTargets==1 && session.RuleState(session.Rules[0],now+.06)=="Routing","a fresh SPS rise during a pending zero stays live instead of requiring another release");
            Check(!f.Adapter.Writes.Any(w=>w.Value==18),"SPS rise waits for the required zero before submission");
        }
        finally {f.Adapter.AllowZero.Set();}
        Wait(()=>f.Adapter.Writes.Any(w=>w.Value==18),"SPS latest rise after zero");
        Put(session,A,0f,now+.07); session.Tick(now+.08,true);
        f.Controller.Request("fixture","v1",23); session.Tick(now+.09,true);
        Check(f.Controller.State("fixture","v1").RequestedPercent==23 && session.ActiveTargets==0,"a retained neutral SPS source cannot overwrite a fresh owner edit");
        Put(session,A,.9f,now+.10); session.Tick(now+.11,true);
        Check(f.Controller.State("fixture","v1").RequestedPercent==23,"positive SPS remains blocked after an owner edit during neutral");
        Wait(()=>f.Adapter.Writes.Any(w=>w.Value==4),"owner output settles before fresh SPS rearm");
        Put(session,A,0f,now+.12); session.Tick(now+.13,true);
        Put(session,A,.5f,now+.14); session.Tick(now+.15,true);
        Check(f.Controller.State("fixture","v1").RequestedPercent==50,"a new neutral then positive SPS input rearms after owner override");
        Put(session,A,0f,now+.16); session.Tick(now+.17,true);
        session.Tick(now+2.17,true);
        Check(f.Controller.State("fixture","v1").InputSource==Guid.Empty && session.ActiveTargets==0,"expired neutral SPS releases its source rather than keeping cached ownership");
        using var stopped=new Fixture(); using var stoppedSession=stopped.Session(); now=ToyOscRouter.Clock;
        stoppedSession.Configure([Rule()],now); stoppedSession.Tick(now,true);
        Put(stoppedSession,A,.6f,now+.01); stoppedSession.Tick(now+.02,true);
        Put(stoppedSession,A,0f,now+.03); stoppedSession.Tick(now+.04,true); stopped.Controller.Stop("fixture");
        Put(stoppedSession,A,.9f,now+.05); stoppedSession.Tick(now+.06,true);
        Check(stopped.Controller.State("fixture","v1").RequestedPercent==0 && stoppedSession.ActiveTargets==0,"owner Stop during retained neutral prevents the next SPS rise from restarting output");
        Put(stoppedSession,A,0f,now+.07); stoppedSession.Tick(now+.08,true);
        Put(stoppedSession,A,.5f,now+.09); stoppedSession.Tick(now+.10,true);
        Check(stopped.Controller.State("fixture","v1").RequestedPercent==50,"fresh release rearms SPS after Stop during neutral");
    }
    private static void LiveRows()
    {
        var adapter=new FakeAdapter(); using var toys=new LocalToysPage(_=>adapter);
        using var host=new Panel {Size=new(1240,608)}; using var page=new SpsPage(toys,()=>true,new SpsStore(Path.Combine(artifacts,"live-rows.json")));
        host.Controls.Add(page); _=host.Handle; _=toys.Handle; toys.StartRememberedConnections();
        while(toys.Busy) {Application.DoEvents(); Thread.Sleep(5);}
        var controller=toys.Controller!; var preference=controller.State("fixture","v1").Preference;
        controller.SetPreference(preference with {Sensitivity=50,Maximum=40}); controller.Request("fixture","v1",63);
        page.Tick(); var row=page.Board.Rows.Single(r=>r.Target.ChannelId=="v1");
        Check(row.Percent==63 && row.Status.Contains("Preview 30%"),"SPS row shows immediate requested intensity separately from the capped native preview");
        adapter.Battery(42); controller.Request("fixture","v1",81); Thread.Sleep(40); page.Tick();
        row=page.Board.Rows.Single(r=>r.Target.ChannelId=="v1");
        Check(row.Percent==81 && row.Status.Contains("battery 42%"),"visible SPS rows refresh intensity and current battery within a 40 ms frame");
        controller.Request("fixture","v1",0); controller.SetPreference(preference);
        using var image=new Bitmap(1240,608); page.Board.DrawToBitmap(image,new(0,0,1240,608)); image.Save(Path.Combine(artifacts,"sps-live-rows.png"));
    }
    private static void Storage()
    {
        var file = Path.Combine(artifacts, "fixture-mappings.json"); var store = new SpsStore(file); var rule = Rule() with { DeadZone = 12, SmoothingMs = 120, Gain = 1.5 };
        store.Save([rule]); var loaded = store.Load(); Check(loaded.Count == 1 && loaded[0].Targets.SequenceEqual(rule.Targets) && loaded[0].Gain == 1.5, "versioned mapping settings round-trip independently of existing configuration");
        using var f = new Fixture(); using var session = f.Session(); session.Configure(loaded, ToyOscRouter.Clock);
        session.Tick(ToyOscRouter.Clock, true);
        Check(session.ActiveTargets == 0 && !File.ReadAllText(file).Contains("running", StringComparison.OrdinalIgnoreCase), "saved assignments start automatic monitoring without restoring live values");
        var fresh = ToyOscRouter.Clock; Put(session, A, .5f, fresh); session.Tick(fresh + .05, true);
        Check(session.ActiveTargets == 1, "loaded assignments route the first fresh input without manual activation");
        File.WriteAllText(file, "{ damaged fixture"); var before = File.ReadAllText(file); Check(store.Load().Count == 0 && store.Warning is not null && File.ReadAllText(file) == before, "corrupt mapping file is retained without partial loading or automatic overwrite");
        var rejected = 0;
        foreach (var json in new[] { "{\"version\":2,\"rules\":[]}", "{\"version\":1,\"rules\":[null]}", "{\"version\":1,\"rules\":[{\"name\":null}]}" })
        { File.WriteAllText(file, json); try { SpsStore.Read(file); } catch (InvalidDataException) { rejected++; } }
        Check(rejected == 3, "unsupported versions and malformed rule entries are rejected atomically");
        var bad = 0; foreach (var invalid in new[] { rule with { InputMax = 0 }, rule with { Gain = double.NaN }, rule with { DeadZone = 100 }, rule with { Targets = [new("fixture", "v1"), new("fixture", "v1")] } })
        { try { SpsStore.Validate([invalid]); } catch (InvalidDataException) { bad++; } }
        Check(bad == 4, "invalid ranges, nonfinite scale, impossible dead zone and duplicate targets are rejected");
        File.Delete(file);
    }
    private static void Socket()
    {
        using var f = new Fixture(); using var session = f.Session(); using var toy = new ToyControlSession(() => f.Adapter.Snapshot, () => f.Controller);
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)); using var router = new ToyOscRouter(0, ((IPEndPoint)peer.Client.LocalEndPoint!).Port); using var sender = new UdpClient();
        var now = ToyOscRouter.Clock; session.Configure([Rule()], now); session.Tick(now, true);
        void Send(string address, object value) { var packet = OscCodec.Encode(address, value); sender.Send(packet, packet.Length, new IPEndPoint(IPAddress.Loopback, router.ListenPort)); }
        Send(A, .8f); Thread.Sleep(60); session.Reset(ToyOscRouter.Clock, "Fixture reset"); var forwarded = 0;
        router.Tick(toy, (_, _) => forwarded++, (message, at) => session.Apply(message, at)); session.Tick(ToyOscRouter.Clock, true);
        Check(session.Signals.Count == 1 && f.Controller.State("fixture", "v1").RequestedPercent == 0 && forwarded == 1, "shared UDP dispatch uses actual receipt time so pre-reset queued input cannot activate SPS");
        Send(A, .6f); Thread.Sleep(40); router.Tick(toy, (_, _) => forwarded++, (message, at) => session.Apply(message, at)); session.Tick(ToyOscRouter.Clock, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 60 && forwarded == 2, "fresh SPS datagrams use the existing single socket without changing PiCtrl forwarding");
        Send(A, .9f); Thread.Sleep(310); router.Tick(toy, (_, _) => forwarded++, (message, at) => session.Apply(message, at));
        Check(router.Dropped > 0 && forwarded == 2, "late queued UDP values are discarded without replay or forwarding");
        router.SendSpsPresence(true); peer.Client.ReceiveTimeout = 2000; IPEndPoint remote = new(IPAddress.Any, 0); var presence = OscCodec.Decode(peer.Receive(ref remote)).Single();
        Check(presence.Address == "/avatar/parameters/OGB_ENABLED" && presence.Arguments[0] is true, "verified VRCFury presence flag enables avatar contacts without a toy request");
        router.SendSpsPresence(false); var off = OscCodec.Decode(peer.Receive(ref remote)).Single(); Check(off.Arguments[0] is false, "disabling avatar signals withdraws the presence flag");
    }
    private static T Field<T>(object owner, string name) => (T)owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner)!;
    private static void BoardModel()
    {
        var source = Rule(); var migrated = SpsBoardModel.FromRules([source]); var compiled = SpsBoardModel.Compile(migrated);
        Check(migrated.Holes.Count == 1 && migrated.Wires.Count == 1 && compiled.Single().Address == A && compiled.Single().Targets.Single() == source.Targets.Single(), "legacy exact mappings migrate to visible holes and wires with their target and calibration intact");
        var wire = migrated.Wires[0] with { ExactInputs = [] }; migrated.Wires[0] = wire;
        SpsBoardModel.Discover(migrated, SpsSignals.Describe("/avatar/parameters/OGB/Pen/Fixture/PenOthers")!);
        SpsBoardModel.Discover(migrated, SpsSignals.Describe("/avatar/parameters/OGB/Pen/Fixture/FrotOthers")!);
        Check(SpsBoardModel.Source(SpsSignals.Describe(A)!) == SpsSources.OtherBody && SpsBoardModel.Source(SpsSignals.Describe("/avatar/parameters/OGB/Pen/Fixture/PenSelf")!) == SpsSources.MySockets
            && SpsBoardModel.Source(SpsSignals.Describe("/avatar/parameters/OGB/Pen/Fixture/FrotOthers")!) == SpsSources.OtherPlugs, "source filters distinguish body contact, self sockets and other-player plugs");
        migrated.Holes[0] = migrated.Holes[0] with { Sources = SpsSources.OtherPlugs };
        Check(SpsBoardModel.Compile(migrated).Single().Address.EndsWith("/FrotOthers"), "hole filter changes compile only the selected interaction sources");
        var file = Path.Combine(artifacts, "board-fixture.json"); var store = new SpsBoardStore(file); migrated = migrated with { HolesLocked = true }; store.Save(migrated);
        var loaded = SpsBoardStore.Read(file);
        Check(loaded.HolesLocked && loaded.Wires[0].Target == wire.Target && loaded.Holes[0].Sources == SpsSources.OtherPlugs, "positions, lock, source filters and connections round-trip without activity or hardware identifiers");
        File.WriteAllText(file, "{ broken fixture"); var fallback = store.Load([source]);
        Check(store.Warning is not null && fallback.Wires.Count == 1 && File.ReadAllText(file) == "{ broken fixture", "corrupt board settings preserve the file and recover existing exact mappings");
        store.Save(fallback); var backup = Directory.GetFiles(artifacts, "board-fixture.json.preserved-*").Last();
        Check(File.ReadAllText(backup) == "{ broken fixture", "explicit replacement preserves a damaged board file in a separate backup");
        var invalid = fallback with { Holes = [fallback.Holes[0] with { X = float.NaN }] }; var rejected = false;
        try { SpsBoardStore.Validate(invalid); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, "nonfinite marker positions are rejected before persistence or routing");
        File.Delete(file); File.Delete(backup);
    }
    private static void Depth()
    {
        const string root = "/avatar/parameters/OGB/Orf/FixtureSocket/PenOthersNewRoot", tip = "/avatar/parameters/OGB/Orf/FixtureSocket/PenOthersNewTip";
        using var f = new Fixture(); using var session = f.Session(); var now = ToyOscRouter.Clock;
        var rule = Rule(tip) with { RootAddress = root }; session.Configure([rule], now);
        Put(session, root, .9f, now + .01); Put(session, tip, 1f, now + .01); session.Tick(now + .02, true);
        Check(session.ActiveTargets == 0 && session.RuleState(rule, now + .02) == "Waiting for depth calibration", "socket depth cannot invent plug length from an already-held root and tip pair");
        Put(session, root, .7f, now + .03); Put(session, tip, .9f, now + .03); session.Tick(now + .04, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 0, "outside-tip approach calibrates length while insertion output remains off");
        Put(session, root, .9f, now + .13); Put(session, tip, 1f, now + .13); session.Tick(now + .14, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent is >= 51 and <= 53, "calibrated root distance produces normalized insertion rather than raw one-metre proximity");
        var motion = rule with { Response = SpsResponse.Motion, Gain = .1 }; session.Configure([motion], now + .15);
        Put(session, root, .92f, now + .16); Put(session, tip, 1f, now + .16); session.Tick(now + .17, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 0, "movement mode ignores derivatives from before the connection change");
        Put(session, root, .94f, now + .26); Put(session, tip, 1f, now + .26); session.Tick(now + .27, true);
        Check(f.Controller.State("fixture", "v1").RequestedPercent is >= 9 and <= 10, "movement-based motor power follows normalized insertion speed");
        session.Tick(now + .43, true); Check(f.Controller.State("fixture", "v1").RequestedPercent == 0, "movement-based depth stops after 150 ms without new movement");
        session.Configure([rule], now + .44); Put(session, root, .94f, now + .45); Put(session, tip, 1f, now + .45); session.Tick(now + .46, true);
        session.Apply(new(root, [float.NaN]), now + .47);
        Check(f.Controller.State("fixture", "v1").RequestedPercent == 0 && session.ActiveTargets == 0, "invalid root contact ends a depth-driven lease immediately");
        session.Reset(now + .5, "Fixture avatar reset"); Put(session, root, .95f, now + .51); Put(session, tip, 1f, now + .51); session.Tick(now + .52, true);
        Check(session.ActiveTargets == 0, "avatar reset discards depth calibration instead of replaying previous geometry");
        var graph = SpsBoardModel.FromRules([]); SpsBoardModel.Discover(graph, SpsSignals.Describe(root)!); graph.Wires.Add(new() { HoleId = graph.Holes[0].Id, Target = new("fixture", "v1") });
        var waiting = SpsBoardModel.Compile(graph).Single();
        Check(waiting.Address == tip && waiting.RootAddress == root, "a root-only hole waits for the matching tip instead of routing root proximity as depth");
        SpsBoardModel.Discover(graph, SpsSignals.Describe(tip)!); SpsBoardModel.Discover(graph, SpsSignals.Describe("/avatar/parameters/OGB/Orf/FixtureSocket/PenOthers")!);
        Check(SpsBoardModel.Compile(graph).Count == 1, "a depth pair supersedes legacy scalar penetration input instead of combining incompatible values");
    }
    private static void Click(Button button) => typeof(Button).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, [EventArgs.Empty]);
    private static string QueryTree(string avatar="fixture-avatar",float root=.7f,float tip=.9f) => JsonSerializer.Serialize(new
    {
        CONTENTS=new { change=new { VALUE=new[] { avatar } }, parameters=new { CONTENTS=new Dictionary<string,object>
        {
            ["OGB"]=new { CONTENTS=new Dictionary<string,object>
            {
                ["root"]=new { FULL_PATH="/avatar/parameters/OGB/Orf/Mouth/PenOthersNewRoot",TYPE="f",ACCESS=3,VALUE=new[] { root } },
                ["tip"]=new { FULL_PATH="/avatar/parameters/OGB/Orf/Mouth/PenOthersNewTip",TYPE="f",ACCESS=3,VALUE=new[] { tip } }
            } },
            ["feedback"]=new { FULL_PATH="/avatar/parameters/SzebsToy_Rx_Online",TYPE="f",ACCESS=3,VALUE=new[] { 1f } }
        } } }
    });
    private static SpsQueryFrame QueryFrame(string avatar,float root,float tip,double at)
    { using var json=JsonDocument.Parse(QueryTree(avatar,root,tip)); return SpsOscQuery.Parse(json.RootElement,at,at); }
    private static void Query()
    {
        var now=ToyOscRouter.Clock; var frame=QueryFrame("fixture-avatar",.7f,.9f,now);
        Check(frame.Messages.Length==2 && frame.Messages.All(m=>m.Address.Contains("/OGB/Orf/Mouth/")), "read-only query snapshots include only recognized SPS contacts and ignore application feedback");
        using var handler=new QueryHandler(); using var client=new System.Net.Http.HttpClient(handler); using var reader=new SpsOscQuery(client,()=>[12345]);
        var read=reader.ReadOnceAsync(9000).GetAwaiter().GetResult();
        Check(read?.Messages.Length==2 && handler.Requests.All(r=>r.Method=="GET" && r.Host=="127.0.0.1"), "query discovery validates the local VRChat host and sends only read requests");
        handler.Avatar=QueryTree("fixture-avatar",.91f,1f); read=reader.ReadOnceAsync(9000).GetAwaiter().GetResult();
        Check(read?.Messages.Single(m=>m.Address.EndsWith("NewTip")).Arguments[0] is 1f && handler.Requests.Count(r=>r.Path=="/avatar")==2, "each query obtains a fresh unchanged tip instead of replaying the first snapshot");
        reader.Reset(); handler.HostName="Another-app";
        Check(reader.ReadOnceAsync(9000).GetAwaiter().GetResult() is null, "query reader rejects an unrelated application advertising the same port");
        handler.HostName="VRChat-Client-fixture"; handler.OscPort=9100;
        Check(reader.ReadOnceAsync(9000).GetAwaiter().GetResult() is null, "query reader rejects a host with a different configured avatar OSC port");
        handler.OscPort=9000; handler.Avatar="{";
        Check(reader.ReadOnceAsync(9000).GetAwaiter().GetResult() is null, "malformed query documents cannot renew live intent");
        handler.Avatar=QueryTree("fixture-avatar",1.2f,1f);
        Check(reader.ReadOnceAsync(9000).GetAwaiter().GetResult() is null, "out-of-range query contacts atomically reject the snapshot");
        handler.Avatar=new string('x',2*1024*1024+1);
        Check(reader.ReadOnceAsync(9000).GetAwaiter().GetResult() is null, "query response size is bounded before parsing");
        handler.Avatar=QueryTree(); handler.Fail=true;
        Check(reader.ReadOnceAsync(9000).GetAwaiter().GetResult() is null, "failed query reads produce no new snapshot or replay");
        handler.Fail=false; handler.Delay=100; var interrupted=reader.ReadOnceAsync(9000); reader.Reset();
        Check(interrupted.GetAwaiter().GetResult() is null,"reset invalidates an in-flight query before it can publish a stale frame");
        handler.Delay=1500; var deadline=System.Diagnostics.Stopwatch.StartNew();
        Check(reader.ReadOnceAsync(9000).GetAwaiter().GetResult() is null && deadline.ElapsedMilliseconds<1200,"a hung query is canceled by the full-read deadline without blocking the UI");
        handler.Delay=0;
        reader.Dispose(); reader.Reset(); reader.Dispose(); reader.Reset();
        var closedRequests=handler.Requests.Count;
        reader.Tick(true,9000,_=>throw new Exception("Disposed reader delivered input."));
        Check(reader.ReadOnceAsync(9000).GetAwaiter().GetResult() is null && handler.Requests.Count==closedRequests,
            "disposed query reader tolerates repeated reset/dispose without starting another read");
        using var f=new Fixture(); using var session=f.Session();
        const string root="/avatar/parameters/OGB/Orf/Mouth/PenOthersNewRoot",tip="/avatar/parameters/OGB/Orf/Mouth/PenOthersNewTip";
        Put(session,root,.7f,now+.01); Put(session,tip,.9f,now+.01); session.Tick(now+.02,true);
        var rule=Rule(tip) with { RootAddress=root }; session.Configure([rule],now+.03);
        Put(session,root,.9f,now+.04); Put(session,tip,1f,now+.04); session.Tick(now+.05,true);
        Check(f.Controller.State("fixture","v1").RequestedPercent is >=51 and <=53, "approach is calibrated while unassigned and fresh insertion works after connecting the toy");
        Put(session,root,.91f,now+2.1); session.Tick(now+2.11,true);
        Check(f.Controller.State("fixture","v1").RequestedPercent==0, "UDP-only moving root cannot renew an expired unchanged tip");
        Put(session,root,.91f,now+2.12); Put(session,tip,1f,now+2.12); session.Tick(now+2.13,true);
        Check(f.Controller.State("fixture","v1").RequestedPercent is >=56 and <=58, "fresh current-value snapshot restores insertion with a stationary tip and moving root");
        var recent=session.Signals.Single(s=>s.Address==root); Put(session,root,0f,now+2.0);
        Check(session.Signals.Single(s=>s.Address==root)==recent, "an older query cannot overwrite a newer UDP sample");
        f.Controller.Stop(); session.Tick(now+2.14,true);
        Put(session,root,.95f,now+2.15); Put(session,tip,1f,now+2.15); session.Tick(now+2.16,true);
        Check(f.Controller.State("fixture","v1").RequestedPercent==0, "query refresh cannot reclaim an owner-stopped function with held positive input");
        Put(session,root,0f,now+2.17); Put(session,tip,0f,now+2.17); session.Tick(now+2.18,true);
        Put(session,root,.7f,now+2.19); Put(session,tip,.9f,now+2.19); session.Tick(now+2.20,true);
        Put(session,root,.9f,now+2.21); Put(session,tip,1f,now+2.21); session.Tick(now+2.22,true);
        Check(f.Controller.State("fixture","v1").RequestedPercent is >=51 and <=53, "fresh query release and subsequent approach rearm the existing owner gate");
        session.Tick(now+4.23,true);
        Check(f.Controller.State("fixture","v1").RequestedPercent==0, "loss of fresh query and UDP contacts expires output within the exact contact deadline");
    }
    private static int LiveQuery()
    {
        using var reader=new SpsOscQuery(); var frame=reader.ReadOnceAsync(9000).GetAwaiter().GetResult();
        if(frame is null) throw new Exception("No verified local VRChat query snapshot.");
        var contactCount=frame.Messages.Length;
        var active=frame.Messages.Count(m=>m.Arguments[0] is float f && f>0);
        var second=reader.ReadOnceAsync(9000).GetAwaiter().GetResult();
        if(second is null || second.RequestedAt<=frame.RequestedAt || second.Avatar!=frame.Avatar) throw new Exception("Live query refresh failed.");
        // Fake inventory only. Feed the actual snapshot through the same page
        // branch used when the live input port is unavailable, without Add.
        using var toys=new LocalToysPage(_=>new FakeAdapter());
        using var page=new SpsPage(toys,()=>false,new SpsStore(Path.Combine(artifacts,"live-read-only-discovery.json")));
        page.Size=new(1240,608); page.PerformLayout(); page.Board.SetDiscoveryMode(true); page.ReceiveQuery(second); page.Tick();
        var exact=page.DiscoveryCandidates.Any(c=>c.SupportedInputs.Contains(Handjob));
        if(page.DiscoveryCandidates.Count==0 || page.Session.Signals.Count!=0 || page.Session.ActiveTargets!=0)
            throw new Exception("Live read-only discovery did not remain separate from output.");
        File.WriteAllText(Path.Combine(artifacts,"live-query.json"),JsonSerializer.Serialize(new { LiveVrChatRead=true,Contacts=contactCount,PositiveContacts=active,Refreshed=true,
            DiscoveryWithoutListener=true,DiscoveryZones=page.DiscoveryCandidates.Count,ExactOwnerSampleFound=exact,OutputController=false,PhysicalDevice=false,SettingsChanged=false }));
        using(var picture=Capture(page.Board)) picture.Save(Path.Combine(artifacts,"sps-live-query-discovery.png"));
        Console.WriteLine($"PASS production reader discovered local VRChat: {contactCount} SPS contacts, {active} positive; second fresh read succeeded.");
        Console.WriteLine($"PASS production page discovered {page.DiscoveryCandidates.Count} zones with UDP unavailable; owner's Handjob example present: {exact}. Read-only; no output controller or settings changes."); return 0;
    }
    private sealed class QueryHandler : System.Net.Http.HttpMessageHandler
    {
        public string HostName="VRChat-Client-fixture",Avatar=QueryTree(); public int OscPort=9000,Delay; public bool Fail;
        public List<(string Method,string Host,string Path)> Requests=[];
        protected override async Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request,CancellationToken cancellation)
        {
            Requests.Add((request.Method.Method,request.RequestUri!.Host,request.RequestUri.AbsolutePath));
            if(Delay>0) await Task.Delay(Delay,cancellation).ConfigureAwait(false);
            var content=request.RequestUri.Query=="?HOST_INFO"?JsonSerializer.Serialize(new { NAME=HostName,OSC_IP="127.0.0.1",OSC_PORT=OscPort,OSC_TRANSPORT="UDP" }):Avatar;
            return new System.Net.Http.HttpResponseMessage(Fail?HttpStatusCode.ServiceUnavailable:HttpStatusCode.OK) { Content=new System.Net.Http.StringContent(content) };
        }
    }
    private static void Ui()
    {
        var adapter = new FakeAdapter(); using var toys = new LocalToysPage(_ => adapter); var store = new SpsStore(Path.Combine(artifacts, "ui-mappings.json"));
        var boardFile = Path.ChangeExtension(store.PathName, ".board.json"); File.Delete(store.PathName); File.Delete(boardFile);
        SpsBoardStore.Export(boardFile,new SpsBoardDocument(1,false,[],[]));
        using var form = new OverlayForm(new BridgeConfig(), initialSaveConfig: _ => { }, initialLocalToys: toys, initialSpsStore: store);
        var sps = Field<SpsPage>(form, "spsPage"); var nav = Field<Button>(form, "spsNav"); var toyNav = Field<Button>(form, "toyControlNav"); var logNav = Field<Button>(form, "logsNav");
        Check(nav.Top > toyNav.Top && nav.Top < logNav.Top, "SPS navigation stays between Toy control and Log");
        // The polished form also has a separate title bar. Detach the actual
        // content shell, never the first control or the live startup Form.
        var shell = form.Controls.OfType<TableLayoutPanel>().Single(); form.Controls.Remove(shell); using var host = new Panel { Size = form.ClientSize }; host.Controls.Add(shell);
        using var previewHost = new Form { StartPosition=FormStartPosition.Manual,Location=new(-16000,-16000),ClientSize=host.Size,ShowInTaskbar=false };
        previewHost.Controls.Add(host); previewHost.Show(); Application.DoEvents();
        _ = host.Handle; host.PerformLayout(); shell.PerformLayout(); _ = toys.Handle;
        toys.StartRememberedConnections(); while (toys.Busy) { Application.DoEvents(); Thread.Sleep(5); }
        using var peer = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        Field<ToyControlPage>(form, "toyControlPage").ConfigureOsc(0, ((IPEndPoint)peer.Client.LocalEndPoint!).Port);
        typeof(OverlayForm).GetMethod("ShowPage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, [sps, nav]);
        const string root = "/avatar/parameters/OGB/Orf/Mouth/PenOthersNewRoot", tip = "/avatar/parameters/OGB/Orf/Mouth/PenOthersNewTip";
        const string chest = "/avatar/parameters/VFH/Zone/Touch/Chest/Others";
        sps.Receive(new(root, [.7f]), ToyOscRouter.Clock); sps.Receive(new(tip, [.9f]), ToyOscRouter.Clock);
        sps.Receive(new(chest, [.4f]), ToyOscRouter.Clock); sps.Receive(new("/avatar/parameters/OGB/Pen/Groin/PenOthers", [.5f]), ToyOscRouter.Clock);
        typeof(SpsPage).GetMethod("PaintBoard", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(sps, []);
        var board = sps.Board; _ = board.Handle; board.PerformLayout();
        Check(sps.Document.Holes.Count==0 && sps.DiscoveryCandidates.Count==2,"new plugs and sockets remain in discovery instead of spawning unsolicited markers");
        Tap(board,Center(board.DiscoveryBox));
        Check(board.DiscoveryMode && board.Discoveries.Count==2,"Discovery toggle replaces body-map interaction with a filtered plug/socket list");
        using(var discoveryImage=Capture(board)) discoveryImage.Save(Path.Combine(artifacts,"sps-discovery.png"));
        Tap(board,Center(board.DiscoveryAddBox(0))); Tap(board,Center(board.DiscoveryAddBox(1)));
        Check(sps.Document.Holes.Count==2 && sps.Document.Wires.Count==0 && board.Discoveries.All(r=>r.Added),"list Add buttons create saved markers and change to Added without assigning toys");
        var addedGraph=File.ReadAllText(boardFile); Tap(board,Center(board.DiscoveryAddBox(0)));
        Check(File.ReadAllText(boardFile)==addedGraph,"an already-added discovery cannot create a duplicate marker");
        var hiddenHole=Point.Round(board.HolePoint(sps.Document.Holes[0])); Drag(board,hiddenHole,new(hiddenHole.X+20,hiddenHole.Y+20));
        Check(File.ReadAllText(boardFile)==addedGraph && !board.Dragging,"hidden body-map holes cannot be moved or connected in discovery mode");
        Tap(board,Center(board.DiscoveryBox));
        Check(!board.DiscoveryMode && sps.Document.Holes.Count==2,"turning Discovery off restores the human body map with the new markers");
        sps.OptionsPresenter=options=> { Descendants(options).OfType<TextBox>().First().Text="Chest"; options.DialogResult=DialogResult.OK; };
        typeof(SpsPage).GetMethod("CustomInput",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(sps,[chest]);
        sps.OptionsPresenter=null;
        Mouse(board,"OnMouseMove",Point.Round(board.HolePoint(sps.Document.Holes[0])));
        Check(board.HoveredHole==sps.Document.Holes[0].Id,"hole names appear only for the hovered circle");
        typeof(SpsBoard).GetMethod("OnMouseLeave",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(board,[EventArgs.Empty]);
        Check(board.HoveredHole is null,"leaving a circle hides its name and status");
        var actualQuery=QueryFrame("ui-avatar",.7f,.9f,ToyOscRouter.Clock);
        sps.ReceiveQuery(actualQuery);
        Thread.Sleep(5); sps.ReceiveQuery(QueryFrame("ui-avatar",.71f,.91f,ToyOscRouter.Clock)); sps.Tick();
        Check(sps.Session.Signals.Single(s=>s.Address==root).Value>.70,"fresh query frames reach the actual SPS page independently of change-only UDP");
        sps.ResetInput(); sps.ReceiveQuery(actualQuery);
        Check(sps.Session.Signals.Count==0,"a query started before an OSC reset cannot replay into the page");
        sps.Receive(new(root,[.7f]),ToyOscRouter.Clock); sps.Receive(new(tip,[.9f]),ToyOscRouter.Clock);
        Check(sps.Document.Holes.Count == 3 && sps.Document.Holes.Single(h => h.Name == "Mouth").Inputs.Count == 2, "root and tip automatically group into one hole without typed OSC addresses");
        Check(board.Rows.Count == 2 && board.Rows.All(r => r.Target.ChannelId != "pos"), "body-map toy list exposes independent motors and excludes position settings");
        Check(sps.Document.Wires.Count == 0 && sps.Session.ActiveTargets == 0 && sps.SignalsEnabled, "automatic hole discovery and avatar presence do not assign or actuate toys");
        Check(Math.Abs(board.LeftBox.Width / board.RightBox.Width - 300f / 874) < .001, "left toy list and right body box preserve the reference proportions");
        var mouth = sps.Document.Holes.Single(h => h.Name == "Mouth"); var at = Point.Round(board.HolePoint(mouth)); var destination = new Point(at.X + 28, at.Y + 45);
        var prior = mouth;
        Drag(board, at, destination);
        mouth = sps.Document.Holes.Single(h => h.Id == mouth.Id);
        Check(mouth.X != prior.X && mouth.Y != prior.Y && SpsBoardStore.Read(boardFile).Holes.Single(h => h.Id == mouth.Id).X == mouth.X, "holding and dragging a hole moves and persists its normalized position");
        Tap(board, Center(board.LockBox));
        Check(sps.Document.HolesLocked && SpsBoardStore.Read(boardFile).HolesLocked, "top-right Lock freezes holes and persists the lock");
        at = Point.Round(board.HolePoint(mouth)); Drag(board, at, new(at.X + 40, at.Y + 40));
        Check(sps.Document.Holes.Single(h => h.Id == mouth.Id) == mouth, "locked hole dragging cannot alter its position");
        Drag(board, Point.Round(board.Port(0)), Point.Round(board.HolePoint(mouth)));
        Check(sps.Document.Wires.Count == 1 && sps.Document.Wires[0].Target.ChannelId == "v1", "dragging a toy function to a locked hole creates its saved connection");
        var first = sps.Document.Wires[0]; var other = sps.Document.Holes.Single(h => h.Name == "Chest");
        Drag(board, Point.Round(board.Port(1)), Point.Round(board.HolePoint(other)));
        Check(sps.Document.Wires.Count == 2 && SpsBoardStore.Read(boardFile).Wires.Count == 2, "independent toy functions can connect to different holes");
        Thread.Sleep(15); sps.Receive(new(root, [.7f]), ToyOscRouter.Clock); sps.Receive(new(tip, [.9f]), ToyOscRouter.Clock); sps.Tick();
        Thread.Sleep(30); sps.Receive(new(root, [.9f]), ToyOscRouter.Clock); sps.Receive(new(tip, [1f]), ToyOscRouter.Clock);
        sps.Receive(new(chest, [.6f]), ToyOscRouter.Clock); sps.Tick();
        Check(toys.Controller!.State("fixture", "v1").RequestedPercent is >= 51 and <= 53 && toys.Controller.State("fixture", "v2").RequestedPercent == 60, "body-map connections route calibrated insertion and contact level automatically through Test mode");
        var routingBeforeDiscovery=File.ReadAllText(boardFile); var routingRequest=toys.Controller.State("fixture","v1").RequestedPercent;
        Tap(board,Center(board.DiscoveryBox)); Drag(board,Point.Round(board.Port(0)),Point.Round(board.HolePoint(mouth)));
        Check(board.DiscoveryMode && File.ReadAllText(boardFile)==routingBeforeDiscovery && sps.Session.ActiveTargets==2
            && toys.Controller.State("fixture","v1").RequestedPercent==routingRequest && !board.Dragging,
            "discovery view preserves existing live routing and prevents connections to its hidden human");
        Tap(board,Center(board.DiscoveryBox));
        Check(!File.Exists(store.PathName), "body-map editing preserves the original exact-mapping configuration file");
        Tap(board, Center(board.LockBox)); var chestAt = Point.Round(board.HolePoint(other));
        Drag(board, chestAt, new(chestAt.X + 9, chestAt.Y + 5));
        Check(toys.Controller.State("fixture", "v2").RequestedPercent == 60 && sps.Session.ActiveTargets == 2, "moving and saving an active hole does not interrupt existing toy output");
        Drag(board, Point.Round(board.HolePoint(sps.Document.Holes.Single(h => h.Id == other.Id))), chestAt); Tap(board, Center(board.LockBox));
        sps.ReceiveQuery(QueryFrame("new-ui-avatar",.9f,1f,ToyOscRouter.Clock));
        Check(sps.Session.ActiveTargets==0 && toys.Controller.State("fixture","v1").RequestedPercent==0 && toys.Controller.State("fixture","v2").RequestedPercent==0,
            "an avatar identity change in query data releases all previous SPS output immediately");
        Thread.Sleep(5); sps.Receive(new(root,[.7f]),ToyOscRouter.Clock); sps.Receive(new(tip,[.9f]),ToyOscRouter.Clock); sps.Tick();
        Thread.Sleep(20); sps.Receive(new(root,[.9f]),ToyOscRouter.Clock); sps.Receive(new(tip,[1f]),ToyOscRouter.Clock); sps.Receive(new(chest,[.6f]),ToyOscRouter.Clock); sps.Tick();
        var presented = 0;
        var savedBeforeGear=File.ReadAllText(boardFile);
        sps.OptionsPresenter=options=>
        {
            Check(options is SpsWireOptions && options.Text=="Connection options","gear opens the connection's existing options menu"); presented++;
        };
        var gear=board.WireGearBox(first);
        Check(!gear.IsEmpty,"visible manual connection has an options gear");
        var gearEdge=Point.Round(new PointF(gear.X+gear.Width/2,gear.Y+gear.Height*.84f));
        Mouse(board,"OnMouseMove",gearEdge);
        Check(board.HoveredWire==first.Id && board.Cursor==Cursors.Hand,"gear badge edge exposes a clickable connection target");
        Tap(board,gearEdge);
        Check(presented==1 && File.ReadAllText(boardFile)==savedBeforeGear,"canceling the gear menu keeps connections and their options unchanged");
        typeof(SpsBoard).GetMethod("OnMouseLeave",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(board,[EventArgs.Empty]);
        Check(board.HoveredWire is null,"leaving a connection clears its gear highlight and tooltip");
        presented=0;
        sps.OptionsPresenter = options =>
        {
            Check(options is SpsWireOptions, "clicking a connection opens its option window"); presented++;
            using (var capture = Capture(options)) capture.Save(Path.Combine(artifacts, "sps-line-options.png"));
            Field<RadioButton>(options, "motion").Checked = true; Click(Descendants(options).OfType<Button>().Single(b => b.Text == "Apply"));
        };
        Point line;
        using (var path = board.WirePath(first)) { path.Flatten(); line = Point.Round(path.PathPoints[path.PointCount / 2]); }
        Tap(board, line);
        Check(presented == 1 && sps.Document.Wires.Single(w => w.Id == first.Id).Response == SpsResponse.Motion, "line option applies and saves movement-based power to that connection");
        sps.OptionsPresenter = options =>
        {
            Check(options is SpsHoleOptions, "double-clicking a hole opens its options"); presented++;
            using (var capture = Capture(options)) capture.Save(Path.Combine(artifacts, "sps-hole-options.png"));
            var switches = Descendants(options).OfType<SpsSourceSwitch>().ToArray();
            Check(switches.Length == 6 && switches.Single(s => s.Text == "Other players' SPS plugs").Checked && !switches.Single(s => s.Text == "My SPS sockets").Checked, "hole switches match the requested other/self controls and defaults");
            using(var redrawn=new Bitmap(switches[0].Width,switches[0].Height))
            using(var graphics=Graphics.FromImage(redrawn))
            {
                graphics.Clear(Color.Magenta);
                for(var repaint=0;repaint<8;repaint++) typeof(SpsSourceSwitch).GetMethod("OnPaint",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(switches[0],[new PaintEventArgs(graphics,new(0,0,redrawn.Width,redrawn.Height))]);
                Check(redrawn.GetPixel(redrawn.Width-3,3).ToArgb()==switches[0].BackColor.ToArgb(),"switch repaint clears its entire opaque surface instead of copying dialog text and buttons");
            }
            Check(switches.GroupBy(s=>s.Parent).Single().Key!.Controls.Cast<Control>().SelectMany(a=>switches.Where(b=>b!=a).Select(b=>Rectangle.Intersect(a.Bounds,b.Bounds))).All(r=>r.Width==0 || r.Height==0),"all six source switches have distinct nonoverlapping layout cells");
            switches.Single(s => s.Text == "Other players' SPS plugs").Checked = false;
            Click(Descendants(options).OfType<Button>().Single(b => b.Text == "Apply"));
        };
        Mouse(board, "OnMouseDown", Point.Round(board.HolePoint(mouth)), 2); Mouse(board, "OnMouseUp", Point.Round(board.HolePoint(mouth)));
        Check(presented == 2 && !sps.Document.Holes.Single(h => h.Id == mouth.Id).Sources.HasFlag(SpsSources.OtherPlugs) && toys.Controller.State("fixture", "v1").RequestedPercent == 0, "disabling a hole interaction releases its existing output without changing the other motor");
        Check(toys.Controller.State("fixture", "v2").RequestedPercent == 60, "hole options preserve unrelated function routing");
        Tap(board, Center(board.LockBox)); at = Point.Round(board.HolePoint(mouth)); Mouse(board, "OnMouseDown", at); Mouse(board, "OnMouseMove", new(at.X + 20, at.Y + 20));
        typeof(SpsBoard).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(board, [new KeyEventArgs(Keys.Escape)]);
        Mouse(board, "OnMouseUp", new(at.X + 20, at.Y + 20));
        Check(!board.Dragging && sps.Document.Holes.Single(h => h.Id == mouth.Id).X == mouth.X, "Escape cancels a hole move without saving it");
        var graphBefore = File.ReadAllText(boardFile); Drag(board, Point.Round(board.Port(0)), new Point((int)board.RightBox.Right - 5, (int)board.RightBox.Bottom - 5));
        Check(File.ReadAllText(boardFile) == graphBefore && sps.Document.Wires.Count == 2, "dropping a connection outside a hole has no saved or output effect");
        Drag(board, Point.Round(board.Port(1)), Point.Round(board.HolePoint(sps.Document.Holes.Single(h => h.Id == mouth.Id))));
        Drag(board, Point.Round(board.Port(0)), Point.Round(board.HolePoint(sps.Document.Holes.Single(h => h.Id == other.Id))));
        Check(sps.Document.Wires.Count == 4 && sps.Document.Wires.Count(w => w.HoleId == mouth.Id) == 2 && sps.Document.Wires.Count(w => w.Target.ChannelId == "v1") == 2,
            "multiple toy functions can share one hole and one function can connect to several holes");
        var extra = sps.Document.Wires.Last();
        sps.OptionsPresenter = options => Click(Descendants(options).OfType<Button>().Single(b => b.Text == "Disconnect"));
        using (var path = board.WirePath(extra)) { path.Flatten(); Tap(board, Point.Round(path.PathPoints[path.PointCount / 2])); }
        Check(sps.Document.Wires.Count == 3 && sps.Document.Wires.All(w => w.Id != extra.Id) && sps.Document.Wires.Any(w => w.Id == first.Id), "Disconnect removes only the clicked connection and preserves other assignments");
        var manualWires=JsonSerializer.Serialize(sps.Document.Wires); presented=0; sps.OptionsPresenter=_=>presented++;
        Tap(board,Center(board.AllToysBox));
        Check(board.WireGearBox(first).IsEmpty,"all-holes mode hides the gears on paused manual connections");
        Check(sps.Document.AllToysToAllHoles && SpsBoardStore.Read(boardFile).AllToysToAllHoles && sps.Session.Rules.All(r=>r.Targets.Count==2),"visible all-holes toggle persists and routes all connected scalar functions");
        Check(sps.Session.ActiveTargets==0,"enabling all-holes mode releases manual output and requires fresh input");
        Tap(board,line); Drag(board,Point.Round(board.Port(0)),Point.Round(board.HolePoint(mouth)));
        Tap(board,new((int)(board.StopBox.Left-board.StopBox.Width/2-10),(int)(board.StopBox.Top+board.StopBox.Height/2)));
        Check(presented==0 && JsonSerializer.Serialize(sps.Document.Wires)==manualWires,"grey manual lines cannot be edited, cleared or dragged while all-holes mode is active");
        Thread.Sleep(5); sps.Receive(new(chest,[.8f]),ToyOscRouter.Clock); sps.Tick();
        Check(toys.Controller.State("fixture","v1").RequestedPercent==80 && toys.Controller.State("fixture","v2").RequestedPercent==80,"fresh all-holes contact drives both functions even when their manual wires use different responses");
        Tap(board,Center(board.StopBox)); Thread.Sleep(5); sps.Receive(new(chest,[.9f]),ToyOscRouter.Clock); sps.Tick();
        Check(sps.Session.ActiveTargets==0,"all-holes Stop still rejects held positive input until contacts are released");
        foreach(var address in new[] {root,tip,chest,"/avatar/parameters/OGB/Pen/Groin/PenOthers"}) sps.Receive(new(address,[0f]),ToyOscRouter.Clock);
        sps.Tick(); Thread.Sleep(5); sps.Receive(new(chest,[.5f]),ToyOscRouter.Clock); sps.Tick();
        Check(toys.Controller.State("fixture","v1").RequestedPercent==50 && toys.Controller.State("fixture","v2").RequestedPercent==50,"releasing all assigned contacts rearms all-holes mode after Stop");
        adapter.AddMotor(); sps.Tick();
        Check(sps.Session.Rules.All(r=>r.Targets.Count==3) && toys.Controller.State("fixture","v3").RequestedPercent==0
            && new[]{"v1","v2"}.All(c=>toys.Controller.State("fixture",c).RequestedPercent==50),
            "a newly reported scalar function needs fresh input while unchanged automatic functions continue");
        Thread.Sleep(5); sps.Receive(new(chest,[.6f]),ToyOscRouter.Clock); sps.Tick();
        Check(new[] {"v1","v2","v3"}.All(c=>toys.Controller.State("fixture",c).RequestedPercent==60),"fresh contact drives the new function together with existing automatic targets");
        var beforeAuto=board.Dock; board.Dock=DockStyle.None; board.Size=new(1240,608);
        typeof(SpsPage).GetMethod("PaintBoard",BindingFlags.Instance|BindingFlags.NonPublic)!.Invoke(sps,[]);
        using(var image=Capture(board)) image.Save(Path.Combine(artifacts,"sps-all-holes.png")); board.Dock=beforeAuto;
        Tap(board,Center(board.AllToysBox));
        Check(!sps.Document.AllToysToAllHoles && JsonSerializer.Serialize(sps.Document.Wires)==manualWires,"switching all-holes off restores the exact saved manual lines");
        // Return the moved fixture mouth to its default anatomical position for
        // the screenshots, using the same saved drag interaction.
        var defaultMouth = SpsBoardModel.Hole(tip, 0);
        Drag(board, Point.Round(board.HolePoint(sps.Document.Holes.Single(h => h.Id == mouth.Id))), Point.Round(board.HolePoint(defaultMouth)));
        foreach (var size in new[] { new Size(1040, 760), new Size(790, 590) })
        {
            form.Size = size; host.Size = form.ClientSize; host.PerformLayout(); shell.PerformLayout(); sps.CreateControl(); sps.PerformLayout();
            typeof(SpsPage).GetMethod("PaintBoard", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(sps, []);
            using var image = Capture(host); image.Save(Path.Combine(artifacts, $"sps-body-{size.Width}.png"));
            Check(board.BodyBox.Height > 200 && board.LockBox.Right <= board.RightBox.Right && board.ToyBox(1).Bottom < board.LeftBox.Bottom, $"body-map holes, top bar and toy list fit at {size.Width}px");
            var resizedGear=board.WireGearBox(sps.Document.Wires.Single(w=>w.Id==first.Id));
            presented=0; sps.OptionsPresenter=_=>presented++; Tap(board,Center(resizedGear));
            Check(!resizedGear.IsEmpty && presented==1,$"connection gear follows resized wire and opens its menu at {size.Width}px");
            Tap(board,Center(board.DiscoveryBox));
            for(var candidateIndex=0;candidateIndex<Math.Min(board.DiscoveryPageSize,board.Discoveries.Count);candidateIndex++)
            {
                var addBox=board.DiscoveryAddBox(candidateIndex); var rowBox=board.DiscoveryRowBox(candidateIndex);
                Check(rowBox.Contains(addBox) && rowBox.Bottom<board.BodyBox.Bottom,$"discovery row and Add button fit at {size.Width}px");
            }
            using(var discoveryView=Capture(host)) discoveryView.Save(Path.Combine(artifacts,$"sps-discovery-{size.Width}.png"));
            Tap(board,Center(board.DiscoveryBox));
        }
        var oldDock = board.Dock; board.Dock = DockStyle.None; board.Size = new(1240, 608);
        using (var image = Capture(board))
        {
            Check(image.Size == new Size(1240, 608), "reference board renders at the exact supplied picture dimensions");
            image.Save(Path.Combine(artifacts, "sps-body-reference.png"));
        }
        board.Dock = oldDock;
        Tap(board, Center(board.StopBox));
        Check(toys.Controller.State("fixture", "v1").RequestedPercent == 0 && toys.Controller.State("fixture", "v2").RequestedPercent == 0, "board Stop all uses the existing owner stop for every toy");
        Check(!form.Visible && Field<System.Diagnostics.Process?>(form, "bridge") is null && toys.Snapshot!.Devices.All(d => d.IsSimulated), "offscreen body-map checks never start the live app, bridge or physical discovery");
        using var closeHandler=new QueryHandler { Delay=1500 };
        using var closeClient=new System.Net.Http.HttpClient(closeHandler);
        using var closeReader=new SpsOscQuery(closeClient,()=>[12345]);
        typeof(SpsPage).GetField("query",BindingFlags.Instance|BindingFlags.NonPublic)!.SetValue(sps,closeReader);
        sps.StartMonitoring(); var delivered=0; closeReader.Tick(true,9000,_=>delivered++);
        Wait(()=>closeHandler.Requests.Count>0,"pending close query");
        var pendingClose=Field<Task<SpsQueryFrame?>>(closeReader,"pending");
        host.Controls.Remove(shell); form.Controls.Add(shell); _=form.Handle;
        var closingTime=System.Diagnostics.Stopwatch.StartNew(); form.Close();
        while(!form.IsDisposed && closingTime.ElapsedMilliseconds<3000) { Application.DoEvents(); Thread.Sleep(5); }
        Check(form.IsDisposed,"actual form close completes after SPS disposal and the Toy control reset callback");
        Check(pendingClose.GetAwaiter().GetResult() is null && delivered==0,
            "form close cancels the pending query without publishing or replaying contacts");
        sps.ResetInput(); sps.StartMonitoring(); sps.Shutdown(); closeReader.Reset(); closeReader.Dispose();
        sps.Receive(new(root,[1f]),ToyOscRouter.Clock); sps.ReceiveQuery(QueryFrame("closed-avatar",1f,1f,ToyOscRouter.Clock));
        Check(!sps.SignalsEnabled && !Field<System.Windows.Forms.Timer>(sps,"timer").Enabled && sps.Session.ActiveTargets==0,
            "late reset/start/input callbacks cannot restart the closed SPS page");
        toys.ShutdownAsync().GetAwaiter().GetResult(); File.Delete(store.PathName); File.Delete(boardFile);
    }
    private static Point Center(RectangleF rect) => Point.Round(new(rect.X + rect.Width / 2, rect.Y + rect.Height / 2));
    private static void Mouse(Control control, string method, Point at, int clicks = 1) => typeof(Control).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(control, [new MouseEventArgs(MouseButtons.Left, clicks, at.X, at.Y, 0)]);
    private static void Drag(Control control, Point from, Point to) { Mouse(control, "OnMouseDown", from); Mouse(control, "OnMouseMove", to); Mouse(control, "OnMouseUp", to); }
    private static void Tap(Control control, Point at) { Mouse(control, "OnMouseDown", at); Mouse(control, "OnMouseUp", at); }
    private static IEnumerable<Control> Descendants(Control owner)
    { foreach (Control child in owner.Controls) { yield return child; foreach (var nested in Descendants(child)) yield return nested; } }
    private static Bitmap Capture(Control owner)
    {
        // Only isolated options dialogs are shown offscreen to initialize their
        // real native child controls. The application Form is never shown.
        var dialog = owner as SpsOptionsForm;
        if (dialog is not null) { dialog.StartPosition = FormStartPosition.Manual; dialog.Location = new(-16000, -16000); dialog.Show(); dialog.PerformLayout(); }
        var image = new Bitmap(owner.Width, owner.Height); owner.DrawToBitmap(image, new(0, 0, image.Width, image.Height));
        dialog?.Hide(); return image;
    }
    private sealed class Fixture : IDisposable
    {
        public FakeAdapter Adapter { get; } = new();
        public LocalToyController Controller { get; }
        public Fixture() => Controller = new(Adapter, new LocalToyControlStore(), automaticControls: true);
        public SpsSession Session() => new(() => Adapter.Snapshot, () => Controller);
        public void Dispose() => Controller.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
    private sealed class FakeAdapter : IRememberedToyAdapter, ILocalToyOutput
    {
        private LocalToyDescriptor device = new("fixture", "fake", "Fixture toy", "Fixture", ToyConnectionState.Connected,
            [new("v1", ToyCapabilityType.Vibration, "Vibration 1", 0, 20, 1, true, null), new("v2", ToyCapabilityType.Vibration, "Vibration 2", 0, 20, 1, true, null), new("pos", ToyCapabilityType.Position, "Position", 0, 100, 1, true, null)], true, 67, DateTimeOffset.UtcNow, 10, true);
        public event Action? Changed;
        public event Action<string>? OutputFailed { add { } remove { } }
        public System.Collections.Concurrent.ConcurrentQueue<(string Channel, int Value)> Writes { get; } = new();
        public bool BlockZero;
        public ManualResetEventSlim ZeroEntered { get; }=new(false);
        public ManualResetEventSlim AllowZero { get; }=new(false);
        private bool scanning, concurrentScanning;
        public ToyAdapterSnapshot Snapshot => new(ToyAdapterAvailability.Ready, scanning, "Fixture adapter", [device]) { SupportsOutputWhileScanning = concurrentScanning };
        public void Scan(bool concurrent) { scanning = true; concurrentScanning = concurrent; Changed?.Invoke(); }
        public void Connect(bool connected) { device = device with { ConnectionState = connected ? ToyConnectionState.Connected : ToyConnectionState.Disconnected }; Changed?.Invoke(); }
        public void Battery(int value) {device=device with {BatteryPercent=value}; Changed?.Invoke();}
        public void AddMotor() { device=device with {Capabilities=device.Capabilities.Concat([new ToyCapability("v3",ToyCapabilityType.Vibration,"Vibration 3",0,20,1,true,null)]).ToArray()}; Changed?.Invoke(); }
        public void UseMotorAndPump() { device=device with { Capabilities=[device.Capabilities[0],new("p",ToyCapabilityType.Pump,"Pump",0,3,1,true,null)] }; Changed?.Invoke(); }
        public void SendLevel(string key, string channel, int value)
        {
            if(value==0 && BlockZero) {ZeroEntered.Set(); if(!AllowZero.Wait(3000)) throw new TimeoutException("Fixture zero wait");}
            Writes.Enqueue((channel,value));
        }
        public Task StartScanAsync(CancellationToken c = default) => Task.CompletedTask;
        public Task StopScanAsync(CancellationToken c = default) => Task.CompletedTask;
        public Task ConnectAsync(string key, CancellationToken c = default) => Task.CompletedTask;
        public Task DisconnectAsync(string key, CancellationToken c = default) => Task.CompletedTask;
        public Task ReadBatteryAsync(string key, CancellationToken c = default) => Task.CompletedTask;
        public Task InitializeAsync(CancellationToken c = default) => Task.CompletedTask;
        public Task EnableAutomaticConnectionsAsync(CancellationToken c = default, Func<bool>? canScan = null) { Changed?.Invoke(); return Task.CompletedTask; }
        public Task SetAutoConnectAsync(string key, bool enabled, CancellationToken c = default) => Task.CompletedTask;
        public Task ForgetAsync(string key, CancellationToken c = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
