using System.Text.Json;
using ShockWave.Overlay.Toys;

namespace ShockWave.Overlay.SteamVr;

// Only explicitly invoked diagnostics construct this fake adapter. No native
// device, provider, OSC socket, settings store or SteamVR session is opened.
internal static class PanelIntegrationChecks
{
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);var checks=new List<string>();
        void Check(bool condition,string name) {if(!condition) throw new InvalidOperationException(name);checks.Add(name);}
        try
        {
            var app=typeof(PanelIntegrationChecks).Assembly;
            Check(new[]{"ShockWave.ShaderLab.GpuRenderer","ShockWave.Overlay.GpuPanelFrameRenderer","ShockWave.Overlay.PanelRenderWorker","ShockWave.Overlay.SteamVr.PanelPreview","ShockWave.ShaderLab.OscListener"}.All(name=>app.GetType(name) is null),
                "production app excludes the retired renderer, demo OSC listener and unused preview control");
            Check(app.GetReferencedAssemblies().All(name=>name.Name!="Vortice.D3DCompiler"),"production app has no shader compiler reference");
            var fake=new FakeAdapter();
            var controller=new LocalToyController(fake,new LocalToyControlStore(),automaticControls:true);
            try
            {
                using var session=new ToyControlSession(()=>fake.Snapshot,()=>controller);
                var panel=new PanelModel(session,(_,_) => ("Vibration",35));var input=new PanelInput(panel);
                panel.ContextInvalidated+=()=>input.CancelPointer();panel.LayoutChanged+=()=>input.CancelPointer();
                var now=ToyOscRouter.Clock;
                void Tick(double delta=0) {now+=delta;session.Tick(now);panel.Tick(now);panel.MarkPresented();}
                PointF Center(string id) {var r=panel.Layout().Hits.First(h=>h.Id==id).Rect;return new(r.X+r.Width/2,r.Y+r.Height/2);}
                void Click(string id,string source="desktop") {var p=Center(id);input.Down(source,p);input.Up(source,p);Tick();}
                Tick();Check(panel.Functions.Count==10 && panel.PageCount==2,"real inventory has ten rows and overflow paging");
                Check(panel.Functions[0].BatteryPercent==67 && panel.Functions[0].SensitivityPercent==35,"inventory battery and owner sensitivity reach panel");
                Click("row0");Check(session.Model.Functions.First().Selected,"row selection changes production session");
                panel.ChangePage(1);panel.MarkPresented();Check(panel.Functions.Count==2 && panel.Functions[0].Name=="Fixture","overflow contains remaining functions");
                panel.ChangePage(-1);panel.MarkPresented();Check(panel.Functions[0].Selected,"selection survives panel paging");
                Click("record");Check(session.Model.RecordArmed && panel.Recording,"Record uses shared production recorder");
                var graph=Center("graph");input.Down("vr1",graph);Check(session.Model.Touching && panel.Drawing,"controller curve opens production input lease");
                var outside=new PointF(-400,panel.Layout().Plot.Top-200);input.Move("vr1",outside);Tick(.25);
                Check(input.IsDrawing("vr1") && session.Model.Functions.First().Level==1,"captured curve clamps and continues beyond grid");
                input.Up("vr0",outside);Check(input.IsDrawing("vr1"),"other controller cannot release drawing");
                input.Up("vr1",outside);Tick(.1);
                Check(session.Model.LoopCount==1 && panel.Functions[0].Playing && ReferenceEquals(panel.Functions[0].Pattern,session.Model.Functions.First().Function.Pattern),"release loops the production clip without a second playback engine");
                Check(fake.Writes.All(value=>value==0),"Test mode sends no positive native values");
                using(var image=PanelView.Render(panel,"integration")) image.Save(Path.Combine(output,"integrated-wrist.png"));
                Click("stop");Tick(.1);Check(session.Model.LoopCount==0 && panel.Functions.All(f=>f.Level==0),"Stop ends production loops and held values");
                panel.SetCompact(false);Tick();Click("row0");Check(input.GripDown("vr0","grab-left"),"physical rail grip starts independently of pointer");
                input.Down("vr1",Center("graph"));input.Move("vr1",outside);Tick(.25);
                Check(input.IsGrabbing("vr0") && input.IsDrawing("vr1"),"free hand draws through production while holding panel");
                input.GripUp("vr0");Check(input.IsDrawing("vr1"),"grip release preserves free-hand drawing");
                input.Cancel();Tick();Check(!session.Model.Touching,"cancel discards unfinished production capture");
                var layout=panel.Layout();
                using(var image=PanelView.Render(panel,"integration"))
                {
                    image.Save(Path.Combine(output,"integrated-panel.png"));
                    Check(image.GetPixel(2,2).A==0 && image.GetPixel((int)layout.MainArea.Left+4,(int)layout.MainArea.Top+4).A==0,"floating frame and main background remain transparent");
                }
                var calls=new List<(string Type,int Intensity,int Duration)>();
                var targets=new[]{new ShockerControlSession.Target("local","Local","OpenShock",true,40,800,false),new ShockerControlSession.Target("remote","Remote","PiShock",true,25,500,true)};
                session.Shockers.Inventory=()=>targets;session.Shockers.Send=(target,type,intensity,duration,_)=>{calls.Add((type,intensity,duration));return true;};
                session.SetPage(true);Tick();Click("shocker0");Click("shocker1");
                panel.SetShockerIntensity(80);panel.SetShockerDuration(1500);Tick();
                Check(calls.Count==0 && session.Shockers.Selected.Length==2 && panel.ShockerIntensity==80 && panel.ShockerDurationMs==1500,"shocker multi-selection and sliders send no command");
                Click("shock","vr1");Check(calls.Count==0 && session.Shockers.Armed,"first Shock click only arms");
                input.Down("vr1",Center("shock"));input.Move("vr1",Center("vibrate"));input.Up("vr1",Center("vibrate"));
                Check(calls.Count==0,"held shock click cannot cross into another command");
                Click("shock","vr1");Check(calls.Count==2 && calls[0]==("Shock",40,800) && calls[1]==("Shock",25,500),"second released Shock click submits once per target with independent caps");
                Tick(.1);Check(calls.Count==2,"ticking cannot replay discrete shock submissions");
                Click("shock","desktop");Click("shock","vr1");Check(calls.Count==2 && !session.Shockers.Armed,"another input source cannot confirm a desktop arm");
                Click("shock","vr1");Tick(3);Click("shock","vr1");Check(calls.Count==2,"expired confirmation never submits");
                panel.SetShockerDuration(500);Check(!session.Shockers.Armed,"slider edit cancels confirmation");Tick();
                Click("vibrate");Click("beep");Check(calls.Count==6 && calls[^1].Type=="Sound","Vibrate and Beep route through production one-shot submission");
                Click("shock","vr1");panel.SetTransparency(100);Check(!session.Shockers.Armed && panel.TransparencyPercent==100,"fully hidden panel cancels pending confirmation");
                var before=calls.Count;input.Down("vr1",Center("shock"));input.Up("vr1",Center("shock"));Check(calls.Count==before,"fully transparent panel is inert");
                panel.SetTransparency(50);using(var image=PanelView.Render(panel,"integration"))
                {image.Save(Path.Combine(output,"integrated-shockers-half.png"));Check(image.GetPixel(2,2).A==0,"whole-overlay fade retains clear frame");}
                var config=BridgeConfig.Parse("{\"shockers\":[],\"dryRun\":true}");Check(config.PanelTransparencyPercent==0,"old settings default to a visible panel");
                config.PanelTransparencyPercent=65;Check(BridgeConfig.Parse(config.ToJson()).PanelTransparencyPercent==65,"transparency round-trips without replacing existing settings");
                var frozen=panel.CaptureDisplay();var frozenIntensity=frozen.ShockerIntensity;panel.SetShockerIntensity(15);
                Check(frozen.ShockerIntensity==frozenIntensity,"render snapshot does not read later live controls");
                using(var gpu=new GpuOverlayTexture(software:true,fixedSize:PanelSizing.MaximumSurface))
                {
                    nint handle=0;var stable=true;
                    for(var i=0;i<12;i++)
                    {
                        panel.SetContentSize(980+i*20,650+i*12);using var image=PanelView.Render(panel.CaptureDisplay(),"GPU fixture");
                        gpu.Upload(image);if(handle==0) handle=gpu.Descriptor.handle;else stable &= handle==gpu.Descriptor.handle;
                        gpu.PresentationCommitted();
                    }
                    Check(stable && gpu.SurfaceSize==PanelSizing.MaximumSurface,"integrated resize uses one persistent GPU surface");
                }
                Check(controller.TestMode,"diagnostics retain Test mode");
                fake.Connected=false;Tick();Check(panel.Functions.All(f=>!f.Connected),"disconnect updates availability without cached demo inventory");
                fake.Connected=true;Tick();Check(panel.Functions.All(f=>!f.Selected && !f.Playing),"reconnected toys start unselected without replay");
                session.SetPage(false);Tick();session.SelectPanelFunction("fixture","f0");Tick();
                session.DrawPanel(.52f,true);Tick(.1);
                var desktop=DesktopPanelView.Capture(session,0,[new(now-.1,.2f,true),new(now,.52f,true)]);
                using(var image=DesktopPanelView.Render(desktop))
                {
                    image.Save(Path.Combine(output,"desktop-classic-toys.png"));
                    Check(image.Size==new Size(1240,608) && image.GetPixel(30,300).ToArgb()==DesktopPanelView.Sidebar.ToArgb(),"desktop replica retains original sidebar geometry and palette");
                    Check(image.GetPixel(350,350).ToArgb()==DesktopPanelView.Panel.ToArgb() && image.GetPixel(500,200).ToArgb()!=DesktopPanelView.Panel.ToArgb(),"original desktop main panel and selected grid are recreated without HLSL");
                    Check(image.GetPixel(720,48).ToArgb()==DesktopPanelView.Panel.ToArgb(),"Touch button is absent from the desktop toy view");
                }
                foreach(var hit in new[]{DesktopPanelLayout.OpenVrHit,DesktopPanelLayout.RecenterHit,13,25})
                {using var image=DesktopPanelView.Render(desktop with {Hover=hit});image.Save(Path.Combine(output,$"desktop-toys-hover-{hit}.png"));}
                var frozenDesktop=DesktopPanelView.Capture(session,0,[]);var frozenLevel=frozenDesktop.Rows[0].Level;session.DrawPanel(.9f,false);
                Check(frozenDesktop.Rows[0].Level==frozenLevel && session.Model.Level!=frozenLevel,"desktop worker receives immutable values without session access");
                session.Release(false);panel.SetTransparency(100);panel.SetCompact(true);Tick();
                using(var image=DesktopPanelView.Render(DesktopPanelView.Capture(session,0,[])))
                    Check(image.GetPixel(500,300).A==255 && image.Size==DesktopPanelView.Size,"desktop replica is independent of wrist placement and overlay transparency");
                session.SetPage(true);Tick();session.Shockers.SelectPanelTarget("local");session.Shockers.SetSlider(30,.8f);session.Shockers.Press(0,32,now);session.Shockers.Release(0);
                var shockDesktop=DesktopPanelView.Capture(session,0,[]);
                foreach(var hit in new[]{DesktopPanelLayout.OpenVrHit,DesktopPanelLayout.RecenterHit,13})
                {using var image=DesktopPanelView.Render(shockDesktop with {Hover=hit});image.Save(Path.Combine(output,$"desktop-shockers-hover-{hit}.png"));}
                using(var labelImage=shockDesktop.Labels.Draw()) labelImage.Save(Path.Combine(output,"desktop-shocker-labels.png"));
                using(var image=DesktopPanelView.Render(shockDesktop))
                {
                    image.Save(Path.Combine(output,"desktop-classic-shockers.png"));
                    Check(image.GetPixel(820,254).ToArgb()==DesktopPanelView.Accent.ToArgb() && image.GetPixel(1060,175).ToArgb()==DesktopPanelView.Panel.ToArgb(),"desktop uses one rectangular Shock button and removes the old circles");
                    Check(image.GetPixel(720,48).ToArgb()==DesktopPanelView.Panel.ToArgb(),"Touch button is absent from the desktop shocker view");
                }
            }
            finally {controller.DisposeAsync().AsTask().GetAwaiter().GetResult();}
            CheckSliderFrames(Check,output);
            CheckShockFeedback(Check,output);
            WristMenuChecks.Run(Check,output);
            VrInputInstallationChecks.Run(Check,output);
            var flags=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
            using(var modeForm=new OverlayForm(new BridgeConfig {DryRun=false}))
            {
                var modePage=(ToyControlPage)typeof(OverlayForm).GetField("toyControlPage",flags)!.GetValue(modeForm)!;
                modePage.Session.SetPage(true);modePage.Panel.Tick(ToyOscRouter.Clock);
                Check(!modePage.Session.Shockers.IsTestMode() && modePage.Panel.ModeLabel=="LIVE MODE","shocker feedback follows the app mode without a connected Bluetooth controller");
            }
            BridgeConfig? saved=null;
            using(var form=new OverlayForm(new BridgeConfig {DryRun=true,PanelTransparencyPercent=40,PanelHand="Right",PanelStartMode="Panel",PanelAutoOpenInVr=true},initialSaveConfig:next=>{saved=next;throw new IOException("Fixture stops before bridge restart");}))
            {
                var slider=(PercentSlider)typeof(OverlayForm).GetField("panelTransparencyInput",flags)!.GetValue(form)!;
                var page=(ToyControlPage)typeof(OverlayForm).GetField("toyControlPage",flags)!.GetValue(form)!;
                Check(slider.Value==40 && page.Panel.TransparencyPercent==40,"real Settings loads whole-overlay transparency");
                var hand=(ComboBox)typeof(OverlayForm).GetField("panelHandInput",flags)!.GetValue(form)!;
                var mode=(ComboBox)typeof(OverlayForm).GetField("panelStartModeInput",flags)!.GetValue(form)!;
                var auto=(CheckBox)typeof(OverlayForm).GetField("panelAutoOpenInput",flags)!.GetValue(form)!;
                Check(hand.SelectedItem as string=="Right" && mode.SelectedItem as string=="Panel" && auto.Checked && page.Panel.WristHand==Mount.RightHand && !page.Panel.Compact,"Settings restores the chosen hand and startup panel mode");
                Check(page.AutoOpenInVr && !page.AutoOpenPending,"constructing an isolated Settings fixture never starts an automatic VR session");
                mode.SelectedItem="Wrist";
                Check(page.Panel.Compact && page.Panel.WristHand==Mount.RightHand && saved is null,"choosing Wrist previews the mounted mode before saving");
                mode.SelectedItem="Panel";
                Check(!page.Panel.Compact && page.Panel.Mount==Mount.Floating && saved is null,"choosing Panel immediately drops the detailed panel before saving");
                hand.SelectedItem="Left";
                Check(!page.Panel.Compact && page.Panel.WristHand==Mount.LeftHand && saved is null,"changing the remembered hand keeps a selected floating panel floating");
                page.OpenVrAction=()=>true;page.Panel.SetCompact(true);page.OpenInVr();
                Check(!page.Panel.Compact && page.Panel.Mount==Mount.Floating,"Open in VR reapplies the chosen Panel mode after returning to the wrist");
                var placements=0;page.Panel.PlacementChanged+=()=>placements++;page.OpenInVr();
                Check(placements==0 && !page.Panel.Compact,"repeated Open in VR preserves an already-correct floating placement");
                slider.Value=65;Check(page.Panel.TransparencyPercent==65,"Settings slider previews transparency immediately");
                hand.SelectedItem="Left";mode.SelectedItem="Wrist";auto.Checked=false;
                typeof(OverlayForm).GetMethod("SaveSettings",flags)!.Invoke(form,null);
                Check(saved?.PanelTransparencyPercent==65 && saved.DryRun,"Save settings includes transparency and preserves Test mode");
                Check(saved?.PanelHand=="Left" && saved.PanelStartMode=="Wrist" && !saved.PanelAutoOpenInVr,"Save settings retains hand, startup mode and automatic-open choice");
                var settings=(Control)typeof(OverlayForm).GetField("settingsPage",flags)!.GetValue(form)!;
                var navigation=(Button)typeof(OverlayForm).GetField("settingsNav",flags)!.GetValue(form)!;
                typeof(OverlayForm).GetMethod("ShowPage",flags)!.Invoke(form,[settings,navigation]);
                var shell=form.Controls.OfType<TableLayoutPanel>().Single();form.Controls.Remove(shell);
                using(var host=new Form {ClientSize=new(1040,760),ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new(-32000,-32000)})
                {
                    host.Controls.Add(shell);host.Show();Application.DoEvents();
                    if(slider.Parent?.Parent is ScrollableControl scroll) scroll.ScrollControlIntoView(slider.Parent);
                    Application.DoEvents();using var image=new Bitmap(host.Width,host.Height);host.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(output,"integrated-settings.png"));
                    host.Hide();host.Controls.Remove(shell);form.Controls.Add(shell);
                }
            }
            var oldConfig=BridgeConfig.Parse("{\"shockers\":[],\"dryRun\":true}");
            Check(oldConfig.PanelHand=="" && oldConfig.PanelStartMode=="Wrist" && !oldConfig.PanelAutoOpenInVr,"old configurations preserve their last wrist and default automatic opening off");
            var roundTrip=BridgeConfig.Parse(new BridgeConfig {PanelHand="Right",PanelStartMode="Panel",PanelAutoOpenInVr=true}.ToJson());
            Check(roundTrip.PanelHand=="Right" && roundTrip.PanelStartMode=="Panel" && roundTrip.PanelAutoOpenInVr,"VR startup options survive config round-trip");
            var startup=new VrAutoOpen();var opens=0;
            bool Open() {opens++;return true;}
            startup.Arm(false);startup.Tick(0,true,Open);Check(opens==0 && !startup.Pending,"automatic opening stays off by default");
            startup.Arm(true);startup.Tick(0,false,Open);startup.Tick(1,true,Open);startup.Tick(1.5,true,Open);
            Check(opens==0 && startup.Pending,"automatic opening waits for SteamVR and a short settling interval");
            startup.Tick(2,true,Open);startup.Tick(100,true,Open);Check(opens==1 && !startup.Pending,"automatic opening succeeds once without reopening continuously");
            startup.Arm(true);startup.Tick(0,true,()=>false);startup.Tick(1,true,()=>false);startup.Tick(3,true,()=>false);startup.Tick(8,true,()=>false);
            Check(startup.Attempts==3 && !startup.Pending,"failed overlay startup has bounded retries");
            startup.Arm(true);startup.Cancel();startup.Tick(100,true,Open);Check(opens==1 && !startup.Pending,"manual cancellation prevents later automatic opening");
            File.WriteAllText(Path.Combine(output,"panel-integration.json"),JsonSerializer.Serialize(new {passed=true,total=checks.Count,checks,physicalDevices=false,realSettings=false},new JsonSerializerOptions {WriteIndented=true}));return 0;
        }
        catch(Exception error) {File.WriteAllText(Path.Combine(output,"failure.txt"),error.ToString());return 1;}
    }
    private static void CheckSliderFrames(Action<bool,string> check,string output)
    {
        using var session=new ToyControlSession(()=>null,()=>null);
        session.Shockers.Inventory=()=>[new("slider-fixture","Fixture","Mock",true,100,15000,false)];
        var commands=0;session.Shockers.Send=(_,_,_,_,_)=>{commands++;return true;};
        var panel=new PanelModel(session);var input=new PanelInput(panel);
        panel.ContextInvalidated+=()=>input.CancelPointer();panel.LayoutChanged+=()=>input.CancelPointer();
        void Tick() {session.Tick(ToyOscRouter.Clock);panel.Tick(ToyOscRouter.Clock);}
        var samples=new List<object>();
        foreach(var compact in new[]{true,false})
        {
            panel.SetCompact(compact);session.SetPage(true);Tick();
            if(session.Shockers.Selected.Length==0) {panel.ToggleShocker(0);Tick();}
            var mode=compact ? "wrist" : "panel";
            foreach(var id in new[]{"intensity","duration"})
            {
                var rect=panel.Layout().Hits.Single(h=>h.Id==id).Rect;
                PointF At(float fraction)=>compact ? new(rect.Left+16+fraction*(rect.Width-32),rect.Top+rect.Height/2)
                    : new(rect.Left+rect.Width/2,rect.Bottom-16-fraction*(rect.Height-32));
                panel.MarkPresented();input.Down("vr1",At(.1f));
                check(input.IsSliding("vr1"),$"{mode} {id}: trigger captures the slider");
                using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
                using var worker=new RasterPanelWorker((snapshot,version)=>
                {
                    Thread.Sleep(80); // A deliberately slow complete raster frame.
                    var image=PanelView.Render(snapshot,version);
                    image.Tag=id=="intensity" ? snapshot.ShockerIntensity : snapshot.ShockerDurationMs;
                    entered.Set();if(!release.Wait(5000)) {image.Dispose();throw new TimeoutException("Slider fixture release");}
                    return image;
                });
                (Bitmap Image,PanelFrameStamp Stamp) Take()
                {
                    var deadline=Environment.TickCount64+5000;
                    while(Environment.TickCount64<deadline)
                    {
                        if(worker.Failure is { } error) throw new InvalidOperationException("Slider renderer failed",error);
                        if(worker.TryTake(out var image,out var stamp) && image is not null) return(image,stamp);
                        Thread.Sleep(2);
                    }
                    throw new TimeoutException("Slider fixture frame");
                }
                var progressing=true;var actionsBlocked=true;var previous=-1;
                try
                {
                    for(var step=0;step<4;step++)
                    {
                        entered.Reset();release.Reset();worker.Submit(panel.CaptureDisplay(),panel.CaptureFrameStamp());
                        if(!entered.Wait(5000)) throw new TimeoutException("Slider fixture render");
                        input.Move("vr1",At(.25f+step*.15f));release.Set();
                        var frame=Take();using var image=frame.Image;
                        var rendered=(int)image.Tag!;var live=id=="intensity" ? panel.ShockerIntensity : panel.ShockerDurationMs;
                        var display=panel.CanDisplay(frame.Stamp);
                        progressing &= display && rendered>previous && rendered!=live;
                        if(display) panel.MarkPresented(frame.Stamp.Input);
                        actionsBlocked &= !panel.AcceptsInput;
                        samples.Add(new {mode,slider=id,rendered,live,display,actionsReady=panel.AcceptsInput});previous=rendered;
                        if(step==3) image.Save(Path.Combine(output,$"slider-drag-{mode}-{id}.png"));
                    }
                    check(progressing,$"{mode} {id}: slow frames keep advancing throughout continuous movement");
                    check(actionsBlocked,$"{mode} {id}: visible older values do not authorize commands");
                    var value=id=="intensity" ? panel.ShockerIntensity : panel.ShockerDurationMs;
                    input.Move("vr0",At(0));input.Up("vr0",At(0));
                    check(input.IsSliding("vr1") && value==(id=="intensity" ? panel.ShockerIntensity : panel.ShockerDurationMs),$"{mode} {id}: other controller cannot move or release capture");
                    input.Move("vr1",At(2));input.Up("vr1",At(2));
                    var shock=panel.Layout().Hits.Single(h=>h.Id=="shock").Rect;
                    var point=new PointF(shock.Left+shock.Width/2,shock.Top+shock.Height/2);
                    input.Down("vr1",point);input.Up("vr1",point);
                    check(!input.PointerActive && !session.Shockers.Armed && (id=="intensity" ? panel.ShockerIntensity==100 : panel.ShockerDurationMs==15000),$"{mode} {id}: off-board drag clamps, releases, and blocks Shock until current values display");
                    worker.Submit(panel.CaptureDisplay(),panel.CaptureFrameStamp());var final=Take();using(final.Image)
                    {
                        if(panel.CanDisplay(final.Stamp)) panel.MarkPresented(final.Stamp.Input);
                        check(panel.AcceptsInput,$"{mode} {id}: final frame restores actions after release");
                    }
                }
                finally {release.Set();}
            }
            var old=panel.CaptureFrameStamp();panel.ToggleShocker(0);Tick();
            check(!panel.CanDisplay(old),$"{mode}: changed target selection rejects an old slider frame");
            old=panel.CaptureFrameStamp();panel.ToggleResize();
            check(!panel.CanDisplay(old),$"{mode}: changed resize controls reject an old slider frame");
            old=panel.CaptureFrameStamp();session.SetPage(false);Tick();
            check(!panel.CanDisplay(old),$"{mode}: changing page rejects an old slider frame");
        }
        // Visual captures for the wrist-only confirmation prompt, using the
        // existing fake targets and native trigger path. No command is sent.
        panel.SetCompact(true);session.SetPage(true);Tick();
        if(session.Shockers.Selected.Length==0) panel.ToggleShocker(0);
        panel.SetShockerIntensity(60);panel.SetShockerDuration(2000);Tick();
        void Capture(string name) {using var image=PanelView.Render(panel.CaptureDisplay(),"confirmation preview");image.Save(Path.Combine(output,name+".png"));}
        Capture("wrist-confirmation-idle");
        panel.MarkPresented();var shockRect=panel.Layout().Hits.Single(h=>h.Id=="shock").Rect;
        var shockPoint=new PointF(shockRect.Left+shockRect.Width/2,shockRect.Top+shockRect.Height/2);
        input.Down("vr1",shockPoint);input.Up("vr1",shockPoint);Tick();Capture("wrist-confirmation-armed");
        panel.SetContentSize(PanelSizing.MinimumContent.Width,PanelSizing.MinimumContent.Height);Capture("wrist-confirmation-small");
        panel.SetContentSize(PanelSizing.MaximumContent.Width,PanelSizing.MaximumContent.Height);Capture("wrist-confirmation-large");
        panel.SetContentSize(PanelSizing.DefaultContent.Width,PanelSizing.DefaultContent.Height);
        session.Shockers.Tick(ToyOscRouter.Clock+3.1);panel.Tick(ToyOscRouter.Clock+3.1);Capture("wrist-confirmation-expired");
        check(commands==0,"continuous slider render checks send no device commands");
        File.WriteAllText(Path.Combine(output,"slider-rendering.json"),JsonSerializer.Serialize(new {injectedRenderDelayMs=80,samples,physicalDevices=false},new JsonSerializerOptions {WriteIndented=true}));
    }
    private static void CheckShockFeedback(Action<bool,string> check,string output)
    {
        using var session=new ToyControlSession(()=>null,()=>null);
        var s=session.Shockers;var preview=true;
        s.IsTestMode=()=>preview;
        s.Inventory=()=>[new("first","Fixture first","Mock",true,40,2000,false),new("second","Fixture second","Mock",true,25,500,true)];
        var calls=new List<(string Id,string Type,int Percent,int Duration)>();
        s.Send=(target,type,percent,duration,_)=>{calls.Add((target.Id,type,percent,duration));return true;};
        var now=ToyOscRouter.Clock;session.SetPage(true);session.Tick(now);s.SelectPanelTarget("first");s.SelectPanelTarget("second");
        s.SetSlider(30,.8f);s.SetSlider(31,1);
        var panel=new PanelModel(session);panel.Tick(now);
        s.Press(4,32,now);s.Release(4);
        check(s.Armed && s.ShockFeedback(now)=="" && calls.Count==0,"first Shock press has no submitted-strength receipt");
        s.Press(4,33,now+.1);s.Release(4);panel.Tick(now+.12);
        check(calls.SequenceEqual(new[]{("first","Shock",40,2000),("second","Shock",25,500)}),"shock receipt uses the actual submitted per-target capped values");
        check(panel.ShockerFeedback=="Test shock · 25–40%","confirmed group shows the capped strength range with an explicit Test label");
        var frozen=panel.CaptureDisplay();var old=panel.CaptureFrameStamp();
        void Capture(string name,PanelModel model) {using var image=PanelView.Render(model,"feedback preview");image.Save(Path.Combine(output,name+".png"));}
        Capture("wrist-shock-feedback-range",frozen);
        frozen.SetContentSize(PanelSizing.MinimumContent.Width,PanelSizing.MinimumContent.Height);Capture("wrist-shock-feedback-small",frozen);
        preview=false;s.SetSlider(30,.2f);s.SetSlider(31,0);s.Cancel();panel.Tick(now+.2);panel.MarkPresented();
        old=panel.CaptureFrameStamp();
        check(panel.ShockerFeedback=="Test shock · 25–40%" && calls.Count==2,"slider, confirmation and mode edits do not rewrite an already submitted shock receipt");
        panel.Tick(now+.65);
        check(panel.ShockerFeedback=="Test shock · 40%","shorter capped target duration expires independently from the remaining shock");
        Capture("wrist-shock-feedback-single",panel.CaptureDisplay());
        panel.Tick(now+2.2);
        check(panel.ShockerFeedback=="","submitted strength disappears after the last capped duration");
        check(!panel.CanDisplay(old) && panel.AcceptsInput,"expired receipt images are rejected without changing action readiness");
        check(frozen.ShockerFeedback=="Test shock · 25–40%","raster snapshot freezes feedback without reading the later live state");
        Capture("wrist-shock-feedback-expired",panel.CaptureDisplay());
        void Confirm(double at) {s.Press(4,32,at);s.Release(4);s.Press(4,33,at+.1);s.Release(4);}
        s.Send=(_,_,_,_,_)=>false;Confirm(now+3);
        check(s.ShockFeedback(now+3.2)=="","failed submission creates no running-strength display");
        s.Send=(target,type,percent,duration,_)=>
        {
            if(target.Id!="first") return false;
            calls.Add((target.Id,type,percent,duration));return true;
        };
        s.SetSlider(30,.8f);s.SetSlider(31,1);Confirm(now+4);panel.Tick(now+4.12);
        check(panel.ShockerFeedback=="Shock sent · 40%" && calls.Count==3,"partial success displays only the accepted target's strength");
        Capture("wrist-shock-feedback-live",panel.CaptureDisplay());
        using(var image=DesktopPanelView.Render(DesktopPanelView.Capture(session,0,[]))) image.Save(Path.Combine(output,"desktop-shock-feedback-live.png"));
        panel.SetCompact(false);panel.Tick(now+4.2);
        check(panel.ShockerStatus=="Shock sent · 40%","floating panel uses the same receipt in its confirmation status position");
        Capture("panel-shock-feedback-live",panel.CaptureDisplay());
        preview=true;panel.Tick(now+4.3);
        check(panel.ShockerFeedback=="Shock sent · 40%","later Test-mode changes do not relabel the captured command");
        s.Press(4,32,now+4.4);s.Release(4);panel.Tick(now+4.4);
        check(panel.ShockerArmed && panel.ShockerStatus=="Release, then press Shock again","a fresh confirmation prompt takes priority over an older receipt");
        s.Cancel();s.Tick(now+6.2);panel.Tick(now+6.2);
        var before=calls.Count;for(var i=0;i<20;i++) {s.Tick(now+7+i);_ = s.ShockFeedback(now+7+i);}
        check(calls.Count==before && s.ShockFeedback(now+27)=="","feedback timers never resend or replay a shock");
        s.Press(4,34,now+28);s.Release(4);s.Press(4,35,now+28.1);s.Release(4);
        check(s.ShockFeedback(now+28.2)=="","Vibrate and Sound do not start a shock-strength display");
    }
    internal sealed class FakeAdapter : ILocalToyAdapter,ILocalToyOutput
    {
        public bool Connected=true;
        public System.Collections.Concurrent.ConcurrentQueue<int> Writes {get;}=new();
        public event Action? Changed {add {} remove {}}
        public event Action<string>? OutputFailed {add {} remove {}}
        public ToyAdapterSnapshot Snapshot=>new(ToyAdapterAvailability.Ready,false,"Isolated fixture",[
            new("fixture","fake","Fixture","Fixture",Connected ? ToyConnectionState.Connected : ToyConnectionState.Disconnected,
                Enumerable.Range(0,12).Select(i=>new ToyCapability("f"+i,ToyCapabilityType.Vibration,"Vibration "+i,0,20,1,true,null)).ToArray(),true,67,DateTimeOffset.UtcNow,10,true)]);
        public void SendLevel(string device,string channel,int value)=>Writes.Enqueue(value);
        public Task StartScanAsync(CancellationToken cancel=default)=>Task.CompletedTask;
        public Task StopScanAsync(CancellationToken cancel=default)=>Task.CompletedTask;
        public Task ConnectAsync(string key,CancellationToken cancel=default)=>Task.CompletedTask;
        public Task DisconnectAsync(string key,CancellationToken cancel=default)=>Task.CompletedTask;
        public Task ReadBatteryAsync(string key,CancellationToken cancel=default)=>Task.CompletedTask;
        public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
