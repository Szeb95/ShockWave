namespace ShockWave.Overlay;

// Desktop-only controls. Uploaded avatars keep their existing layout/contract.
internal static class DesktopPanelLayout
{
    public static readonly RectangleF OpenVr=new(365,630,56,56),Recenter=new(429,630,56,56),Stop=new(493,630,56,56),Record=new(557,630,56,56);
    public static readonly RectangleF Shock=new(810,344,346,64);
    public static RectangleF Slider(int index)=>new(400+index*160,240,108,326);
    public const int ShockHit=36;
    public const int OpenVrHit=37;
    public const int RecenterHit=38;
    private static bool Circle(RectangleF rect,PointF point)
    {var dx=point.X-(rect.X+28);var dy=point.Y-(rect.Y+28);return dx*dx+dy*dy<=28*28;}
    public static bool IsStop(PointF point)=>Circle(Stop,point);
    public static int Hit(PointF point,ToyControlSession session)
    {
        if(IsStop(point)) return 13;
        if(Circle(OpenVr,point)) return OpenVrHit;
        if(Circle(Recenter,point)) return RecenterHit;
        if(session.ShockerPage && Shock.Contains(point)) return ShockHit;
        if(session.ShockerPage) for(var i=0;i<2;i++) if(Slider(i).Contains(point)) return 30+i;
        if(!session.ShockerPage && Circle(Record,point)) return 25;
        var hit=ToyControlLayout.Hit(point,session);
        return hit is 11 or 13 or 25 or 26 or 28 or 29 or 30 or 31 or 32 or 33 ? 0 : hit;
    }
}
