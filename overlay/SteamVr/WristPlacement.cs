#nullable enable
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Valve.VR;

namespace ShockWave.Overlay.SteamVr;

internal sealed record WristRotation([property:JsonRequired] float X,[property:JsonRequired] float Y,[property:JsonRequired] float Z,[property:JsonRequired] float W)
{
    [JsonIgnore] public bool Valid=>float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Z) && float.IsFinite(W) && new Quaternion(X,Y,Z,W).LengthSquared() is >.99f and <1.01f;
    [JsonIgnore] public Quaternion Value=>Quaternion.Normalize(new(X,Y,Z,W));
    public static WristRotation From(Quaternion rotation) {var q=Quaternion.Normalize(rotation);return new(q.X,q.Y,q.Z,q.W);}
}
internal sealed record WristFit([property:JsonRequired] float Side,[property:JsonRequired] float Height,[property:JsonRequired] float Forward,[property:JsonRequired] float Tilt,WristRotation? Orientation=null)
{
    public static WristFit Default=>new(0,.075f,.03f,-90);
    [JsonIgnore] public bool Valid=>float.IsFinite(Side) && float.IsFinite(Height) && float.IsFinite(Forward) && float.IsFinite(Tilt) &&
        Math.Abs(Side)<=.6f && Math.Abs(Height)<=.6f && Math.Abs(Forward)<=.6f && Math.Abs(Tilt)<=180 && (Orientation is null || Orientation.Valid);
    [JsonIgnore] public Vector3 Position=>new(Side,Height,Forward);
    [JsonIgnore] public Quaternion Rotation=>Orientation?.Value ?? Quaternion.CreateFromAxisAngle(Vector3.UnitX,Tilt*MathF.PI/180);
    public HmdMatrix34_t Matrix()=>VrPoseMath.Matrix(Position,Rotation);
    public WristFit WithPose(Vector3 position,Quaternion rotation)=>this with {Side=position.X,Height=position.Y,Forward=position.Z,Orientation=WristRotation.From(rotation)};
    public WristFit WithTilt(float value)
    {
        if(Orientation is null) return this with {Tilt=value};
        var delta=Quaternion.CreateFromAxisAngle(Vector3.UnitX,(value-Tilt)*MathF.PI/180);
        return this with {Tilt=value,Orientation=WristRotation.From(delta*Rotation)};
    }
}
internal sealed record WristSettings(int Version,WristFit Left,WristFit Right,Mount LastWrist=Mount.LeftHand)
{
    public static WristSettings Default=>new(2,WristFit.Default,WristFit.Default);
    [JsonIgnore] public bool Valid=>Version==2 && Left is not null && Right is not null && Left.Valid && Right.Valid && LastWrist is Mount.LeftHand or Mount.RightHand;
    public WristFit For(Mount hand)=>hand==Mount.RightHand ? Right : Left;
    public WristSettings With(Mount hand,WristFit fit)=>hand==Mount.RightHand ? this with {Right=fit,LastWrist=hand} : this with {Left=fit,LastWrist=hand};
}

