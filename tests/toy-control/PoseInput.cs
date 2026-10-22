using System.Numerics;

internal static class PoseInput
{
    internal static void Aim(Action<string,object> put,string hand,float x,float y,bool hit=true,float size=.5f)
    {
        var scale=.4f+size*1.2f;
        var origin=new Vector3((x/1240-.5f)*.74f*scale,(.5f-(y-100)/608)*.74f*608/1240*scale,-.5f);
        var tip=origin+Vector3.UnitZ*.2f;
        put("Size",size);put("InPointerReady",true);put("InPose"+hand+"OHit",hit);put("InPose"+hand+"THit",hit);
        foreach(var end in new[]{"O","T"})
        {
            var v=end=="O" ? origin : tip;
            put("InPose"+hand+end+"X",v.X/3.5f+.5f);put("InPose"+hand+end+"Y",v.Y/3.5f+.5f);put("InPose"+hand+end+"Z",v.Z/3.5f+.5f);
        }
    }
}
