using ShockWave.ShaderLab;
using ShockWave.Overlay.SteamVr;
using NativeModel = ShockWave.Overlay.SteamVr.PanelModel;

namespace ShockWave.Overlay;

internal sealed class ToyControlPage : UserControl
{
    private readonly NativeModel panelModel;
    private readonly PanelInput input;
    private readonly SteamVrHost vr;
    private readonly DesktopPanelCanvas surface;
    private readonly Label notice=new() {Dock=DockStyle.Bottom,Height=64,ForeColor=Color.FromArgb(181,186,193)};
    private readonly System.Windows.Forms.Timer timer=new() {Interval=10};
    private readonly DisplayCadence controlCadence=new(1d/30),displayCadence=new(1d/30);
    private readonly DisplayCadence sliderDisplayCadence=new(1d/60);
    private readonly Action<byte[],OscMessage[]> forward;
    private ToyOscRouter? osc;
    private int listenPort=9001,sendPort=9000;
    private string listenHost="127.0.0.1",error="",lastStatus="";
    private bool closed;
    internal Func<NativeModel,string,Bitmap> RenderFrame {get;set;}=PanelView.Render;
    internal Func<DesktopPanelFrame,Bitmap> RenderDesktopFrame {get;set;}=DesktopPanelView.Render;
    private RasterPanelWorker? renderer;
    private DesktopPanelWorker? desktopRenderer;
    private readonly List<DesktopSample> history=[];
    private bool desktopPressed,drawing;
    private int slider,hover,clearRevision=-1;
    private double lastHistory;
    private readonly VrAutoOpen startup=new();
    private Mount preferredHand;
    private bool startAsPanel,autoOpenInVr;
    internal Func<bool> OpenVrAction {get;set;}
    internal Func<bool> VrAvailable {get;set;}=SteamVrAvailable;
    internal bool AutoOpenPending=>startup.Pending;
    public string PreferredHand=>preferredHand==Mount.RightHand ? "Right" : "Left";
    public string PreferredStartMode=>startAsPanel ? "Panel" : "Wrist";
    public bool AutoOpenInVr=>autoOpenInVr;
    public ToyControlSession Session {get;}
    internal NativeModel Panel => panelModel;
    internal PanelInput Input => input;
    internal DesktopPanelCanvas DesktopSurface=>surface;
    internal bool DesktopReady=>surface.PaintedCookie.Length>0 && surface.PaintedCookie==DesktopPanelView.InputCookie(Session);
    public bool Listening => osc is {Fault:null} && timer.Enabled && !closed;
    public bool Connected => vr.Running || Listening && Session.Vrc.IsLive;
    public int AvatarPort => sendPort;
    public string Status => error.Length>0 ? error : vr.Running ? vr.Status : osc?.Fault ?? (Connected ? "Connected" : Listening ? Session.Vrc.WaitingReason : "Inactive");
    public event Action? Changed;
    internal event Action<string>? Diagnostic;
    public event Action<OscMessage,double>? OscInput;
    public event Action? OscReset;
    public Func<bool>? SpsPresence {get;set;}
    public ToyControlPage(LocalToysPage toys,Action<byte[],OscMessage[]> forward,WristPlacementStore? placementStore=null)
    {
        this.forward=forward;Session=new(()=>toys.Snapshot,()=>toys.Controller);
        panelModel=new(Session,(toy,function)=>
        {
            var device=(toy.StartsWith("remote/",StringComparison.Ordinal) ? Session.Remote?.Snapshot : toys.Snapshot)?.Devices.FirstOrDefault(d=>d.DeviceKey==toy);
            var capability=device?.Capabilities.FirstOrDefault(c=>c.ChannelId==function);
            try
            {
                var state=(toy.StartsWith("remote/",StringComparison.Ordinal) ? (Toys.IToyInputController?)Session.Remote : toys.Controller)?.State(toy,function);
                return (capability?.Type.ToString() ?? "Unknown",state?.Preference.Sensitivity ?? 100);
            }
            catch(InvalidOperationException) {return (capability?.Type.ToString() ?? "Unknown",100);}
        });
        input=new(panelModel,()=>desktopPressed || drawing || slider!=0);vr=new(panelModel,input,placementStore ?? new WristPlacementStore(null));panelModel.SetMount(vr.SavedWrist);
        preferredHand=vr.SavedWrist;OpenVrAction=StartVr;
        surface=new() {Dock=DockStyle.Fill};
        Dock=DockStyle.Fill;BackColor=Color.FromArgb(30,31,34);
        Controls.Add(surface);Controls.Add(notice);
        panelModel.LayoutChanged+=()=>{input.CancelPointer();vr.PlacementChanged();displayCadence.Reset();};
        panelModel.PlacementChanged+=vr.PlacementChanged;
        panelModel.ContextInvalidated+=()=>input.CancelPointer();
        Session.PageChanged+=()=>{CancelDesktopInput();panelModel.Tick(ToyOscRouter.Clock);};
        surface.MouseDown+=(_,e)=>DesktopDown(e);
        surface.MouseMove+=(_,e)=>DesktopMove(e);
        surface.MouseLeave+=(_,_)=>{hover=0;surface.Cursor=Cursors.Default;displayCadence.Reset();};
        surface.MouseUp+=(_,e)=>{if(e.Button==MouseButtons.Left) ReleaseDesktopInput(true);};
        surface.MouseCaptureChanged+=(_,_)=>{if(!surface.Capture) CancelDesktopInput();};
        surface.MouseWheel+=(_,e)=>{if(!desktopPressed && !input.PointerActive) Session.Action(e.Delta<0 ? "InNext" : "InPrev",false);};
        timer.Tick+=(_,_)=>Tick();
    }
    public void ConfigureVrOptions(string? hand,string? mode,bool autoOpen)
    {
        var nextHand=hand=="Right" ? Mount.RightHand : hand=="Left" ? Mount.LeftHand : preferredHand;
        var nextPanel=mode=="Panel";var changed=preferredHand!=nextHand || startAsPanel!=nextPanel;
        preferredHand=nextHand;startAsPanel=nextPanel;autoOpenInVr=autoOpen;
        if(!autoOpen) startup.Cancel();
        if(changed) ApplyPreferredPlacement();
    }
    private void ApplyPreferredPlacement()
    {
        if(panelModel.Compact==!startAsPanel && panelModel.WristHand==preferredHand) return;
        panelModel.SetMount(preferredHand);if(startAsPanel) panelModel.SetCompact(false);
    }
    private bool StartVr() {if(vr.Running) return true;ApplyPreferredPlacement();Prepare();return vr.Start();}
    public bool OpenInVr() {startup.Cancel();ApplyPreferredPlacement();return OpenVrAction();}
    public void CloseVr() {startup.Cancel();vr.Dispose();}
    public void RecallVr() {CancelDesktopInput();vr.Recall();}
    public void OpenWristMenu()=>vr.OpenWristMenu();
    public void AdjustWristPosition()=>vr.AdjustWristPosition();
    public void ReconnectVrc()=>ConfigureOsc(listenPort,sendPort,listenHost);
    public void StartWithApp()=>startup.Arm(autoOpenInVr);
    private static bool SteamVrAvailable()
    {var processes=System.Diagnostics.Process.GetProcessesByName("vrserver");try {return processes.Length>0;} finally {foreach(var process in processes) process.Dispose();}}
    public void SetTransparency(int value) {panelModel.SetTransparency(value);displayCadence.Reset();}
    public void ConfigureOsc(int listen,int send,string host="127.0.0.1")
    {
        if(closed) return;listenPort=listen;sendPort=send;listenHost=host;OscReset?.Invoke();
        Session.Vrc.Reset("Waiting for VRC");osc?.Dispose();osc=null;error="";
        try {osc=new(listen,send,host);}
        catch(System.Net.Sockets.SocketException) {error=$"OSC port {listen} is in use. Close the other receiver, then Reconnect VRC.";}
        timer.Start();UpdateStatus();
    }
    private void Prepare() {var now=ToyOscRouter.Clock;Session.Tick(now);panelModel.Tick(now);}
    private void DesktopDown(MouseEventArgs e)
    {
        if(e.Button!=MouseButtons.Left) return;var point=surface.Logical(e.Location);
        if(DesktopPanelLayout.IsStop(point)) {CancelDesktopInput();input.CancelPointer();panelModel.StopAll(clearSelection:true);return;}
        Session.Tick(ToyOscRouter.Clock);
        var hit=DesktopPanelLayout.Hit(point,Session);
        if(hit==DesktopPanelLayout.RecenterHit) {RecallVr();return;}
        if(!DesktopReady || input.PointerActive || desktopPressed) return;
        if(hit==0) return;
        if(hit==DesktopPanelLayout.OpenVrHit) {OpenInVr();return;}
        if(hit==DesktopPanelLayout.ShockHit && Session.Shockers.Armed && !Session.Shockers.ArmedFromDesktop) {Session.Shockers.Cancel();return;}
        desktopPressed=true;surface.Capture=true;
        if(Session.ShockerPage) Session.Shockers.Press(0,hit==DesktopPanelLayout.ShockHit ? Session.Shockers.ArmedFromDesktop ? 33 : 32 : hit,ToyOscRouter.Clock);
        if(hit is 30 or 31) {slider=hit;SetDesktopSlider(point,hit);}
        if(hit==10) {drawing=true;Session.Draw(ClampedGraph(point),false,true);}
        else if(hit is >=1 and <=7 && !Session.ShockerPage) Session.Action("InRow"+(hit-1),false);
        else if(ToyControlLayout.Action(hit) is { } action) Session.Action(action,false);
    }
    private static PointF ClampedGraph(PointF point)
    {
        var graph=ShockWave.ShaderLab.PanelLayout.Graph;return new(Math.Clamp(point.X,graph.Left,graph.Right),Math.Clamp(point.Y,graph.Top,graph.Bottom));
    }
    private void SetDesktopSlider(PointF point,int hit)
    {
        var rect=DesktopPanelLayout.Slider(hit-30);
        Session.Shockers.SetSlider(hit,Math.Clamp(1-(point.Y-rect.Top)/rect.Height,0,1));
    }
    private void DesktopMove(MouseEventArgs e)
    {
        var point=surface.Logical(e.Location);hover=DesktopPanelLayout.Hit(point,Session);surface.Cursor=hover!=0 ? Cursors.Hand : Cursors.Default;
        if(slider!=0) SetDesktopSlider(point,slider);
        if(drawing && Session.Model.Touching && !Session.TouchFromVrc) Session.Draw(ClampedGraph(point),false);
    }
    private void ReleaseDesktopInput(bool commit)
    {
        var wasDrawing=drawing;drawing=desktopPressed=false;slider=0;Session.Shockers.Release(0);
        if(wasDrawing && !Session.TouchFromVrc) Session.Release(commit);
        surface.Capture=false;
    }
    private void CancelDesktopInput()=>ReleaseDesktopInput(false);
    internal void Tick()
    {
        if(closed) return;
        try
        {
            var now=ToyOscRouter.Clock;
            if(controlCadence.Due(now))
            {
                osc?.Tick(Session,forward,(message,time)=>OscInput?.Invoke(message,time));Session.Tick(now);
                osc?.SendSpsPresence(SpsPresence?.Invoke() ?? false);osc?.Send(Session);
            }
            if(startup.Pending && now>=startup.NextCheck) startup.Tick(now,VrAvailable(),()=>OpenVrAction());
            panelModel.Tick(now);vr.Tick();
            if(drawing && (!Session.Model.Touching || Session.TouchFromVrc)) CancelDesktopInput();
            if(renderer?.Failure is not null || desktopRenderer?.Failure is not null) throw new InvalidOperationException("Panel display unavailable");
            if(renderer?.TryTake(out var readyFrame,out var stamp)==true && readyFrame is not null)
            {
                using(readyFrame)
                    if(vr.Running && panelModel.CanDisplay(stamp)) {vr.Upload(readyFrame);if(vr.Running) panelModel.MarkPresented(stamp.Input);}
            }
            if(desktopRenderer?.TryTake(out var desktopFrame,out var desktopCookie)==true && desktopFrame is not null)
            {if(desktopCookie==DesktopPanelView.InputCookie(Session)) surface.SetFrame(desktopFrame,desktopCookie);else desktopFrame.Dispose();}
            var form=FindForm();var draw=Visible && IsHandleCreated && form is {Visible:true,WindowState:not FormWindowState.Minimized};
            var cadence=input.PointerOwner is { } owner && input.IsSliding(owner) ? sliderDisplayCadence : displayCadence;
            if((draw || vr.Running) && cadence.Due(now))
            {
                if(Session.ClearRevision!=clearRevision) {history.Clear();clearRevision=Session.ClearRevision;}
                if(now-lastHistory>=1d/30) {history.Add(new(now,Session.Model.Level,Session.Model.SelectedCount>0));history.RemoveAll(s=>s.Time<now-8);if(history.Count>240) history.RemoveAt(0);lastHistory=now;}
                if(vr.Running) {renderer??=new(RenderFrame);renderer.Submit(panelModel.CaptureDisplay(),panelModel.CaptureFrameStamp());}
                if(draw) {desktopRenderer??=new(RenderDesktopFrame);desktopRenderer.Submit(DesktopPanelView.Capture(Session,hover,history));Session.ClearRequested=false;}
            }
            UpdateStatus();
        }
        catch(Exception e) when(e is InvalidOperationException or IOException or System.Net.Sockets.SocketException or SharpGen.Runtime.SharpGenException)
        {error="Panel paused. Reconnect VRC to restart the display.";CancelDesktopInput();input.Cancel();Session.Suspend();vr.Dispose();renderer?.Dispose();renderer=null;desktopRenderer?.Dispose();desktopRenderer=null;timer.Stop();Diagnostic?.Invoke("Panel paused: "+e.GetType().Name);UpdateStatus();}
    }
    private void UpdateStatus()
    {
        var shockNotice=Session.Shockers.Armed ? Session.Shockers.ArmedFromDesktop ? "Release, then press Shock again within 3 seconds" : "Pending confirmation in the other view"
            : Session.Shockers.Notice.Replace("start with the right circle","press Shock to start again").Replace("press the LEFT green circle","press Shock again");
        notice.Text=(Session.ShockerPage ? shockNotice+"\nShock → release → Shock within 3 seconds. Sliders send no commands."
            : Session.Notice+"\nEach owner's Sensitivity and Max % apply.")+"\n"+(startup.Pending ? "Waiting for SteamVR · automatic opening enabled" : vr.Status);
        var status=Status;if(status==lastStatus) return;lastStatus=status;Changed?.Invoke();
    }
    public void StopRenderingTouch() {CancelDesktopInput();if(!Session.Shockers.ArmedFromNative) Session.Shockers.Cancel();if(input.IsPointerOwnedBy("desktop")) input.CancelPointer("desktop");panelModel.CancelDesktopConfirmation();}
    public void Shutdown()
    {
        if(closed) return;closed=true;startup.Cancel();timer.Stop();CancelDesktopInput();input.Cancel();vr.Dispose();renderer?.Dispose();renderer=null;desktopRenderer?.Dispose();desktopRenderer=null;OscReset?.Invoke();Session.Dispose();osc?.Dispose();osc=null;
    }
    protected override void Dispose(bool disposing) {if(disposing) {Shutdown();timer.Dispose();}base.Dispose(disposing);}
}
