#nullable enable
using System.Numerics;
using Valve.VR;

namespace ShockWave.Overlay.SteamVr;

internal static class PanelSizing
{
    public static readonly Size DefaultContent=new(684,532);
    public static readonly Size MinimumContent=new(420,360),MaximumContent=new(960,720);
    public static readonly Size MaximumSurface=new(298+MaximumContent.Width,108+MaximumContent.Height);
    public static Size ClampPanel(float width,float height)=>new((int)Math.Clamp(MathF.Round(width/4)*4,960,1256),(int)Math.Clamp(MathF.Round(height/4)*4,640,828));
    public static Size Clamp(float width,float height)=>new(
        (int)Math.Clamp(MathF.Round(width/4)*4,MinimumContent.Width,MaximumContent.Width),
        (int)Math.Clamp(MathF.Round(height/4)*4,MinimumContent.Height,MaximumContent.Height));
    public static Size Reference(bool wrist)=>wrist ? new(982,640) : new(1160,760);
    public static float WidthMeters(float referenceWidth,bool wrist,Size size)=>referenceWidth*size.Width/Reference(wrist).Width;
    // Grow up and right from the existing lower-left anchor. Fixed-size toy
    // rows/icons keep their physical size, including during corner dragging.
    public static HmdMatrix34_t Pose(HmdMatrix34_t anchor,float referenceWidth,bool wrist,Size size)
    {
        var reference=Reference(wrist);var density=referenceWidth/reference.Width;
        return VrPoseMath.Compose(anchor,VrPoseMath.Matrix(new Vector3((size.Width-reference.Width)*density/2,(size.Height-reference.Height)*density/2,0),Quaternion.Identity));
    }
    public static PointF Remap(PointF point,Size presented,Size current)=>new(point.X,point.Y+current.Height-presented.Height);
}
