using System.Text.Json;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Imaging;
using System.Reflection;
using System.Security.Cryptography;
using ShockWave.Overlay;
using ShockWave.Overlay.Toys;
using ShockWave.ShaderLab;

internal static partial class Program
{
    private static void RenderStress()
    {
        using var receiver=new Fixture();using var sender=new Fixture();using var local=new LocalToysPage();
        sender.Session.Receive("inventory",Element(receiver.Session.PublishInventory()));
        var frames=0;
        using var page=new ToyControlPage(local,(_,_)=>{});
        page.RenderDesktopFrame=snapshot=>{Thread.Sleep(180);Interlocked.Increment(ref frames);return DesktopPanelView.Render(snapshot);};
        page.Session.Remote=sender.Session.Toys;
        using var preview=new Form {Width=1050,Height=800,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new(-32000,-32000)};
        preview.Controls.Add(page);preview.Show();Application.DoEvents();
        page.Session.Tick(ToyOscRouter.Clock);page.Session.Model.Functions.First().Function.Selected=true;
        page.Session.Draw(new(600,300),false,true);
        var levels=sender.Session.Toys.Levels();var expected=levels[0].Percent;
        receiver.Session.Receive("state",Element(levels),150);
        var last=Environment.TickCount64;long maximumGap=0,maximumWork=0;var ticks=0;var expired=false;
        using var timer=new System.Windows.Forms.Timer {Interval=33};var until=Environment.TickCount64+2500;
        timer.Tick+=(_,_)=>{
            if(Environment.TickCount64>=until){timer.Stop();return;}
            var at=Environment.TickCount64;maximumGap=Math.Max(maximumGap,at-last);last=at;ticks++;
            var work=Stopwatch.GetTimestamp();page.Tick();maximumWork=Math.Max(maximumWork,(long)Math.Ceiling(Stopwatch.GetElapsedTime(work).TotalMilliseconds));
            expired|=receiver.Controller.State("fixture","v1").RequestedPercent==0;
            receiver.Session.Receive("state",Element(sender.Session.Toys.Levels()),150);
        };
        timer.Start();
        while(Environment.TickCount64<until){Application.DoEvents();Thread.Sleep(5);}
        timer.Stop();preview.Hide();
        var final=receiver.Controller.State("fixture","v1").RequestedPercent;
        File.WriteAllText(Path.Combine(artifacts,"render-performance.json"),JsonSerializer.Serialize(new {maximumGap,maximumWork,ticks,expired,frames,expected,final,PhysicalOutput=false,InjectedGraphicsDelayMs=180,InputRemainingMs=150}));
        Check(!expired && ticks>10 && final==expected && frames>0,"slow 180 ms raster panel frames do not expire a 150 ms simulated receiver lease");
    }
    private static void RenderSnapshots()
    {
        using var fixture=new Fixture();using var session=new ToyControlSession(()=>fixture.Adapter.Snapshot,()=>fixture.Controller);
        session.Tick(ToyOscRouter.Clock);session.Model.Functions.First().Function.Selected=true;session.Model.Draw(.2f);
        var snapshot=ToyControlLabels.Capture(session);
        static byte[] Hash(ToyControlLabels.Frame frame)
        {using var image=frame.Draw();using var bytes=new MemoryStream();image.Save(bytes,ImageFormat.Png);return SHA256.HashData(bytes.ToArray());}
        var before=Hash(snapshot);session.Model.Draw(.9f);
        var frozen=Task.Run(()=>Hash(snapshot)).GetAwaiter().GetResult();
        Check(before.SequenceEqual(frozen) && !before.SequenceEqual(Hash(ToyControlLabels.Capture(session))),
            "background label drawing uses captured values while the live graph changes");
    }
    private static PanelRenderFrame RenderFrame(int revision=0)
    {
        using var session=new ToyControlSession(()=>null,()=>null);
        var state=default(ShaderState);state.ResolutionTime=new(100,100,revision,revision);
        return new(100,100,state,revision,null,[],ToyControlLabels.Capture(session),true,revision);
    }
    private static void RenderQueue()
    {
        var ui=Environment.CurrentManagedThreadId;var backend=new ControlledPanelRenderer();var factoryThread=0;
        var worker=new PanelRenderWorker(()=>{factoryThread=Environment.CurrentManagedThreadId;return backend;});
        try
        {
            worker.Submit(RenderFrame(1));Wait(()=>backend.Entered.IsSet,"graphics worker did not enter the blocked frame");
            for(var revision=2;revision<=10;revision++)worker.Submit(RenderFrame(revision));
            Check(worker.TakeMetrics().Skipped==8,"slow graphics retains one newest frame instead of a stale frame queue");
            Check(!worker.AcceptsInput(1) && !worker.AcceptsInput(10),"desktop clicks wait for the first displayed input layout");
            using var owner=new Fixture();var toy=owner.Session.PublishInventory().Toys[0];
            owner.Session.Receive("state",Element(new[]{new RemoteLevel(toy.Id,toy.Functions[0].Id,65,1)}));
            owner.Controller.Stop();
            Check(owner.Controller.State("fixture","v1").RequestedPercent==0 && !backend.Release.IsSet,
                "owner Stop remains immediate while the graphics worker is blocked");
            backend.Release.Set();Wait(()=>backend.Revisions.Count==2,"newest visual frame was not drawn");
            Check(backend.Revisions.SequenceEqual(new[]{1,10}),"superseded frames cannot erase the newest Clear revision or display state");
            Wait(()=>worker.AcceptsInput(10),"new input layout was not acknowledged");
            Check(!worker.AcceptsInput(1) && !worker.AcceptsInput(11),"desktop clicks cannot use an old or undisplayed target layout");
        }
        finally {backend.Release.Set();worker.Dispose();}
        Wait(()=>worker.Completed,"graphics worker did not finish cleanup");
        Check(factoryThread!=ui && backend.Threads.All(t=>t==factoryThread) && backend.DisposeThread==factoryThread,
            "graphics creation, drawing and disposal stay on one separate owning thread");
        var waiting=new ControlledPanelRenderer();var shutdown=new PanelRenderWorker(()=>waiting);
        try
        {
            shutdown.Submit(RenderFrame());Wait(()=>waiting.Entered.IsSet,"shutdown fixture did not start");
            shutdown.Submit(RenderFrame(2));shutdown.Dispose();
            Check(!shutdown.Completed,"closing the page does not wait for the blocked graphics call");
            shutdown.Submit(RenderFrame(3));
        }
        finally {waiting.Release.Set();shutdown.Dispose();}
        Wait(()=>shutdown.Completed,"disposed graphics worker did not release resources");
        Check(waiting.Revisions.Count==1,"closing discards pending and later display frames");
    }
    private static void RenderDesktopInput()
    {
        using var receiver=new Fixture(2);using var sender=new Fixture();using var local=new LocalToysPage();
        sender.Session.Receive("inventory",Element(receiver.Session.PublishInventory()));
        using var page=new ToyControlPage(local,(_,_)=>{});page.Session.Remote=sender.Session.Toys;
        using var preview=new Form {Width=1050,Height=800,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new(-32000,-32000)};
        preview.Controls.Add(page);preview.Show();Application.DoEvents();
        var flags=BindingFlags.NonPublic|BindingFlags.Instance;var surface=page.DesktopSurface;
        void Paint() {using var image=new Bitmap(Math.Max(1,surface.Width),Math.Max(1,surface.Height));surface.DrawToBitmap(image,surface.ClientRectangle);}
        void Display()
        {
            var until=Environment.TickCount64+3000;
            do {page.Tick();Application.DoEvents();Paint();Thread.Sleep(5);if(Environment.TickCount64>until) throw new Exception("Desktop replica did not display current state");} while(!page.DesktopReady);
        }
        void Mouse(string method,float x,float y)
        {
            var p=surface.Pixel(new(x,y));typeof(Control).GetMethod(method,flags)!.Invoke(surface,[new MouseEventArgs(MouseButtons.Left,1,p.X,p.Y,0)]);
        }
        void Click(float x,float y) {Mouse("OnMouseDown",x,y);Mouse("OnMouseUp",x,y);}
        page.Session.Tick(ToyOscRouter.Clock);page.Session.Model.Functions.First().Function.Selected=true;page.Session.Draw(new(600,300),false,true);
        Paint();Click(521,658);
        Check(sender.Session.Toys.Levels().Length==0 && page.Session.Model.SelectedCount==0,"desktop Stop ends remote input before the first replica frame");
        Display();var pointerMode=page.Session.PointerMode;Click(720,148);
        Check(page.Session.PointerMode==pointerMode && !surface.Capture,"removed Touch button is inert on the desktop toy page");
        Check(!page.Controls.OfType<FlowLayoutPanel>().Any(),"both control pages have no top toolbar");
        var vrOpens=0;page.OpenVrAction=()=>{vrOpens++;return true;};Click(393,658);
        Check(vrOpens==1 && !surface.Capture,"bottom-left VR icon opens from Toy control");
        Click(150,270);Check(page.Session.Model.SelectedCount==1,"desktop replica mouse uses the original function-row position");
        Display();Click(393,148);Check(!page.Session.Model.RecordArmed,"former upper-left Record position is inert");
        Click(585,658);Check(page.Session.Model.RecordArmed,"bottom Record icon uses the shared recorder");
        Display();Click(585,658);Display();Click(550,148);
        Check(!page.Session.Model.RecordArmed && page.Session.Model.LoopCount==0 && !surface.Capture,"removed Play loop area is inert");
        Display();page.Session.Model.Functions.Skip(1).First().Function.Selected=true;Click(600,300);
        Check(sender.Session.Toys.Levels().Length==0,"desktop replica rejects an undisplayed selection");
        Display();Mouse("OnMouseDown",600,300);Check(sender.Session.Toys.Levels().Length==2,"desktop curve controls displayed selected remote functions");
        Mouse("OnMouseMove",1200,120);typeof(Control).GetMethod("OnMouseLeave",flags)!.Invoke(surface,[EventArgs.Empty]);
        Check(page.Session.Model.Touching && page.Session.Model.Level==1,"desktop curve stays captured and clamped outside the old graph");
        page.Panel.MarkPresented();var native=page.Panel.Layout().Plot;page.Input.Down("vr1",new(native.X+native.Width/2,native.Y+native.Height/2));
        Check(!page.Input.PointerActive,"native pointer cannot take over a held desktop drawing");
        Mouse("OnMouseUp",1200,120);Click(521,658);Display();Click(150,270);Display();page.Tick();page.Panel.MarkPresented();
        native=page.Panel.Layout().Plot;var nativePoint=new PointF(native.X+native.Width/2,native.Y+native.Height/2);page.Input.Down("vr1",nativePoint);
        Mouse("OnMouseDown",600,300);Check(page.Input.IsDrawing("vr1") && !surface.Capture,"desktop cannot take over a held native drawing");
        page.Input.Up("vr1",nativePoint);Click(521,658);Display();
        var oldLayout=ToyControlLayout.Row(0);page.Panel.SetCompact(false);page.SetTransparency(100);Display();
        Check(ToyControlLayout.Row(0)==oldLayout && page.DesktopReady,"VR placement and full transparency do not change the desktop layout");
        using(var image=new Bitmap(surface.Width,surface.Height)) {surface.DrawToBitmap(image,surface.ClientRectangle);image.Save(Path.Combine(artifacts,"desktop-replica-toys.png"));Check(image.GetPixel(surface.Width/2,surface.Height/2).A==255,"desktop replica stays opaque when the VR panel is hidden");}
        page.SetTransparency(0);
        var submissions=0;
        page.Session.Shockers.Inventory=()=>[new("fixture-shocker","Fixture","Mock",true,100,15000,false),new("fixture-second","Second","Mock",true,40,500,false)];
        page.Session.Shockers.Send=(_,_,_,_,_)=>{submissions++;return true;};page.Session.SetPage(true);Display();Click(720,148);
        Check(page.Session.PointerMode==pointerMode && !surface.Capture,"removed Touch button is inert on the desktop shocker page");
        Click(393,658);Check(vrOpens==2,"same bottom-left VR icon opens from Shocker control");
        Click(150,270);Display();Click(150,334);Check(page.Session.Shockers.Selected.Length==2 && submissions==0,"desktop original shocker rows select independently without output");
        Display();Click(150,334);Check(page.Session.Shockers.Selected.Length==1,"desktop re-click deselects only one shocker");
        Display();Mouse("OnMouseDown",454,305);Mouse("OnMouseUp",454,305);Click(983,376);Check(!page.Session.Shockers.Armed,"desktop Shock waits for current displayed intensity");
        Display();Mouse("OnMouseDown",614,501);Mouse("OnMouseUp",614,501);Click(983,376);Check(!page.Session.Shockers.Armed,"desktop Shock waits for current displayed duration");
        Display();Click(1088,308);Click(878,308);Check(!page.Session.Shockers.Armed && submissions==0,"removed ARM and SHOCK circles are inert");
        Mouse("OnMouseDown",983,376);Mouse("OnMouseMove",983,548);Mouse("OnMouseUp",983,548);
        Check(page.Session.Shockers.ArmedFromDesktop && submissions==0,"first Shock press only arms and held dragging cannot activate Sound");
        Click(983,376);Check(submissions==0,"desktop confirmation waits until the armed state is painted");
        Display();Mouse("OnMouseDown",983,376);Mouse("OnMouseMove",983,462);Mouse("OnMouseUp",983,462);
        Check(submissions==1 && !page.Session.Shockers.Armed,"fresh second Shock press submits once and held dragging does not trigger Vibrate");
        Display();Click(983,376);Click(521,658);Check(!page.Session.Shockers.Armed,"desktop Stop bypasses pending arm display");
        Display();Click(983,376);Display();
        using(var image=new Bitmap(surface.Width,surface.Height)) {surface.DrawToBitmap(image,surface.ClientRectangle);image.Save(Path.Combine(artifacts,"desktop-replica-shockers.png"));}
        page.Session.Shockers.Cancel();page.Panel.Tick(ToyOscRouter.Clock);page.Panel.PreviewShocker("Shock","vr1");Display();Click(983,376);
        Check(submissions==1 && !page.Session.Shockers.Armed,"desktop confirmation cannot complete an arm from the native panel");
        page.Shutdown();preview.Hide();
    }
    private static void RenderFailure()
    {
        using var receiver=new Fixture();using var sender=new Fixture();using var local=new LocalToysPage();
        sender.Session.Receive("inventory",Element(receiver.Session.PublishInventory()));
        using var page=new ToyControlPage(local,(_,_)=>{});page.Session.Remote=sender.Session.Toys;
        page.RenderDesktopFrame=_=>throw new IOException("Injected desktop raster failure");
        using var preview=new Form {Width=850,Height=650,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new(-32000,-32000)};
        preview.Controls.Add(page);preview.Show();Application.DoEvents();
        page.Session.Tick(ToyOscRouter.Clock);page.Session.Model.Functions.First().Function.Selected=true;page.Session.Draw(new(600,300),false,true);
        var until=Environment.TickCount64+3000;
        while(!page.Status.Contains("paused",StringComparison.OrdinalIgnoreCase) && Environment.TickCount64<until) {page.Tick();Application.DoEvents();Thread.Sleep(5);}
        Check(page.Status.Contains("paused",StringComparison.OrdinalIgnoreCase) && sender.Session.Toys.Levels().Length==0,"raster failure suspends production remote input without replay");
        page.Shutdown();preview.Hide();
    }
    private static void RenderGpu()
    {
        var shader=Path.Combine(AppContext.BaseDirectory,"ToyControl","Shaders","Panel.hlsl");
        var gpu=new CapturePanelRenderer(shader,Path.Combine(artifacts,"background-toy-panel.png"));
        var worker=new PanelRenderWorker(()=>gpu);
        try
        {
            using var fixture=new Fixture();using var session=new ToyControlSession(()=>fixture.Adapter.Snapshot,()=>fixture.Controller);
            session.Tick(ToyOscRouter.Clock);session.Model.Functions.First().Function.Selected=true;session.Model.Draw(.65f);
            worker.Submit(new(1240,608,ShaderState.From(session.DisplayModel,1240,608,(float)ToyOscRouter.Clock,0,0),0,null,[],ToyControlLabels.Capture(session),true));
            Wait(()=>worker.Ready || worker.Failure is not null,"background GPU preview did not finish",20000);
            Check(worker.Failure is null && File.Exists(Path.Combine(artifacts,"background-toy-panel.png")),"the historical shader fixture draws captured labels/state on its own thread");
        }
        finally {worker.Dispose();}
        Wait(()=>worker.Completed,"background GPU did not release its resources",10000);
        using var local=new LocalToysPage();using var page=new ToyControlPage(local,(_,_)=>{});
        using var preview=new Form {Width=850,Height=650,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new(-32000,-32000)};
        preview.Controls.Add(page);preview.Show();Application.DoEvents();page.Tick();
        Wait(()=>{page.Tick();Application.DoEvents();return page.DesktopReady;},"native raster preview did not finish",20000);
        Check(page.DesktopReady,"replicated desktop panel displays a completed immutable frame");
        var closing=Stopwatch.GetTimestamp();page.Shutdown();
        Check(Stopwatch.GetElapsedTime(closing).TotalMilliseconds<250,"native display shutdown does not wait for its rendering thread");preview.Hide();
    }
    private sealed class CapturePanelRenderer(string path,string output) : IPanelFrameRenderer
    {
        private readonly GpuPanelFrameRenderer renderer=new(path,0);
        public void Draw(PanelRenderFrame frame)
        {
            renderer.Draw(frame);
            var gpu=(GpuRenderer)typeof(GpuPanelFrameRenderer).GetField("renderer",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(renderer)!;
            using var image=gpu.Capture();image.Save(output);
        }
        public void Dispose()=>renderer.Dispose();
    }
    private sealed class ControlledPanelRenderer : IPanelFrameRenderer
    {
        public readonly ManualResetEventSlim Entered=new(),Release=new();
        public readonly ConcurrentQueue<int> Revisions=new(),Threads=new();
        public int DisposeThread;
        public void Draw(PanelRenderFrame frame)
        {
            Threads.Enqueue(Environment.CurrentManagedThreadId);Revisions.Enqueue(frame.ClearRevision);
            if(Revisions.Count==1){Entered.Set();if(!Release.Wait(TimeSpan.FromSeconds(5)))throw new IOException("fixture display timed out");}
        }
        public void Dispose()=>DisposeThread=Environment.CurrentManagedThreadId;
    }
    private sealed class BrokenPanelRenderer : IPanelFrameRenderer
    {
        public void Draw(PanelRenderFrame frame)=>throw new IOException("fixture display error");
        public void Dispose() { }
    }
    private sealed class SlowPanelRenderer : IPanelFrameRenderer
    {
        private int frames;
        public int Frames=>Volatile.Read(ref frames);
        public void Draw(PanelRenderFrame frame){Thread.Sleep(180);Interlocked.Increment(ref frames);}
        public void Dispose() { }
    }
}
