using System.Drawing.Imaging;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using static Vortice.Direct3D11.D3D11;
using static Vortice.DXGI.DXGI;

namespace ShockWave.ShaderLab;

[InlineArray(PanelModel.MaximumFunctions)]
internal struct FunctionStateBuffer { private GraphFunctionState first; }
[StructLayout(LayoutKind.Sequential)]
internal struct GraphFunctionState { public Vector4 State, Color, LoopView; }

[StructLayout(LayoutKind.Sequential)]
internal struct ShaderState
{
    public Vector4 ResolutionTime, State, PageInfo;
    public Vector4 TouchData, LiveColor, PatternData;
    public Vector4 Row0, Row1, Row2, Row3, Row4, Row5, Row6;
    public Vector4 Color0, Color1, Color2, Color3, Color4, Color5, Color6;
    public FunctionStateBuffer Functions;
    public Vector4 ShockData, ShockFlags;
    public static ShaderState From(PanelModel model, int width, int height, float time, int hover, int packets)
    {
        var visible = model.Visible.ToArray();
        Vector4 Row(int index) => index < visible.Length ? new(visible[index].Level, visible[index].Selected ? 1 : 0, visible[index].Connected ? 1 : 0, visible[index].Function.LoopRunning ? 1 : 0) : Vector4.Zero;
        Vector4 Color(int index) => index < visible.Length ? FunctionColors.Vector(visible[index].Function.Color) : Vector4.Zero;
        var liveColor = FunctionColors.Vector(model.LiveColor);
        liveColor.W = !model.AdaptivePaging || model.SelectedCount>0 ? 1 : 0;
        var result = new ShaderState
        {
            ResolutionTime = new(width, height, time, model.Level),
            State = new(model.Touching ? 1 : 0, hover, model.Route, visible.Length),
            PageInfo = new(model.Page, model.PageCount, model.FunctionCount, packets),
            TouchData = new(model.TouchPosition.X, model.TouchPosition.Y, model.Touching ? 1 : 0, model.FunctionCount),
            LiveColor = liveColor,
            PatternData=new(model.DisplayPattern is null ? 0 : 1,(float)model.DisplayPhase,model.RecordArmed ? 1 : 0,model.Recording ? 1 : 0),
            Row0 = Row(0), Row1 = Row(1), Row2 = Row(2), Row3 = Row(3), Row4 = Row(4), Row5 = Row(5), Row6 = Row(6),
            Color0 = Color(0), Color1 = Color(1), Color2 = Color(2), Color3 = Color(3), Color4 = Color(4), Color5 = Color(5), Color6 = Color(6)
        };
        var windowSeconds = model.DisplayPattern?.Duration ?? 8;
        var playhead = model.DisplayPattern is null ? 1 : model.DisplayPhase;
        var windowStart = model.Now - playhead * windowSeconds;
        var i = 0;
        foreach (var f in model.Functions)
        {
            var loopView = Vector4.Zero;
            if (f.Connected && f.Function.LoopRunning && f.Function.Pattern is { } clip)
            {
                var cycles = (windowStart - f.Function.LoopStarted) / clip.Duration;
                // Extend the repeating pattern over the visible window, including
                // before its start. This is a pattern preview, not motor telemetry.
                loopView = new((float)(cycles - Math.Floor(cycles)), (float)(windowSeconds / clip.Duration), (float)playhead, 0);
            }
            result.Functions[i++] = new() { State = new(f.Level, f.Selected ? 1 : 0, f.Connected ? 1 : 0, f.Function.LoopRunning ? 1 : 0), Color = FunctionColors.Vector(f.Function.Color), LoopView = loopView };
        }
        return result;
    }
}

