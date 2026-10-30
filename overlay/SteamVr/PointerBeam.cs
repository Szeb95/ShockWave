#nullable enable
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Numerics;
using Valve.VR;

namespace ShockWave.Overlay.SteamVr;

internal readonly record struct PointerAim(int Hand,PointF Point,Vector3 Origin,Vector3 Hit);
internal readonly record struct BeamPose(HmdMatrix34_t Transform,float Length);

internal static class PointerBeam
{
    public const int TextureWidth=1024,TextureHeight=16;
    public static float QuadHeight(float length)=>length*TextureHeight/TextureWidth;
    public static bool ContainsBoard(PointF p,Size size)
    {
        var bounds=new RectangleF(4,4,size.Width-8,size.Height-8);
        if(!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !bounds.Contains(p)) return false;
        const float radius=28;
        var dx=p.X-Math.Clamp(p.X,bounds.Left+radius,bounds.Right-radius);
        var dy=p.Y-Math.Clamp(p.Y,bounds.Top+radius,bounds.Bottom-radius);
        return dx*dx+dy*dy<=radius*radius;
    }
    public static PointerAim? Choose(PointerAim? left,PointerAim? right,string activeSource)
        => activeSource=="vr0" ? left : activeSource=="vr1" ? right : right ?? left;
    public static bool TryPose(Vector3 origin,Vector3 hit,Vector3 viewer,out BeamPose pose)
    {
        pose=default;
        if(!Finite(origin) || !Finite(hit) || !Finite(viewer)) return false;
        var distance=Vector3.Distance(origin,hit);
        if(distance<.025f || distance>3) return false;
        var x=(hit-origin)/distance;
        var start=origin+x*.012f;var end=hit-x*.001f;
        var center=(start+end)*.5f;
        var facing=viewer-center;facing-=x*Vector3.Dot(facing,x);
        if(facing.LengthSquared()<1e-8f)
        {
            facing=MathF.Abs(x.Y)<.9f ? Vector3.UnitY : Vector3.UnitX;
            facing-=x*Vector3.Dot(facing,x);
        }
        var z=Vector3.Normalize(facing);var y=Vector3.Normalize(Vector3.Cross(z,x));z=Vector3.Cross(x,y);
        pose=new(new HmdMatrix34_t {m0=x.X,m1=y.X,m2=z.X,m3=center.X,m4=x.Y,m5=y.Y,m6=z.Y,m7=center.Y,m8=x.Z,m9=y.Z,m10=z.Z,m11=center.Z},distance-.013f);
        return true;
    }
    private static bool Finite(Vector3 v)=>float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    public static Bitmap Image()
    {
        var bitmap=new Bitmap(TextureWidth,TextureHeight,PixelFormat.Format32bppArgb);
        using var g=Graphics.FromImage(bitmap);g.Clear(Color.Transparent);g.SmoothingMode=SmoothingMode.AntiAlias;
        foreach(var (width,color) in new[]{(12f,Color.FromArgb(24,PanelView.Green)),(6f,Color.FromArgb(65,PanelView.Green)),(2f,Color.FromArgb(235,PanelView.Green)),(.8f,Color.FromArgb(245,216,255,235))})
        {using var pen=new Pen(color,width);g.DrawLine(pen,0,TextureHeight/2,TextureWidth,TextureHeight/2);}
        return bitmap;
    }
}

// A visual-only camera-facing beam quad. It can never receive mouse input,
// activate a system laser or capture focus from the scene application.
internal sealed class PointerBeamOverlay : IDisposable
{
    private readonly CVROverlay overlays;
    private GpuOverlayTexture? texture;
    private OverlayVisibility? visibility;
    public ulong Handle {get;private set;}
    public PointerBeamOverlay(CVROverlay overlays,int adapter,string key)
    {
        this.overlays=overlays;
        try
        {
            ulong handle=0;Check(overlays.CreateOverlay(key,"ShockWave pointer beam",ref handle));Handle=handle;
            visibility=new(show=>Check(show ? overlays.ShowOverlay(Handle) : overlays.HideOverlay(Handle)));
            Check(overlays.SetOverlayInputMethod(Handle,VROverlayInputMethod.None));
            Check(overlays.SetOverlaySortOrder(Handle,1));
            texture=new GpuOverlayTexture(adapter);using var bitmap=PointerBeam.Image();texture.Upload(bitmap);
            var descriptor=texture.Descriptor;Check(overlays.SetOverlayTexture(Handle,ref descriptor));texture.PresentationCommitted();
        }
        catch {Dispose();throw;}
    }
    public void Update(BeamPose pose,bool show=true)
    {
        Check(overlays.SetOverlayWidthInMeters(Handle,pose.Length));
        // Keep the native image aspect (64:1). No runtime texel stretching is
        // needed, so the visible beam and native overlay geometry agree.
        var transform=pose.Transform;
        Check(overlays.SetOverlayTransformAbsolute(Handle,ETrackingUniverseOrigin.TrackingUniverseStanding,ref transform));
        visibility!.Set(show);
    }
    public void Hide()=>visibility?.Set(false);
    private static void Check(EVROverlayError error) {if(error!=EVROverlayError.None) throw new InvalidOperationException("Pointer visual: "+error);}
    public void Dispose()
    {
        if(Handle!=0) {try {overlays.ClearOverlayTexture(Handle);overlays.DestroyOverlay(Handle);}catch { }Handle=0;}
        texture?.Dispose();texture=null;visibility=null;
    }
}
