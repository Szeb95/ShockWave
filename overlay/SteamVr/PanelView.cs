#nullable enable
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace ShockWave.Overlay.SteamVr;

internal sealed record Hit(string Id,RectangleF Rect,string Text,float Radius=0)
{
    public bool Contains(PointF point)=>PanelLayout.ContainsRounded(Rect,Radius,point);
}
internal sealed class PanelLayout
{
    public bool Wrist { get; }
    public Size Size { get; }
    public RectangleF Graph { get; }
    public RectangleF Plot { get; }
    public RectangleF MainArea { get; }
    public List<Hit> Hits { get; }=[];
    public PanelLayout(bool compact,int toyCount=4,bool shockers=false,Size? contentSize=null,bool resizeHandles=false,int wristRowCapacity=0)
    {
        Wrist=compact;toyCount=Math.Clamp(toyCount,1,10);
        var content=contentSize ?? (compact ? PanelSizing.DefaultContent : PanelSizing.Reference(false));
        content=compact ? PanelSizing.Clamp(content.Width,content.Height) : PanelSizing.ClampPanel(content.Width,content.Height);
        var rows=Math.Max(toyCount,Math.Clamp(wristRowCapacity,1,10));
        Size=compact ? new(298+content.Width,Math.Max(108+content.Height,152+(rows-1)*52)) : content;
        MainArea=compact ? new(274,Size.Height-84-content.Height,content.Width,content.Height) : new(364,24,Size.Width-408,Size.Height-128);
        if(compact)
        {
            Plot=Graph=MainArea;
            for(var i=0;i<toyCount;i++) Add("row"+i,24,Size.Height-128-i*52,234,44,"",22);
            AddWristActions();
            Add("record",24,Size.Height-68,56,56,"Record",28);Add("stop",88,Size.Height-68,56,56,"Stop all",28);
        }
        else
        {
            var pitch=Math.Min(82,(Size.Height-224)/toyCount);
            for(var i=0;i<toyCount;i++) Add("row"+i,56,108+i*pitch,276,pitch-8,"");
            Graph=MainArea;Plot=new(414,106,Size.Width-474,Size.Height-238);
            AddPanelActions();
        }
        if(shockers)
        {
            Hits.Clear();
            if(compact)
            {
                for(var i=0;i<toyCount;i++) Add("shocker"+i,24,Size.Height-128-i*52,234,44,"",22);
                Add("intensity",MainArea.X,MainArea.Y+MainArea.Height*228/532,MainArea.Width,48,"Intensity");Add("duration",MainArea.X,MainArea.Y+MainArea.Height*352/532,MainArea.Width,48,"Duration");
                string[] commands=["shock","vibrate","beep","stop"];string[] labels=["Shock","Vibrate","Beep","Stop all"];
                for(var i=0;i<commands.Length;i++) Add(commands[i],24+i*58,Size.Height-68,50,50,labels[i],25);
                AddWristActions();
            }
            else
            {
                var pitch=Math.Min(82,(Size.Height-224)/toyCount);
                for(var i=0;i<toyCount;i++) Add("shocker"+i,56,108+i*pitch,276,pitch-8,"",12);
                Add("intensity",MainArea.X+42,120,80,MainArea.Height-150,"Intensity");Add("duration",MainArea.X+152,120,80,MainArea.Height-150,"Duration");
                AddPanelActions(true);
            }
            Graph=Plot=RectangleF.Empty;
        }
        if(resizeHandles) Add("resize-corner",MainArea.Right-(compact ? 48 : 64),MainArea.Top+(compact ? 0 : 82),48,48,"Hold to resize",24);
        if(!shockers) Add("graph",Plot.X,Plot.Y,Plot.Width,Plot.Height,"");
    }
    private void AddPanelActions(bool shockers=false)
    {
        var y=MainArea.Bottom+16;
        (string Id,string Text)[] buttons=[("layout","Return to wrist"),("larger","Bigger"),("resize","Resize panel"),("smaller","Smaller"),("switch","Shockers / toys")];
        for(var i=0;i<buttons.Length;i++) Add(buttons[i].Id,MainArea.Right-56-i*64,y,56,56,buttons[i].Text,28);
        if(shockers)
        {
            string[] commands=["shock","vibrate","beep","stop"];string[] labels=["Shock","Vibrate","Beep","Stop all"];
            for(var i=0;i<commands.Length;i++) Add(commands[i],56+i*58,y,50,50,labels[i],25);
        }
        else {Add("record",56,y,56,56,"Record",28);Add("stop",120,y,56,56,"Stop all",28);}
        Add("grab-left",4,Size.Height/2f-66,28,132,"Touch and hold grip",12);Add("grab-right",Size.Width-32,Size.Height/2f-66,28,132,"Touch and hold grip",12);
    }
    private void AddWristActions()
    {
        (string Id,string Text)[] buttons=[("drop","Expand / drop"),("larger","Bigger"),("resize","Resize area"),("smaller","Smaller"),("move","Hold to move"),("switch","Shockers / toys")];
        var pitch=Wrist ? 64 : 72;var y=Wrist ? Size.Height-68 : 584;
        var index=0;
        foreach(var button in buttons)
        {
            if(button.Id is "move" or "resize" && !Wrist) continue;
            Add(button.Id,Size.Width-80-index++*pitch,y,56,56,button.Text,28);
        }
    }
    private void Add(string id,float x,float y,float w,float h,string text,float radius=0)=>Hits.Add(new(id,new(x,y,w,h),text,radius));
    public string? At(PointF p)=>Hits.FirstOrDefault(h=>h.Id!="size" && h.Contains(p))?.Id;
    public bool ContainsSurface(PointF p)=>Wrist ? MainArea.Contains(p) || Hits.Any(h=>h.Contains(p)) : PointerBeam.ContainsBoard(p,Size);
    internal static bool ContainsRounded(RectangleF bounds,float radius,PointF p)
    {
        if(!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !bounds.Contains(p)) return false;
        if(radius<=0) return true;
        var dx=p.X-Math.Clamp(p.X,bounds.Left+radius,bounds.Right-radius);
        var dy=p.Y-Math.Clamp(p.Y,bounds.Top+radius,bounds.Bottom-radius);
        return dx*dx+dy*dy<=radius*radius;
    }
}