internal sealed class GpuRenderer : IDisposable
{
    private readonly ID3D11Device device;
    private readonly ID3D11DeviceContext context;
    private readonly IDXGIFactory2 factory;
    private readonly IDXGISwapChain1? swapChain;
    private ID3D11Texture2D output = null!;
    private ID3D11RenderTargetView outputView = null!;
    private readonly ID3D11Texture2D[] history = new ID3D11Texture2D[2];
    private readonly ID3D11RenderTargetView[] historyViews = new ID3D11RenderTargetView[2];
    private readonly ID3D11ShaderResourceView[] historyResources = new ID3D11ShaderResourceView[2];
    private readonly ID3D11Texture2D labels;
    private readonly ID3D11ShaderResourceView labelResource;
    private readonly ID3D11Texture2D patternTexture;
    private readonly ID3D11ShaderResourceView patternResource;
    private readonly ID3D11Texture2D loopTexture;
    private readonly ID3D11ShaderResourceView loopResource;
    private readonly PatternClip?[] loopClips = new PatternClip?[PanelModel.MaximumFunctions];
    private readonly float[] loopSamples = new float[240 * PanelModel.MaximumFunctions];
    private readonly ID3D11Buffer constants;
    private readonly ID3D11SamplerState sampler;
    private readonly ID3D11RasterizerState rasterizer;
    private ID3D11VertexShader? vertexShader;
    private ID3D11PixelShader? panelShader, historyShader;
    private int historyIndex;
    public string Backend { get; }
    public int Width { get; private set; }
    public int Height { get; private set; }
    public string ShaderPath { get; private set; }
    public string CompileStatus { get; private set; } = "HLSL ready";
    public string? CompileError { get; private set; }

    public GpuRenderer(string path, int width, int height, nint handle = 0, bool forceWarp = false)
    {
        var flags = DeviceCreationFlags.BgraSupport;
        var result = D3D11CreateDevice(nint.Zero, forceWarp ? DriverType.Warp : DriverType.Hardware, flags,
            [FeatureLevel.Level_11_0], out device, out _, out context);
        Backend = forceWarp ? "Direct3D 11 / WARP" : "Direct3D 11 / GPU";
        if (result.Failure && !forceWarp)
        {
            D3D11CreateDevice(nint.Zero, DriverType.Warp, flags, [FeatureLevel.Level_11_0], out device, out _, out context).CheckError();
            Backend = "Direct3D 11 / WARP";
        }
        else result.CheckError();
        factory = CreateDXGIFactory1<IDXGIFactory2>();
        if (handle != 0)
        {
            swapChain = factory.CreateSwapChainForHwnd(device, handle, new SwapChainDescription1
            {
                Width = (uint)width, Height = (uint)height, Format = Format.B8G8R8A8_UNorm,
                BufferCount = 2, BufferUsage = Usage.RenderTargetOutput, SampleDescription = SampleDescription.Default,
                SwapEffect = SwapEffect.FlipDiscard, Scaling = Scaling.Stretch, AlphaMode = AlphaMode.Ignore
            });
            factory.MakeWindowAssociation(handle, WindowAssociationFlags.IgnoreAltEnter);
        }
        Width = width; Height = height;
        MakeOutput();
        for (var i = 0; i < 2; i++)
        {
            history[i] = Texture(240, 1, Format.R32G32B32A32_Float, BindFlags.RenderTarget | BindFlags.ShaderResource);
            historyViews[i] = device.CreateRenderTargetView(history[i]);
            historyResources[i] = device.CreateShaderResourceView(history[i]);
        }
        labels = Texture(PanelLayout.Width, PanelLayout.Height, Format.B8G8R8A8_UNorm, BindFlags.ShaderResource);
        labelResource = device.CreateShaderResourceView(labels);
        patternTexture=Texture(240,1,Format.R32G32B32A32_Float,BindFlags.ShaderResource);
        patternResource=device.CreateShaderResourceView(patternTexture);
        UploadPattern(null);
        loopTexture=Texture(240,PanelModel.MaximumFunctions,Format.R32_Float,BindFlags.ShaderResource);
        loopResource=device.CreateShaderResourceView(loopTexture);
        UploadLoopTexture();
        constants = device.CreateBuffer((uint)Unsafe.SizeOf<ShaderState>(), BindFlags.ConstantBuffer);
        sampler = device.CreateSamplerState(new SamplerDescription(Filter.MinMagMipLinear, TextureAddressMode.Clamp, TextureAddressMode.Clamp, TextureAddressMode.Clamp));
        rasterizer = device.CreateRasterizerState(new RasterizerDescription(CullMode.None, FillMode.Solid));
        ShaderPath = Path.GetFullPath(path);
        if (!Reload(path)) throw new InvalidOperationException(CompileError);
        ClearHistory();
    }

