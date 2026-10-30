using System.Drawing.Drawing2D;

namespace ShockWave.Overlay;

internal sealed class DesktopPanelCanvas : Control
{
    private Bitmap? frame;
    private RectangleF viewport;
    private string pendingCookie="";
    public string PaintedCookie {get;private set;}="";
    public DesktopPanelCanvas()
    {SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.ResizeRedraw|ControlStyles.Opaque,true);DoubleBuffered=true;}
    public void SetFrame(Bitmap image,string cookie) {var old=frame;frame=image;pendingCookie=cookie;old?.Dispose();Invalidate();}
    public PointF Logical(Point point)=>viewport.Width>0 ? new((point.X-viewport.X)*1240/viewport.Width,100+(point.Y-viewport.Y)*608/viewport.Height) : new(-100,-100);
    public Point Pixel(PointF logical)=>new((int)(viewport.Left+logical.X*viewport.Width/1240),(int)(viewport.Top+(logical.Y-100)*viewport.Height/608));
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(DesktopPanelView.Background);
        var scale=Math.Min(Width/1240f,Height/608f);if(scale<=0) return;
        viewport=new((Width-1240*scale)/2,0,1240*scale,608*scale);
        if(frame is null) return;
        e.Graphics.InterpolationMode=InterpolationMode.HighQualityBicubic;e.Graphics.DrawImage(frame,viewport);PaintedCookie=pendingCookie;
    }
    protected override void OnPaintBackground(PaintEventArgs e) {}
    protected override void Dispose(bool disposing) {if(disposing) {frame?.Dispose();frame=null;}base.Dispose(disposing);}
}
