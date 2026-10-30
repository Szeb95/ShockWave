#nullable enable
using System.Numerics;
using Valve.VR;

namespace ShockWave.Overlay.SteamVr;

// Floating placement is session-only. A grip-relative rigid transform keeps
// the touched side handle attached to the controller without a center snap.
internal sealed class PanelGrabSession
{
    public string? Source {get;private set;}
    public bool Active=>Source is not null;
    public HmdMatrix34_t WorldPose {get;private set;}
    private HmdMatrix34_t original,offset;
    public bool Begin(string source,HmdMatrix34_t hand,HmdMatrix34_t panel)
    {
        if(Active || source is not ("vr0" or "vr1") || !VrPoseMath.TryRead(hand,out var position,out var rotation) || !VrPoseMath.TryRead(panel,out _,out _)) return false;
        var inverse=Quaternion.Inverse(rotation);
        offset=VrPoseMath.Compose(VrPoseMath.Matrix(Vector3.Transform(-position,inverse),inverse),panel);
        Source=source;original=panel;WorldPose=panel;return true;
    }
    public bool Update(string source,HmdMatrix34_t hand)
    {
        if(Source!=source || !VrPoseMath.TryRead(hand,out _,out _)) return false;
        var pose=VrPoseMath.Compose(hand,offset);if(!VrPoseMath.TryRead(pose,out _,out _)) return false;
        WorldPose=pose;return true;
    }
    public bool Finish(string source,bool commit,out HmdMatrix34_t pose)
    {
        pose=default;if(Source!=source) return false;
        pose=commit ? WorldPose : original;Source=null;WorldPose=pose;return true;
    }
}
