using System.Drawing.Drawing2D;
using System.Numerics;
using ShockWave.ShaderLab;

namespace ShockWave.Overlay;

internal sealed record DesktopFunction(float Level,bool Selected,bool Connected,bool Looping,Color Color,PatternClip? Clip,double Started);
internal sealed record DesktopSample(double Time,float Level,bool Valid);
internal sealed record DesktopPanelFrame(ToyControlLabels.Frame Labels,DesktopFunction[] Rows,DesktopFunction[] Functions,
    DesktopSample[] History,bool Shockers,bool Recording,bool Touching,Vector2 Touch,Color LiveColor,int SelectedCount,
    int PageCount,int Hover,PatternClip? Pattern,double Phase,double Now,int Intensity,int Duration,bool Armed,bool Ready,float Remaining,string Cookie);

// Recreates the original desktop geometry/palette with ordinary drawing calls.
// The worker receives immutable display values, never the live control session.
internal static class DesktopPanelView
{
    public static readonly Size Size=new(1240,608);
    public static readonly Color Background=Color.FromArgb(30,31,34),Sidebar=Color.FromArgb(17,18,20),
        Panel=Color.FromArgb(43,45,49),Muted=Color.FromArgb(56,58,64),Accent=Color.FromArgb(16,163,127),White=Color.FromArgb(242,243,245);
    public static string InputCookie(ToyControlSession session)=>System.Text.Json.JsonSerializer.Serialize(new {
        session.ShockerPage,TestMode=session.ShockerPage ? session.Shockers.IsTestMode() : session.TestMode,session.InputModeRevision,session.DisplayModel.Page,session.Model.RecordArmed,
        Rows=session.DisplayModel.Visible.Select(r=>new {r.Toy.Id,Function=r.Function.Id,r.Function.Generation,r.Connected}),
        Selected=session.DisplayModel.Functions.Where(r=>r.Selected).Select(r=>new {r.Toy.Id,Function=r.Function.Id}),
        Shock=session.ShockerPage ? new {session.Shockers.Intensity,session.Shockers.DurationMs,session.Shockers.Armed,session.Shockers.ArmedFromDesktop,session.Shockers.Ready} : null
    });
    public static DesktopPanelFrame Capture(ToyControlSession session,int hover,IEnumerable<DesktopSample> history)
    {
        var model=session.DisplayModel;
        static DesktopFunction Row(FunctionBinding r)=>new(r.Level,r.Selected,r.Connected,r.Function.LoopRunning,r.Function.Color,r.Function.Pattern,r.Function.LoopStarted);
        return new(ToyControlLabels.Capture(session,showInputMode:false,showPageSwitch:false,desktopStyle:true),model.Visible.Select(Row).ToArray(),model.Functions.Select(Row).ToArray(),history.ToArray(),
            session.ShockerPage,model.RecordArmed || model.Recording,model.Touching,model.TouchPosition,model.LiveColor,model.SelectedCount,model.PageCount,
            hover,model.DisplayPattern,model.DisplayPhase,model.Now,session.Shockers.Intensity,session.Shockers.DurationMs,
            session.Shockers.ArmedFromDesktop,session.Shockers.Ready,session.Shockers.Remaining(model.Now),InputCookie(session));
    }
    public static Bitmap Render(DesktopPanelFrame frame)
    {
        var image=new Bitmap(Size.Width,Size.Height,System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            using var g=Graphics.FromImage(image);g.SmoothingMode=SmoothingMode.AntiAlias;g.Clear(Background);g.TranslateTransform(0,-100);
            Fill(g,new(24,105,300,598),18,Sidebar);Fill(g,new(342,105,874,598),18,Panel);
            void Button(RectangleF rect,int id,Color fill)=>Fill(g,rect,10,frame.Hover==id ? Mix(fill,White,.045f) : fill);
            for(var i=0;i<frame.Rows.Length;i++)
            {
                var row=frame.Rows[i];var rect=ToyControlLayout.Row(i);var color=frame.Shockers ? Accent : row.Color;
                var fill=row.Selected ? Mix(Muted,color,.18f) : Panel;
                Button(rect,i+1,row.Connected ? fill : Mix(Color.Black,fill,.65f));
                Fill(g,new(rect.X,rect.Y+12,3,38),1,row.Connected ? color : Mix(Color.Black,color,.7f));
                var center=new PointF(rect.X+243,rect.Y+20);
                using(var brush=new SolidBrush(row.Selected ? color : Mix(Color.FromArgb(64,66,71),color,.25f))) g.FillEllipse(brush,center.X-8,center.Y-8,16,16);
                if(row.Selected) {using var pen=new Pen(Sidebar,2);g.DrawLines(pen,new PointF[]{new(center.X-4,center.Y),new(center.X-1,center.Y+3),new(center.X+4,center.Y-3)});}
                if(row.Level>0) Fill(g,new(rect.X+12,rect.Y+54,244*Math.Clamp(row.Level,0,1),2),1,color);
            }
            if(frame.PageCount>1) {Button(ShockWave.ShaderLab.PanelLayout.Previous,15,Muted);Button(ShockWave.ShaderLab.PanelLayout.Next,16,Muted);}
            var openVr=DesktopPanelLayout.OpenVr;
            using(var brush=new SolidBrush(frame.Hover==DesktopPanelLayout.OpenVrHit ? Muted : Color.FromArgb(39,42,48))) g.FillEllipse(brush,openVr);
            DesktopPanelIcons.OpenVr(g,new(openVr.X+12,openVr.Y+12,32,32),White);
            var recenter=DesktopPanelLayout.Recenter;
            using(var brush=new SolidBrush(frame.Hover==DesktopPanelLayout.RecenterHit ? Muted : Color.FromArgb(39,42,48))) g.FillEllipse(brush,recenter);
            DesktopPanelIcons.Recenter(g,new(recenter.X+12,recenter.Y+12,32,32),White);
            var stop=DesktopPanelLayout.Stop;var stopFill=Color.FromArgb(62,43,47);
            using(var brush=new SolidBrush(frame.Hover==13 ? Mix(stopFill,White,.08f) : stopFill)) g.FillEllipse(brush,stop);
            SteamVr.PanelIcons.Draw(g,"stop",new(stop.X+12,stop.Y+12,32,32),Color.FromArgb(255,175,175));
            if(frame.Shockers)
            {
                for(var i=0;i<2;i++)
                {
                    var rect=DesktopPanelLayout.Slider(i);var x=rect.Left+rect.Width/2;
                    var value=i==0 ? frame.Intensity/100f : (frame.Duration-300)/14700f;var y=rect.Bottom-Math.Clamp(value,0,1)*rect.Height;
                    Fill(g,new(x-5,rect.Top,10,rect.Height),5,Muted);if(y<rect.Bottom) Fill(g,new(x-5,y,10,rect.Bottom-y),5,Accent);
                    using var brush=new SolidBrush(White);g.FillEllipse(brush,x-14,y-14,28,28);
                }
                Button(DesktopPanelLayout.Shock,DesktopPanelLayout.ShockHit,frame.Armed && frame.Ready ? Accent : Muted);
                Button(ToyControlLayout.ShockButton(34),34,Muted);Button(ToyControlLayout.ShockButton(35),35,Muted);
                if(frame.Armed) Fill(g,new(810,653,346*frame.Remaining,4),2,Accent);
            }
            else
            {
                var r=DesktopPanelLayout.Record;
                using(var brush=new SolidBrush(frame.Recording ? SteamVr.PanelView.Green : frame.Hover==25 ? Muted : Color.FromArgb(39,42,48))) g.FillEllipse(brush,r);
                SteamVr.PanelIcons.Draw(g,"record",new(r.X+12,r.Y+12,32,32),frame.Recording ? Color.FromArgb(25,27,31) : White);
                if(frame.SelectedCount>0) Graph(g,frame);
            }
            using var labels=frame.Labels.Draw();g.DrawImageUnscaled(labels,0,0);HoverLabel(g,frame);return image;
        }
        catch {image.Dispose();throw;}
    }
    private static void HoverLabel(Graphics g,DesktopPanelFrame frame)
    {
        (RectangleF Bounds,string Text)? hint=frame.Hover switch {
            DesktopPanelLayout.OpenVrHit=>(DesktopPanelLayout.OpenVr,"Open in VR"),
            DesktopPanelLayout.RecenterHit=>(DesktopPanelLayout.Recenter,"Recenter"),
            13=>(DesktopPanelLayout.Stop,"Stop all"),
            25 when !frame.Shockers=>(DesktopPanelLayout.Record,"Record"),
            _=>null
        };
        if(hint is not { } label) return;
        using var font=new Font("Segoe UI",12,FontStyle.Regular,GraphicsUnit.Pixel);
        using var brush=new SolidBrush(White);
        using var format=new StringFormat {Alignment=StringAlignment.Center,LineAlignment=StringAlignment.Far,FormatFlags=StringFormatFlags.NoWrap};
        g.TextRenderingHint=System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.DrawString(label.Text,font,brush,new PointF(label.Bounds.Left+label.Bounds.Width/2,label.Bounds.Top-5),format);
    }
    private static void Graph(Graphics g,DesktopPanelFrame frame)
    {
        var r=ShockWave.ShaderLab.PanelLayout.Graph;Fill(g,r,8,Sidebar);
        var saved=g.Save();using(var clip=Rounded(r,8)) g.SetClip(clip);
        using(var grid=new Pen(Muted,1))
        {
            for(var x=0;x<=8;x++) g.DrawLine(grid,r.X+x*r.Width/8,r.Top,r.X+x*r.Width/8,r.Bottom);
            for(var y=0;y<=4;y++) g.DrawLine(grid,r.Left,r.Y+y*r.Height/4,r.Right,r.Y+y*r.Height/4);
        }
        var window=frame.Pattern?.Duration ?? 8;var phase=frame.Pattern is null ? 1 : frame.Phase;var start=frame.Now-phase*window;
        foreach(var f in frame.Functions.Where(f=>f.Connected && !f.Selected))
        {
            if(f.Looping && f.Clip is { } clip)
            {
                var points=Enumerable.Range(0,240).Select(i=>new PointF(r.X+i*r.Width/239,r.Bottom-clip.LoopAt(start+i*window/239-f.Started)*r.Height)).ToArray();
                using var pen=new Pen(Color.FromArgb(174,f.Color),2.3f);g.DrawLines(pen,points);
            }
            else {using var pen=new Pen(Color.FromArgb(150,f.Color),1.8f);g.DrawLine(pen,r.Left,r.Bottom-f.Level*r.Height,r.Right,r.Bottom-f.Level*r.Height);}
        }
        PointF[] curve=frame.Pattern is { } pattern ? Enumerable.Range(0,240).Select(i=>new PointF(r.X+i*r.Width/239,r.Bottom-pattern.At(i*pattern.Duration/239)*r.Height)).ToArray()
            : frame.History.Where(s=>s.Valid && s.Time>=frame.Now-8).Select(s=>new PointF(r.Right-(float)(frame.Now-s.Time)*r.Width/8,r.Bottom-s.Level*r.Height)).ToArray();
        if(curve.Length>1)
        {
            using var fill=new SolidBrush(Color.FromArgb(22,frame.LiveColor));g.FillPolygon(fill,[new(curve[0].X,r.Bottom),..curve,new(curve[^1].X,r.Bottom)]);
            using var glow=new Pen(Color.FromArgb(28,frame.LiveColor),7);g.DrawLines(glow,curve);
            using var pen=new Pen(frame.LiveColor,3.2f);g.DrawLines(pen,curve);
        }
        if(frame.Pattern is not null)
        {
            var x=r.Left+(float)frame.Phase*r.Width;using var pen=new Pen(Color.FromArgb(70,White),1);g.DrawLine(pen,x,r.Top,x,r.Bottom);
        }
        if(frame.Touching)
        {
            var x=r.Left+Math.Clamp(frame.Touch.X,0,1)*r.Width;var y=r.Bottom-Math.Clamp(frame.Touch.Y,0,1)*r.Height;
            using var brush=new SolidBrush(frame.LiveColor);g.FillEllipse(brush,x-7,y-7,14,14);
        }
        g.Restore(saved);
    }
    private static Color Mix(Color a,Color b,float value)=>Color.FromArgb((int)(a.R+(b.R-a.R)*value),(int)(a.G+(b.G-a.G)*value),(int)(a.B+(b.B-a.B)*value));
    private static GraphicsPath Rounded(RectangleF r,float radius)
    {
        var d=Math.Min(radius*2,Math.Min(r.Width,r.Height));var p=new GraphicsPath();
        p.AddArc(r.X,r.Y,d,d,180,90);p.AddArc(r.Right-d,r.Y,d,d,270,90);p.AddArc(r.Right-d,r.Bottom-d,d,d,0,90);p.AddArc(r.X,r.Bottom-d,d,d,90,90);p.CloseFigure();return p;
    }
    private static void Fill(Graphics g,RectangleF rect,float radius,Color color)
    {if(rect.Width<=0 || rect.Height<=0) return;using var path=Rounded(rect,radius);using var brush=new SolidBrush(color);g.FillPath(brush,path);}
}
