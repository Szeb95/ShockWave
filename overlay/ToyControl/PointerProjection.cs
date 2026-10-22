using System.Drawing;
using System.Numerics;

namespace ShockWave.Overlay;

// Contact-encoded hand pose, measured in panel-root metres. No scene collider
// participates in selection; the resulting point always lies on the panel.
internal static class PointerProjection
{
    internal const float Span=3.5f, Baseline=.2f, MaximumDistance=2f;
    internal static bool TryProject(Vector3 origin,Vector3 tip,float size,out PointF point)
    {
        point=default;
        var direction=tip-origin; var length=direction.Length();
        if(!float.IsFinite(length) || length<.12f || length>.3f || !float.IsFinite(size) || size is <0 or >1) return false;
        direction/=length;
        if(Math.Abs(direction.Z)<.12f) return false; // Grazing rays amplify tracking noise.
        var distance=-origin.Z/direction.Z;
        if(!float.IsFinite(distance) || distance<.025f || distance>MaximumDistance) return false;
        var hit=origin+direction*distance;
        var scale=.4f+size*1.2f;
        point=new((hit.X/(.74f*scale)+.5f)*1240,100+(.5f-hit.Y/(.74f*608/1240*scale))*608);
        return Rounded(point,new(24,105,300,598)) || Rounded(point,new(342,105,874,598));
    }
    private static bool Rounded(PointF p,RectangleF r)
    {
        if(!r.Contains(p)) return false;
        var dx=p.X-Math.Clamp(p.X,r.Left+8,r.Right-8); var dy=p.Y-Math.Clamp(p.Y,r.Top+8,r.Bottom-8);
        return dx*dx+dy*dy<=64;
    }
}