internal sealed class PanelInput(PanelModel model,Func<bool>? inputBlocked=null)
{
    private string? source,pressed;
    private readonly Dictionary<string,string> gripSources=[];
    public bool Active=>source is not null || gripSources.Count>0;
    public string? PointerOwner=>source;
    public bool PointerActive=>source is not null;
    public bool ExternalInputBlocked=>inputBlocked?.Invoke()==true;
    public int GripCount=>gripSources.Count;
    public bool IsPointerOwnedBy(string from)=>source==from;
    public bool IsOwnedBy(string from)=>source==from || gripSources.ContainsKey(from);
    public event Action<string,bool>? MoveFinished;
    public event Action<string,bool>? GrabFinished;
    public bool IsDrawing(string from)=>source==from && pressed=="graph" && model.Owner==from;
    public bool IsMoving(string from)=>source==from && pressed=="move" && model.MovingWrist;
    public bool IsSliding(string from)=>source==from && pressed is "intensity" or "duration" && model.ShockerPage;
    public bool IsResizing(string from)=>source==from && pressed=="resize-corner" && model.ResizingContent;
    public bool IsGrabbing(string from)=>gripSources.ContainsKey(from) && model.GrabbingPanel;
    public bool GripDown(string from,string handle)
    {
        if(!model.AcceptsInput || from is not ("vr0" or "vr1") || gripSources.ContainsKey(from) || gripSources.Count>=2 || gripSources.Values.Contains(handle) || model.Compact || model.TransparencyPercent==100 || handle is not ("grab-left" or "grab-right")) return false;
        if(source==from) CancelPointer(from);
        if(gripSources.Count==0 && !model.BeginPanelGrab(from)) return false;
        gripSources.Add(from,handle);model.HeldGrabHandles.Add(handle);model.ActiveGrabHandle=gripSources.Values.First();return true;
    }
    public void GripUp(string from)
    {
        if(!IsGrabbing(from)) return;
        var handle=gripSources[from];gripSources.Remove(from);GrabFinished?.Invoke(from,true);model.HeldGrabHandles.Remove(handle);
        model.GrabbingPanel=gripSources.Count>0;model.ActiveGrabHandle=gripSources.Values.FirstOrDefault();
    }
    public void Down(string from,PointF point)
    {
        if(model.TransparencyPercent==100 || IsGrabbing(from) || !model.WristInputReady) return;
        var layout=model.Layout();var hit=layout.At(point);
        if(hit is null) return;
        if(hit=="stop") {CancelPointer();model.StopAll(clearSelection:true);return;}
        if(inputBlocked?.Invoke()==true) return;
        if(!model.AcceptsInput) return;
        if(hit is "grab-left" or "grab-right" or "wrist-handle") {model.PlacementNotice("Touch the handle and hold the controller grip");return;}
        if(source is not null) return;
        source=from;pressed=hit;
        if(hit=="resize-corner" && !model.BeginContentResize(point)) {source=null;pressed=null;return;}
        if(hit=="move" && !model.BeginWristMove(from)) {source=null;pressed=null;return;}
        if(hit=="graph" && !model.BeginDraw(from,Value(layout,point))) {source=null;pressed=null;return;}
        if(IsSliding(from)) Slide(layout,point);
        model.Cursor=point;
    }
    public void Move(string from,PointF point)
    {
        if(model.TransparencyPercent==100) {Cancel();return;}
        if(!model.WristInputReady) {model.Cursor=null;return;}
        if(IsGrabbing(from)) return;
        if(source is not null && source!=from) return;
        if(!float.IsFinite(point.X) || !float.IsFinite(point.Y)) {model.Cursor=null;return;}
        var layout=model.Layout();model.Cursor=layout.ContainsSurface(point) ? point : null;
        if(source==from && pressed=="graph")
        {
            model.MoveDraw(from,Value(layout,point));
        }
        else if(IsSliding(from)) Slide(layout,point);
        else if(IsResizing(from))
        {
            model.ResizeContent(point);
            var current=model.Layout();var mapped=PanelSizing.Remap(point,layout.Size,current.Size);
            model.Cursor=current.ContainsSurface(mapped) ? mapped : null;
        }
    }
    public void Up(string from,PointF point)
    {
        if(!model.WristInputReady) {CancelPointer(from);return;}
        if(source!=from || IsGrabbing(from)) return;
        var layout=model.Layout();var action=pressed;source=null;pressed=null;
        if(action=="move") {MoveFinished?.Invoke(from,model.MovingWrist);model.MovingWrist=false;}
        else if(action=="graph") model.EndDraw(from,true);
        else if(action=="resize-corner") model.EndContentResize(true);
        else if(action is "intensity" or "duration") { }
        else if(action==layout.At(point) && model.AcceptsInput) Activate(action,from);
    }
    public void CancelPointer(string? onlySource=null)
    {
        if(onlySource is not null && source!=onlySource) return;
        var from=source;var moving=pressed=="move";source=null;pressed=null;
        if(moving && from is not null) MoveFinished?.Invoke(from,false);
        model.MovingWrist=false;model.CancelGesture();
    }
    public void CancelGrip()
    {
        var owners=gripSources.Keys.ToArray();gripSources.Clear();
        foreach(var owner in owners) GrabFinished?.Invoke(owner,false);
        model.GrabbingPanel=false;model.ActiveGrabHandle=null;model.HeldGrabHandles.Clear();model.GripContacts.Clear();
    }
    public void Cancel() {CancelPointer();CancelGrip();}
    private static float Value(PanelLayout layout,PointF p)=>Math.Clamp(1-(p.Y-layout.Plot.Y)/layout.Plot.Height,0,1);
    private void Slide(PanelLayout layout,PointF point)
    {
        var rect=layout.Hits.Single(h=>h.Id==pressed).Rect;
        var fraction=layout.Wrist ? Math.Clamp((point.X-rect.Left-16)/(rect.Width-32),0,1) : Math.Clamp(1-(point.Y-rect.Top-16)/(rect.Height-32),0,1);
        if(pressed=="intensity") model.SetShockerIntensity((int)Math.Round(fraction*100));
        else model.SetShockerDuration(300+(int)Math.Round(fraction*147)*100);
    }
    private void Activate(string? action,string from)
    {
        if(action?.StartsWith("row",StringComparison.Ordinal)==true) model.ToggleSelection(int.Parse(action[3..]));
        else if(action?.StartsWith("shocker",StringComparison.Ordinal)==true) model.ToggleShocker(int.Parse(action[7..]));
        else switch(action)
        {
            case "record": model.ToggleRecord();break;
            case "drop": model.SetCompact(!model.Compact);break;
            case "switch": model.SwitchPage();break;
            case "resize": model.ToggleResize();break;
            case "beep": model.PreviewShocker("Beep",from);break;
            case "vibrate": model.PreviewShocker("Vibrate",from);break;
            case "shock": model.PreviewShocker("Shock",from);break;
            case "layout": model.SetCompact(!model.Compact);break;
            case "smaller": model.SetWidth(model.WidthMeters-.02f);break;
            case "larger": model.SetWidth(model.WidthMeters+.02f);break;
            case "previous": model.ChangePage(-1);break;
            case "next": model.ChangePage(1);break;
        }
    }
}

