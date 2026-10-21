using System.Drawing;
using ShockWave.ShaderLab;

namespace ShockWave.Overlay;

// Production layout; the separate Shader Lab preview keeps its original layout.
internal static class ToyControlLayout
{
    public static RectangleF Row(int row) => new(40,240+row*64,268,60);
    public static readonly RectangleF Clear=new(40,176,132,60), Stop=new(176,176,132,60);
    public static readonly RectangleF Record=new(365,127,130,42), Play=new(499,127,155,42);
    public static readonly RectangleF InputMode=new(658,127,130,42);
    public static RectangleF Destination(int route) => new(892+route*104,127,100,42);
    public static readonly RectangleF PageSwitch=new(1052,127,152,42);
    public static RectangleF Slider(int index) => new(400+index*160,240,108,350);
    public static RectangleF ShockButton(int hit) => hit switch { 32=>new(1020,240,136,136),33=>new(810,240,136,136),34=>new(810,430,346,64),_=>new(810,516,346,64) };
    public static int Hit(PointF p, ToyControlSession session) => Hit(p,session.DisplayModel,session.ShockerPage);
    public static int Hit(PointF p, PanelModel? model = null, bool shockers=false)
    {
        for(var row=0;row<(model?.Visible.Count() ?? PanelModel.PageSize);row++) if(Row(row).Contains(p)) return row+1;
        if(PageSwitch.Contains(p)) return 29;
        if(shockers)
        {
            for(var i=0;i<2;i++) if(Slider(i).Contains(p)) return 30+i;
            for(var i=32;i<=35;i++)
            {
                var rect=ShockButton(i); var dx=p.X-(rect.X+rect.Width/2); var dy=p.Y-(rect.Y+rect.Height/2);
                if(i<34 ? dx*dx+dy*dy<=68*68 : rect.Contains(p)) return i;
            }
            if(Stop.Contains(p)) return 13;
            if(model?.PageCount>1) { if(PanelLayout.Previous.Contains(p)) return 15; if(PanelLayout.Next.Contains(p)) return 16; }
            return InputMode.Contains(p) ? 28 : 0;
        }
        if(model?.SelectedCount > 0 && PanelLayout.Graph.Contains(p)) return 10;
        if(Clear.Contains(p)) return 11; if(Stop.Contains(p)) return 13;
        if(model?.PageCount > 1) { if(PanelLayout.Previous.Contains(p)) return 15; if(PanelLayout.Next.Contains(p)) return 16; }
        if(Record.Contains(p)) return 25; if(Play.Contains(p)) return 26;
        if(InputMode.Contains(p)) return 28;
        return 0;
    }
    public static string? Action(int hit) => hit switch { 11=>"InClear",13=>"InStop",15=>"InPrev",16=>"InNext",25=>"InRecord",26=>"InPlay",28=>"InInputMode",29=>"InPage",_=>null };
}
