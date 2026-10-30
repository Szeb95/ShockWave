#nullable enable
using System.Numerics;
using Valve.VR;

namespace ShockWave.Overlay.SteamVr;

internal static class PanelContact
{
    public const float ReachMeters=.035f;
    // Distance from the grip center to the rounded rail in three dimensions.
    // Controller orientation and ray direction are deliberately irrelevant.
    public static bool TryHandle(HmdMatrix34_t hand,HmdMatrix34_t surface,float width,Size image,PanelLayout layout,out string? handle)
    {
        handle=null;
        if(layout.Wrist || !float.IsFinite(width) || width<=0 || image.Width<=0 || image.Height<=0 ||
            !VrPoseMath.TryRead(hand,out var position,out _) || !VrPoseMath.TryRead(surface,out var center,out _)) return false;
        var delta=position-center;var right=new Vector3(surface.m0,surface.m4,surface.m8);var up=new Vector3(surface.m1,surface.m5,surface.m9);var normal=new Vector3(surface.m2,surface.m6,surface.m10);
        var depth=Vector3.Dot(delta,normal);if(Math.Abs(depth)>ReachMeters) return false;
        var density=width/image.Width;
        var pixel=PanelSizing.Remap(new(Vector3.Dot(delta,right)/density+image.Width/2f,image.Height/2f-Vector3.Dot(delta,up)/density),image,layout.Size);
        var nearest=ReachMeters*ReachMeters;
        foreach(var hit in layout.Hits.Where(h=>h.Id is "grab-left" or "grab-right"))
        {
            var x=Math.Clamp(pixel.X,hit.Rect.Left+hit.Radius,hit.Rect.Right-hit.Radius);
            var y=Math.Clamp(pixel.Y,hit.Rect.Top+hit.Radius,hit.Rect.Bottom-hit.Radius);
            var dx=pixel.X-x;var dy=pixel.Y-y;var outside=Math.Max(0,MathF.Sqrt(dx*dx+dy*dy)-hit.Radius)*density;
            var distance=outside*outside+depth*depth;
            if(distance<=nearest) {handle=hit.Id;nearest=distance;}
        }
        return handle is not null;
    }
}