internal static class PanelView
{
    public static readonly Color Green=Color.FromArgb(85,220,155);
    private static readonly Color Background=Color.FromArgb(25,27,31),Card=Color.FromArgb(39,42,48),Muted=Color.FromArgb(148,157,169),White=Color.FromArgb(235,239,244);
    public static Bitmap Render(PanelModel model,string version)
    {
        var bitmap=Draw(model,version);PanelAppearance.ApplyTransparency(bitmap,model.TransparencyPercent);return bitmap;
    }
    private static Bitmap Draw(PanelModel model,string version)
    {
        var l=model.Layout();var bitmap=new Bitmap(l.Size.Width,l.Size.Height,PixelFormat.Format32bppArgb);
        using var g=Graphics.FromImage(bitmap);g.SmoothingMode=SmoothingMode.AntiAlias;g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;g.Clear(Color.Transparent);
        if(!l.Wrist)
        {
            DrawFloating(g,model,l);DrawResizeHandle(g,model,l);
            if(model.Cursor is { } cursor && l.ContainsSurface(cursor)) DrawCursor(g,cursor,model.Drawing);
            return bitmap;
        }
        var drawer=model.WristMenuEnabled && !model.WristFitMode;
        if(drawer && model.WristMenuAmount<=.005f) {DrawWristHandle(g,model,l);return bitmap;}
        var opening=g.Save();
        if(drawer)
        {
            var anchor=WristMenuFrame.Anchor(l.Size);
            g.TranslateTransform(anchor.X,anchor.Y);g.ScaleTransform(model.WristMenuAmount,model.WristMenuAmount);g.TranslateTransform(-anchor.X,-anchor.Y);
        }
        if(model.ShockerPage)
        {
            if(!l.Wrist) Fill(g,new(4,4,l.Size.Width-8,l.Size.Height-8),Background,28);
            DrawShockers(g,model,l);
            DrawResizeHandle(g,model,l);
            if(model.WristInputReady && model.Cursor is { } point && l.ContainsSurface(point)) DrawCursor(g,point,false);
            g.Restore(opening);if(drawer) DrawWristHandle(g,model,l);
            return bitmap;
        }
        if(l.Wrist)
        {
            DrawWrist(g,model,l);
            DrawResizeHandle(g,model,l);
            if(model.WristInputReady && model.Cursor is { } point && l.ContainsSurface(point)) DrawCursor(g,point,model.Drawing);
            g.Restore(opening);if(drawer) DrawWristHandle(g,model,l);
            return bitmap;
        }
        return bitmap;
    }
    private static void DrawWristHandle(Graphics g,PanelModel model,PanelLayout layout)
    {
        if(model.WristAnchorVisible)
        {
            var icon=WristMenuFrame.Icon(layout.Size);Fill(g,icon,Card,28);
            using(var path=Rounded(icon,28)) using(var pen=new Pen(model.WristMenuAmount==0 && model.WristHandleContact ? Green : Muted,1.8f)) g.DrawPath(pen,path);
            PanelIcons.Draw(g,"move",new(icon.X+12,icon.Y+12,32,32),White);
        }
        if(!model.WristHandleVisible || model.WristMenuAmount<=.005f && !model.WristMenuDragging) return;
        var rect=WristMenuFrame.Bar(layout.Size,model.WristMenuAmount);var ready=model.WristHandleContact || model.WristMenuDragging;
        // Same rounded grey/green rail and three grip marks as the floating panel.
        Fill(g,rect,ready ? Color.FromArgb(16,163,127) : Color.FromArgb(56,58,64),12);
        using var penRail=new Pen(ready ? Color.FromArgb(17,18,20) : Color.FromArgb(181,186,193),2) {StartCap=LineCap.Round,EndCap=LineCap.Round};
        var x=rect.X+rect.Width/2;var y=rect.Y+rect.Height/2;
        for(var i=-1;i<=1;i++) g.DrawLine(penRail,x+i*10,y-4,x+i*10,y+4);
    }
    private static void DrawFloating(Graphics g,PanelModel model,PanelLayout l)
    {
        var accent=Color.FromArgb(16,163,127);var sidebar=Color.FromArgb(17,18,20);var panel=Color.FromArgb(43,45,49);var faint=Color.FromArgb(181,186,193);
        Text(g,model.ShockerPage ? "SHOCKERS" : "TOY FUNCTIONS",60,44,17,White,true);
        Text(g,model.ShockerPage ? "Choose shockers" : "Choose functions to draw together",60,74,12,faint);
        if(model.ShockerPage)
        {
            Text(g,model.ShockerStatus,l.MainArea.Left+20,48,16,faint,true);
            for(var i=0;i<model.Shockers.Count;i++)
            {
                var item=model.Shockers[i];var rect=l.Hits.Single(h=>h.Id=="shocker"+i).Rect;
                Fill(g,rect,item.Selected ? Color.FromArgb(43,65,57) : panel,12);
                Fill(g,new(rect.Left,rect.Top+10,3,rect.Height-20),accent,1.5f);
                Text(g,item.Name,rect.Left+16,rect.Top+(rect.Height<65 ? rect.Height/2 : 14),18,item.Connected ? item.Selected ? accent : White : Muted,true,false,false,rect.Height<65);
                if(rect.Height>=65) Text(g,!item.Connected ? "Unavailable" : item.Selected ? "Selected" : "Tap to select",rect.Left+16,rect.Top+42,12,faint);
            }
            foreach(var h in l.Hits.Where(h=>h.Id is "intensity" or "duration"))
            {
                var fraction=h.Id=="intensity" ? model.ShockerIntensity/100f : (model.ShockerDurationMs-300)/14700f;
                var text=h.Id=="intensity" ? $"{model.ShockerIntensity}%" : $"{model.ShockerDurationMs/1000d:0.0}s";
                var x=h.Rect.X+h.Rect.Width/2;var top=h.Rect.Top+16;var bottom=h.Rect.Bottom-16;var y=bottom-(bottom-top)*fraction;
                Text(g,h.Text,x,h.Rect.Top-24,16,White,true,true);Text(g,text,x,h.Rect.Bottom+14,22,White,true,true);
                using var track=new Pen(Color.FromArgb(56,58,64),8) {StartCap=LineCap.Round,EndCap=LineCap.Round};g.DrawLine(track,x,top,x,bottom);
                using var filled=new Pen(accent,8) {StartCap=LineCap.Round,EndCap=LineCap.Round};g.DrawLine(filled,x,bottom,x,y);
                using var knob=new SolidBrush(White);g.FillEllipse(knob,x-11,y-11,22,22);
            }
            if(model.ShockerPreview is { } command) Text(g,$"{command}",l.MainArea.Right-128,l.MainArea.Top+298,18,accent,true,true);
        }
        else
        {
            Text(g,model.Drawing ? model.Recording ? "Recording" : "Live input" : "Live curve",l.MainArea.Left+20,44,21,White,true);
            Text(g,$"{model.SelectedLevel*100:0}%",l.MainArea.Right-92,45,25,accent,true,false,true);
            Text(g,$"{model.SelectedCount} selected",l.MainArea.Left+20,76,12,faint);
            for(var i=0;i<model.Functions.Count;i++) DrawFunction(g,l.Hits.Single(h=>h.Id=="row"+i).Rect,model.Functions[i]);
            DrawGraph(g,model,l);
            for(var i=0;i<=4;i++) Text(g,$"{100-i*25:0}"+(i is 0 or 4 ? "%" : ""),l.Plot.Left-12,l.Plot.Top+l.Plot.Height*i/4,12,faint,false,false,true,true);
            Text(g,"PAST",l.Plot.Left,l.Plot.Bottom+11,11,faint);Text(g,"NOW",l.Plot.Right,l.Plot.Bottom+11,11,faint,false,false,true);
        }
        Text(g,model.ModeLabel+" "+model.PageLabel,56,l.Size.Height-124,12,faint);
        DrawWristActions(g,model,l);
        foreach(var h in l.Hits.Where(h=>h.Id is "grab-left" or "grab-right"))
        {
            var ready=model.GripContacts.Contains(h.Id) || model.GrabbingPanel && model.HeldGrabHandles.Contains(h.Id);Fill(g,h.Rect,ready ? accent : Color.FromArgb(56,58,64),12);
            using var pen=new Pen(ready ? sidebar : faint,2) {StartCap=LineCap.Round,EndCap=LineCap.Round};
            var x=h.Rect.X+h.Rect.Width/2;
            for(var i=-1;i<=1;i++) g.DrawLine(pen,x-4,h.Rect.Y+h.Rect.Height/2+i*10,x+4,h.Rect.Y+h.Rect.Height/2+i*10);
        }
    }
    private static void DrawWrist(Graphics g,PanelModel model,PanelLayout layout)
    {
        DrawGraph(g,model,layout);
        for(var i=0;i<model.Functions.Count;i++)
        {
            var f=model.Functions[i];var rect=layout.Hits.Single(h=>h.Id=="row"+i).Rect;
            Fill(g,rect,f.Selected ? Color.FromArgb(38,65,53) : Card,22);
            if(f.Selected)
            {
                using var path=Rounded(rect,22);using var pen=new Pen(f.Color,1.8f);g.DrawPath(pen,path);
            }
            using(var dot=new SolidBrush(f.Color)) g.FillEllipse(dot,rect.X+17,rect.Y+10,10,10);
            ToyName(g,f,new(rect.X+40,rect.Y,rect.Width-(f.IsRemoteControlled ? 142 : 116),30),20);
            DrawToyInfo(g,rect,f,40,15,false);
        }
        DrawWristActions(g,model,layout);
    }
    private static void DrawWristActions(Graphics g,PanelModel model,PanelLayout layout)
    {
        foreach(var h in layout.Hits.Where(h=>h.Id is "larger" or "smaller" or "resize" or "drop" or "layout" or "record" or "stop" or "switch" or "move" or "shock" or "vibrate" or "beep" or "previous" or "next"))
        {
            var preview=h.Id is "shock" or "vibrate" or "beep" && string.Equals(model.ShockerPreview,h.Text,StringComparison.Ordinal);
            var active=h.Id=="record" && model.Recording && !model.ShockerPage || h.Id=="move" && model.MovingWrist || h.Id=="resize" && model.ResizeHandles || h.Id=="shock" && model.ShockerArmed;
            var unavailable=h.Id=="record" && model.ShockerPage;
            var amber=Color.FromArgb(238,182,102);
            Fill(g,h.Rect,preview ? amber : active ? Green : h.Id=="stop" ? Color.FromArgb(62,43,47) : Card,h.Radius);
            var color=unavailable ? Color.FromArgb(95,101,109) : active || preview ? Background : h.Id=="stop" ? Color.FromArgb(255,175,175) : h.Id=="shock" || h.Id=="switch" && model.ShockerPage ? amber : White;
            var iconSize=Math.Min(32,h.Rect.Width-20);
            if(h.Id is "previous" or "next") Text(g,h.Id=="previous" ? "‹" : "›",h.Rect.X+h.Rect.Width/2,h.Rect.Y+h.Rect.Height/2,24,color,true,true,false,true);
            else PanelIcons.Draw(g,h.Id,new(h.Rect.X+(h.Rect.Width-iconSize)/2,h.Rect.Y+(h.Rect.Height-iconSize)/2,iconSize,iconSize),color);
            if(model.Cursor is { } cursor && h.Contains(cursor))
            {
                using var path=Rounded(h.Rect,h.Radius);using var pen=new Pen(Color.FromArgb(175,Green),1.8f);g.DrawPath(pen,path);
                var label=unavailable ? "Record (toys)" : h.Id=="shock" && model.ShockerArmed ? "Confirm Shock" : h.Id=="switch" ? model.ShockerPage ? "Toy curves" : "Shockers" : h.Text;
                Text(g,label,h.Rect.X+h.Rect.Width/2,h.Rect.Top-(layout.Wrist ? 8 : 14),layout.Wrist ? 12 : 16,White,false,true);
            }
        }
    }
    private static void DrawShockers(Graphics g,PanelModel model,PanelLayout layout)
    {
        var amber=Color.FromArgb(238,182,102);
        Text(g,model.ModeLabel,layout.MainArea.Left,layout.MainArea.Top+layout.MainArea.Height*82/532,15,Muted);
        for(var i=0;i<model.Shockers.Count;i++)
        {
            var item=model.Shockers[i];var rect=layout.Hits.Single(h=>h.Id=="shocker"+i).Rect;
            Fill(g,rect,item.Selected ? Color.FromArgb(65,55,39) : Card,22);
            if(item.Selected) {using var path=Rounded(rect,22);using var pen=new Pen(amber,1.8f);g.DrawPath(pen,path);}
            using(var dot=new SolidBrush(item.Selected && model.ShockerPreview is not null ? White : amber)) g.FillEllipse(dot,rect.X+17,rect.Y+17,10,10);
            Text(g,item.Name,rect.X+40,rect.Y+rect.Height/2,22,White,item.Selected,false,false,true);
        }
        foreach(var h in layout.Hits.Where(h=>h.Id is "intensity" or "duration"))
        {
            var fraction=h.Id=="intensity" ? model.ShockerIntensity/100f : (model.ShockerDurationMs-300)/14700f;
            var text=h.Id=="intensity" ? $"{model.ShockerIntensity}%" : $"{model.ShockerDurationMs/1000d:0.0}s";
            Text(g,h.Text,h.Rect.Left,h.Rect.Top-36,20,Muted);Text(g,text,h.Rect.Right,h.Rect.Top-36,24,White,true,false,true);
            var left=h.Rect.Left+16;var right=h.Rect.Right-16;var y=h.Rect.Y+h.Rect.Height/2;var x=left+(right-left)*fraction;
            using var track=new Pen(Color.FromArgb(82,92,96),6) {StartCap=LineCap.Round,EndCap=LineCap.Round};g.DrawLine(track,left,y,right,y);
            using var filled=new Pen(amber,6) {StartCap=LineCap.Round,EndCap=LineCap.Round};g.DrawLine(filled,left,y,x,y);
            using var knob=new SolidBrush(amber);g.FillEllipse(knob,x-10,y-10,20,20);
        }
        var feedback=model.ShockerArmed ? "Press again to shock within 3 sec" : model.ShockerFeedback;
        if(feedback.Length>0)
        {
            var duration=layout.Hits.Single(h=>h.Id=="duration").Rect;
            var icons=layout.Hits.Single(h=>h.Id=="shock").Rect;
            Text(g,feedback,layout.MainArea.Left+layout.MainArea.Width/2,(duration.Bottom+icons.Top)/2,24,amber,true,true);
        }
        var back=layout.Hits.FirstOrDefault(h=>h.Id=="layout");if(back is not null) {Fill(g,back.Rect,Card,back.Radius);Text(g,back.Text,back.Rect.X+back.Rect.Width/2,back.Rect.Y+back.Rect.Height/2,20,White,true,true);}
        if(model.ShockerPreview is { } command) Text(g,$"{command}",layout.MainArea.Left,layout.MainArea.Top+layout.MainArea.Height*134/532,18,amber,true);
        DrawWristActions(g,model,layout);
    }
    private static void DrawResizeHandle(Graphics g,PanelModel model,PanelLayout layout)
    {
        var handle=layout.Hits.FirstOrDefault(h=>h.Id=="resize-corner");if(handle is null) return;
        Fill(g,handle.Rect,model.ResizingContent ? Green : Card,24);
        using(var path=Rounded(handle.Rect,24)) using(var pen=new Pen(Green,1.8f)) g.DrawPath(pen,path);
        PanelIcons.Draw(g,"move",new(handle.Rect.X+10,handle.Rect.Y+10,28,28),model.ResizingContent ? Background : Green);
    }
    private static void DrawFunction(Graphics g,RectangleF rect,PanelFunction f)
    {
        var radius=Math.Min(14,rect.Height/2);var small=rect.Height<65;
        Fill(g,rect,f.Selected ? Color.FromArgb(40,59,52) : Color.FromArgb(43,45,49),radius);
        Fill(g,new(rect.Left,rect.Top+8,3,rect.Height-16),f.Color,1.5f);
        if(f.Selected) {using var pen=new Pen(Color.FromArgb(150,f.Color),1.6f);using var path=Rounded(rect,radius);g.DrawPath(pen,path);}
        using(var brush=new SolidBrush(f.Color)) g.FillEllipse(brush,rect.X+18,rect.Y+(small ? 6 : 17),12,12);
        ToyName(g,f,new(rect.X+46,rect.Y+(small ? 0 : 7),rect.Width-(f.IsRemoteControlled ? 199 : 173),small ? 24 : 32),small ? 17 : 23);
        if(!small)
        {
            Text(g,f.Function+(f.Playing ? " · Loop" : ""),rect.X+46,rect.Y+43,13,Muted);
        }
        DrawToyInfo(g,rect,f,46,small ? 12 : 23);
    }
    private static void ToyName(Graphics g,PanelFunction toy,RectangleF bounds,float size)
    {
        using var font=new Font("Segoe UI",size,toy.Selected ? FontStyle.Bold : FontStyle.Regular,GraphicsUnit.Pixel);
        using var brush=new SolidBrush(toy.Selected ? White : Color.FromArgb(192,200,208));
        using var format=new StringFormat {LineAlignment=StringAlignment.Center,Trimming=StringTrimming.EllipsisCharacter,FormatFlags=StringFormatFlags.NoWrap};
        g.DrawString(toy.Name,font,brush,bounds,format);
    }
    private static void DrawToyInfo(Graphics g,RectangleF rect,PanelFunction toy,float left,float batteryY,bool percentages=true)
    {
        var battery=toy.BatteryPercent is { } value ? Math.Clamp(value,0,100) : (int?)null;
        var color=battery is <=10 ? Color.FromArgb(255,175,175) : battery is <=20 ? Color.FromArgb(238,182,102) : Color.FromArgb(186,199,194);
        var x=rect.Right-(percentages ? 91 : 40);var height=percentages ? 9 : 11;var width=percentages ? 15 : 20;var y=rect.Y+batteryY-height/2f;
        using(var pen=new Pen(color,1.3f)) {g.DrawRectangle(pen,x,y,width,height);g.DrawLine(pen,x+width+2,y+height/2-1.5f,x+width+2,y+height/2+1.5f);}
        if(battery is >0) {using var fill=new SolidBrush(color);g.FillRectangle(fill,x+2,y+2,(width-4)*battery.Value/100f,height-4);}
        if(percentages) Text(g,battery is { } percent ? $"{percent}%" : "—",rect.Right-17,rect.Y+batteryY,15,color,true,false,true,true);
        else if(battery is null) Text(g,"?",x+width/2,y+height/2,10,color,true,true);
        PanelIcons.Draw(g,PanelIcons.FunctionIcon(toy.Function),new(x-26,rect.Y+batteryY-10,20,20),Color.FromArgb(211,224,219));
        if(toy.IsRemoteControlled) PanelIcons.Draw(g,"remote",new(x-52,rect.Y+batteryY-10,20,20),Color.FromArgb(119,204,255));
        var level=float.IsFinite(toy.Level) ? Math.Clamp(toy.Level,0,1) : 0;
        var track=new RectangleF(rect.X+left,rect.Bottom-8,rect.Width-left-(percentages ? 62 : 18),3);
        Fill(g,track,Color.FromArgb(68,77,80),1.5f);
        if(level>0)
        {
            var fillWidth=track.Width*level;using var fill=new SolidBrush(Color.FromArgb(toy.Selected ? 235 : 180,toy.Color));
            g.FillRectangle(fill,track.X,track.Y,fillWidth,track.Height);
        }
        if(percentages) Text(g,$"{level*100:0}%",rect.Right-17,track.Y+1.5f,11,Muted,false,false,true,true);
    }
    private static void DrawGraph(Graphics g,PanelModel model,PanelLayout l)
    {
        var r=l.Plot;
        using(var pen=new Pen(Color.FromArgb(160,148,174,167),l.Wrist ? 1.6f : 1.3f))
        {
            var columns=l.Wrist ? 3 : 8;var rows=l.Wrist ? 3 : 4;
            for(var i=0;i<=columns;i++) g.DrawLine(pen,r.X+r.Width*i/columns,r.Y,r.X+r.Width*i/columns,r.Bottom);
            for(var i=0;i<=rows;i++) g.DrawLine(pen,r.X,r.Y+r.Height*i/rows,r.Right,r.Y+r.Height*i/rows);
        }
        var old=g.Save();g.SetClip(r);
        foreach(var f in model.Functions)
        {
            if(!f.Selected && !f.Playing) continue;
            var points=f.History.Select(s=>new PointF(r.Right-(float)(model.Now-s.Time)/7*r.Width,r.Bottom-s.Value*r.Height)).ToArray();
            if(points.Length>1) {using var pen=new Pen(Color.FromArgb(f.Selected ? 230 : 95,f.Color),f.Selected ? 3.2f : 2);g.DrawLines(pen,points);}
            if(f.Selected && !model.Drawing && !f.Playing && f.Clip.Count>1)
            {
                var duration=Math.Max(.01,f.Clip[^1].Time);
                var clip=f.Clip.Select(s=>new PointF(r.X+(float)(s.Time/duration)*r.Width,r.Bottom-s.Value*r.Height)).ToArray();
                using var pen=new Pen(Color.FromArgb(140,f.Color),2) {DashStyle=DashStyle.Dash};g.DrawLines(pen,clip);
            }
        }
        g.Restore(old);
        if(l.Wrist) return;
        if(model.SelectedCount==0) Text(g,"Select a function on the left",r.X+r.Width/2,r.Y+r.Height/2,21,Muted,false,true);
        else if(!model.Drawing && model.Functions.All(f=>f.History.Count<2 || f.History.All(s=>s.Value<.001)) && model.Functions.All(f=>f.Clip.Count==0))
            Text(g,"Point, hold the trigger and draw",r.X+r.Width/2,r.Y+r.Height/2,18,Muted,false,true);
    }
    private static void DrawCursor(Graphics g,PointF c,bool drawing)
    {
        var radius=drawing ? 13 : 10;
        using var halo=new Pen(Color.FromArgb(220,12,21,17),6);
        using var ring=new Pen(Green,2.6f);using var center=new SolidBrush(Color.FromArgb(221,255,237));
        g.DrawEllipse(halo,c.X-radius,c.Y-radius,radius*2,radius*2);g.DrawEllipse(ring,c.X-radius,c.Y-radius,radius*2,radius*2);
        g.FillEllipse(center,c.X-2.5f,c.Y-2.5f,5,5);
    }
    private static void WaveLogo(Graphics g,RectangleF r)
    {
        Fill(g,r,Color.FromArgb(35,64,51),14);
        using var pen=new Pen(Green,3.5f) {StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round};
        g.DrawLines(pen,new PointF[]{new(r.X+10,r.Y+26),new(r.X+18,r.Y+26),new(r.X+23,r.Y+14),new(r.X+29,r.Y+35),new(r.X+34,r.Y+24),new(r.X+39,r.Y+24)});
    }
    internal static GraphicsPath Rounded(RectangleF r,float radius)
    {
        var p=new GraphicsPath();var d=radius*2;
        p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;
    }
    private static void Fill(Graphics g,RectangleF r,Color color,float radius) {using var b=new SolidBrush(color);using var p=Rounded(r,radius);g.FillPath(b,p);}
    private static void Text(Graphics g,string text,float x,float y,float size,Color color,bool bold=false,bool centered=false,bool right=false,bool middle=false)
    {
        using var font=new Font("Segoe UI",size,bold ? FontStyle.Bold : FontStyle.Regular,GraphicsUnit.Pixel);
        using var brush=new SolidBrush(color);using var format=new StringFormat {Alignment=centered ? StringAlignment.Center : right ? StringAlignment.Far : StringAlignment.Near,LineAlignment=centered || middle ? StringAlignment.Center : StringAlignment.Near,FormatFlags=StringFormatFlags.NoWrap};
        g.DrawString(text,font,brush,new PointF(x,y),format);
    }
}
