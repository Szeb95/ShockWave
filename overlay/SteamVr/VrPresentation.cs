#nullable enable
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Valve.VR;

namespace ShockWave.Overlay.SteamVr;

internal static class VrPresentation
{
    // GDI's BGRA byte order differs from OpenVR SetOverlayRaw's RGBA input.
    // Rows stay top-to-bottom; native mouse/UV origins are converted at input.
    public static void CopyRgba(Bitmap frame,byte[] pixels)
    {
        if(pixels.Length!=checked(frame.Width*frame.Height*4)) throw new ArgumentException("Wrong overlay image size");
        var data=frame.LockBits(new(0,0,frame.Width,frame.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try {for(var y=0;y<frame.Height;y++) Marshal.Copy(data.Scan0+y*data.Stride,pixels,y*frame.Width*4,frame.Width*4);}
        finally {frame.UnlockBits(data);}
        for(var i=0;i<pixels.Length;i+=4) (pixels[i],pixels[i+2])=(pixels[i+2],pixels[i]);
    }
    public static HmdMatrix34_t Floating(HmdMatrix34_t head)
    {
        var yaw=MathF.Atan2(head.m2,head.m10);var c=MathF.Cos(yaw);var s=MathF.Sin(yaw);
        return new() {m0=c,m2=s,m3=head.m3-.55f*s,m5=1,m7=head.m7-.14f,m8=-s,m10=c,m11=head.m11-.55f*c};
    }
    public static HmdMatrix34_t Hand(float tilt,float height,float forward)
    {
        var a=tilt*MathF.PI/180;
        return new() {m0=1,m5=MathF.Cos(a),m6=-MathF.Sin(a),m7=height,m9=MathF.Sin(a),m10=MathF.Cos(a),m11=forward};
    }
}