// The lab owns a separate preference file. Null paths are in-memory test stores.
internal sealed class WristPlacementStore(string? path)
{
    public string? LastError {get;private set;}
    public static string DefaultPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"SzebsToys","steamvr-wrist-placement.json");
    public WristSettings Load()
    {
        if(path is null) return WristSettings.Default;
        var sourcePath=path;
        if(path==DefaultPath && !File.Exists(path)) sourcePath=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"ShockWaveVrPanelLab","wrist-placement.json");
        if(!File.Exists(sourcePath)) return WristSettings.Default;
        try
        {
            if(new FileInfo(sourcePath).Length>16384) return WristSettings.Default;
            var data=JsonSerializer.Deserialize<WristSettings>(File.ReadAllText(sourcePath));
            if(data?.Version==1) data=data with {Version=2};
            return data is {Valid:true} ? data : WristSettings.Default;
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or NotSupportedException) {return WristSettings.Default;}
    }
    public bool Save(WristSettings settings)
    {
        LastError=null;
        if(!settings.Valid) {LastError="Invalid placement data";return false;}
        if(path is null) return true;
        string? temporary=null;
        try
        {
            var fullPath=Path.GetFullPath(path);Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            temporary=fullPath+".tmp-"+Guid.NewGuid().ToString("N");
            var json=JsonSerializer.Serialize(settings,new JsonSerializerOptions {WriteIndented=true});
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                var bytes=Encoding.UTF8.GetBytes(json);stream.Write(bytes);stream.Flush(true);
            }
            for(var attempt=0;;attempt++)
            {
                try {File.Move(temporary,fullPath,true);break;}
                catch(IOException error) when(attempt<3 && (error.HResult&0xffff) is 32 or 33) {Thread.Sleep(20);}
            }
            temporary=null;return true;
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException) {LastError=error.Message;return false;}
        finally {if(temporary is not null) {try {File.Delete(temporary);} catch(IOException) { } catch(UnauthorizedAccessException) { }}}
    }
}

// A rigid offset from the aiming pointer keeps the grabbed point at its initial
// distance and orientation. The resulting hand-relative pose is saved on release.
internal sealed class WristMoveSession(WristPlacementStore store)
{
    public WristSettings Settings {get;private set;}=store.Load();
    public string? Source {get;private set;}
    public bool Active=>Source is not null;
    private Mount hand;
    private WristFit original=WristFit.Default;
    private WristSettings before=WristSettings.Default;
    private HmdMatrix34_t pointerOffset;
    public HmdMatrix34_t WorldPose {get;private set;}
    public bool Set(Mount mount,WristFit fit)
    {
        Cancel();if(mount==Mount.Floating || !fit.Valid) return false;
        Settings=Settings.With(mount,fit);return store.Save(Settings);
    }
    public bool Begin(string source,Mount mount,HmdMatrix34_t mounted,HmdMatrix34_t aiming,HmdMatrix34_t? displayedPanel=null)
    {
        if(Active || mount==Mount.Floating || source!=(mount==Mount.LeftHand ? "vr1" : "vr0") || !TryInverse(mounted,out _) || !TryInverse(aiming,out var inverseAim)) return false;
        var world=displayedPanel ?? VrPoseMath.Compose(mounted,Settings.For(mount).Matrix());
        if(!VrPoseMath.TryRead(world,out _,out _)) return false;
        Source=source;hand=mount;before=Settings;original=Settings.For(hand);
        pointerOffset=VrPoseMath.Compose(inverseAim,world);WorldPose=world;return true;
    }
    public bool Update(string source,Mount mount,HmdMatrix34_t mounted,HmdMatrix34_t aiming)
    {
        if(Source!=source || mount!=hand) return false;
        if(!TryInverse(mounted,out var inverseHand) || !VrPoseMath.TryRead(aiming,out _,out _)) {Cancel();return false;}
        var world=VrPoseMath.Compose(aiming,pointerOffset);var relative=VrPoseMath.Compose(inverseHand,world);
        if(!VrPoseMath.TryRead(relative,out var position,out var rotation)) {Cancel();return false;}
        var fit=original.WithPose(position,rotation);
        if(!fit.Valid) {Cancel();return false;}
        Settings=Settings.With(hand,fit);WorldPose=world;return true;
    }
    public bool Finish(string source,out bool saved)
    {
        saved=false;if(Source!=source) return false;
        Source=null;saved=store.Save(Settings);return true;
    }
    public void Cancel()
    {
        if(Active) Settings=before;
        Source=null;WorldPose=default;
    }
    private static bool TryInverse(HmdMatrix34_t pose,out HmdMatrix34_t inverse)
    {
        inverse=default;if(!VrPoseMath.TryRead(pose,out var position,out var rotation)) return false;
        var inverted=Quaternion.Inverse(rotation);inverse=VrPoseMath.Matrix(Vector3.Transform(-position,inverted),inverted);return true;
    }
}
