using System.Diagnostics;
using ShockWave.VrDemo.Shared;

namespace ShockWave.ShaderLab;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        try
        {
            if (args.Contains("--self-test")) return SelfTests.Run(Value(args, "--artifacts") ?? Path.Combine(AppContext.BaseDirectory, "checks"), args.Contains("--warp"));
            var port = int.Parse(Value(args, "--port") ?? "9002");
            if (port is < 1 or > 65535) throw new ArgumentException("OSC port must be 1..65535");
            var receivePort=int.Parse(Value(args,"--vrc-receive") ?? "9001");
            var sendPort=int.Parse(Value(args,"--vrc-send") ?? "9000");
            if(receivePort is <1 or >65535 || sendPort is <1 or >65535) throw new ArgumentException("VR OSC ports must be 1..65535");
            using var form = new PanelForm(port, args.Contains("--demo"),args.Contains("--vr-demo"),receivePort,sendPort);
            Application.Run(form); return 0;
        }
        catch (Exception e)
        {
            if (args.Contains("--self-test"))
            {
                var folder = Value(args, "--artifacts") ?? Path.Combine(AppContext.BaseDirectory, "checks");
                Directory.CreateDirectory(folder); File.WriteAllText(Path.Combine(folder, "failure.txt"), e.ToString());
            }
            else MessageBox.Show(e.Message, "ShockWave Shader Lab", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }
    private static string? Value(string[] args, string key)
    {
        var index = Array.IndexOf(args, key); return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
}

internal sealed class RenderSurface : Control
{
    public RenderSurface() { SetStyle(ControlStyles.Opaque | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true); Dock = DockStyle.Fill; }
    protected override void OnPaintBackground(PaintEventArgs e) { }
    protected override void OnPaint(PaintEventArgs e) { }
}

internal sealed class PanelForm : Form
{
    private readonly RenderSurface surface = new();
    private readonly PanelModel model = new();
    private readonly OscListener listener;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 16 };
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private GpuRenderer? renderer;
    private bool drawing, disposed;
    private int hover, labelRevision = -1, lastPacketCount = -1, lastRejected = -1;
    private long lastSecond = -1;
    private double lastHistory, lastPoll, reloadAt;
    private DateTime shaderStamp;
    private PatternClip? uploadedPattern;
    private readonly bool demo;
    private readonly bool startVr;
    private readonly int vrReceivePort,vrSendPort;
    private VrcDemoLink? vrLink;
    public PanelForm(int port, bool demo,bool startVr=false,int vrReceivePort=9001,int vrSendPort=9000)
    {
        this.demo = demo;
        this.startVr=startVr; this.vrReceivePort=vrReceivePort; this.vrSendPort=vrSendPort;
        Text = "ShockWave Shader Lab — HLSL / OSC preview";
        ClientSize = new(1240, 800); MinimumSize = new(1000, 685); StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(30, 31, 34);
        listener = new OscListener(port);
        Controls.Add(surface);
        surface.MouseDown += MouseDownOnPanel;
        surface.MouseMove += (_, e) =>
        {
            var p = PanelLayout.Logical(e.Location, surface.ClientSize);
            hover = PanelLayout.Hit(p);
            surface.Cursor = hover != 0 ? Cursors.Hand : Cursors.Default;
            if (drawing)
            {
                model.AdvanceTo(clock.Elapsed.TotalSeconds);
                if (PanelLayout.Graph.Contains(p)) model.DrawAt(p);
                else model.Release(commitRecording:false);
            }
        };
        surface.MouseUp += (_, e) => { if (drawing && e.Button == MouseButtons.Left) Release(); };
        surface.MouseCaptureChanged += (_, _) => { if (!surface.Capture && drawing) Release(false); };
        surface.MouseWheel += (_, e) => { if (!drawing) model.ChangePage(e.Delta < 0 ? 1 : -1); };
        Deactivate += (_, _) => { if(drawing || vrLink is null) Release(false); };
        timer.Tick += (_, _) => Tick();
        Shown += (_, _) =>
        {
            try
            {
                renderer = new GpuRenderer(Path.Combine(AppContext.BaseDirectory, "Shaders", "Panel.hlsl"), surface.Width, surface.Height, surface.Handle);
                shaderStamp = LatestShaderWrite(); timer.Start();
                if (demo) listener.SendDemo();
                if(startVr) ToggleVr();
            }
            catch (Exception e) { MessageBox.Show(this, e.Message, "Renderer startup", MessageBoxButtons.OK, MessageBoxIcon.Error); Close(); }
        };
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape) { Release(false); model.StopAll(); }
            if (e.KeyCode == Keys.F5) Reload();
        };
    }
    private void Release(bool commit=true)
    {
        drawing=false; surface.Capture=false; model.AdvanceTo(clock.Elapsed.TotalSeconds); model.Release(commit);
    }
    private void MouseDownOnPanel(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left || renderer is null) return;
        model.AdvanceTo(clock.Elapsed.TotalSeconds);
        var p = PanelLayout.Logical(e.Location, surface.ClientSize);
        var hit = PanelLayout.Hit(p);
        if (hit is >= 1 and <= 6) model.ToggleVisible(hit - 1);
        else if (hit == 10)
        {
            if(model.RecordArmed && model.SelectedCount>0) renderer.ClearHistory();
            drawing=true; surface.Capture=true; model.DrawAt(p);
        }
        else if (hit == 11) renderer.ClearHistory();
        else if (hit == 12) model.ZeroSelected();
        else if (hit == 13) { Release(false); model.StopAll(); }
        else if (hit == 14) listener.SendDemo();
        else if (hit == 15) model.ChangePage(-1);
        else if (hit == 16) model.ChangePage(1);
        else if (hit is >= 20 and <= 22) model.ChangeRoute(hit - 20);
        else if (hit == 23)
        {
            using var picker = new OpenFileDialog { Filter = "HLSL shader|*.hlsl", InitialDirectory = Path.GetDirectoryName(renderer.ShaderPath), FileName = Path.GetFileName(renderer.ShaderPath) };
            if (picker.ShowDialog(this) == DialogResult.OK) { renderer.Reload(picker.FileName); labelRevision = -1; shaderStamp = LatestShaderWrite(); ShowCompileError(); }
        }
        else if (hit == 24) { Reload(); ShowCompileError(); }
        else if(hit==25) model.ToggleRecord();
        else if(hit==26) model.PlaySelected();
        else if(hit==27) model.HoldSelected();
        else if(hit==28) ToggleVr();
    }
    private void ToggleVr()
    {
        if(vrLink is not null) { vrLink.Dispose(); vrLink=null; model.VrStatus="VR OSC: off"; labelRevision=-1; return; }
        try { vrLink=new(model,vrReceivePort,vrSendPort); model.VrStatus=vrLink.Status; }
        catch(Exception e) when(e is System.Net.Sockets.SocketException or InvalidOperationException)
        {
            model.VrStatus="VR OSC: port unavailable";
            MessageBox.Show(this,$"The separate VR demo could not open 127.0.0.1:{vrReceivePort}. Another OSC program may already be using it. " +
                "Close that program for this test, or use --vrc-receive and VRChat's matching output port. No existing port or program was changed.\n\n"+e.Message,"VR OSC demo",MessageBoxButtons.OK,MessageBoxIcon.Information);
        }
        labelRevision=-1;
    }
    private void ShowCompileError()
    {
        if (renderer?.CompileError is string error) MessageBox.Show(this, error, "HLSL compilation", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }
    private DateTime LatestShaderWrite()
    {
        if (renderer is null) return DateTime.MinValue;
        return Directory.EnumerateFiles(Path.GetDirectoryName(renderer.ShaderPath)!, "*", SearchOption.AllDirectories)
            .Where(p => Path.GetExtension(p) is ".hlsl" or ".hlsli").Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max();
    }
    private void Reload() { renderer?.Reload(); labelRevision = -1; }
    private void Tick()
    {
        if (renderer is null) return;
        try
        {
            listener.Drain(model);
            var now = clock.Elapsed.TotalSeconds;
            model.AdvanceTo(now);
            if(vrLink is not null)
            {
                vrLink.Tick(now); model.VrStatus=vrLink.Status;
                if(vrLink.ClearRequested) { renderer.ClearHistory(); vrLink.ClearRequested=false; }
            }
            // Playback uses monotonic time even when no window is being rendered.
            if(WindowState==FormWindowState.Minimized) { lastHistory=now; return; }
            if (now - lastPoll > 0.4)
            {
                lastPoll = now; var stamp = LatestShaderWrite();
                if (stamp != shaderStamp) { shaderStamp = stamp; reloadAt = now + 0.7; }
            }
            if (reloadAt != 0 && now >= reloadAt) { Reload(); reloadAt = 0; }
            renderer.Resize(surface.Width, surface.Height);
            if(!ReferenceEquals(uploadedPattern,model.DisplayPattern))
            {
                uploadedPattern=model.DisplayPattern; renderer.UploadPattern(uploadedPattern);
            }
            renderer.UploadLoopPatterns(model);
            var state = ShaderState.From(model, renderer.Width, renderer.Height, (float)now, hover, listener.Packets);
            var steps = Math.Min(4, (int)((now - lastHistory) * 30));
            for (var i = 0; i < steps; i++) renderer.StepHistory(state);
            lastHistory += steps / 30.0;
            if (now - lastHistory > 4 / 30.0) lastHistory = now;
            if (labelRevision != model.Revision || lastPacketCount != listener.Packets || lastRejected != listener.Rejected || lastSecond != (long)now)
            {
                var status = listener.Fault ?? $"OSC · 127.0.0.1:{listener.Port}";
                using var text = PanelLabels.Create(model, status, renderer.CompileStatus, renderer.Backend, listener.Packets, listener.Rejected);
                renderer.UploadLabels(text);
                labelRevision = model.Revision; lastPacketCount = listener.Packets; lastRejected = listener.Rejected; lastSecond = (long)now;
            }
            renderer.Render(state);
        }
        catch (Exception e) { timer.Stop(); MessageBox.Show(this, e.Message, "Rendering stopped", MessageBoxButtons.OK, MessageBoxIcon.Error); Close(); }
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposed) { disposed = true; model.StopAll(); timer.Stop(); timer.Dispose(); vrLink?.Dispose(); listener.Dispose(); renderer?.Dispose(); }
        base.Dispose(disposing);
    }
}
