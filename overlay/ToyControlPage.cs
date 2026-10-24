using System.Diagnostics;
using ShockWave.ShaderLab;

namespace ShockWave.Overlay;

internal sealed class ToyControlPage : UserControl
{
    private sealed class Surface : Control
    {
        public Surface() { SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint,true); }
        protected override void OnPaintBackground(PaintEventArgs e) { }
        protected override void OnPaint(PaintEventArgs e) { }
    }
    private readonly Surface surface = new();
    private readonly Panel canvas = new() { Dock=DockStyle.Fill };
    private readonly Label notice = new() { Dock=DockStyle.Bottom, Height=58, ForeColor=Color.FromArgb(181,186,193) };
    private readonly Button retry = new() { Text="Reconnect VRC", FlatStyle=FlatStyle.Flat, BackColor=Color.FromArgb(56,58,64), ForeColor=Color.White, Size=new(135,32) };
    private readonly Label title=new() { Text="Toy control", ForeColor=Color.White, Font=new Font("Segoe UI",21,FontStyle.Bold), Bounds=new(0,0,350,42) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval=33 };
    private readonly Action<byte[],OscMessage[]> forward;
    private PanelRenderWorker? renderer;
    private readonly Func<nint,IPanelFrameRenderer> createRenderer;
    private ToyControlLabels.Frame? labels;
    private ToyOscRouter? osc;
    private int listenPort=9001, sendPort=9000, hover, labelsRevision=-1, inputModeRevision=-1;
    private string listenHost="127.0.0.1";
    private double lastLabels, lastHistory;
    private long lastGraphicsReport=Environment.TickCount64,maximumControlWork,maximumDisplayWork;
    private long inputRevision,labelsInputRevision=-1;
    private string inputLayout="";
    private string lastStatus="", error="";
    private bool drawing, closed;
    private int slider;
    public ToyControlSession Session { get; }
    public bool Listening => osc is { Fault:null } && timer.Enabled && !closed;
    public bool Connected => Listening && Session.Vrc.IsLive;
    public int AvatarPort => sendPort;
    public string Status => error.Length>0 ? error : osc?.Fault ?? (Connected ? "Connected" : Listening ? Session.Vrc.WaitingReason : "Inactive");
    public event Action? Changed;
    internal event Action<string>? Diagnostic;
    public event Action<OscMessage, double>? OscInput;
    public event Action? OscReset;
    public Func<bool>? SpsPresence { get; set; }
    public ToyControlPage(LocalToysPage toys, Action<byte[],OscMessage[]> forward,Func<nint,IPanelFrameRenderer>? initialRenderer=null)
    {
        this.forward=forward; createRenderer=initialRenderer ?? (handle=>new GpuPanelFrameRenderer(Path.Combine(AppContext.BaseDirectory,"ToyControl","Shaders","Panel.hlsl"),handle));
        Session=new(() => toys.Snapshot,() => toys.Controller);
        Dock=DockStyle.Fill; BackColor=Color.FromArgb(30,31,34);
        var header=new Panel { Dock=DockStyle.Top, Height=65 };
        header.Controls.AddRange([title,retry]); header.Resize+=(_,_) => retry.Location=new(Math.Max(0,header.Width-retry.Width),4);
        retry.FlatAppearance.BorderSize=0; retry.Click+=(_,_) => ConfigureOsc(listenPort,sendPort,listenHost);
        Controls.Add(canvas); Controls.Add(notice); Controls.Add(header); canvas.Controls.Add(surface); canvas.Resize+=(_,_) => Arrange();
        surface.HandleDestroyed+=(_,_)=>{renderer?.Dispose();renderer=null;labels=null;labelsRevision=-1;};
        surface.MouseDown+=(_,e) =>
        {
            if(e.Button!=MouseButtons.Left) return;
            var p=Logical(e.Location);
            // Stop has the same fixed position on both pages and never waits for graphics.
            if(ToyControlLayout.Stop.Contains(p)) {StopRenderingTouch();Session.StopAll();return;}
            Session.Tick(ToyOscRouter.Clock);
            if(renderer?.AcceptsInput(CurrentInputRevision())!=true) return;
            var hit=ToyControlLayout.Hit(p,Session);
            if(Session.ShockerPage) { surface.Capture=true; Session.Shockers.Press(0,hit,ToyOscRouter.Clock); }
            if(hit is 30 or 31) { slider=hit; surface.Capture=true; Session.Slider(p,hit); }
            if(hit==10) { drawing=true; surface.Capture=true; Session.Draw(p,false,true); }
            else if(hit is >=1 and <=7 && !Session.ShockerPage) Session.Action("InRow"+(hit-1),false);
            else if(ToyControlLayout.Action(hit) is { } action) Session.Action(action,false);
        };
        surface.MouseMove+=(_,e) =>
        {
            var p=Logical(e.Location); hover=ToyControlLayout.Hit(p,Session); surface.Cursor=hover!=0 ? Cursors.Hand : Cursors.Default;
            if(slider!=0) Session.Slider(p,slider);
            if(drawing) { if(PanelLayout.Graph.Contains(p)) Session.Draw(p,false); else Release(false); }
        };
        surface.MouseUp+=(_,e) => { if(e.Button==MouseButtons.Left) { Session.Shockers.Release(0); slider=0; if(drawing) Release(true); else surface.Capture=false; } };
        surface.MouseCaptureChanged+=(_,_) => { if(!surface.Capture) { slider=0; Session.Shockers.Release(0); if(drawing) Release(false); } };
        surface.MouseWheel+=(_,e) => { if(!drawing) Session.Action(e.Delta<0 ? "InNext" : "InPrev",false); };
        timer.Tick+=(_,_) => Tick();
    }
    private PointF Logical(Point point) => new(point.X*1240f/Math.Max(1,surface.Width),100+point.Y*608f/Math.Max(1,surface.Height));
    private long CurrentInputRevision()
    {
        var model=Session.DisplayModel;
        // A skipped picture must never make a click select/control a different
        // target than the visible rows/page. Only an opaque cookie leaves the UI.
        var layout=System.Text.Json.JsonSerializer.Serialize(new {
            Session.ShockerPage,Session.InputModeRevision,model.Page,model.Route,
            Rows=model.Visible.Select(row=>new {Toy=row.Toy.Id,Function=row.Function.Id,row.Connected}).ToArray(),
            Selected=model.Functions.Where(row=>row.Selected).Select(row=>new {Toy=row.Toy.Id,Function=row.Function.Id}).ToArray(),
            Shocker=Session.ShockerPage ? new {Session.Shockers.Intensity,Session.Shockers.DurationMs,Session.Shockers.Armed,Session.Shockers.Ready} : null
        });
        if(layout!=inputLayout) {inputLayout=layout;inputRevision++;}
        return inputRevision;
    }
    private void Arrange()
    {
        var width=Math.Max(1,Math.Min(canvas.Width,(int)(canvas.Height*1240f/608))); var height=Math.Max(1,(int)(width*608f/1240));
        surface.SetBounds(Math.Max(0,(canvas.Width-width)/2),0,width,height);
    }
    private void Release(bool commit) { drawing=false; surface.Capture=false; Session.Release(commit); }
    public void ConfigureOsc(int listen, int send, string host="127.0.0.1")
    {
        if(closed) return; listenPort=listen; sendPort=send; listenHost=host;
        OscReset?.Invoke();
        Session.Vrc.Reset("Waiting for VRC"); osc?.Dispose(); osc=null; error="";
        try { osc=new(listen,send,host); }
        catch(System.Net.Sockets.SocketException) { error=$"OSC port {listen} is in use. Close Shader Lab or another OSC receiver, then Reconnect VRC."; }
        timer.Start(); UpdateStatus();
    }
    internal void Tick()
    {
        if(closed) return;
        try
        {
            if(renderer?.Failure is not null) throw new InvalidOperationException("Panel graphics unavailable");
            var started=Stopwatch.GetTimestamp();
            osc?.Tick(Session,forward,(message,now) => OscInput?.Invoke(message,now)); var now=ToyOscRouter.Clock; Session.Tick(now);
            osc?.SendSpsPresence(SpsPresence?.Invoke() ?? false); osc?.Send(Session);
            maximumControlWork=Math.Max(maximumControlWork,(long)Math.Ceiling(Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            started=Stopwatch.GetTimestamp();
            var form=FindForm(); var draw=Visible && IsHandleCreated && form is { Visible:true,WindowState:not FormWindowState.Minimized } && surface.Width>1 && surface.Height>1;
            if(draw)
            {
                if(renderer is null) {var handle=surface.Handle;var create=createRenderer;renderer=new(()=>create(handle));}
                var state=ShaderState.From(Session.DisplayModel,surface.Width,surface.Height,(float)now,hover,osc?.Received ?? 0);
                state.ShockData=new(Session.ShockerPage ? 1 : 0,Session.Shockers.Intensity/100f,(Session.Shockers.DurationMs-300)/14700f,Session.Shockers.Remaining(now));
                state.ShockFlags=new(Session.Shockers.Armed ? 1 : 0,Session.Shockers.Ready ? 1 : 0,0,0);
                var stepHistory=now-lastHistory>=1/30d;if(stepHistory) lastHistory=now;
                var currentInput=CurrentInputRevision();
                if(labelsInputRevision!=currentInput || now-lastLabels>=.05 && (labelsRevision!=Session.DisplayModel.Revision+Session.Shockers.Revision || inputModeRevision!=Session.InputModeRevision || now-lastLabels>=1))
                { labels=ToyControlLabels.Capture(Session); labelsRevision=Session.DisplayModel.Revision+Session.Shockers.Revision; inputModeRevision=Session.InputModeRevision; labelsInputRevision=currentInput; lastLabels=now; }
                labels ??= ToyControlLabels.Capture(Session);
                renderer.Submit(new(surface.Width,surface.Height,state,Session.ClearRevision,Session.Model.DisplayPattern,
                    Session.Model.Functions.Select(row=>row.Connected ? row.Function.Pattern : null).ToArray(),labels,stepHistory,currentInput));
                Session.ClearRequested=false;
            }
            else renderer?.Pause();
            maximumDisplayWork=Math.Max(maximumDisplayWork,(long)Math.Ceiling(Stopwatch.GetElapsedTime(started).TotalMilliseconds));
            if(Environment.TickCount64-lastGraphicsReport>=5000)
            {
                var metrics=renderer?.TakeMetrics() ?? default;
                if(metrics.MaximumFrameMs>=100 || metrics.Skipped>0 || maximumControlWork>=100 || maximumDisplayWork>=100)
                    Diagnostic?.Invoke($"Panel display 5s: frames {metrics.Frames}, skipped {metrics.Skipped}, max graphics {metrics.MaximumFrameMs} ms; control work {maximumControlWork} ms, display snapshot {maximumDisplayWork} ms.");
                lastGraphicsReport=Environment.TickCount64;maximumControlWork=maximumDisplayWork=0;
            }
        }
        catch(Exception e) when(e is InvalidOperationException or IOException or System.Net.Sockets.SocketException or SharpGen.Runtime.SharpGenException)
        { error="Toy control paused. Reconnect VRC or reopen the app."; Session.Suspend(); renderer?.Dispose(); renderer=null; timer.Stop(); }
        UpdateStatus();
    }
    private void UpdateStatus()
    {
        var current=Status;
        title.Text=Session.ShockerPage ? "Shocker control" : "Toy control";
        notice.Text=Session.ShockerPage ? Session.Shockers.Notice+"\nRight circle → release → left circle within 3 seconds. Sliders send no commands. Each owner's limits apply."
            : (error.Length>0 ? error : osc?.Fault ?? (Listening && !Connected ? Session.Vrc.WaitingReason : Session.Notice))+"\nEach owner's Sensitivity and Max % apply. Desktop input replaces that function's VR loop.";
        if(current==lastStatus) return; lastStatus=current; labelsRevision=-1; Changed?.Invoke();
    }
    public void StopRenderingTouch() { if(drawing) Release(false); slider=0; Session.Shockers.Release(0); Session.Shockers.Cancel(); }
    public void Shutdown()
    {
        if(closed) return; closed=true; timer.Stop(); OscReset?.Invoke(); Session.Dispose(); osc?.Dispose(); osc=null; renderer?.Dispose(); renderer=null;
    }
    protected override void Dispose(bool disposing) { if(disposing) { Shutdown(); timer.Dispose(); } base.Dispose(disposing); }
}
