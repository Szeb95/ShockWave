#nullable enable
using System.Numerics;
using Valve.VR;

namespace ShockWave.Overlay.SteamVr;

internal static class PanelRayProjection
{
    // Continue an already captured drag on the infinite panel plane. Starting
    // input still requires the normal visible-board/graph intersection.
    public static bool TryProject(HmdMatrix34_t controller,HmdMatrix34_t panel,float width,Size image,out PointF point)
    {
        point=default;
        if(!float.IsFinite(width) || width<=0 || image.Width<=0 || image.Height<=0 ||
            !VrPoseMath.TryRead(controller,out var origin,out _) || !VrPoseMath.TryRead(panel,out var center,out _)) return false;
        var direction=new Vector3(-controller.m2,-controller.m6,-controller.m10);
        var normal=new Vector3(panel.m2,panel.m6,panel.m10);
        var denominator=Vector3.Dot(direction,normal);
        if(denominator>=-.0001f) return false;
        var distance=Vector3.Dot(center-origin,normal)/denominator;
        if(!float.IsFinite(distance) || distance<=0 || distance>=3) return false;
        var offset=origin+direction*distance-center;
        var x=Vector3.Dot(offset,new(panel.m0,panel.m4,panel.m8));
        var y=Vector3.Dot(offset,new(panel.m1,panel.m5,panel.m9));
        var height=width*image.Height/image.Width;
        point=new((x/width+.5f)*image.Width,(.5f-y/height)*image.Height);
        return float.IsFinite(point.X) && float.IsFinite(point.Y);
    }
}
