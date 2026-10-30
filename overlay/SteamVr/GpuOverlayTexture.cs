#nullable enable
using System.Drawing.Imaging;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Valve.VR;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace ShockWave.Overlay.SteamVr;

// GDI+ still draws the entire UI. Direct3D only copies the finished bitmap;
// no shader, swap chain, draw call or window capture is involved.
internal sealed class GpuOverlayTexture : IDisposable
{
    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private ID3D11Texture2D? upload,present;
    private readonly List<IDisposable> retired=[];
    private int width,height;
    private readonly Size? fixedSize;
    private Bitmap? padded;
    public bool Ready=>present is not null;
    public Size SurfaceSize=>new(width,height);
    public Texture_t Descriptor=>new() {handle=present?.NativePointer ?? 0,eType=ETextureType.DirectX,eColorSpace=EColorSpace.Gamma};
    public GpuOverlayTexture(int adapterIndex=-1,bool software=false,Size? fixedSize=null)
    {
        if(fixedSize is { } size && (size.Width<=0 || size.Height<=0)) throw new ArgumentOutOfRangeException(nameof(fixedSize));
        this.fixedSize=fixedSize;
        using var factory=CreateDXGIFactory1<IDXGIFactory1>();
        IDXGIAdapter1? adapter=null;
        try
        {
            if(adapterIndex>=0 && !software) factory.EnumAdapters1((uint)adapterIndex,out adapter).CheckError();
            D3D11CreateDevice(adapter?.NativePointer ?? 0,software ? DriverType.Warp : adapter is null ? DriverType.Hardware : DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,[FeatureLevel.Level_11_0],out device,out _,out context).CheckError();
        }
        finally {adapter?.Dispose();}
    }
    public void Upload(Bitmap frame)
    {
        if(fixedSize is { } size)
        {
            if(frame.Width>size.Width || frame.Height>size.Height) throw new ArgumentOutOfRangeException(nameof(frame),"Panel exceeds the fixed surface");
            if(frame.Size!=size)
            {
                padded ??=new Bitmap(size.Width,size.Height,PixelFormat.Format32bppArgb);
                using(var g=Graphics.FromImage(padded))
                {
                    g.CompositingMode=CompositingMode.SourceCopy;g.Clear(Color.Transparent);
                    g.DrawImageUnscaled(frame,0,size.Height-frame.Height);
                }
                frame=padded;
            }
        }
        if(frame.Width!=width || frame.Height!=height || present is null) Resize(frame.Width,frame.Height);
        var data=frame.LockBits(new(0,0,frame.Width,frame.Height),ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try
        {
            if(data.Stride<=0) throw new InvalidOperationException("Unexpected panel bitmap row order");
            context.UpdateSubresource(upload!,0,null,data.Scan0,(uint)data.Stride,0);
            // Update the runtime-facing resource only with a whole-image GPU copy.
            context.CopyResource(present!,upload!);context.Flush();
        }
        finally {frame.UnlockBits(data);}
    }
    private void Resize(int w,int h)
    {
        var desc=new Texture2DDescription {Width=(uint)w,Height=(uint)h,MipLevels=1,ArraySize=1,
            Format=Format.B8G8R8A8_UNorm,SampleDescription=SampleDescription.Default,Usage=ResourceUsage.Default};
        var nextUpload=device.CreateTexture2D(desc);
        ID3D11Texture2D nextPresent;
        try
        {
            desc.BindFlags=BindFlags.ShaderResource | BindFlags.RenderTarget;desc.MiscFlags=ResourceOptionFlags.Shared;
            nextPresent=device.CreateTexture2D(desc);
        }
        catch {nextUpload.Dispose();throw;}
        if(upload is not null) retired.Add(upload);if(present is not null) retired.Add(present);
        upload=nextUpload;present=nextPresent;width=w;height=h;
    }
    // The old size remains alive until both overlays accepted the replacement.
    public void PresentationCommitted() {foreach(var resource in retired) resource.Dispose();retired.Clear();}
    public byte[] ReadbackBgra()
    {
        if(present is null) throw new InvalidOperationException("No complete panel image");
        var desc=present.Description;desc.BindFlags=BindFlags.None;desc.MiscFlags=ResourceOptionFlags.None;
        desc.Usage=ResourceUsage.Staging;desc.CPUAccessFlags=CpuAccessFlags.Read;
        using var staging=device.CreateTexture2D(desc);context.CopyResource(staging,present);
        var data=context.Map(staging,0,MapMode.Read);
        try
        {
            var bytes=new byte[width*height*4];
            for(var y=0;y<height;y++) Marshal.Copy(data.DataPointer+y*(int)data.RowPitch,bytes,y*width*4,width*4);
            return bytes;
        }
        finally {context.Unmap(staging,0);}
    }
    public void Dispose()
    {
        PresentationCommitted();present?.Dispose();upload?.Dispose();padded?.Dispose();present=null;upload=null;padded=null;
        context.Dispose();device.Dispose();
    }
}

internal sealed class OverlayVisibility(Action<bool> apply)
{
    private bool shown;
    public void Set(bool visible)
    {
        if(shown==visible) return;
        apply(visible);shown=visible;
    }
    public void Reset()=>shown=false;
}
