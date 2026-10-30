using System.Numerics;
using Valve.VR;

namespace ShockWave.Overlay.SteamVr;

// Pure geometry/state. Opening is presentation only, never a device command.
internal readonly record struct WristMenuFrame(HmdMatrix34_t Pose,Size Size,float Width)
{
    public static PointF Anchor(Size size)=>new(674,size.Height-40);
    public static PointF Top(Size size)=>new(Math.Clamp(674,74,size.Width-74),14);
    public static RectangleF Icon(Size size)
    {var a=Anchor(size);return new(a.X-28,a.Y-28,56,56);}
    public static RectangleF Handle(Size size,float amount)
        =>amount<=0 ? Icon(size) : Bar(size,amount);
    public static RectangleF Bar(Size size,float amount)
    {
        amount=Math.Clamp(amount,0,1);var a=Anchor(size);var b=Top(size);
        var x=a.X+(b.X-a.X)*amount;var y=a.Y-46+(b.Y-a.Y+46)*amount;
        return new(x-66,y-12,132,24);
    }
    public bool Sample(HmdMatrix34_t hand,float amount,out float projection,out float distance,out float anchorDistance,out float length)
        =>Sample(hand,amount,out projection,out distance,out anchorDistance,out length,out _);
    public bool Sample(HmdMatrix34_t hand,float amount,out float projection,out float distance,out float anchorDistance,out float length,out float handleDistance)
    {
        projection=distance=anchorDistance=length=handleDistance=0;
        if(!float.IsFinite(Width) || Width<=0 || Size.Width<148 || Size.Height<100 ||
            !VrPoseMath.TryRead(Pose,out var origin,out var rotation) || !VrPoseMath.TryRead(hand,out var position,out _)) return false;
        var local=Vector3.Transform(position-origin,Quaternion.Inverse(rotation));
        var density=Width/PanelSizing.Reference(true).Width;var a=Anchor(Size);var b=Top(Size);
        var height=Size.Height;
        Vector3 Point(PointF p)=>new((p.X-491)*density,(height-320-p.Y)*density,0);
        var anchor=Point(a);var direction=Point(b)-anchor;length=direction.Length();
        if(length<.01f) return false;
        projection=Vector3.Dot(local-anchor,direction/length);anchorDistance=Vector3.Distance(local,anchor);
        var pixel=new PointF(local.X/density+491,Size.Height-320-local.Y/density);
        var rect=Handle(Size,amount);var radius=rect.Height/2;
        var centerX=rect.X+rect.Width/2;var centerY=rect.Y+rect.Height/2;var half=Math.Max(0,rect.Width/2-radius);
        var dx=pixel.X-Math.Clamp(pixel.X,centerX-half,centerX+half);
        var dy=pixel.Y-centerY;
        var outside=Math.Max(0,MathF.Sqrt(dx*dx+dy*dy)-radius)*density;
        handleDistance=MathF.Sqrt(outside*outside+local.Z*local.Z);
        // Permit a hovering grip above the plane so controllers need not collide.
        var depth=local.Z*WristMenuGesture.Reach/WristMenuGesture.DepthReach;
        distance=MathF.Sqrt(outside*outside+depth*depth);return true;
    }
    internal HmdMatrix34_t HandAt(PointF point,float depth=0)
    {
        var density=Width/PanelSizing.Reference(true).Width;
        return VrPoseMath.Compose(Pose,VrPoseMath.Matrix(new((point.X-491)*density,(Size.Height-320-point.Y)*density,depth),Quaternion.Identity));
    }
}

internal sealed class WristMenuGesture
{
    public const float RevealDistance=.18f,HideDistance=.24f,Reach=.055f,DepthReach=.12f;
    public float Amount {get;private set;}
    public float Target {get;private set;}
    public bool Active {get;private set;}
    public bool Near {get;private set;}
    public bool AnchorNear {get;private set;}
    public bool Contact {get;private set;}
    public bool HandleVisible=>Near || Active;
    public bool AnchorVisible=>AnchorNear || Active;
    private bool released;
    private float offset,originalTarget;
    private double last=double.NaN;
    public void Reset() {Amount=Target=0;Active=Near=AnchorNear=Contact=released=false;last=double.NaN;}
    public void Open() {Reset();Amount=Target=1;}
    public void Cancel()
    {
        if(Active) Amount=Target=originalTarget;
        Active=Near=AnchorNear=Contact=released=false;last=double.NaN;
    }
    public void Reanchor(WristMenuFrame frame,HmdMatrix34_t hand)
    {if(Active && frame.Sample(hand,Amount,out var p,out _,out _,out var length)) offset=p-Amount*length;}
    public bool Update(WristMenuFrame frame,HmdMatrix34_t hand,bool valid,bool down,bool canBegin,double now,bool gripAvailable=true)
    {
        if(!gripAvailable && Active) Cancel();
        if(!valid || !double.IsFinite(now) || double.IsFinite(last) && now<last ||
            !frame.Sample(hand,Amount,out var p,out var contact,out var away,out var length,out var handleDistance)) {Cancel();return false;}
        var dt=double.IsFinite(last) ? Math.Clamp(now-last,0,.05) : 0;last=now;
        Near=handleDistance<=(Near ? HideDistance : RevealDistance);
        AnchorNear=away<=(AnchorNear ? HideDistance : RevealDistance);Contact=contact<=Reach;
        var fresh=gripAvailable && down && released;released=gripAvailable && !down;var started=false;
        if(!Active && fresh && canBegin && HandleVisible && Contact)
        {originalTarget=Target>=.5f ? 1 : 0;offset=p-Amount*length;Active=true;started=true;}
        if(Active)
        {
            var travel=p-offset;Target=Math.Clamp(travel/length,0,1);
            if(travel<=.015f) Target=0;else if(travel>=length-.01f) Target=1;
            if(!down) {Active=false;Target=Target>=.4f ? 1 : 0;}
        }
        Amount+=(Target-Amount)*(float)(1-Math.Exp(-dt/.035));
        if(Math.Abs(Target-Amount)<.002f) Amount=Target;
        return started;
    }
}
