using System.Drawing.Drawing2D;

namespace ShockWave.Overlay;

internal static class DesktopPanelIcons
{
    public static void Recenter(Graphics graphics,RectangleF rect,Color color)
    {
        var state=graphics.Save();graphics.TranslateTransform(rect.X,rect.Y);graphics.ScaleTransform(rect.Width/24,rect.Height/24);
        using var pen=new Pen(color,1.7f) {StartCap=LineCap.Round,EndCap=LineCap.Round};
        graphics.DrawEllipse(pen,5,5,14,14);
        graphics.DrawLine(pen,12,2,12,7);graphics.DrawLine(pen,12,17,12,22);
        graphics.DrawLine(pen,2,12,7,12);graphics.DrawLine(pen,17,12,22,12);
        using var brush=new SolidBrush(color);graphics.FillEllipse(brush,10.5f,10.5f,3,3);
        graphics.Restore(state);
    }
    public static void OpenVr(Graphics graphics,RectangleF rect,Color color)
    {
        var state=graphics.Save();graphics.TranslateTransform(rect.X,rect.Y);graphics.ScaleTransform(rect.Width/24,rect.Height/24);
        using var pen=new Pen(color,1.9f) {LineJoin=LineJoin.Round,StartCap=LineCap.Round,EndCap=LineCap.Round};
        graphics.DrawLines(pen,new PointF[]{new(4,7),new(20,7),new(21,17),new(15,17),new(12,14),new(9,17),new(3,17),new(4,7)});
        graphics.Restore(state);
    }
}
