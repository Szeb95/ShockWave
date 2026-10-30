#nullable enable
using System.Diagnostics;

namespace ShockWave.Overlay.SteamVr;

internal enum Mount { LeftHand, RightHand, Floating }
internal sealed record Sample(double Time, float Value);
internal sealed class PanelShocker(string name)
{
    public string Name {get;}=name;
    public bool Selected {get;set;}
    public bool Connected {get;set;}
}

internal sealed class PanelFunction(string name, string function, Color color)
{
    public string Name { get; } = name;
    public string Function { get; set; } = function;
    public ShockWave.ShaderLab.PatternClip? Pattern {get;set;}
    public Color Color { get; } = color;
    public int? BatteryPercent {get;set;}
    public int SensitivityPercent {get;set;}=100;
    public bool IsRemoteControlled {get;set;}
    public bool Connected {get;set;}
    public bool Selected { get; set; }
    public float Level { get; set; }
    public bool Playing { get; set; }
    public double LoopStarted { get; set; }
    public List<Sample> Clip { get; set; } = [];
    public List<Sample> History { get; } = [];
}

// Presentation and placement only. The production session owns all input leases,
// recordings, playback and commands. This model never advances an output loop.
internal sealed class PanelModel
{
    private readonly ToyControlSession session;
    private readonly Func<string,string,(string Function,int Sensitivity)> metadata;
    private ShockWave.ShaderLab.FunctionBinding[] rows=[];
    private ShockWave.ShaderLab.FunctionBinding[] shockRows=[];
    private string inventoryKey="";
    private int page;
    private bool syncing;
    public event Action? ContextInvalidated;
    public bool WristMenuEnabled {get;private set;}
    public float WristMenuAmount {get;private set;}=1;
    public bool WristHandleVisible {get;private set;}
    public bool WristAnchorVisible {get;private set;}
    public bool WristMenuDragging {get;private set;}
    public bool WristHandleContact {get;private set;}
    public bool WristFitMode {get;private set;}
    public bool WristInputReady=>!Compact || !WristMenuEnabled || WristFitMode || WristMenuAmount==1 && !WristMenuDragging;
    public void EnableWristMenu() {WristMenuEnabled=true;SetWristMenu(0,false,false,false);}
    public void SetWristMenu(float amount,bool visible,bool dragging,bool contact,bool? anchorVisible=null)
    {WristMenuAmount=Math.Clamp(amount,0,1);WristHandleVisible=visible;WristMenuDragging=dragging;WristHandleContact=contact;WristAnchorVisible=anchorVisible ?? visible;}
    public void SetWristFitMode(bool value)=>WristFitMode=value;
    public void CancelWristMenuInput()
    {
        ContextInvalidated?.Invoke();CancelGesture();session.Shockers.Cancel();ResizeHandles=false;
        Notice="Wrist menu · active patterns continue";
    }
    private string presentedInput="";
    private string InputSignature(bool includeSliderValues=true,bool includeFeedback=false)=>System.Text.Json.JsonSerializer.Serialize(new {
        Compact,Mount,page,ResizeHandles,
        WristMenuEnabled,WristFitMode,WristInputReady,WristMenuDragging,WristHandleVisible,WristAnchorVisible,
        WristPhase=WristInputReady ? 2 : WristMenuAmount==0 && !WristMenuDragging ? 0 : 1,
        session.ShockerPage,TestMode=session.ShockerPage ? session.Shockers.IsTestMode() : session.TestMode,session.Model.RecordArmed,
        Rows=session.DisplayModel.Functions.Select(r=>new {r.Toy.Id,Function=r.Function.Id,r.Function.Generation,r.Connected,r.Selected}),
        Shock=session.ShockerPage ? new {Intensity=includeSliderValues ? (int?)session.Shockers.Intensity : null,
            DurationMs=includeSliderValues ? (int?)session.Shockers.DurationMs : null,session.Shockers.Armed,
            session.Shockers.ArmedFromNative,session.Shockers.ArmedFromDesktop,session.Shockers.ArmedFromVrc} : null,
        Feedback=includeFeedback && session.ShockerPage && !session.Shockers.Armed ? ShockerFeedback : null
    });
    public bool AcceptsInput => presentedInput.Length>0 && presentedInput==InputSignature();
    public void MarkPresented()=>presentedInput=InputSignature();
    internal string InputCookie=>InputSignature();
    internal void MarkPresented(string cookie)=>presentedInput=cookie;
    // A completed slider image can trail the live value by one frame. Show it
    // without authorizing actions at values the wearer has not yet seen.
    // Feedback expiry invalidates old images without changing action readiness.
    internal PanelFrameStamp CaptureFrameStamp()=>new(InputSignature(),InputSignature(false,true));
    internal bool CanDisplay(PanelFrameStamp stamp)=>stamp.Context==InputSignature(false,true);
    public PanelModel(ToyControlSession session,Func<string,string,(string,int)>? metadata=null)
    { this.session=session;this.metadata=metadata ?? ((_,_) => ("Unknown",100)); }
    private PanelModel() {session=null!;metadata=null!;}
    public List<PanelFunction> Functions { get; } = [];
    public bool Compact { get; private set; } = true;
    public int TransparencyPercent {get;private set;}
    public bool ShockerPage {get;private set;}
    public List<PanelShocker> Shockers {get;}=[];
    private int displayIntensity,displayDuration,displaySelectedCount;
    private float displayLevel;
    private bool displayArmed,displayTest;
    private string displayShockFeedback="";
    public int ShockerIntensity => session?.Shockers.Intensity ?? displayIntensity;
    public int ShockerDurationMs => session?.Shockers.DurationMs ?? displayDuration;
    public bool ShockerArmed => session?.Shockers.Armed ?? displayArmed;
    public string ShockerFeedback => session?.Shockers.ShockFeedback(Now) ?? displayShockFeedback;
    public string? ShockerPreview => null;
    private bool InTestMode => session is null ? displayTest : ShockerPage ? session.Shockers.IsTestMode() : session.TestMode;
    public string ModeLabel => InTestMode ? "TEST MODE" : "LIVE MODE";
    public string ShockerStatus => ShockerArmed ? "Release, then press Shock again" : ShockerFeedback.Length>0 ? ShockerFeedback : ModeLabel;
    private string? armedBy;
    public void CancelDesktopConfirmation() {if(armedBy=="desktop") {session.Shockers.Cancel();armedBy=null;}}
    private int displayPageCount=1;
    public int PageCount => session is null ? displayPageCount : Math.Max(1,(session.DisplayModel.FunctionCount+9)/10);
    public string PageLabel => PageCount>1 ? $"{page+1} / {PageCount}" : "";
    private Size contentSize=PanelSizing.DefaultContent,panelSize=PanelSizing.Reference(false),resizeStart;
    private bool resizeWrist;
    private PointF resizeOrigin;
    public Size ContentSize=>Compact ? contentSize : panelSize;
    public bool ResizeHandles {get;private set;}
    public bool ResizingContent {get;private set;}
    public PanelLayout Layout()
    {
        var layout=new PanelLayout(Compact,ShockerPage ? Shockers.Count : Functions.Count,ShockerPage,ContentSize,ResizeHandles,Functions.Count);
        if(Compact && WristMenuEnabled && !WristFitMode)
        {
            layout.Hits.RemoveAll(h=>h.Id=="move");
            if(WristHandleVisible) layout.Hits.Add(new("wrist-handle",WristMenuFrame.Handle(layout.Size,WristMenuAmount),"Touch and hold grip",10));
        }
        if(PageCount>1)
        {
            var x=Compact ? ShockerPage ? 256 : 170 : 252;
            layout.Hits.Add(new("previous",new(x,layout.Size.Height-60,32,32),"Previous toys",16));
            layout.Hits.Add(new("next",new(x+38,layout.Size.Height-60,32,32),"Next toys",16));
        }
        return layout;
    }
    public Mount Mount { get; private set; } = Mount.LeftHand;
    private Mount wristMount=Mount.LeftHand;
    private float wristWidth=.24f,panelWidth=.44f;
    public float WidthMeters => Compact ? wristWidth : panelWidth;
    public bool Recording { get; private set; }
    public bool MovingWrist {get;set;}
    public bool GrabbingPanel {get;set;}
    public string? ActiveGrabHandle {get;set;}
    public HashSet<string> HeldGrabHandles {get;}=[];
    public HashSet<string> GripContacts {get;}=[];
    public Mount WristHand=>wristMount;
    public bool Drawing => owner is not null;
    public string? Owner => owner;
    public PointF? Cursor { get; set; }
    public double Now { get; private set; }
    public string Notice { get; private set; } = "Select a function, then draw";
    public event Action? PlacementChanged;
    public event Action? LayoutChanged;
    private string? owner;
    private double lastHistory;
    public int SelectedCount => session?.Model.SelectedCount ?? displaySelectedCount;
    public float SelectedLevel => session?.Model.Functions.Where(f=>f.Selected && f.Connected).Select(f=>f.Level).DefaultIfEmpty(0).Average() ?? displayLevel;
    public int PlayingCount => Functions.Count(f=>f.Playing);
    // Called on the UI thread. The render worker gets only copied presentation
    // data and immutable samples/clips; no session/controller reference survives.
    internal PanelModel CaptureDisplay()
    {
        var copy=new PanelModel {Compact=Compact,TransparencyPercent=TransparencyPercent,ShockerPage=ShockerPage,
            WristMenuEnabled=WristMenuEnabled,WristMenuAmount=WristMenuAmount,WristHandleVisible=WristHandleVisible,WristAnchorVisible=WristAnchorVisible,WristMenuDragging=WristMenuDragging,WristHandleContact=WristHandleContact,WristFitMode=WristFitMode,
            contentSize=contentSize,panelSize=panelSize,ResizeHandles=ResizeHandles,ResizingContent=ResizingContent,Mount=Mount,wristMount=wristMount,
            wristWidth=wristWidth,panelWidth=panelWidth,Recording=Recording,MovingWrist=MovingWrist,GrabbingPanel=GrabbingPanel,
            ActiveGrabHandle=ActiveGrabHandle,owner=owner,Cursor=Cursor,Now=Now,Notice=Notice,page=page,displayPageCount=PageCount,
            displayIntensity=ShockerIntensity,displayDuration=ShockerDurationMs,displayArmed=ShockerArmed,displayTest=InTestMode,displayShockFeedback=ShockerFeedback,
            displaySelectedCount=SelectedCount,displayLevel=SelectedLevel};
        foreach(var f in Functions)
        {
            var item=new PanelFunction(f.Name,f.Function,f.Color) {Pattern=f.Pattern,BatteryPercent=f.BatteryPercent,SensitivityPercent=f.SensitivityPercent,
                IsRemoteControlled=f.IsRemoteControlled,Connected=f.Connected,Selected=f.Selected,Level=f.Level,Playing=f.Playing,LoopStarted=f.LoopStarted,Clip=[..f.Clip]};
            item.History.AddRange(f.History);copy.Functions.Add(item);
        }
        foreach(var s in Shockers) copy.Shockers.Add(new(s.Name) {Selected=s.Selected,Connected=s.Connected});
        copy.HeldGrabHandles.UnionWith(HeldGrabHandles);copy.GripContacts.UnionWith(GripContacts);return copy;
    }

