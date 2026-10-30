#nullable enable
using System.Buffers;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace ShockWave.Overlay.SteamVr;

internal static class PanelAppearance
{
    public static void ApplyTransparency(Bitmap bitmap,int percent)
    {
        percent=Math.Clamp(percent,0,100);if(percent==0) return;
        var data=bitmap.LockBits(new(0,0,bitmap.Width,bitmap.Height),ImageLockMode.ReadWrite,PixelFormat.Format32bppArgb);
        var stride=Math.Abs(data.Stride);var pixels=ArrayPool<byte>.Shared.Rent(checked(stride*bitmap.Height));
        try
        {
            if(data.Stride>=0) Marshal.Copy(data.Scan0,pixels,0,stride*bitmap.Height);
            else for(var y=0;y<bitmap.Height;y++) Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride),pixels,y*stride,stride);
            for(var y=0;y<bitmap.Height;y++) for(var x=0;x<bitmap.Width;x++)
            {
                var alpha=y*stride+x*4+3;pixels[alpha]=(byte)((pixels[alpha]*(100-percent)+50)/100);
            }
            if(data.Stride>=0) Marshal.Copy(pixels,0,data.Scan0,stride*bitmap.Height);
            else for(var y=0;y<bitmap.Height;y++) Marshal.Copy(pixels,y*stride,IntPtr.Add(data.Scan0,y*data.Stride),stride);
        }
        finally {bitmap.UnlockBits(data);ArrayPool<byte>.Shared.Return(pixels);}
    }
}
