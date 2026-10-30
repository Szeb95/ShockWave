#nullable enable
using System.Numerics;
using Valve.VR;

namespace ShockWave.Overlay.SteamVr;

// Time-based filtering keeps the same feel at different update rates. Faster
// deliberate movements get less filtering; loss/recovery never trails old poses.
internal sealed class PoseSmoother(float positionSeconds,float rotationSeconds)
{
    private Vector3 position,lastRaw;
    private Quaternion rotation,lastRawRotation;
    private double lastTime;
    public bool Initialized {get;private set;}
    public bool Restarted {get;private set;}
    public static PoseSmoother Pointer()=>new(.034f,.042f);
    public static PoseSmoother HandPanel()=>new(.040f,.045f);
    public void Reset() {Initialized=false;Restarted=false;}
    public bool TryUpdate(HmdMatrix34_t target,double now,out HmdMatrix34_t result)
    {
        result=default;Restarted=false;
        if(!double.IsFinite(now) || !VrPoseMath.TryRead(target,out var raw,out var rawRotation) || Initialized && now<lastTime) {Reset();return false;}
        var dt=now-lastTime;
        if(Initialized && dt==0) {result=VrPoseMath.Matrix(position,rotation);return true;}
        if(!Initialized || dt>.18 || Vector3.Distance(position,raw)>.35f || VrPoseMath.Angle(rotation,rawRotation)>2.1f)
        {position=raw;rotation=rawRotation;Initialized=true;Restarted=true;}
        else
        {
            var motion=Math.Clamp(Math.Max(Vector3.Distance(raw,lastRaw)/(float)dt/.75f,VrPoseMath.Angle(rawRotation,lastRawRotation)/(float)dt/4),0,1);
            var responsiveness=1-.65f*motion;
            var positionBlend=1-MathF.Exp(-(float)dt/(positionSeconds*responsiveness));
            var rotationBlend=1-MathF.Exp(-(float)dt/(rotationSeconds*responsiveness));
            position=Vector3.Lerp(position,raw,positionBlend);rotation=Quaternion.Normalize(Quaternion.Slerp(rotation,rawRotation,rotationBlend));
        }
        lastRaw=raw;lastRawRotation=rawRotation;lastTime=now;
        result=VrPoseMath.Matrix(position,rotation);return true;
    }
}

internal static class VrPoseMath
{
    public static float Angle(Quaternion a,Quaternion b)=>2*MathF.Acos(Math.Clamp(MathF.Abs(Quaternion.Dot(a,b)),0,1));
    public static bool TryRead(HmdMatrix34_t m,out Vector3 position,out Quaternion rotation)
    {
        position=new(m.m3,m.m7,m.m11);rotation=Quaternion.Identity;
        var x=new Vector3(m.m0,m.m4,m.m8);var y=new Vector3(m.m1,m.m5,m.m9);var z=new Vector3(m.m2,m.m6,m.m10);
        if(!Finite(position) || !Finite(x) || !Finite(y) || !Finite(z) ||
            x.LengthSquared() is <.5f or >1.5f || y.LengthSquared() is <.5f or >1.5f || z.LengthSquared() is <.5f or >1.5f ||
            Math.Abs(Vector3.Dot(x,y))>.2f || Math.Abs(Vector3.Dot(x,z))>.2f || Math.Abs(Vector3.Dot(y,z))>.2f) return false;
        // OpenVR uses column-vector transforms; System.Numerics uses row vectors.
        var matrix=new Matrix4x4(m.m0,m.m4,m.m8,0,m.m1,m.m5,m.m9,0,m.m2,m.m6,m.m10,0,0,0,0,1);
        rotation=Quaternion.Normalize(Quaternion.CreateFromRotationMatrix(matrix));
        return float.IsFinite(rotation.X) && float.IsFinite(rotation.Y) && float.IsFinite(rotation.Z) && float.IsFinite(rotation.W);
    }
    private static bool Finite(Vector3 v)=>float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    public static HmdMatrix34_t Matrix(Vector3 position,Quaternion rotation)
    {
        var m=Matrix4x4.CreateFromQuaternion(rotation);
        return new() {m0=m.M11,m1=m.M21,m2=m.M31,m3=position.X,m4=m.M12,m5=m.M22,m6=m.M32,m7=position.Y,m8=m.M13,m9=m.M23,m10=m.M33,m11=position.Z};
    }
    public static HmdMatrix34_t Compose(HmdMatrix34_t a,HmdMatrix34_t b)=>new() {
        m0=a.m0*b.m0+a.m1*b.m4+a.m2*b.m8,m1=a.m0*b.m1+a.m1*b.m5+a.m2*b.m9,m2=a.m0*b.m2+a.m1*b.m6+a.m2*b.m10,m3=a.m0*b.m3+a.m1*b.m7+a.m2*b.m11+a.m3,
        m4=a.m4*b.m0+a.m5*b.m4+a.m6*b.m8,m5=a.m4*b.m1+a.m5*b.m5+a.m6*b.m9,m6=a.m4*b.m2+a.m5*b.m6+a.m6*b.m10,m7=a.m4*b.m3+a.m5*b.m7+a.m6*b.m11+a.m7,
        m8=a.m8*b.m0+a.m9*b.m4+a.m10*b.m8,m9=a.m8*b.m1+a.m9*b.m5+a.m10*b.m9,m10=a.m8*b.m2+a.m9*b.m6+a.m10*b.m10,m11=a.m8*b.m3+a.m9*b.m7+a.m10*b.m11+a.m11
    };
}
