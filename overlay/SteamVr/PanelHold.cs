#nullable enable
using System.Numerics;
using Valve.VR;

namespace ShockWave.Overlay.SteamVr;

// Placement capture is independent from pointer capture. Contact anchors stay
// attached to their grips through layout/scale edits and one/two-hand handoff.
internal sealed class PanelHoldSession
{
    private sealed class Grip(string handle,PointF fraction,HmdMatrix34_t hand)
    {
        public string Handle=handle;
        public PointF Fraction=fraction;
        public HmdMatrix34_t Hand=hand;
        public Vector3 Contact;
        public Quaternion RelativeRotation;
    }
    private readonly Dictionary<string,Grip> grips=[];
    private HmdMatrix34_t original;
    private float originalWidth;
    private Quaternion twoRotation;
    private PanelLayout? layout;
    public bool Active=>grips.Count>0;
    public int Count=>grips.Count;
    public string? Source=>grips.Keys.FirstOrDefault();
    public HmdMatrix34_t WorldPose {get;private set;}
    public float Width {get;private set;}
    public bool Contains(string source)=>grips.ContainsKey(source);
    public bool Begin(string source,string handle,HmdMatrix34_t hand,HmdMatrix34_t panel,float width,PanelLayout current)
    {
        if(current.Wrist || source is not ("vr0" or "vr1") || handle is not ("grab-left" or "grab-right") || grips.ContainsKey(source) || grips.Count>=2 || grips.Values.Any(g=>g.Handle==handle) ||
            !VrPoseMath.TryRead(hand,out var position,out var handRotation) || !VrPoseMath.TryRead(panel,out var center,out var panelRotation) || !float.IsFinite(width) || width is <.16f or >.6f) return false;
        if(grips.Count==0) {WorldPose=panel;Width=width;original=panel;originalWidth=width;}
        var inverse=Quaternion.Inverse(panelRotation);var local=Vector3.Transform(position-center,inverse);var density=width/PanelSizing.Reference(false).Width;
        var pixel=new PointF(local.X/density+580,current.Size.Height-380-local.Y/density);
        var rect=current.Hits.Single(h=>h.Id==handle).Rect;
        var fraction=new PointF(Math.Clamp((pixel.X-rect.Left)/rect.Width,0,1),Math.Clamp((pixel.Y-rect.Top)/rect.Height,0,1));
        var grip=new Grip(handle,fraction,hand);grips.Add(source,grip);layout=current;
        Capture(grip,current,Width);
        if(grips.Count==2) twoRotation=panelRotation;
        return true;
    }
    public bool SetHand(string source,HmdMatrix34_t hand)
    {
        if(!grips.TryGetValue(source,out var grip) || !VrPoseMath.TryRead(hand,out _,out _)) return false;
        grip.Hand=hand;return true;
    }
    public bool Update(PanelLayout current,float width)
    {
        if(!Active || current.Wrist || !float.IsFinite(width) || width is <.16f or >.6f) return false;
        layout=current;
        if(grips.Count==1)
        {
            var g=grips.Values.First();if(!VrPoseMath.TryRead(g.Hand,out var position,out var rotation)) return false;
            var orientation=Quaternion.Normalize(rotation*g.RelativeRotation);var target=position+Vector3.Transform(g.Contact,rotation);
            var pose=VrPoseMath.Matrix(target-Vector3.Transform(Anchor(g,current,width),orientation),orientation);
            if(!VrPoseMath.TryRead(pose,out _,out _)) return false;
            Width=width;WorldPose=pose;return true;
        }
        var left=grips.Values.Single(g=>g.Handle=="grab-left");var right=grips.Values.Single(g=>g.Handle=="grab-right");
        var a=Contact(left);var b=Contact(right);var desired=b-a;
        var distance=desired.Length();if(!float.IsFinite(distance)) return false;
        if(distance<.04f) return true;
        var unitA=Anchor(left,current,1);var unitB=Anchor(right,current,1);var local=unitB-unitA;
        if(local.Length()<.01f) return false;
        var nextWidth=Math.Clamp(distance/local.Length(),.16f,.6f);
        var from=Vector3.Normalize(Vector3.Transform(local,twoRotation));var to=Vector3.Normalize(desired);
        var orientation2=Quaternion.Normalize(FromTo(from,to)*twoRotation);
        var middle=(Anchor(left,current,nextWidth)+Anchor(right,current,nextWidth))/2;
        var result=VrPoseMath.Matrix((a+b)/2-Vector3.Transform(middle,orientation2),orientation2);
        if(!VrPoseMath.TryRead(result,out _,out _)) return false;
        Width=nextWidth;WorldPose=result;return true;
    }
    public bool Release(string source)
    {
        if(!grips.Remove(source)) return false;
        if(grips.Count==1 && layout is not null) Capture(grips.Values.First(),layout,Width);
        return true;
    }
    public bool Cancel(string source)
    {
        if(!grips.ContainsKey(source)) return false;
        grips.Clear();WorldPose=original;Width=originalWidth;return true;
    }
    private void Capture(Grip grip,PanelLayout current,float width)
    {
        VrPoseMath.TryRead(grip.Hand,out var position,out var rotation);VrPoseMath.TryRead(WorldPose,out var center,out var orientation);
        var world=center+Vector3.Transform(Anchor(grip,current,width),orientation);var inverse=Quaternion.Inverse(rotation);
        grip.Contact=Vector3.Transform(world-position,inverse);grip.RelativeRotation=Quaternion.Normalize(inverse*orientation);
    }
    private static Vector3 Contact(Grip grip)
    {
        VrPoseMath.TryRead(grip.Hand,out var position,out var rotation);return position+Vector3.Transform(grip.Contact,rotation);
    }
    private static Vector3 Anchor(Grip grip,PanelLayout current,float width)
    {
        var r=current.Hits.Single(h=>h.Id==grip.Handle).Rect;var x=r.X+r.Width*grip.Fraction.X;var y=r.Y+r.Height*grip.Fraction.Y;var density=width/1160;
        return new((x-580)*density,(current.Size.Height-380-y)*density,0);
    }
    private static Quaternion FromTo(Vector3 from,Vector3 to)
    {
        var dot=Math.Clamp(Vector3.Dot(from,to),-1,1);
        if(dot> .999999f) return Quaternion.Identity;
        if(dot<-.999999f) {var axis=Vector3.Cross(from,Math.Abs(from.X)<.8f ? Vector3.UnitX : Vector3.UnitY);return Quaternion.CreateFromAxisAngle(Vector3.Normalize(axis),MathF.PI);}
        return Quaternion.Normalize(new Quaternion(Vector3.Cross(from,to),1+dot));
    }
}