    private ID3D11Texture2D Texture(int w, int h, Format format, BindFlags bind) => device.CreateTexture2D(new Texture2DDescription
    {
        Width = (uint)w, Height = (uint)h, MipLevels = 1, ArraySize = 1, Format = format,
        SampleDescription = SampleDescription.Default, Usage = ResourceUsage.Default, BindFlags = bind
    });
    private void MakeOutput()
    {
        output = swapChain is null ? Texture(Width, Height, Format.B8G8R8A8_UNorm, BindFlags.RenderTarget) : swapChain.GetBuffer<ID3D11Texture2D>(0);
        outputView = device.CreateRenderTargetView(output);
    }
    public void Resize(int w, int h)
    {
        if (w <= 0 || h <= 0 || (w == Width && h == Height)) return;
        context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        outputView.Dispose(); output.Dispose();
        Width = w; Height = h;
        swapChain?.ResizeBuffers(2, (uint)w, (uint)h, Format.B8G8R8A8_UNorm, SwapChainFlags.None).CheckError();
        MakeOutput();
    }
    public bool Reload(string? path = null)
    {
        ID3D11VertexShader? nextVs = null;
        ID3D11PixelShader? nextPs = null, nextHistory = null;
        try
        {
            var nextPath = Path.GetFullPath(path ?? ShaderPath);
            var source = Expand(nextPath, Path.GetDirectoryName(nextPath)!, 0);
            using var vs = Compile(source, "VSMain", nextPath, "vs_5_0");
            using var ps = Compile(source, "PSMain", nextPath, "ps_5_0");
            using var hs = Compile(source, "HistoryPS", nextPath, "ps_5_0");
            nextVs = device.CreateVertexShader(vs.AsSpan());
            nextPs = device.CreatePixelShader(ps.AsSpan());
            nextHistory = device.CreatePixelShader(hs.AsSpan());
            vertexShader?.Dispose(); panelShader?.Dispose(); historyShader?.Dispose();
            vertexShader = nextVs; panelShader = nextPs; historyShader = nextHistory;
            ShaderPath = nextPath; CompileError = null; CompileStatus = "HLSL ready"; return true;
        }
        catch (Exception e)
        {
            nextVs?.Dispose(); nextPs?.Dispose(); nextHistory?.Dispose();
            CompileError = e.Message; CompileStatus = "Shader error — previous version kept"; return false;
        }
    }
    private static Blob Compile(string source, string entry, string path, string profile)
    {
        var result = Compiler.Compile(source, entry, path, profile, out var code, out var errors);
        var error = errors?.AsString(); errors?.Dispose();
        if (result.Failure) { code?.Dispose(); throw new InvalidOperationException(error ?? result.ToString()); }
        return code;
    }
    private static string Expand(string path, string root, int depth)
    {
        if (depth > 8) throw new FormatException("Shader include nesting limit");
        return Regex.Replace(File.ReadAllText(path), "(?m)^\\s*#include\\s+\"([^\"]+)\"\\s*$", m =>
        {
            var included = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(path)!, m.Groups[1].Value));
            if (!included.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new FormatException("Shader includes must stay inside the selected shader folder");
            return Expand(included, root, depth + 1);
        });
    }
    private void Bind(ShaderState state)
    {
        context.UpdateSubresource(in state, constants);
        context.IASetInputLayout(null);
        context.IASetPrimitiveTopology(PrimitiveTopology.TriangleList);
        context.RSSetState(rasterizer);
        context.OMSetBlendState(null);
        context.OMSetDepthStencilState(null);
        context.VSSetShader(vertexShader);
        context.PSSetConstantBuffer(0, constants);
        context.PSSetSampler(0, sampler);
    }
    public void ClearHistory()
    {
        context.PSSetShaderResource(0, null!);
        context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        foreach (var view in historyViews) context.ClearRenderTargetView(view, new Color4(0, 0, 0, 0));
        historyIndex = 0;
    }
    public void StepHistory(ShaderState state)
    {
        var next = 1 - historyIndex;
        context.PSSetShaderResource(0, null!);
        context.OMSetRenderTargets(historyViews[next]);
        context.RSSetViewport(new Viewport(240, 1));
        Bind(state); context.PSSetShader(historyShader);
        context.PSSetShaderResource(0, historyResources[historyIndex]);
        context.Draw(3, 0);
        context.PSSetShaderResource(0, null!);
        context.OMSetRenderTargets((ID3D11RenderTargetView)null!);
        historyIndex = next;
    }
    public void UploadLabels(Bitmap bitmap)
    {
        context.PSSetShaderResource(1, null!);
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try { context.UpdateSubresource(labels, 0, null, data.Scan0, (uint)data.Stride, 0); }
        finally { bitmap.UnlockBits(data); }
    }
    public unsafe void UploadPattern(PatternClip? clip)
    {
        context.PSSetShaderResource(2,null!);
        var values=new Vector4[240];
        if(clip is not null) for(var i=0;i<240;i++) values[i]=new(clip.At(i/239.0*clip.Duration),1,0,1);
        fixed(Vector4* data=values) context.UpdateSubresource(patternTexture,0,null,(nint)data,240*16,0);
    }
    public bool UploadLoopPatterns(PanelModel model)
        => UploadLoopPatterns(model.Functions.Select(row=>row.Connected ? row.Function.Pattern : null).ToArray());
    public bool UploadLoopPatterns(IReadOnlyList<PatternClip?> rows)
    {
        var changed=false;
        for(var row=0;row<loopClips.Length;row++)
        {
            var clip=row<rows.Count ? rows[row] : null;
            if(ReferenceEquals(loopClips[row],clip)) continue;
            loopClips[row]=clip; changed=true;
            for(var x=0;x<240;x++) loopSamples[row*240+x]=clip?.At(x/239.0*clip.Duration) ?? 0;
        }
        if(changed) UploadLoopTexture();
        return changed;
    }
    private unsafe void UploadLoopTexture()
    {
        context.PSSetShaderResource(3,null!);
        fixed(float* data=loopSamples) context.UpdateSubresource(loopTexture,0,null,(nint)data,240*4,0);
    }
    public void Render(ShaderState state, bool present = true)
    {
        context.PSSetShaderResource(0, null!);
        context.OMSetRenderTargets(outputView);
        context.RSSetViewport(new Viewport(Width, Height));
        Bind(state); context.PSSetShader(panelShader);
        context.PSSetShaderResource(0, historyResources[historyIndex]);
        context.PSSetShaderResource(1, labelResource);
        context.PSSetShaderResource(2,patternResource);
        context.PSSetShaderResource(3,loopResource);
        context.Draw(3, 0);
        if (present && swapChain is not null) swapChain.Present(1, PresentFlags.None).CheckError();
    }
    private ID3D11Texture2D Readback(ID3D11Texture2D texture)
    {
        var desc = texture.Description;
        desc.BindFlags = BindFlags.None; desc.Usage = ResourceUsage.Staging; desc.CPUAccessFlags = CpuAccessFlags.Read; desc.MiscFlags = ResourceOptionFlags.None;
        var staging = device.CreateTexture2D(desc);
        context.CopyResource(staging, texture);
        return staging;
    }
    public unsafe Vector4[] ReadHistory()
    {
        using var staging = Readback(history[historyIndex]);
        var data = context.Map(staging, 0, MapMode.Read);
        try { return new ReadOnlySpan<Vector4>((void*)data.DataPointer, 240).ToArray(); }
        finally { context.Unmap(staging, 0); }
    }
    public unsafe Bitmap Capture()
    {
        using var staging = Readback(output);
        var mapped = context.Map(staging, 0, MapMode.Read);
        var bitmap = new Bitmap(Width, Height, PixelFormat.Format32bppArgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, Width, Height), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            for (var y = 0; y < Height; y++)
                Buffer.MemoryCopy((byte*)mapped.DataPointer + y * mapped.RowPitch, (byte*)data.Scan0 + y * data.Stride, data.Stride, Width * 4);
        }
        finally { bitmap.UnlockBits(data); context.Unmap(staging, 0); }
        return bitmap;
    }
    public void Dispose()
    {
        context.ClearState(); context.Flush();
        vertexShader?.Dispose(); panelShader?.Dispose(); historyShader?.Dispose();
        outputView.Dispose(); output.Dispose();
        foreach (var view in historyViews) view.Dispose();
        foreach (var view in historyResources) view.Dispose();
        foreach (var texture in history) texture.Dispose();
        labelResource.Dispose(); labels.Dispose(); patternResource.Dispose(); patternTexture.Dispose(); loopResource.Dispose(); loopTexture.Dispose(); constants.Dispose(); sampler.Dispose(); rasterizer.Dispose();
        swapChain?.Dispose(); factory.Dispose(); context.Dispose(); device.Dispose();
    }
}
