using System.Drawing;
using System.Text.Json;
using ShockWave.Overlay;
using ShockWave.Overlay.Toys;
using ShockWave.ShaderLab;
using ShockWave.ToyControl.Shared;

internal static class ShockerControlChecks
{
    internal static void Run(Action<bool,string> check,string artifacts,bool warp)
    {
        using var session=new ToyControlSession(()=>null,()=>null);
        var target=new ShockerControlSession.Target("fixture","My shocker","OpenShock",true,40,2000,false);
        var remote=target with { Id="remote/fixture",Name="Remote Friend",Remote=true };
        var inventory=new[] { target,remote };
        var output=new List<(string Id,string Type,int Percent,int Duration)>();
        var s=session.Shockers; s.Inventory=()=>inventory;
        s.Send=(t,type,p,d,_)=> { output.Add((t.Id,type,p,d)); return true; };
        var now=ToyOscRouter.Clock; session.Tick(now); session.SetPage(true); s.Select(0);
        s.SetSlider(30,.8f); s.SetSlider(31,1);
        check(output.Count==0 && s.Intensity==80 && s.DurationMs==15000,"shocker sliders change values without submitting commands");
        s.Press(0,33,now); s.Release(0);
        check(output.Count==0 && !s.Armed,"left grey circle cannot shock before arming");
        s.Press(0,32,now);
        check(s.Armed && output.Count==0,"right circle only arms, without output");
        s.Press(0,33,now+.1);
        check(output.Count==0 && s.Armed,"one held input cannot cross from arm to confirm");
        s.Release(0); s.Press(0,33,now+.2); s.Release(0);
        check(output.Count==1 && output[0]==("fixture","Shock",40,2000) && !s.Armed,"fresh left confirmation submits once with absolute 80-to-40 cap and duration cap");
        s.Press(0,33,now+.3); s.Release(0);
        check(output.Count==1,"confirm hold/repeated clicks never replay a completed shock");
        s.Press(0,32,now); s.Release(0); s.Tick(now+=3); s.Press(0,33,now); s.Release(0);
        check(output.Count==1 && !s.Armed,"confirmation expires at exactly three seconds");
        s.Press(0,32,now); s.Release(0); s.SetSlider(30,.79f); s.Press(0,33,now+.1); s.Release(0);
        check(output.Count==1,"changing intensity cancels a pending confirmation");
        s.Press(0,32,now); s.Release(0); s.SetSlider(31,.4f); s.Press(0,33,now+.1); s.Release(0);
        check(output.Count==1,"changing duration cancels a pending confirmation");
        s.Press(0,32,now); s.Release(0); s.Select(1); s.Press(0,33,now+.1); s.Release(0);
        check(output.Count==1 && s.Selected.Select(t=>t.Id).ToHashSet().SetEquals([target.Id,remote.Id]) && s.Model.SelectedCount==2,
            "adding a shocker cancels confirmation and preserves the first selection");
        s.Select(0);
        check(s.Selected.Single().Id==remote.Id && s.Model.SelectedCount==1,"tapping a selected shocker deselects only that row");
        s.Press(0,34,now); s.Release(0); s.Press(0,35,now); s.Release(0);
        check(output.Count==3 && output[1].Type=="Vibrate" && output[2].Type=="Sound" && output[1].Id==remote.Id,"Vibrate and Sound use the selected remote target and absolute owner caps");
        s.Press(0,32,now); s.Release(0); session.SetPage(false); session.SetPage(true); s.Press(0,33,now+.1); s.Release(0);
        check(output.Count==3,"switching pages cancels confirmation");
        s.Press(0,32,now); s.Release(0); session.Action("InInputMode",false); s.Press(0,33,now+.1); s.Release(0);
        check(output.Count==3,"switching touch/pointer cancels confirmation");
        s.Press(0,32,now); s.Release(0); inventory=[target,remote with { Available=false }]; s.Tick(now); s.Press(0,33,now+.1); s.Release(0);
        check(output.Count==3 && !s.Ready,"disconnect or pause revokes an armed target before submission");
        inventory=[target,remote]; s.Tick(now); s.Select(0); s.SetSlider(30,0); s.Press(0,32,now); s.Release(0); s.Press(0,34,now); s.Release(0);
        check(output.Count==3 && !s.Armed,"zero intensity blocks arming and output");
        s.SetSlider(30,.8f);
        s.Press(1,32,now); s.Press(2,33,now+.01); s.Release(1); s.Release(2);
        check(output.Count==3,"two simultaneously held hands cannot bypass the release requirement");
        s.Press(2,33,now+.2); s.Release(2);
        check(output.Count==4,"either hand can make the fresh second press after both release");

        // Drive real production OSC parsing and page-aware hit testing, no UDP or hardware.
        session.Action("InInputMode",false);
        void Put(string name,object value)=>session.Vrc.Apply(new("/avatar/parameters/"+ToyControlContract.Prefix+name,[value]),now);
        Put("Visible",true); Put("InBeat",true); session.Tick(now);
        void Touch(float x,float y,bool down)
        { Put("InPanelX",x/1240); Put("InPanelY",1-(y-100)/608); Put("InPanelTouch",down); session.Tick(now); }
        Touch(1088,308,true);
        check(!s.Armed,"older avatar without shocker-view acknowledgement cannot arm through graph coordinates");
        Put("InShockerReady",true); session.Tick(now); Touch(1088,308,false);
        Touch(1088,308,true); Touch(878,308,true);
        check(s.Armed && output.Count==4,"VR finger drag across both circles cannot confirm a held touch");
        Touch(878,308,false); Touch(878,308,true); Touch(878,308,false);
        check(output.Count==5 && !s.Armed,"fresh VR touch confirms once through production OSC input");
        Touch(454,240,true); Touch(454,415,true); Touch(454,415,false);
        check(s.Intensity==50 && output.Count==5,"VR intensity slider has a thick hit area and sends no signals");
        Touch(1088,308,true); Touch(1088,308,false); Put("Visible",false); session.Tick(now);
        check(!s.Armed,"hiding or losing the avatar cancels pending shock confirmation");
        Put("Visible",true); Put("InBeat",false); Put("InShockerReady",true); session.Tick(now); session.Action("InInputMode",false);
        void Point(string hand,float x,bool down)
        { PoseInput.Aim(Put,hand,x,308); Put("InPointer"+hand+"Press",down); session.Tick(now); }
        foreach(var hand in new[] { "L","R" })
        {
            var before=output.Count; Point(hand,1088,false); Point(hand,1088,true); Point(hand,878,true);
            check(output.Count==before,"held "+hand+" pointer cannot slide into confirmation");
            Point(hand,878,false); Point(hand,878,true); Point(hand,878,false);
            check(output.Count==before+1,"independent "+hand+" pointer release/confirmation submits once");
        }
        session.SetPage(false);session.SetPage(true);
        Point("L",1088,false);Point("L",1088,true);
        check(session.Vrc.PointerValid[0] && !s.Armed,"shocker-page hover stays visible while fresh view acknowledgement is pending, with commands blocked");
        Put("InShockerBeat",true);session.Tick(now);now+=.25;Put("InShockerBeat",false);session.Tick(now);
        check(session.Vrc.ShockerReady && !s.Armed,"two fresh page pulses recover a lost ready packet without replaying the held arm press");
        Point("L",1088,false);Point("L",1088,true);Point("L",1088,false);
        check(s.Armed,"a fresh press can arm after the pulse-based page confirmation");s.Cancel();
        now+=.76;Put("InBeat",true);session.Tick(now);
        check(!session.Vrc.ShockerReady,"view-pulse recovery expires when the page stops acknowledging");
        PoseInput.Aim(Put,"L",1128,148);Put("InPointerLPress",false);session.Tick(now);Put("InPointerLPress",true);session.Tick(now);
        check(!session.ShockerPage,"pointer can navigate back from an unacknowledged shocker page");
        Put("InPointerLPress",false);session.Tick(now);session.SetPage(true);Put("InShockerReady",true);session.Tick(now);
        check(ToyControlLayout.Hit(new(410,400),session)==30 && ToyControlLayout.Hit(new(660,400),session)==31,
            "108-pixel-wide slider hit boxes match the new vertical tracks");
        check(ToyControlLayout.Hit(new(810,240),session)==0 && ToyControlLayout.Hit(new(878,308),session)==33,
            "shock targets use circular hit boxes, excluding the square corners");
        s.Press(0,32,now); s.Release(0);
        session.StopAll(); check(!s.Armed,"Stop all cancels confirmation without inventing a stop-shock command");

        using var renderer=new GpuRenderer(Path.Combine(AppContext.BaseDirectory,"ToyControl","Shaders","Panel.hlsl"),1240,608,forceWarp:warp);
        void Render(string file)
        {
            using var labels=ToyControlLabels.Create(session); renderer.UploadLabels(labels);
            var state=ShaderState.From(session.DisplayModel,1240,608,(float)now,0,0);
            state.ShockData=new(1,s.Intensity/100f,(s.DurationMs-300)/14700f,s.Remaining(now)); state.ShockFlags=new(s.Armed ? 1 : 0,s.Ready ? 1 : 0,0,0);
            renderer.Render(state,false); using var image=renderer.Capture(); image.Save(Path.Combine(artifacts,file));
            var green=image.GetPixel(s.Armed ? 878 : 1088,260);
            var grey=image.GetPixel(s.Armed ? 1088 : 878,260);
            check(green.G>green.R*2 && Math.Abs(grey.G-grey.R)<15,"desktop HLSL renders the correct green circle: "+file);
        }
        Render("shocker-control.png"); s.Press(0,32,now); s.Release(0); Render("shocker-control-armed.png");
        var frame=new VrFeedback().Frame(s.Model,true);
        foreach(var name in ToyControlContract.FeedbackFields()) frame.TryAdd(name,0);
        frame["ShockerPage"]=1; frame["ShockIntensity"]=s.Intensity/100f; frame["ShockDuration"]=(s.DurationMs-300)/14700f;
        frame["ShockReady"]=1; frame["ShockArmed"]=1; frame["ShockRemaining"]=1;
        File.WriteAllText(Path.Combine(artifacts,"shocker-feedback.json"),JsonSerializer.Serialize(new {version=ToyControlContract.Version,values=frame.Select(v=>new {name=v.Key,value=v.Value})}));
        check(renderer.CompileError is null,"production shocker shader compiles on Direct3D 11");

        // Follow avatar OSC through the shared session with a remote selected target.
        // The callback records the command only; no provider or hardware is contacted.
        session.Vrc.Reset();if(session.PointerMode) session.Action("InInputMode",false);
        session.SetPage(true);now+=.1;s.Select(0);s.Select(1);s.SetSlider(30,.8f);s.SetSlider(31,1);
        Put("Visible",true);Put("InBeat",true);Put("InShockerReady",true);session.Tick(now);
        var remoteBefore=output.Count;
        Touch(1088,308,false);Touch(1088,308,true);Touch(1088,308,false);
        Touch(878,308,true);Touch(878,308,false);
        check(output.Count==remoteBefore+1 && output[^1]==(remote.Id,"Shock",40,2000),
            "in-game touch confirmation routes one remote shock with the advertised owner caps");
        session.Action("InInputMode",false);remoteBefore=output.Count;
        Point("L",1088,false);Point("L",1088,true);Point("L",1088,false);
        Point("L",878,true);Point("L",878,false);
        check(output.Count==remoteBefore+1 && output[^1]==(remote.Id,"Shock",40,2000),
            "in-game pointer confirmation routes one remote shock with the advertised owner caps");

        Multiple(check,artifacts,warp);
    }

