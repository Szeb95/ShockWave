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
    private GpuRenderer? renderer;
    private ToyOscRouter? osc;
    private int listenPort=9001, sendPort=9000, hover, labelsRevision=-1, inputModeRevision=-1;
    private string listenHost="127.0.0.1";
    private double lastLabels, lastHistory;
    private PatternClip? pattern;
    private string lastStatus="", error="";
    private bool drawing, closed;
    private int slider;
    public ToyControlSession Session { get; }
    public bool Listening => osc is { Fault:null } && timer.Enabled && !closed;
    public bool Connected => Listening && Session.Vrc.IsLive;
    public int AvatarPort => sendPort;
    public string Status => error.Length>0 ? error : osc?.Fault ?? (Connected ? "Connected" : Listening ? Session.Vrc.WaitingReason : "Inactive");
    public event Action? Changed;
    public event Action<OscMessage, double>? OscInput;
    public event Action? OscReset;
    public Func<bool>? SpsPresence { get; set; }
    public ToyControlPage(LocalToysPage toys, Action<byte[],OscMessage[]> forward)
    {
        this.forward=forward; Session=new(() => toys.Snapshot,() => toys.Controller);
        Dock=DockStyle.Fill; BackColor=Color.FromArgb(30,31,34);
        var header=new Panel { Dock=DockStyle.Top, Height=65 };
        header.Controls.AddRange([title,retry]); header.Resize+=(_,_) => retry.Location=new(Math.Max(0,header.Width-retry.Width),4);
        retry.FlatAppearance.BorderSize=0; retry.Click+=(_,_) => ConfigureOsc(listenPort,sendPort,listenHost);
        Controls.Add(canvas); Controls.Add(notice); Controls.Add(header); canvas.Controls.Add(surface); canvas.Resize+=(_,_) => Arrange();
        surface.MouseDown+=(_,e) =>
        {
            if(e.Button!=MouseButtons.Left || renderer is null) return;
            var p=Logical(e.Location); Session.Tick(ToyOscRouter.Clock); var hit=ToyControlLayout.Hit(p,Session);
            if(Session.ShockerPage) { surface.Capture=true; Session.Shockers.Press(0,hit,ToyOscRouter.Clock); }
            if(hit is 30 or 31) { slider=hit; surface.Capture=true; Session.Slider(p,hit); }
            if(hit==10) { drawing=true; surface.Capture=true; Session.Draw(p,false,true); }
            else if(hit is >=1 and <=7) Session.Action("InRow"+(hit-1),false);
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
            osc?.Tick(Session,forward,(message,now) => OscInput?.Invoke(message,now)); var now=ToyOscRouter.Clock; Session.Tick(now);
            osc?.SendSpsPresence(SpsPresence?.Invoke() ?? false); osc?.Send(Session);
            var form=FindForm(); var draw=Visible && IsHandleCreated && form is { Visible:true,WindowState:not FormWindowState.Minimized } && surface.Width>1 && surface.Height>1;
            if(draw)
            {
                renderer ??= new(Path.Combine(AppContext.BaseDirectory,"ToyControl","Shaders","Panel.hlsl"),surface.Width,surface.Height,surface.Handle);
                renderer.Resize(surface.Width,surface.Height);
                if(Session.ClearRequested) { renderer.ClearHistory(); Session.ClearRequested=false; }
                if(!ReferenceEquals(pattern,Session.Model.DisplayPattern)) { pattern=Session.Model.DisplayPattern; renderer.UploadPattern(pattern); }
                renderer.UploadLoopPatterns(Session.Model);
                var state=ShaderState.From(Session.DisplayModel,surface.Width,surface.Height,(float)now,hover,osc?.Received ?? 0);
                state.ShockData=new(Session.ShockerPage ? 1 : 0,Session.Shockers.Intensity/100f,(Session.Shockers.DurationMs-300)/14700f,Session.Shockers.Remaining(now));
                state.ShockFlags=new(Session.Shockers.Armed ? 1 : 0,Session.Shockers.Ready ? 1 : 0,0,0);
                if(now-lastHistory>=1/30d) { renderer.StepHistory(state); lastHistory=now; }
                if(now-lastLabels>=.05 && (labelsRevision!=Session.DisplayModel.Revision+Session.Shockers.Revision || inputModeRevision!=Session.InputModeRevision || now-lastLabels>=1))
                { using var labels=ToyControlLabels.Create(Session); renderer.UploadLabels(labels); labelsRevision=Session.DisplayModel.Revision+Session.Shockers.Revision; inputModeRevision=Session.InputModeRevision; lastLabels=now; }
                renderer.Render(state);
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
