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
        var slow=new SlowPanelRenderer();
        using var page=new ToyControlPage(local,(_,_)=>{},_=>slow);
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
        File.WriteAllText(Path.Combine(artifacts,"render-performance.json"),JsonSerializer.Serialize(new {maximumGap,maximumWork,ticks,expired,frames=slow.Frames,expected,final,PhysicalOutput=false,InjectedGraphicsDelayMs=180,InputRemainingMs=150}));
        Check(!expired && ticks>10 && final==expected,"slow 180 ms panel frames do not expire a 150 ms simulated receiver lease");
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
        var backend=new ControlledPanelRenderer();
        using var page=new ToyControlPage(local,(_,_)=>{},_=>backend);page.Session.Remote=sender.Session.Toys;
        using var preview=new Form {Width=850,Height=650,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new(-32000,-32000)};
        preview.Controls.Add(page);preview.Show();Application.DoEvents();
        var flags=BindingFlags.NonPublic|BindingFlags.Instance;
        var surface=(Control)typeof(ToyControlPage).GetField("surface",flags)!.GetValue(page)!;
        void Mouse(string method,float x,float y)
        {
            var point=new Point((int)Math.Round(x*surface.Width/1240),(int)Math.Round((y-100)*surface.Height/608));
            typeof(Control).GetMethod(method,flags)!.Invoke(surface,[new MouseEventArgs(MouseButtons.Left,1,point.X,point.Y,0)]);
        }
        long Revision()=>(long)typeof(ToyControlPage).GetMethod("CurrentInputRevision",flags)!.Invoke(page,null)!;
        page.Session.Tick(ToyOscRouter.Clock);page.Session.Model.Functions.First().Function.Selected=true;
        page.Session.Draw(new(600,300),false,true);page.Tick();
        var worker=(PanelRenderWorker)typeof(ToyControlPage).GetField("renderer",flags)!.GetValue(page)!;
        try
        {
            Wait(()=>backend.Entered.IsSet,"desktop fixture did not start its blocked display");
            Mouse("OnMouseDown",240,205);
            Check(sender.Session.Toys.Levels().Length==0 && page.Session.Model.SelectedCount==0 && !backend.Release.IsSet,
                "desktop Stop all releases input immediately before the first frame finishes");
            backend.Release.Set();Wait(()=>worker.Ready,"desktop fixture did not display its first layout");
            page.Session.Model.Functions.Skip(1).First().Function.Selected=true;
            Mouse("OnMouseDown",600,300);Mouse("OnMouseUp",600,300);
            Check(sender.Session.Toys.Levels().Length==0,"desktop graph cannot control a selection that is not yet displayed");
            page.Tick();Wait(()=>worker.AcceptsInput(Revision()),"desktop fixture did not display the new selection");
            Mouse("OnMouseDown",600,300);
            Check(sender.Session.Toys.Levels().Length==1,"desktop graph accepts fresh input once its current selection is displayed");
            Mouse("OnMouseUp",600,300);Mouse("OnMouseDown",240,205);
            var submissions=0;
            page.Session.Shockers.Inventory=()=>[new("fixture-shocker","Fixture","Mock",true,100,15000,false)];
            page.Session.Shockers.Send=(_,_,_,_)=>{submissions++;return true;};
            page.Session.SetPage(true);page.Session.Tick(ToyOscRouter.Clock);page.Session.Shockers.Select(0);page.Session.Shockers.SetSlider(30,.2f);
            page.Tick();Wait(()=>worker.AcceptsInput(Revision()),"desktop fixture did not display its shocker values");
            page.Session.Shockers.SetSlider(30,.8f);
            Mouse("OnMouseDown",1088,308);Mouse("OnMouseUp",1088,308);
            Check(!page.Session.Shockers.Armed,"desktop confirmation cannot arm with changed but undisplayed shocker values");
            page.Tick();Wait(()=>worker.AcceptsInput(Revision()),"desktop fixture did not display the changed shocker values");
            page.Session.Shockers.SetSlider(31,.2f);
            Mouse("OnMouseDown",1088,308);Mouse("OnMouseUp",1088,308);
            Check(!page.Session.Shockers.Armed,"desktop confirmation also waits for a changed duration to be displayed");
            page.Tick();Wait(()=>worker.AcceptsInput(Revision()),"desktop fixture did not display the changed duration");
            Mouse("OnMouseDown",1088,308);Mouse("OnMouseUp",1088,308);
            Check(page.Session.Shockers.Armed,"desktop confirmation accepts its displayed values with the existing release step");
            Mouse("OnMouseDown",878,308);Mouse("OnMouseUp",878,308);
            Check(page.Session.Shockers.Armed && submissions==0,"desktop shock confirmation waits until the armed state has been displayed");
            page.Tick();Wait(()=>worker.AcceptsInput(Revision()),"desktop fixture did not display the armed state");
            Mouse("OnMouseDown",878,308);Mouse("OnMouseUp",878,308);
            Check(!page.Session.Shockers.Armed && submissions==1,"fresh displayed desktop confirmation submits exactly one fake command");
            page.Tick();Wait(()=>worker.AcceptsInput(Revision()),"desktop fixture did not display the reset confirmation");
            Mouse("OnMouseDown",1088,308);Mouse("OnMouseUp",1088,308);
            Mouse("OnMouseDown",240,205);
            Check(!page.Session.Shockers.Armed,"desktop Stop all cancels confirmation even while its armed frame is pending");
        }
        finally {backend.Release.Set();page.Shutdown();preview.Hide();}
        Wait(()=>worker.Completed,"desktop fixture did not release its graphics resources");
    }
    private static void RenderFailure()
    {
        using var receiver=new Fixture();using var sender=new Fixture();using var local=new LocalToysPage();
        sender.Session.Receive("inventory",Element(receiver.Session.PublishInventory()));
        using var page=new ToyControlPage(local,(_,_)=>{},_=>new BrokenPanelRenderer());page.Session.Remote=sender.Session.Toys;
        using var preview=new Form {Width=850,Height=650,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new(-32000,-32000)};
        preview.Controls.Add(page);preview.Show();Application.DoEvents();
        page.Session.Tick(ToyOscRouter.Clock);page.Session.Model.Functions.First().Function.Selected=true;page.Session.Draw(new(600,300),false,true);
        page.Tick();
        var worker=(PanelRenderWorker)typeof(ToyControlPage).GetField("renderer",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(page)!;
        Wait(()=>worker.Failure is not null,"injected display error was not reported");page.Tick();
        Check(sender.Session.Toys.Levels().Length==0 && page.Status.Contains("paused"),
            "a background graphics failure preserves the existing pause and input-release behavior");preview.Hide();
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
            Check(worker.Failure is null && File.Exists(Path.Combine(artifacts,"background-toy-panel.png")),"the production graphics backend draws captured labels/state on its own thread");
        }
        finally {worker.Dispose();}
        Wait(()=>worker.Completed,"background GPU did not release its resources",10000);
        using var local=new LocalToysPage();using var page=new ToyControlPage(local,(_,_)=>{});
        using var preview=new Form {Width=850,Height=650,ShowInTaskbar=false,StartPosition=FormStartPosition.Manual,Location=new(-32000,-32000)};
        preview.Controls.Add(page);preview.Show();Application.DoEvents();page.Tick();
        var windowWorker=(PanelRenderWorker)typeof(ToyControlPage).GetField("renderer",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(page)!;
        Wait(()=>{Application.DoEvents();return windowWorker.Ready || windowWorker.Failure is not null;},"background window renderer did not finish",20000);
        Check(windowWorker.Failure is null,"the window swap chain can render and present without UI-thread graphics calls");
        page.Tick();page.Shutdown();
        Wait(()=>{Application.DoEvents();return windowWorker.Completed;},"window renderer did not release after page shutdown",10000);
        Check(windowWorker.Completed,"window graphics cleanup finishes after nonblocking page shutdown");preview.Hide();
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
