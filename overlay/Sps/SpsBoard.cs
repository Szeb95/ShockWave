using System.Drawing.Drawing2D;
using System.Drawing.Text;

namespace ShockWave.Overlay.Sps;

internal sealed record SpsToyRow(SpsTarget Target, string Toy, string Function, string Status, Color Color, bool Connected, int Percent);
internal sealed class SpsBoard : Control
{
    private static readonly Color Background = Color.FromArgb(30,31,34), LeftSurface = Color.FromArgb(16,17,19), Surface = Color.FromArgb(41,43,47),
        Input = Color.FromArgb(54,57,63), White = Color.FromArgb(242,243,245), Muted = Color.FromArgb(181,186,193), Accent = Color.FromArgb(16,163,127);
    private SpsBoardDocument document=new(1,false,[],[]);
    public SpsBoardDocument Document { get=>document; set { document=value; RefreshAutomaticView(); } }
    public IReadOnlyList<SpsToyRow> Rows { get; set; } = [];
    public string Status { get; set; } = "WAITING FOR SPS";
    public string StatusDetail { get; set; } = "Automatic · waiting for avatar inputs";
    public Func<SpsHole, double>? HoleLevel { get; set; }
    public Func<SpsHole, string>? HoleState { get; set; }
    public Func<SpsHole, bool>? HoleActive { get; set; }
    public Func<SpsHole,bool?>? HoleEnabled { get; set; }
    internal SpsAutomaticPanel AutomaticPanel { get; }=new();
    public bool DiscoveryMode { get; private set; }
    public IReadOnlyList<SpsDiscoveryRow> Discoveries { get; set; }=[];
    private int discoveryPage;
    internal int DiscoveryPageSize=>Math.Max(1,(int)((BodyBox.Height-54*UiScale)/(72*UiScale)));
    internal int DiscoveryPageCount=>Math.Max(1,(Discoveries.Count+DiscoveryPageSize-1)/DiscoveryPageSize);
    internal int DiscoveryPage=>Math.Clamp(discoveryPage,0,DiscoveryPageCount-1);
    internal string? HoveredHole { get; private set; }
    internal string? HoveredWire { get; private set; }
    private readonly ToolTip connectionHelp = new() { InitialDelay = 350, ReshowDelay = 100, AutoPopDelay = 10000 };
    private int page;
    public int Page { get => Math.Min(page, PageCount - 1); private set => page = value; }
    public int PageCount => Math.Max(1, (Rows.Count + 5) / 6);
    public event Action<SpsTarget, string>? ConnectRequested;
    public event Action<string, float, float>? HoleMoved;
    public event Action<string>? HoleOptionsRequested, WireOptionsRequested;
    public event Action? LockRequested, StopRequested, ClearRequested, SetupRequested, AllToysRequested;
    public event Action? DiscoveryRequested;
    public event Action<string>? DiscoveryAddRequested;
    public event Action<SpsSources>? AutomaticSourcesRequested;
    public event Action<SpsTarget>? ToyOptionsRequested;
    private string? moving, pickedWire;
    private SpsTarget? connecting;
    private Point start, pointer;
    private PointF original;
    private bool dragged;
    public bool Dragging => moving is not null || connecting is not null;
    private float UiScale => Math.Clamp(Math.Min(Width / 1240f, Height / 608f), .60f, 2f);
    private RectangleF Box(float x, float y, float w, float h) => new(x * Width / 1240f, y * Height / 608f, w * Width / 1240f, h * Height / 608f);
    internal RectangleF LeftBox => Box(24, 5, 300, 598);
    internal RectangleF RightBox => Box(342, 5, 874, 598);
    internal RectangleF BodyBox => new(RightBox.X + 45 * UiScale, RightBox.Y + 104 * UiScale, Math.Max(1, RightBox.Width - 90 * UiScale), Math.Max(1, RightBox.Height - 188 * UiScale));
    internal RectangleF LockBox => new(RightBox.Right - 126 * UiScale, RightBox.Top + 22 * UiScale, 102 * UiScale, 42 * UiScale);
    internal RectangleF DiscoveryBox=>new(LockBox.Left-148*UiScale,LockBox.Top,136*UiScale,LockBox.Height);
    internal RectangleF AllToysBox => new(RightBox.Left + 28 * UiScale, RightBox.Top + 62 * UiScale, 236 * UiScale, 28 * UiScale);
    internal RectangleF ToyGearBox(int index) { var row=ToyBox(index); return !Document.AllToysToAllHoles || DiscoveryMode?RectangleF.Empty:new(RightBox.Left+12*UiScale,row.Top+row.Height/2-14*UiScale,28*UiScale,28*UiScale); }
    internal RectangleF StatusBox=>new(RightBox.Right-264*UiScale,RightBox.Bottom-72*UiScale,230*UiScale,45*UiScale);
    internal RectangleF ManualHintBox=>new(SetupBox.Right+12*UiScale,StatusBox.Top,Math.Max(1,StatusBox.Left-SetupBox.Right-24*UiScale),StatusBox.Height);
    private RectangleF ClearBox => new(LeftBox.Left + 16 * UiScale, LeftBox.Top + 72 * UiScale, (LeftBox.Width - 48 * UiScale) / 2, 44 * UiScale);
    internal RectangleF StopBox => new(ClearBox.Right + 16 * UiScale, ClearBox.Top, ClearBox.Width, ClearBox.Height);
    private RectangleF PrevBox => new(LeftBox.Left + 16 * UiScale, LeftBox.Bottom - 55 * UiScale, 52 * UiScale, 36 * UiScale);
    private RectangleF NextBox => new(LeftBox.Right - 68 * UiScale, PrevBox.Y, 52 * UiScale, 36 * UiScale);
    private RectangleF SetupBox => new(RightBox.Left + 24 * UiScale, RightBox.Bottom - 46 * UiScale, 130 * UiScale, 28 * UiScale);
    private RectangleF DiscoveryPrevBox=>new(BodyBox.Left,BodyBox.Bottom-26*UiScale,34*UiScale,26*UiScale);
    private RectangleF DiscoveryNextBox=>new(BodyBox.Left+100*UiScale,DiscoveryPrevBox.Y,34*UiScale,26*UiScale);
    internal RectangleF DiscoveryRowBox(int index)=>new(BodyBox.Left,BodyBox.Top+30*UiScale+index*72*UiScale,BodyBox.Width,64*UiScale);
    internal RectangleF DiscoveryAddBox(int index) { var row=DiscoveryRowBox(index); return new(row.Right-78*UiScale,row.Y+14*UiScale,68*UiScale,36*UiScale); }
    private bool CanAdd(SpsDiscoveryRow row)=>!row.Added && (Document.Holes.Count<64 || Document.Holes.Any(h=>h.Id==row.Id));
    internal RectangleF ToyBox(int visibleIndex)
    {
        var available = LeftBox.Height - 206 * UiScale; var height = Math.Min(56 * UiScale, (available - 8 * UiScale * 5) / 6);
        return new(LeftBox.Left + 16 * UiScale, LeftBox.Top + 139 * UiScale + visibleIndex * (height + 8 * UiScale), LeftBox.Width - 32 * UiScale, height);
    }
    internal PointF Port(int index) { var box = ToyBox(index); return new(box.Right - 20 * UiScale, box.Top + box.Height * .36f); }
    internal PointF HolePoint(SpsHole hole) => new(BodyBox.Left + hole.X * BodyBox.Width, BodyBox.Top + hole.Y * BodyBox.Height);
    private PointF HolePoint(string id)
    {
        var hole = Document.Holes.First(h => h.Id == id);
        if (moving == id && dragged) return new(BodyBox.Left + Math.Clamp(original.X + (pointer.X - start.X) / BodyBox.Width, .03f, .97f) * BodyBox.Width,
            BodyBox.Top + Math.Clamp(original.Y + (pointer.Y - start.Y) / BodyBox.Height, .03f, .97f) * BodyBox.Height);
        return HolePoint(hole);
    }
    public SpsBoard()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.StandardDoubleClick, true);
        BackColor = Background; Dock = DockStyle.Fill; TabStop = true;
        AccessibleName = "SPS body map"; AccessibleDescription = "Drag toy functions to holes. Hold and drag holes to move them. Double-click a hole or click a connection line or its gear for options.";
        Controls.Add(AutomaticPanel); AutomaticPanel.Visible=false;
        AutomaticPanel.SourcesChanged+=sources=>AutomaticSourcesRequested?.Invoke(sources);
    }
    protected override void Dispose(bool disposing) { if(disposing) connectionHelp.Dispose(); base.Dispose(disposing); }
    public void SetDiscoveryMode(bool enabled) { CancelDrag(); ClearHover(); DiscoveryMode=enabled; RefreshAutomaticView(); Invalidate(); }
    internal void RefreshAutomaticView()
    {
        AutomaticPanel.Visible=Document.AllToysToAllHoles && !DiscoveryMode;
        AutomaticPanel.Bounds=Rectangle.Round(new(BodyBox.X+18*UiScale,BodyBox.Y,Math.Max(1,BodyBox.Width-18*UiScale),BodyBox.Height));
        if(AutomaticPanel.Visible) AutomaticPanel.Synchronize(Document,h=>HoleEnabled?.Invoke(h),h=>HoleActive?.Invoke(h)==true);
    }
    private static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var p = new GraphicsPath(); var d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X,r.Y,d,d,180,90); p.AddArc(r.Right-d,r.Y,d,d,270,90); p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90); p.AddArc(r.X,r.Bottom-d,d,d,90,90); p.CloseFigure(); return p;
    }
    private void Fill(Graphics g, RectangleF r, Color color, float radius = 10)
    { using var p = Rounded(r, radius * UiScale); using var brush = new SolidBrush(color); g.FillPath(brush, p); }
    private void PaintText(Graphics g, string text, RectangleF rect, float size = 12, Color? color = null, bool bold = false, bool center = false)
    {
        using var font = new Font("Segoe UI", Math.Max(9, size * UiScale), bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color ?? White); using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap,
            Alignment = center ? StringAlignment.Center : StringAlignment.Near, LineAlignment = StringAlignment.Center };
        g.DrawString(text, font, brush, rect, format);
    }
    private void Button(Graphics g, RectangleF box, string title, Color? color = null, bool enabled = true) { Fill(g,box,color ?? Input,10); PaintText(g,title,box,13,enabled ? White : Muted,true,true); }
    internal GraphicsPath WirePath(SpsWire wire)
    {
        var index = Rows.Skip(Page * 6).Take(6).ToList().FindIndex(r => r.Target == wire.Target); var p = new GraphicsPath(); if (index < 0) return p;
        var a = Port(index); var b = HolePoint(wire.HoleId);
        p.AddBezier(a, new(a.X + 100 * UiScale, a.Y), new(b.X - 90 * UiScale, b.Y), b); return p;
    }
    internal RectangleF WireGearBox(SpsWire wire)
    {
        if(Document.AllToysToAllHoles || DiscoveryMode) return RectangleF.Empty;
        using var path=WirePath(wire); if(path.PointCount!=4) return RectangleF.Empty;
        var points=path.PathPoints;
        var center=new PointF((points[0].X+3*points[1].X+3*points[2].X+points[3].X)/8,
            (points[0].Y+3*points[1].Y+3*points[2].Y+points[3].Y)/8);
        var radius=14*UiScale;
        return new(center.X-radius,center.Y-radius,2*radius,2*radius);
    }
    private void DrawWireGear(Graphics g,SpsWire wire)
    {
        var box=WireGearBox(wire); if(box.IsEmpty) return;
        DrawGear(g,box,Rows.FirstOrDefault(r=>r.Target==wire.Target)?.Color??Muted,HoveredWire==wire.Id,wire.Enabled);
    }
    private void DrawGear(Graphics g,RectangleF box,Color color,bool hover,bool enabled=true)
    {
        var center=new PointF(box.X+box.Width/2,box.Y+box.Height/2);
        using var background=new SolidBrush(hover ? Input : Surface); g.FillEllipse(background,box);
        using var ring=new Pen(Color.FromArgb(hover ? 230 : 140,color),(hover ? 1.8f : 1.2f)*UiScale); g.DrawEllipse(ring,box);
        var teeth=new PointF[32];
        for(var i=0;i<teeth.Length;i++)
        {
            var angle=i*MathF.PI/16; var radius=(i%4 is 1 or 2 ? 9.5f : 7.3f)*UiScale;
            teeth[i]=new(center.X+MathF.Cos(angle)*radius,center.Y+MathF.Sin(angle)*radius);
        }
        using var gear=new SolidBrush(enabled ? White : Muted); g.FillPolygon(gear,teeth);
        g.FillEllipse(background,center.X-3.4f*UiScale,center.Y-3.4f*UiScale,6.8f*UiScale,6.8f*UiScale);
    }
    private SpsHole? HitHole(Point point) => DiscoveryMode || Document.AllToysToAllHoles ? null : Document.Holes.LastOrDefault(h => Distance(point, HolePoint(h.Id)) <= 19 * UiScale);
    private static float Distance(PointF a, PointF b) => MathF.Sqrt((a.X-b.X)*(a.X-b.X)+(a.Y-b.Y)*(a.Y-b.Y));
    private string? HitWire(Point point)
    {
        if (Document.AllToysToAllHoles || DiscoveryMode) return null;
        // Gears are painted over all paths, so their visible target wins at a crossing.
        foreach(var wire in Document.Wires.AsEnumerable().Reverse()) if(WireGearBox(wire).Contains(point)) return wire.Id;
        foreach (var wire in Document.Wires.AsEnumerable().Reverse())
        {
            using var path = WirePath(wire); if (path.PointCount == 0) continue; path.Flatten(null, .5f);
            var points = path.PathPoints;
            for (var i=1;i<points.Length;i++)
            {
                var a=points[i-1]; var b=points[i]; var dx=b.X-a.X; var dy=b.Y-a.Y; var length=dx*dx+dy*dy;
                var t=length<=0 ? 0 : Math.Clamp(((point.X-a.X)*dx+(point.Y-a.Y)*dy)/length,0,1);
                if(Distance(point,new(a.X+t*dx,a.Y+t*dy))<=6*UiScale) return wire.Id;
            }
        }
        return null;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e); var g=e.Graphics; g.SmoothingMode=SmoothingMode.AntiAlias; g.TextRenderingHint=TextRenderingHint.AntiAliasGridFit;
        Fill(g,LeftBox,LeftSurface,22); Fill(g,RightBox,Surface,22);
        PaintText(g,"TOY FUNCTIONS",new(LeftBox.Left+21*UiScale,LeftBox.Top+18*UiScale,LeftBox.Width-42*UiScale,22*UiScale),17,null,true);
        PaintText(g,DiscoveryMode ? "Live toy status" : Document.AllToysToAllHoles ? "All functions follow permitted contacts" : "Drag a function to a hole",new(LeftBox.Left+21*UiScale,LeftBox.Top+44*UiScale,LeftBox.Width-42*UiScale,18*UiScale),12,Muted);
        Button(g,ClearBox,"Clear links",Document.AllToysToAllHoles ? LeftSurface : Input,!Document.AllToysToAllHoles); Button(g,StopBox,"STOP ALL",Color.FromArgb(110,65,68));
        PaintText(g,DiscoveryMode ? "SPS discovery" : Document.AllToysToAllHoles?"Automatic SPS":"SPS body map",new(RightBox.Left+28*UiScale,RightBox.Top+23*UiScale,Math.Max(1,DiscoveryBox.Left-RightBox.Left-42*UiScale),30*UiScale),22,null,true);
        Button(g,DiscoveryBox,DiscoveryMode ? "Discovery ON" : "Discovery",DiscoveryMode ? Color.FromArgb(44,85,80) : Input);
        Button(g,AllToysBox,"All toys → all holes: " + (Document.AllToysToAllHoles ? "ON" : "OFF"),Document.AllToysToAllHoles ? Color.FromArgb(44,85,80) : Input);
        PaintText(g,DiscoveryMode ? "Find plugs and sockets" : Document.AllToysToAllHoles ? "Hole options" : Document.HolesLocked ? "Holes locked" : "Drag holes · double-click for options",
            new(AllToysBox.Right+16*UiScale,AllToysBox.Top,RightBox.Right-AllToysBox.Right-30*UiScale,AllToysBox.Height),12,Muted);
        if(!Document.AllToysToAllHoles) Button(g,LockBox,Document.HolesLocked ? "Locked" : "Lock holes",Document.HolesLocked ? Color.FromArgb(44,85,80) : Input,!DiscoveryMode);
        if(!DiscoveryMode && !Document.AllToysToAllHoles) DrawHuman(g);
        var visible=Rows.Skip(Page*6).Take(6).ToArray();
        for(var i=0;i<visible.Length;i++)
        {
            var row=visible[i]; var box=ToyBox(i); Fill(g,box,Surface,10);
            using var accent=new SolidBrush(row.Color); g.FillRectangle(accent,box.X,box.Y+12*UiScale,3*UiScale,box.Height-18*UiScale);
            var compact=box.Width<180*UiScale;
            PaintText(g,row.Toy,new(box.X+16*UiScale,box.Y+2*UiScale,box.Width-(compact?104:48)*UiScale,16*UiScale),11,Muted);
            PaintText(g,row.Function,new(box.X+16*UiScale,box.Y+18*UiScale,box.Width-(compact?48:126)*UiScale,22*UiScale),16,row.Color,true);
            PaintText(g,$"{row.Percent}%",new(box.Right-94*UiScale,box.Y+(compact?0:18)*UiScale,56*UiScale,22*UiScale),compact?12:16,row.Color,true,true);
            PaintText(g,row.Status,new(box.X+16*UiScale,box.Bottom-18*UiScale,box.Width-30*UiScale,16*UiScale),10,Muted);
            var track=new RectangleF(box.X+16*UiScale,box.Bottom-3*UiScale,box.Width-32*UiScale,2*UiScale);
            using var trackFill=new SolidBrush(Input); g.FillRectangle(trackFill,track);
            if(row.Connected && row.Percent>0) g.FillRectangle(accent,track.X,track.Y,track.Width*Math.Clamp(row.Percent,0,100)/100f,track.Height);
            var p=Port(i); using var portFill=new SolidBrush(row.Connected && !Document.AllToysToAllHoles ? row.Color : Input); g.FillEllipse(portFill,p.X-7*UiScale,p.Y-7*UiScale,14*UiScale,14*UiScale);
            using var ring=new Pen(Color.FromArgb(110,row.Color),1.5f*UiScale); g.DrawEllipse(ring,p.X-9*UiScale,p.Y-9*UiScale,18*UiScale,18*UiScale);
        }
        if(!DiscoveryMode && !Document.AllToysToAllHoles) foreach(var wire in Document.Wires)
        {
            using var path=WirePath(wire); if(path.PointCount==0) continue;
            var row=Rows.FirstOrDefault(r=>r.Target==wire.Target); var color=Document.AllToysToAllHoles ? Color.FromArgb(125,128,134) : row?.Color ?? Muted;
            using var shadow=new Pen(Color.FromArgb(80,0,0,0),5*UiScale); g.DrawPath(shadow,path);
            using var pen=new Pen(Color.FromArgb(Document.AllToysToAllHoles ? 100 : wire.Enabled ? 190 : 65,color),2*UiScale); if(!wire.Enabled || Document.AllToysToAllHoles) pen.DashStyle=DashStyle.Dash; g.DrawPath(pen,path);
        }
        if(connecting is not null)
        {
            var index=Array.FindIndex(visible,r=>r.Target==connecting); if(index>=0)
            { var a=Port(index); using var pen=new Pen(visible[index].Color,2.5f*UiScale) { DashStyle=DashStyle.Dash }; g.DrawBezier(pen,a,new(a.X+100*UiScale,a.Y),new(pointer.X-60*UiScale,pointer.Y),pointer); }
        }
        if(!DiscoveryMode && !Document.AllToysToAllHoles) foreach(var hole in Document.Holes)
        {
            var p=HolePoint(hole.Id); var level=HoleLevel?.Invoke(hole) ?? 0; var hover=connecting is not null && HitHole(pointer)?.Id==hole.Id;
            var active=HoleEnabled?.Invoke(hole)??(level>0 || HoleActive?.Invoke(hole)==true);
            using var fill=new SolidBrush(Surface); g.FillEllipse(fill,p.X-13*UiScale,p.Y-13*UiScale,26*UiScale,26*UiScale);
            using var ring=new Pen(hover || active ? Accent : Color.FromArgb(210,217,226),(hover?3:2)*UiScale); g.DrawEllipse(ring,p.X-13*UiScale,p.Y-13*UiScale,26*UiScale,26*UiScale);
            using var core=new SolidBrush(active ? Accent : Color.FromArgb(90,98,110)); g.FillEllipse(core,p.X-4*UiScale,p.Y-4*UiScale,8*UiScale,8*UiScale);
        }
        foreach(var wire in Document.Wires) DrawWireGear(g,wire);
        if(Document.AllToysToAllHoles && !DiscoveryMode) for(var i=0;i<visible.Length;i++) DrawGear(g,ToyGearBox(i),visible[i].Color,ToyGearBox(i).Contains(pointer));
        // Paint just the hovered label last, so other circles and wires cannot
        // obscure it. Names stay hidden during ordinary viewing.
        if(Document.Holes.FirstOrDefault(h=>h.Id==HoveredHole) is { } named)
        {
            var p=HolePoint(named.Id); var labelWidth=280*UiScale; var left=p.X+21*UiScale;
            if(left+labelWidth>RightBox.Right-14*UiScale) left=p.X-21*UiScale-labelWidth;
            left=Math.Clamp(left,RightBox.Left+14*UiScale,RightBox.Right-labelWidth-14*UiScale);
            Fill(g,new(left,p.Y-18*UiScale,labelWidth,36*UiScale),Color.FromArgb(225,Surface),7);
            PaintText(g,named.Name,new(left+8*UiScale,p.Y-17*UiScale,labelWidth-16*UiScale,18*UiScale),12,null,true);
            PaintText(g,HoleState?.Invoke(named)??named.Kind,new(left+8*UiScale,p.Y+1*UiScale,labelWidth-16*UiScale,15*UiScale),10,Muted);
        }
        if(!DiscoveryMode && Document.Holes.Count==0) PaintText(g,"Use Discovery to add plugs and sockets",new(BodyBox.X,BodyBox.Bottom-26*UiScale,BodyBox.Width,25*UiScale),13,Muted,false,true);
        if(DiscoveryMode) DrawDiscoveries(g);
        Button(g,PrevBox,"‹"); Button(g,NextBox,"›"); PaintText(g,$"{Page+1} / {PageCount}",new(PrevBox.Right,PrevBox.Y,NextBox.Left-PrevBox.Right,PrevBox.Height),12,Muted,false,true);
        PaintText(g,$"{Rows.Select(r=>r.Target.DeviceKey).Distinct().Count()} toys · {Rows.Count} functions",new(LeftBox.Left+24*UiScale,LeftBox.Bottom-18*UiScale,LeftBox.Width-40*UiScale,16*UiScale),10,Muted);
        PaintText(g,"Input setup…",SetupBox,12,Muted);
        var badge=StatusBox; Fill(g,badge,Input,10);
        if(!DiscoveryMode && !Document.AllToysToAllHoles)
        {
            var hint=ManualHintBox;
            PaintText(g,"Hold + drag to move",new(hint.X,hint.Y+3*UiScale,hint.Width,18*UiScale),11,Muted);
            PaintText(g,"Double-click for options",new(hint.X,hint.Y+22*UiScale,hint.Width,18*UiScale),11,Muted);
        }
        PaintText(g,Status,new(badge.X+12*UiScale,badge.Y+3*UiScale,badge.Width-24*UiScale,21*UiScale),13,Status=="SPS CONNECTED" ? Accent : Muted,true);
        PaintText(g,StatusDetail,new(badge.X+12*UiScale,badge.Y+23*UiScale,badge.Width-24*UiScale,17*UiScale),10,Muted);
    }
    private void DrawDiscoveries(Graphics g)
    {
        PaintText(g,$"{Discoveries.Count} possible plugs / sockets · Add to body map",new(BodyBox.X,BodyBox.Y-2*UiScale,BodyBox.Width,22*UiScale),12,Muted);
        var visible=Discoveries.Skip(DiscoveryPage*DiscoveryPageSize).Take(DiscoveryPageSize).ToArray();
        for(var i=0;i<visible.Length;i++)
        {
            var item=visible[i]; var row=DiscoveryRowBox(i); Fill(g,row,Input,8);
            PaintText(g,item.Name,new(row.X+12*UiScale,row.Y+4*UiScale,row.Width-108*UiScale,20*UiScale),15,null,true);
            PaintText(g,$"{item.Kind} · {item.Inputs} inputs · "+(item.Supported ? "Recognized SPS" : "Input setup needed"),
                new(row.X+12*UiScale,row.Y+25*UiScale,row.Width-108*UiScale,16*UiScale),11,item.Supported ? Accent : Muted);
            PaintText(g,item.Stem[19..],new(row.X+12*UiScale,row.Y+43*UiScale,row.Width-108*UiScale,17*UiScale),10,Muted);
            var enabled=CanAdd(item);
            Button(g,DiscoveryAddBox(i),item.Added ? "Added" : "Add",enabled ? Color.FromArgb(16,120,95) : Surface,enabled);
        }
        if(visible.Length==0) PaintText(g,"Waiting for SPS plugs or sockets from your avatar",new(BodyBox.Left,BodyBox.Top+48*UiScale,BodyBox.Width,30*UiScale),14,Muted,false,true);
        if(DiscoveryPageCount>1)
        {
            Button(g,DiscoveryPrevBox,"‹"); Button(g,DiscoveryNextBox,"›");
            PaintText(g,$"{DiscoveryPage+1}/{DiscoveryPageCount}",new(DiscoveryPrevBox.Right,DiscoveryPrevBox.Y,DiscoveryNextBox.Left-DiscoveryPrevBox.Right,DiscoveryPrevBox.Height),11,Muted,false,true);
        }
    }
    private void DrawHuman(Graphics g)
    {
        var height=BodyBox.Height; var scale=height/410f; var x=BodyBox.Left+BodyBox.Width/2-80*scale; var y=BodyBox.Top;
        using var path=new GraphicsPath();
        path.AddBezier(70,72,58,67,54,50,54,32); path.AddBezier(54,32,54,12,65,4,80,4); path.AddBezier(80,4,95,4,106,12,106,32);
        path.AddBezier(106,32,106,50,102,67,90,72); path.AddLine(90,72,90,88);
        path.AddBezier(90,88,108,93,125,93,136,106); path.AddLine(136,106,160,238); path.AddBezier(160,238,164,254,159,268,150,263);
        path.AddLine(150,263,140,243); path.AddLine(140,243,119,146); path.AddLine(119,146,112,211);
        path.AddBezier(112,211,117,248,111,272,106,286); path.AddLine(106,286,105,375);
        path.AddBezier(105,375,118,390,123,399,116,404); path.AddBezier(116,404,100,412,86,404,85,381);
        path.AddLine(85,381,82,294); path.AddLine(82,294,78,294); path.AddLine(78,294,75,381);
        path.AddBezier(75,381,74,404,60,412,44,404); path.AddBezier(44,404,37,399,42,390,55,375);
        path.AddLine(55,375,54,286); path.AddBezier(54,286,49,272,43,248,48,211); path.AddLine(48,211,41,146);
        path.AddLine(41,146,20,243); path.AddLine(20,243,10,263); path.AddBezier(10,263,1,268,-4,254,0,238);
        path.AddLine(0,238,24,106); path.AddBezier(24,106,35,93,52,93,70,88); path.AddLine(70,88,70,72); path.CloseFigure();
        using var transform=new Matrix(scale,0,0,scale,x,y); path.Transform(transform);
        using var fill=new SolidBrush(Color.FromArgb(46,49,54)); using var outline=new Pen(Color.FromArgb(119,127,140),1.6f*UiScale);
        g.FillPath(fill,path); g.DrawPath(outline,path);
        using var interior=new Pen(Color.FromArgb(79,85,95),1.2f*UiScale);
        g.DrawBezier(interior,new PointF(x+53*scale,y+122*scale),new PointF(x+65*scale,y+139*scale),new PointF(x+95*scale,y+139*scale),new PointF(x+107*scale,y+122*scale));
        g.DrawBezier(interior,new PointF(x+50*scale,y+230*scale),new PointF(x+68*scale,y+242*scale),new PointF(x+92*scale,y+242*scale),new PointF(x+110*scale,y+230*scale));
    }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e); if(e.Button!=MouseButtons.Left) return; Focus(); start=pointer=e.Location; dragged=false; pickedWire=null;
        if(DiscoveryBox.Contains(e.Location)) { DiscoveryRequested?.Invoke(); return; }
        if(LockBox.Contains(e.Location) && !Document.AllToysToAllHoles) { if(!DiscoveryMode) LockRequested?.Invoke(); return; }
        if(AllToysBox.Contains(e.Location)) { CancelDrag(); AllToysRequested?.Invoke(); return; }
        if(StopBox.Contains(e.Location)) { StopRequested?.Invoke(); return; }
        if(ClearBox.Contains(e.Location)) { if(!Document.AllToysToAllHoles) ClearRequested?.Invoke(); return; }
        if(SetupBox.Contains(e.Location)) { SetupRequested?.Invoke(); return; }
        if(PrevBox.Contains(e.Location) || NextBox.Contains(e.Location)) { CancelDrag(); Page=Math.Clamp(Page+(NextBox.Contains(e.Location)?1:-1),0,PageCount-1); Invalidate(); return; }
        if(DiscoveryMode)
        {
            if(DiscoveryPrevBox.Contains(e.Location) || DiscoveryNextBox.Contains(e.Location))
            { discoveryPage=Math.Clamp(DiscoveryPage+(DiscoveryNextBox.Contains(e.Location)?1:-1),0,DiscoveryPageCount-1); Invalidate(); return; }
            var visible=Discoveries.Skip(DiscoveryPage*DiscoveryPageSize).Take(DiscoveryPageSize).ToArray();
            for(var i=0;i<visible.Length;i++) if(DiscoveryAddBox(i).Contains(e.Location) && CanAdd(visible[i]))
            { DiscoveryAddRequested?.Invoke(visible[i].Id); return; }
            return;
        }
        if(Document.AllToysToAllHoles)
        {
            for(var i=0;i<Rows.Skip(Page*6).Take(6).Count();i++) if(ToyGearBox(i).Contains(e.Location)) { ToyOptionsRequested?.Invoke(Rows[Page*6+i].Target); return; }
            return;
        }
        if(HitHole(e.Location) is { } hole)
        {
            if(e.Clicks>=2) { CancelDrag(); HoleOptionsRequested?.Invoke(hole.Id); return; }
            if(!Document.HolesLocked) { moving=hole.Id; original=new(hole.X,hole.Y); Capture=true; }
            return;
        }
        if(!Document.AllToysToAllHoles)
            for(var i=0;i<Rows.Skip(Page*6).Take(6).Count();i++) if(ToyBox(i).Contains(e.Location)) { connecting=Rows[Page*6+i].Target; Capture=true; Invalidate(); return; }
        pickedWire=HitWire(e.Location);
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e); pointer=e.Location; if(Dragging) { dragged |= Distance(start,e.Location)>4; Invalidate(); }
        var hovered=HitHole(e.Location)?.Id; if(hovered!=HoveredHole) { HoveredHole=hovered; Invalidate(); }
        var wire=hovered is null && !Dragging ? HitWire(e.Location) : null;
        if(wire!=HoveredWire) { HoveredWire=wire; connectionHelp.SetToolTip(this,wire is null ? null : "Click the line or gear for connection options"); Invalidate(); }
        var add=DiscoveryMode && Enumerable.Range(0,Discoveries.Skip(DiscoveryPage*DiscoveryPageSize).Take(DiscoveryPageSize).Count())
            .Any(i=>DiscoveryAddBox(i).Contains(e.Location) && CanAdd(Discoveries[DiscoveryPage*DiscoveryPageSize+i]));
        var gear=Document.AllToysToAllHoles && !DiscoveryMode && Enumerable.Range(0,Rows.Skip(Page*6).Take(6).Count()).Any(i=>ToyGearBox(i).Contains(e.Location));
        if(gear) connectionHelp.SetToolTip(this,"Click for this toy function's SPS options");
        Cursor=Dragging || hovered is not null || wire is not null || add || gear || DiscoveryBox.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
    }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); ClearHover(); Invalidate(); }
    private void ClearHover() { HoveredHole=null; HoveredWire=null; connectionHelp.SetToolTip(this,null); }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e); if(e.Button!=MouseButtons.Left) return; pointer=e.Location;
        var move=moving; var toy=connecting; var wire=pickedWire; var wasDragged=dragged; var hit=HitHole(e.Location); CancelDrag();
        if(move is not null && wasDragged && !Document.HolesLocked) HoleMoved?.Invoke(move,Math.Clamp(original.X+(e.X-start.X)/BodyBox.Width,.03f,.97f),Math.Clamp(original.Y+(e.Y-start.Y)/BodyBox.Height,.03f,.97f));
        else if(toy is not null && hit is not null) ConnectRequested?.Invoke(toy,hit.Id);
        else if(wire is not null && Distance(start,e.Location)<5) WireOptionsRequested?.Invoke(wire);
    }
    private void CancelDrag() { moving=null; connecting=null; pickedWire=null; dragged=false; Capture=false; Invalidate(); }
    protected override void OnMouseCaptureChanged(EventArgs e) { base.OnMouseCaptureChanged(e); if(!Capture && Dragging) CancelDrag(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if(!Visible) { ClearHover(); CancelDrag(); } }
    protected override void OnKeyDown(KeyEventArgs e) { base.OnKeyDown(e); if(e.KeyCode==Keys.Escape) { CancelDrag(); e.Handled=true; } }
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        if(DiscoveryMode && RightBox.Contains(e.Location))
        { if(e is HandledMouseEventArgs handled) handled.Handled=true; discoveryPage=Math.Clamp(DiscoveryPage-Math.Sign(e.Delta),0,DiscoveryPageCount-1); Invalidate(); return; }
        base.OnMouseWheel(e);
    }
    protected override void OnResize(EventArgs e) { base.OnResize(e); ClearHover(); if(Dragging) CancelDrag(); Page=Math.Min(Page,PageCount-1); RefreshAutomaticView(); }
}