    private static void Multiple(Action<bool,string> check,string artifacts,bool warp)
    {
        using var session=new ToyControlSession(()=>null,()=>null);
        var first=new ShockerControlSession.Target("first","Left shocker","OpenShock",true,40,2000,false);
        var second=first with {Id="remote/second",Name="Remote Right",Remote=true,Maximum=65,DurationMs=500};
        var inventory=new[] {first,second}; var s=session.Shockers;
        s.Inventory=()=>inventory;
        var output=new List<(string Id,string Type,int Percent,int Duration,long Created)>();
        s.Send=(t,type,p,d,created)=>{output.Add((t.Id,type,p,d,created));return true;};
        var now=ToyOscRouter.Clock; session.Tick(now); session.SetPage(true);
        s.Press(0,1,now);s.Release(0);s.Press(0,2,now);s.Release(0);
        s.SetSlider(30,.8f);s.SetSlider(31,1);
        check(s.Selected.Length==2 && s.Model.SelectedCount==2 && output.Count==0,"row presses select multiple shockers without output");
        var frame=new VrFeedback().Frame(s.Model,true);
        check(frame["Selected"]==2/256f && frame[ToyControlContract.Row(0,"State")]==frame[ToyControlContract.Row(1,"State")],
            "existing avatar feedback marks both selected shocker rows without new parameters");
        foreach(var hit in new[] {34,35}) {s.Press(0,hit,now);s.Release(0);}
        check(output.Count==4 && output[0].Percent==40 && output[0].Duration==2000 && output[1].Percent==65 && output[1].Duration==500 &&
            output.Select(o=>o.Type).SequenceEqual(["Vibrate","Vibrate","Sound","Sound"]) && output[0].Created==output[1].Created,
            "Vibrate and Sound submit once per selected local/remote shocker with separate absolute caps and a shared creation time");
        s.Press(0,32,now);s.Press(0,33,now+.1);
        check(s.Armed && output.Count==4,"group arming and held dragging send no commands");
        s.Release(0);s.Press(0,33,now+.2);s.Release(0);
        check(output.Count==6 && output.TakeLast(2).All(o=>o.Type=="Shock") && !s.Armed,"one fresh confirmation submits exactly once to each armed shocker");
        s.Press(0,33,now+.3);s.Release(0);s.Tick(now+=.4);
        check(output.Count==6,"completed group confirmation is not replayed by clicking or ticking");
        s.Press(0,32,now);s.Release(0);s.Select(1);s.Press(0,33,now+.1);s.Release(0);
        check(output.Count==6 && s.Selected.Single().Id==first.Id && !s.Armed,"removing one target cancels the armed group");
        s.Select(1);s.Press(0,32,now);s.Release(0);
        inventory=[first,second with {Maximum=30}];s.Tick(now);s.Press(0,33,now+.1);s.Release(0);
        check(output.Count==6 && !s.Armed && s.Selected.Length==2,"changing a group member's limits cancels confirmation while retaining selection");
        s.Press(0,32,now);s.Release(0);inventory=[first,second with {Available=false}];s.Tick(now);
        s.Press(0,33,now+.1);s.Release(0);
        check(output.Count==6 && s.Selected.Single().Id==first.Id,"disconnect drops only the unavailable selection and cancels the whole arm");
        inventory=[first,second];s.Tick(now);
        check(s.Selected.Single().Id==first.Id && output.Count==6,"returning shocker starts deselected with no automatic command");
        s.Select(1);s.Select(0);s.Select(1);s.Press(0,34,now);s.Release(0);s.Press(0,32,now);s.Release(0);
        check(s.Selected.Length==0 && !s.Ready && !s.Armed && output.Count==6,"deselecting every shocker blocks commands and arming");
        s.Select(0);s.Select(1);inventory=[first,second with {Maximum=0}];s.Tick(now);
        s.Press(0,32,now);s.Release(0);s.Press(0,34,now);s.Release(0);
        check(output.Count==6 && !s.Ready && !s.Armed,"a selected zero-cap shocker blocks the group until its usable limits or selection change");

        inventory=Enumerable.Range(0,9).Select(i=>first with {Id="paged"+i,Name="Shocker "+(i+1)}).ToArray();s.Tick(now);s.Select(0);
        session.Action("InNext",false);s.Select(0);
        check(s.Selected.Select(t=>t.Id).ToHashSet().SetEquals(["paged0","paged6"]),"selection is independent across shocker list pages");
        session.Action("InPrev",false);s.Select(0);
        check(s.Selected.Single().Id=="paged6","deselecting on one page preserves selections on another page");
        inventory=[first,second];s.Tick(now);s.Select(0);s.Select(1);
        s.Send=(t,type,p,d,created)=>{output.Add((t.Id,type,p,d,created));return t.Id!=first.Id;};
        var before=output.Count;s.Press(0,34,now);s.Release(0);s.Tick(now+=.1);
        check(output.Count==before+2 && s.Notice.Contains("1 of 2") && s.Notice.Contains("not retried"),"partial submission is reported and failed members are never retried");
        s.Send=(t,type,p,d,created)=>{output.Add((t.Id,type,p,d,created));s.Cancel();return true;};
        before=output.Count;s.Press(0,34,now);s.Release(0);
        check(output.Count==before+1,"Stop or cancellation during fan-out prevents remaining submissions");
        s.Send=(t,type,p,d,created)=>{output.Add((t.Id,type,p,d,created));Thread.Sleep(300);return true;};
        before=output.Count;s.Press(0,34,now);s.Release(0);s.Tick(now+=.1);
        check(output.Count==before+1 && s.Notice.Contains("1 of 2"),"slow submission expires the shared group deadline without sending a delayed second command");
        s.Send=(t,type,p,d,created)=>{output.Add((t.Id,type,p,d,created));return true;};

        // Both real pointer paths formerly selected twice. Independent toggles
        // must run exactly once, including re-clicking a selected row.
        session.Vrc.Reset();session.Action("InInputMode",false);
        void Put(string name,object value)=>session.Vrc.Apply(new("/avatar/parameters/"+ToyControlContract.Prefix+name,[value]),now);
        Put("Visible",true);Put("InBeat",true);Put("InShockerReady",true);session.Tick(now);
        void Point(string hand,float y,bool down) {PoseInput.Aim(Put,hand,150,y);Put("InPointer"+hand+"Press",down);session.Tick(now);}
        foreach(var hand in new[] {"L","R"})
        {
            Point(hand,270,false);Point(hand,270,true);Point(hand,270,false);
            check(s.Selected.Length==1 && s.Selected[0].Id==second.Id,hand+" pointer deselects its row exactly once without changing its neighbor");
            Point(hand,270,true);Point(hand,270,false);
            check(s.Selected.Length==2,hand+" pointer reselects one row exactly once");
        }
        session.Action("InInputMode",false);
        Put("InRow0",true);session.Tick(now);Put("InRow1",true);session.Tick(now);
        check(s.Selected.Length==1 && s.Selected[0].Id==second.Id,"finger selection toggles one shocker and rejects a neighboring contact during the same touch");
        Put("InRow0",false);Put("InRow1",false);session.Tick(now);now+=.21;session.Tick(now);
        Put("InRow0",true);session.Tick(now);Put("InRow0",false);session.Tick(now);
        check(s.Selected.Length==2,"finger rows can select multiple shockers with a fresh released touch");
        void Touch(float x,bool down)
        {Put("InPanelX",x/1240);Put("InPanelY",1-(308-100)/608f);Put("InPanelTouch",down);session.Tick(now);}
        before=output.Count;Touch(1088,false);Touch(1088,true);Touch(1088,false);Touch(878,true);Touch(878,false);
        check(output.Count==before+2 && output.TakeLast(2).All(o=>o.Type=="Shock"),"one released VR surface confirmation submits once to both selected shockers");
        session.Action("InInputMode",false);
        foreach(var hand in new[] {"L","R"})
        {
            void Aim(float x,bool down) {PoseInput.Aim(Put,hand,x,308);Put("InPointer"+hand+"Press",down);session.Tick(now);}
            before=output.Count;Aim(1088,false);Aim(1088,true);Aim(1088,false);Aim(878,true);Aim(878,false);
            check(output.Count==before+2 && output.TakeLast(2).All(o=>o.Type=="Shock"),hand+" pointer confirms one discrete command per selected shocker");
        }
        using var renderer=new GpuRenderer(Path.Combine(AppContext.BaseDirectory,"ToyControl","Shaders","Panel.hlsl"),1240,608,forceWarp:warp);
        using var labels=ToyControlLabels.Create(session);renderer.UploadLabels(labels);
        var state=ShaderState.From(s.Model,1240,608,(float)now,0,0);
        state.ShockData=new(1,s.Intensity/100f,(s.DurationMs-300)/14700f,0);state.ShockFlags=new(0,s.Ready ? 1 : 0,0,0);
        renderer.Render(state,false);using var image=renderer.Capture();image.Save(Path.Combine(artifacts,"shocker-multiple.png"));
        check(renderer.CompileError is null,"historical HLSL fixture renders the multi-selected shocker layout");
    }
}