    public void Tick(double now)
    {
        Now=Math.Max(Now,now);
        syncing=true;
        try
        {
            var all=session.Model.Functions.ToArray();
            var allShock=session.Shockers.Model.Functions.ToArray();
            if(ShockerPage!=session.ShockerPage)
            { ContextInvalidated?.Invoke();CancelGesture();ShockerPage=session.ShockerPage;page=0;inventoryKey="";LayoutChanged?.Invoke();PlacementChanged?.Invoke(); }
            page=Math.Clamp(page,0,PageCount-1);
            var next=all.Skip(page*10).Take(10).ToArray();
            var nextShock=allShock.Skip(page*10).Take(10).ToArray();
            var key=string.Join("|",next.Concat(nextShock).Select(r=>$"{r.Toy.Id}/{r.Function.Id}/{r.Function.Generation}/{r.Connected}/{r.Toy.Name}/{r.Function.Name}"));
            if(key!=inventoryKey)
            {
                presentedInput="";
                ContextInvalidated?.Invoke();CancelGesture();rows=next;shockRows=nextShock;
                Functions.Clear();Shockers.Clear();
                foreach(var r in rows) Functions.Add(new(r.Toy.Name,r.Function.Name,r.Function.Color));
                foreach(var r in shockRows) Shockers.Add(new(r.Toy.Name));
                inventoryKey=key;LayoutChanged?.Invoke();
            }
            if(owner is not null && (!session.Model.Touching || session.TouchFromVrc))
            { ContextInvalidated?.Invoke();owner=null;Cursor=null; }
            for(var i=0;i<rows.Length;i++)
            {
                var r=rows[i];var f=Functions[i];var meta=metadata(r.Toy.Id,r.Function.Id);
                f.Selected=r.Selected;f.Connected=r.Connected;f.BatteryPercent=r.Connected && r.Toy.Battery>=0 ? r.Toy.Battery : null;
                f.SensitivityPercent=meta.Sensitivity;f.Function=meta.Function;f.IsRemoteControlled=r.Toy.Id.StartsWith("remote/",StringComparison.Ordinal);
                f.Level=r.Connected ? r.Level : 0;f.Playing=r.Function.LoopRunning;f.LoopStarted=r.Function.LoopStarted;
                if(!ReferenceEquals(f.Pattern,r.Function.Pattern))
                { f.Pattern=r.Function.Pattern;f.Clip=r.Function.Pattern is { } clip ? Enumerable.Range(0,121).Select(n=>new Sample(n*clip.Duration/120,clip.At(n*clip.Duration/120))).ToList() : []; }
            }
            for(var i=0;i<shockRows.Length;i++) {Shockers[i].Selected=shockRows[i].Selected;Shockers[i].Connected=shockRows[i].Connected;}
            Recording=session.Model.RecordArmed;
        }
        finally {syncing=false;}
        if(Now-lastHistory>=1d/30)
        {
            foreach(var f in Functions)
            {
                f.History.Add(new(Now,f.Level));
                f.History.RemoveAll(s=>s.Time<Now-7);
                if(f.History.Count>240) f.History.RemoveAt(0);
            }
            lastHistory=Now;
        }
    }
    internal static float Interpolate(IReadOnlyList<Sample> samples,double time)
    {
        if(samples.Count==0) return 0;
        var i=0; while(i+1<samples.Count && samples[i+1].Time<time) i++;
        if(i+1>=samples.Count) return samples[i].Value;
        var a=samples[i]; var b=samples[i+1];
        return a.Value+(b.Value-a.Value)*(float)Math.Clamp((time-a.Time)/Math.Max(.0001,b.Time-a.Time),0,1);
    }
    public void ToggleSelection(int index)
    {
        CancelGesture();
        if(index<0 || index>=rows.Length || !rows[index].Connected) return;
        session.SelectPanelFunction(rows[index].Toy.Id,rows[index].Function.Id);Tick(Now);
    }
    public bool BeginDraw(string source,float value)
    {
        if(ShockerPage || owner is not null || SelectedCount==0 || !float.IsFinite(value)) return false;
        session.DrawPanel(value,true);
        if(!session.Model.Touching || session.TouchFromVrc) return false;
        owner=source;
        Notice=Recording ? "Recording your movement" : "Live input";
        return true;
    }
    public void MoveDraw(string source,float value)
    {
        if(owner!=source || !float.IsFinite(value)) return;
        if(session.Model.Touching && !session.TouchFromVrc) session.DrawPanel(value,false);
    }
    public void EndDraw(string source,bool commit)
    {
        if(owner!=source) return;
        owner=null;Cursor=null;
        if(!session.TouchFromVrc) session.Release(commit);
        Notice=session.Model.PatternStatus;
    }
    public void CancelGesture() {EndContentResize(false);if(owner is { } source) EndDraw(source,false);Cursor=null;}
    public void ToggleRecord()
    {
        if(ShockerPage) {Notice="Record is available on the toy curve page";return;}
        CancelGesture();session.Action("InRecord",false);Recording=session.Model.RecordArmed;Notice=session.Model.PatternStatus;
    }
    public void StopAll(bool keepGrip=true,bool clearSelection=false)
    {
        CancelGesture();foreach(var f in Functions) {f.Level=0;f.Playing=false;}
        MovingWrist=false;ResizeHandles=false;
        if(!keepGrip) {GrabbingPanel=false;ActiveGrabHandle=null;HeldGrabHandles.Clear();GripContacts.Clear();}
        if(!syncing) session.StopAll(preserveSelection:!clearSelection);
        Notice="All stopped · fresh input required";
    }
    public bool BeginWristMove(string source)
    {
        if(!Compact || source!=(Mount==Mount.LeftHand ? "vr1" : "vr0")) {Notice="Hold Move with the free controller in VR";return false;}
        StopAll();MovingWrist=true;Notice="Moving wrist · release to save";return true;
    }
    public void ToggleResize()
    {
        var enable=!ResizeHandles;StopAll();ResizeHandles=enable;
        Notice=enable ? "Hold the top-right cross and drag to resize" : "Resize handle hidden";
    }
    public bool BeginContentResize(PointF point)
    {
        if(!ResizeHandles || ResizingContent) return false;
        StopAll();ResizeHandles=true;resizeStart=ContentSize;resizeWrist=Compact;
        resizeOrigin=new(point.X,Layout().Size.Height-point.Y);ResizingContent=true;
        Notice="Resizing · release to keep";return true;
    }
    public void ResizeContent(PointF point)
    {
        if(!ResizingContent || !float.IsFinite(point.X) || !float.IsFinite(point.Y)) return;
        SetContentSize(resizeStart.Width+point.X-resizeOrigin.X,resizeStart.Height+Layout().Size.Height-point.Y-resizeOrigin.Y);
    }
    internal void SetContentSize(float width,float height)
    {
        if(!float.IsFinite(width) || !float.IsFinite(height)) return;
        if(Compact) contentSize=PanelSizing.Clamp(width,height);else panelSize=PanelSizing.ClampPanel(width,height);
    }
    public void EndContentResize(bool commit)
    {
        if(!ResizingContent) return;
        if(!commit) {if(resizeWrist) contentSize=resizeStart;else panelSize=resizeStart;}
        ResizingContent=false;Notice=commit ? "Size kept · drag again or hide the handle" : "Resize cancelled";
    }
    public bool BeginPanelGrab(string source)
    {
        if(Compact || source is not ("vr0" or "vr1")) {Notice="Touch a side handle and hold the controller grip in VR";return false;}
        GrabbingPanel=true;Notice="Panel held · the other hand can use the pointer";return true;
    }
    public void PlacementNotice(string text)=>Notice=text;
    public void SetCompact(bool compact)
    {
        if(Compact==compact) return;
        StopAll(false);Compact=compact;Mount=compact ? wristMount : Mount.Floating;
        Notice=compact ? "Wrist mode" : "Detailed panel dropped in front of you";
        LayoutChanged?.Invoke();PlacementChanged?.Invoke();
    }
    public void SetMount(Mount mount)
    {
        if(mount==Mount.Floating) {SetCompact(false);return;}
        wristMount=mount;
        if(Mount==mount && Compact) return;
        var layoutChanged=!Compact;
        StopAll(false);Compact=true;Mount=mount;Notice=mount==Mount.LeftHand ? "Left wrist" : "Right wrist";
        if(layoutChanged) LayoutChanged?.Invoke();
        PlacementChanged?.Invoke();
    }
    public void SetWidth(float meters)
    {
        if(!float.IsFinite(meters)) return;
        if(Compact) wristWidth=Math.Clamp(meters,.16f,.6f);else panelWidth=Math.Clamp(meters,.16f,.6f);
        PlacementChanged?.Invoke();
    }
    public void ChangePage(int delta) {CancelGesture();session.Shockers.Cancel();page=(page+delta+PageCount)%PageCount;inventoryKey="";Tick(Now);}
    public void SwitchPage()
    {
        CancelGesture();session.SetPage(!ShockerPage);Tick(Now);
    }
    public void ToggleShocker(int index)
    {
        if(index<0 || index>=Shockers.Count) return;
        session.Shockers.SelectPanelTarget(shockRows[index].Toy.Id);Tick(Now);
    }
    public void SetTransparency(int percent) {TransparencyPercent=Math.Clamp(percent,0,100);if(TransparencyPercent==100) StopAll(false);}
    public void SetShockerIntensity(int value) => session.Shockers.SetSlider(30,Math.Clamp(value,0,100)/100f);
    public void SetShockerDuration(int value) => session.Shockers.SetSlider(31,(Math.Clamp(value,300,15000)-300)/14700f);
    public void PreviewShocker(string command,string source)
    {
        if(!ShockerPage || command is not ("Beep" or "Vibrate" or "Shock")) return;
        if(command=="Shock" && session.Shockers.Armed && armedBy!=source) {session.Shockers.Cancel();armedBy=null;return;}
        var hit=command=="Shock" ? session.Shockers.Armed ? 33 : 32 : command=="Beep" ? 35 : 34;
        var controlSource=source=="desktop" ? 0 : 4;
        session.Shockers.Press(controlSource,hit,Now);session.Shockers.Release(controlSource);
        armedBy=session.Shockers.Armed ? source : null;Notice=session.Shockers.Notice;
    }
}

// A held button cannot become a fresh click after a context reset, loss or recovery.
internal sealed class PressGate
{
    private bool released,down;
    public void Reset() {released=false;down=false;}
    public bool Update(bool valid,bool pressed)
    {
        if(!valid) {Reset();return false;}
        if(!pressed) {released=true;down=false;return false;}
        if(!released || down) return false;
        down=true;return true;
    }
}
