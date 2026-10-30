#nullable enable
using System.Drawing.Drawing2D;

namespace ShockWave.Overlay.SteamVr;

// Original vector marks, drawn directly with GDI+. No icon font or download.
internal static class PanelIcons
{
    public static string FunctionIcon(string? function)=>function?.Trim().ToLowerInvariant() switch {
        "vibration" or "vibrate"=>"fn-vibration",
        "rotation" or "rotate"=>"fn-rotation",
        "pump" or "inflate" or "inflation"=>"fn-pump",
        "suction"=>"fn-suction",
        "stroke" or "stroker" or "thrust" or "thrusting" or "linear" or "position" or "depth"=>"fn-stroke",
        "oscillate" or "oscillation"=>"fn-oscillation",
        "squeeze" or "constrict" or "constriction" or "compress"=>"fn-squeeze",
        "temperature" or "heat" or "heating"=>"fn-heat",
        "light" or "led"=>"fn-light",
        "spray"=>"fn-spray",
        "fingering"=>"fn-fingering",
        _=>"fn-unknown"
    };
    public static void Draw(Graphics graphics,string icon,RectangleF bounds,Color color)
    {
        var state=graphics.Save();
        graphics.TranslateTransform(bounds.X,bounds.Y);graphics.ScaleTransform(bounds.Width/24,bounds.Height/24);
        using var pen=new Pen(color,1.9f) {StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round};
        using var brush=new SolidBrush(color);
        void Line(float x,float y,float right,float bottom)=>graphics.DrawLine(pen,x,y,right,bottom);
        switch(icon)
        {
            case "vibrate":
            case "fn-vibration":
                graphics.DrawRectangle(pen,9,5,6,14);graphics.DrawLines(pen,new PointF[]{new(5,5),new(2,9),new(5,13),new(2,17)});graphics.DrawLines(pen,new PointF[]{new(19,5),new(22,9),new(19,13),new(22,17)});break;
            case "fn-rotation":
                graphics.DrawArc(pen,4,4,16,16,35,290);Line(20,3,20,9);Line(20,9,14,8);break;
            case "fn-pump":
                graphics.DrawRectangle(pen,8,9,8,12);Line(12,3,12,9);Line(7,3,17,3);Line(3,18,3,8);Line(3,8,1,11);Line(3,8,5,11);break;
            case "fn-suction":
                graphics.DrawLines(pen,new PointF[]{new(3,5),new(21,5),new(16,13),new(8,13),new(3,5)});Line(12,13,12,21);Line(8,18,12,21);Line(16,18,12,21);break;
            case "fn-stroke":
                Line(5,2,19,2);Line(5,22,19,22);Line(12,5,12,19);Line(12,5,8,9);Line(12,5,16,9);Line(12,19,8,15);Line(12,19,16,15);break;
            case "fn-oscillation":
                graphics.DrawArc(pen,3,4,18,17,195,150);Line(3,10,3,15);Line(3,15,8,13);Line(21,10,21,15);Line(21,15,16,13);Line(9,21,15,21);Line(12,13,12,21);break;
            case "fn-squeeze":
                Line(1,12,9,12);Line(9,12,5,8);Line(9,12,5,16);Line(23,12,15,12);Line(15,12,19,8);Line(15,12,19,16);Line(12,5,12,19);break;
            case "fn-heat":
                graphics.DrawArc(pen,9,2,6,7,180,180);Line(9,5,9,15);Line(15,5,15,15);graphics.DrawArc(pen,7,13,10,9,310,280);Line(12,6,12,18);break;
            case "fn-light":
                graphics.DrawEllipse(pen,6,2,12,13);Line(9,16,15,16);Line(9,19,15,19);Line(10,22,14,22);break;
            case "fn-spray":
                graphics.DrawRectangle(pen,3,8,8,8);Line(14,8,22,3);Line(14,12,23,12);Line(14,16,22,21);break;
            case "fn-fingering":
                graphics.DrawLines(pen,new PointF[]{new(7,18),new(7,7),new(10,4),new(13,7),new(13,13),new(17,9),new(20,10),new(20,18),new(13,22),new(7,18)});break;
            case "fn-unknown":
                graphics.DrawEllipse(pen,2,2,20,20);graphics.DrawLines(pen,new PointF[]{new(8,8),new(10,6),new(14,6),new(16,9),new(12,13),new(12,15)});graphics.FillEllipse(brush,11,18,2,2);break;
            case "larger": Line(5,12,19,12);Line(12,5,12,19);break;
            case "smaller": Line(5,12,19,12);break;
            case "resize":
                graphics.DrawRectangle(pen,3,5,18,14);Line(7,15,17,9);Line(17,9,12,9);Line(17,9,17,14);Line(7,15,7,10);Line(7,15,12,15);break;
            case "drop":
                Line(14,4,20,4);Line(20,4,20,10);Line(20,4,13,11);
                Line(10,20,4,20);Line(4,20,4,14);Line(4,20,11,13);break;
            case "record": graphics.FillEllipse(brush,5,5,14,14);break;
            case "layout": Line(6,6,18,18);Line(18,6,6,18);break;
            case "stop": graphics.FillRectangle(brush,5,5,14,14);break;
            case "shock":
                graphics.FillPolygon(brush,new PointF[]{new(14,1),new(4,13),new(10,13),new(8,23),new(21,9),new(14,9)});break;
            case "beep":
                graphics.DrawLines(pen,new PointF[]{new(3,9),new(7,9),new(13,4),new(13,20),new(7,15),new(3,15),new(3,9)});
                graphics.DrawArc(pen,11,6,8,12,-70,140);graphics.DrawArc(pen,11,2,12,20,-70,140);break;
            case "move":
                Line(12,2,12,22);Line(2,12,22,12);
                Line(12,2,9,5);Line(12,2,15,5);Line(12,22,9,19);Line(12,22,15,19);
                Line(2,12,5,9);Line(2,12,5,15);Line(22,12,19,9);Line(22,12,19,15);break;
            case "remote":
                graphics.DrawEllipse(pen,4,2,6,6);graphics.DrawArc(pen,1,11,12,12,180,180);
                Line(1,17,1,20);Line(13,17,13,20);
                graphics.DrawEllipse(pen,16,7,6,7);graphics.DrawEllipse(pen,16,12,6,7);Line(19,11,19,15);break;
            case "switch":
                Line(4,5,19,5);Line(19,5,16,2);Line(19,5,16,8);
                Line(20,19,5,19);Line(5,19,8,16);Line(5,19,8,22);
                graphics.DrawLines(pen,new PointF[]{new(13,8),new(9,13),new(14,13),new(11,17)});break;
        }
        graphics.Restore(state);
    }
}
