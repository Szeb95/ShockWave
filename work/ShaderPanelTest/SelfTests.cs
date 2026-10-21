using System.Buffers.Binary;
using System.Drawing.Imaging;
using System.Net;
using System.Net.Sockets;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace ShockWave.ShaderLab;

internal static class SelfTests
{
    public static int Run(string directory, bool warp)
    {
        Directory.CreateDirectory(directory);
        List<string> checks = [];
        void Check(bool condition, string name)
        {
            if (!condition) throw new InvalidOperationException(name);
            checks.Add(name);
        }
        void Reject(Action action, string name)
        {
            var rejected = false;
            try { action(); } catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException or DecoderFallbackException) { rejected = true; }
            Check(rejected, name);
        }
        var model = new PanelModel();
        PatternTests.Run(Check);
        VrcDemoTests.Run(directory,Check);
        var encoded = OscCodec.Encode("/test", "Nora ✨", 42, 0.25f, true, false);
        var decoded = OscCodec.Decode(encoded).Single();
        Check(decoded.Address == "/test" && decoded.Arguments.SequenceEqual(new object[] { "Nora ✨", 42, 0.25f, true, false }), "OSC UTF-8/string/int/float/bool round trip");
        using var bundle = new MemoryStream();
        bundle.Write(Encoding.ASCII.GetBytes("#bundle\0"));
        bundle.Write(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 });
        var word = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(word, encoded.Length); bundle.Write(word); bundle.Write(encoded);
        Check(OscCodec.Decode(bundle.ToArray()).Single().Address == "/test", "Immediate OSC bundle decoding");
        var scheduled = bundle.ToArray(); scheduled[15] = 2;
        Reject(() => OscCodec.Decode(scheduled), "Future scheduled bundle rejected");
        Reject(() => OscCodec.Decode(encoded[..^1]), "Truncated OSC rejected");
        var padding = OscCodec.Encode("/a", 1); padding[3] = 1;
        Reject(() => OscCodec.Decode(padding), "Nonzero OSC padding rejected");
        Reject(() => OscListener.Apply(model, new("/szebs/shader/toy", ["a", "Toy", 2, 50])), "Invalid connection flag rejected");
        Reject(() => PanelModel.Validate("a", "Toy", true, 50, float.NaN), "NaN level rejected");
        Reject(() => PanelModel.Validate("a", "Toy", true, 50, 1.1f), "Out of range level rejected");
        Reject(() => PanelModel.Validate("a", "Toy", true, 101, null), "Out of range battery rejected");

        // Real socket reception, using a separate ephemeral loopback port.
        using var receiver = new OscListener(0);
        receiver.SendDemo();
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (model.Toys.Count == 0 && DateTime.UtcNow < deadline) { receiver.Drain(model); Thread.Sleep(10); }
        Check(model.Toys.Count == 8 && model.FunctionCount == 10 && receiver.Packets > 0, "Live UDP OSC inventory expands eight demo toys into ten function rows");
        Check(model.Functions.All(t => !t.Selected && t.Level == 0), "New function inventory starts unselected at zero");
        Check(model.Toys.Any(t => t.Name.Contains('✨')), "Dynamic Unicode name accepted without a model catalogue");
        Check(model.PageCount == 2 && model.Visible.Count() == 6, "All devices available through bounded paging");
        model.ToggleVisible(0); model.Draw(0.6f); model.Release();
        Check(!model.Touching && model.Toys[0].Functions[0].Level == 0.6f, "Mouse release retains selected function level");
        model.ToggleVisible(0);
        model.ToggleVisible(1); model.Draw(0.3f); model.Release();
        Check(model.Toys[0].Functions[0].Level == 0.6f && model.Toys[1].Functions[0].Level == 0.3f, "Deselect holds A while drawing edits B independently");
        model.ToggleVisible(0);
        Check(model.Toys[0].Functions[0].Level == 0.6f, "Selecting again does not copy another function's level");
        model.ToggleVisible(0); model.ZeroSelected();
        Check(model.Toys[0].Functions[0].Level == 0.6f && model.Toys[1].Functions[0].Level == 0, "Zero selected leaves deselected held function unchanged");
        var refreshed = PanelModel.ParseInventory(OscListener.DemoJson).ToArray();
        refreshed[0] = refreshed[0] with { Name = "My renamed toy" };
        model.ReplaceInventory(refreshed);
        Check(model.Toys[0].Name == "My renamed toy" && model.Toys[0].Functions[0].Level == 0.6f && model.Toys[1].Functions[0].Selected, "Inventory rename preserves binding, selection and held levels when level omitted");
        Reject(() => model.ReplaceInventory(PanelModel.ParseInventory("[{\"id\":\"a\",\"name\":\"Good\"},{\"id\":\"a\",\"name\":\"Duplicate\"}]")), "Duplicate inventory aliases rejected atomically");
        Check(model.Toys.Count == 8 && model.Toys[0].Functions[0].Level == 0.6f, "Rejected snapshot leaves prior state intact");
        model.ChangePage(1); Check(model.Visible.Count() == 4, "Second page shows all remaining connected functions");
        model.ToggleVisible(3); model.Draw(0.45f); model.Release();
        Check(model.Toys[7].Functions[0].Level == 0.45f, "Selection on second page edits its stable toy/function binding");
        model.StopAll(); Check(model.Functions.All(t => t.Level == 0), "Stop all zeros selected and deselected functions");
        model.Upsert(new("demo-lush", "Lush family", true, 82, 0.75f));
        model.Upsert(new("demo-lush", "Lush family", false, 82, null));
        model.Upsert(new("demo-lush", "Lush family", true, 82, null));
        Check(model.Toys[0].Functions[0].Level == 0 && !model.Toys[0].Functions[0].Selected, "Disconnect/reconnect restores no old activity");

        var split = new PanelModel(); split.ReplaceInventory(PanelModel.ParseInventory(OscListener.DemoJson));
        split.ToggleVisible(1); split.Draw(0.6f); split.Release(); split.ToggleVisible(1);
        split.ToggleVisible(2); split.Draw(0.3f); split.Release();
        Check(split.Toys[1].Functions[0].Level == 0.6f && split.Toys[1].Functions[1].Level == 0.3f && split.Toys[0].Functions[0].Level == 0,
            "Two functions on the same toy hold independent levels without changing another toy");
        split.ToggleVisible(1);
        Check(split.Toys[1].Functions[0].Level == 0.6f, "Reselecting a sibling function retains its previous level");
        split.ToggleVisible(1); split.ZeroSelected();
        Check(split.Toys[1].Functions[0].Level == 0.6f && split.Toys[1].Functions[1].Level == 0, "Zero selected affects only the selected sibling function");
        split.Draw(0.3f); split.Release();
        var splitRefresh = PanelModel.ParseInventory(OscListener.DemoJson).ToArray();
        splitRefresh[1] = splitRefresh[1] with { Name = "Renamed Nora", Functions = [new("r", "Rotation renamed"), new("v", "Vibration renamed")] };
        split.ReplaceInventory(splitRefresh);
        Check(split.Toys[1].Functions[0].Id == "r" && split.Toys[1].Functions[0].Selected && split.Toys[1].Functions[0].Level == 0.3f && split.Toys[1].Functions[1].Level == 0.6f,
            "Reordered and renamed functions preserve their scoped aliases, selection and held levels");
        Check(split.Toys[1].Functions[0].Color == FunctionColors.For("demo-nora", "r") && split.Toys[1].Functions[1].Color == FunctionColors.For("demo-nora", "v"), "Function colors remain stable across rename and reorder");
        Check(split.Toys[1].Functions[0].Color != split.Toys[1].Functions[1].Color, "Sibling functions receive different colors");
        Check(split.Toys[0].Functions[0].Id == "v" && split.Toys[0].Functions[0].Level == 0, "The same function alias on different toys stays independent");
        OscListener.Apply(split, new("/szebs/shader/toy", ["demo-nora", "Metadata refresh", true, 55]));
        Check(split.Toys[1].Functions.Count == 2 && split.Toys[1].Functions[1].Level == 0.6f, "Legacy metadata update preserves a multi-function inventory");
        Reject(() => OscListener.Apply(split, new("/szebs/shader/toy", ["demo-nora", "Invalid level update", true, 55, 0.9f])), "Ambiguous toy-level update rejected for a multi-function toy");
        Check(split.Toys[1].Name == "Metadata refresh" && split.Toys[1].Functions[1].Level == 0.6f, "Rejected partial update leaves all names and levels unchanged");
        var invalidSplit = splitRefresh.ToArray();
        invalidSplit[0] = invalidSplit[0] with { Name = "Must not be applied" };
        invalidSplit[1] = invalidSplit[1] with { Functions = null, Level = 0.9f };
        Reject(() => split.ReplaceInventory(invalidSplit), "Ambiguous late entry rejects the complete snapshot atomically");
        Check(split.Toys[0].Name == "Lush family" && split.Toys[1].Functions[1].Level == 0.6f, "Earlier valid entries do not leak out of a rejected snapshot");
        Reject(() => PanelModel.ParseInventory("[{\"id\":\"a\",\"name\":\"Toy\",\"functions\":[{\"id\":\"v\",\"name\":\"First\"},{\"id\":\"v\",\"name\":\"Duplicate\"}]}]"), "Duplicate function alias within one toy rejected");
        Reject(() => PanelModel.ParseInventory("[{\"id\":\"a\",\"name\":\"Toy\",\"functions\":[]}]"), "Empty explicit function inventory rejected");
        var removed = splitRefresh.ToArray(); removed[1] = removed[1] with { Functions = [new("v", "Vibration")] };
        split.ReplaceInventory(removed);
        Check(split.FunctionCount == 9 && split.Toys[1].Functions[0].Level == 0.6f, "Removing a function preserves its remaining sibling's level");
        split.ReplaceInventory(splitRefresh);
        Check(split.Toys[1].Functions[0].Level == 0 && !split.Toys[1].Functions[0].Selected && split.Toys[1].Functions[1].Level == 0.6f, "A removed function reappears without replaying old activity");
        split.Upsert(new("demo-nora", "Nora", false, 55, null)); split.Upsert(new("demo-nora", "Nora", true, 55, null));
        Check(split.Toys[1].Functions.Count == 2 && split.Toys[1].Functions.All(f => f.Level == 0 && !f.Selected), "Device disconnect/reconnect clears every sibling function");
        var legacy = new PanelModel();
        OscListener.Apply(legacy, new("/szebs/shader/toy", ["legacy", "Old sender", true, 80, 0.4f]));
        Check(legacy.FunctionCount == 1 && legacy.Toys[0].Functions[0].Name == "Input" && legacy.Toys[0].Functions[0].Level == 0.4f, "Legacy single-row sender remains compatible without guessing a capability");
        var fullFunctions = Enumerable.Range(0, 128).Select(i => new ToyUpdate("many-" + i, "Toy " + i, true, -1, null, [new("a", "First"), new("b", "Second")])).ToArray();
        split.ReplaceInventory(fullFunctions); split.ChangePage(1000);
        Check(split.FunctionCount == 256 && split.Page == 42 && split.Visible.Count() == 4, "Function paging exposes all 256 rows including the final page");
        Reject(() => split.Upsert(fullFunctions[0] with { Functions = [new("a", "First"), new("b", "Second"), new("c", "Overflow")] }), "Excess function update rejected without truncation");
        Reject(() => split.ReplaceInventory(fullFunctions.Select((t, i) => i == 0 ? t with { Functions = [new("a", "First"), new("b", "Second"), new("c", "Overflow")] } : t).ToArray()), "Excess function snapshot rejected atomically");
        Check(split.FunctionCount == 256 && split.Page == 42, "Rejected overflow retains the full inventory and current page");
        var many = Enumerable.Range(0, 128).Select(i => new ToyUpdate("test-" + i, "Toy " + i, true, -1, null)).ToArray();
        model.ReplaceInventory(many); model.ChangePage(1000);
        Check(model.Toys.Count == 128 && model.Page == 21 && model.Visible.Count() == 2, "Capacity and final page expose every supported test device");
        Reject(() => model.Upsert(new("overflow", "Extra", true, -1, null)), "Inventory capacity is explicit, not silent truncation");
        Check(PanelLayout.Hit(new(100, 200)) == 1 && PanelLayout.Hit(new(600, 640)) == 12, "Mouse hit regions align toy and Zero buttons");
        var scaled = PanelLayout.Logical(new(500, 325), new(1000, 650));
        Check(Math.Abs(scaled.X - 620) < 0.01 && Math.Abs(scaled.Y - 400) < 0.01, "Mouse coordinates scale with window size");
        Check(PanelLayout.Intensity(new(500, 230)) == 1 && PanelLayout.Intensity(new(500, 582)) == 0, "Graph top/bottom emulate full/zero touch");
        using (var sender = new UdpClient())
        {
            var bad = new byte[] { 1, 2, 3 };
            sender.Send(bad, bad.Length, new IPEndPoint(IPAddress.Loopback, receiver.Port));
            deadline = DateTime.UtcNow.AddSeconds(1);
            while (receiver.Rejected == 0 && DateTime.UtcNow < deadline) Thread.Sleep(10);
        }
        Check(receiver.Rejected > 0, "Malformed UDP packet rejected without stopping listener");

        model.ReplaceInventory(PanelModel.ParseInventory(OscListener.DemoJson)); model.ChangePage(-1000);
        model.ToggleVisible(0); model.ToggleVisible(1);
        var shader = Path.Combine(AppContext.BaseDirectory, "Shaders", "Panel.hlsl");
        using var gpu = new GpuRenderer(shader, 1240, 800, forceWarp: warp);
        for (var i = 0; i < 240; i++)
        {
            model.Draw(0.45f + 0.32f * MathF.Sin(i / 17f));
            gpu.StepHistory(ShaderState.From(model, 1240, 800, i / 30f, 0, receiver.Packets));
        }
        model.Release(); model.ToggleVisible(0); model.ChangeRoute(2);
        var history = gpu.ReadHistory();
        Check(history.Length == 240 && history.All(h => h.Y == 1), "HLSL ping-pong history contains exactly 240 valid samples");
        Check(Math.Abs(history[25].X - (0.45f + 0.32f * MathF.Sin(25 / 17f))) < 0.0001, "GPU retains earlier touch heights while new input changes");
        Check(history.Max(h => h.X) - history.Min(h => h.X) > 0.6f, "GPU history captures the full waveform range");
        using (var labels = PanelLabels.Create(model, "OSC · 127.0.0.1:9002", gpu.CompileStatus, gpu.Backend, receiver.Packets, receiver.Rejected)) gpu.UploadLabels(labels);
        gpu.Render(ShaderState.From(model, 1240, 800, 8, 0, receiver.Packets));
        using (var image = gpu.Capture())
        {
            image.Save(Path.Combine(directory, "panel-preview.png"), ImageFormat.Png);
            Check(image.GetPixel(500, 400).G != image.GetPixel(10, 400).G, "Actual Direct3D render contains the graph panel");
            var curve = 0; var text = 0;
            for (var y = 232; y < 580; y += 2) for (var x = 408; x < 1168; x += 2)
            {
                var pixel = image.GetPixel(x, y); var live = model.LiveColor;
                if (Math.Abs(pixel.R-live.R)+Math.Abs(pixel.G-live.G)+Math.Abs(pixel.B-live.B)<25) curve++;
            }
            for (var y = 197; y < 214; y++) for (var x = 55; x < 230; x++)
            {
                var pixel = image.GetPixel(x, y); if (Math.Max(pixel.R,Math.Max(pixel.G,pixel.B)) > 190) text++;
            }
            Check(curve > 160, "Rendered shader contains a visible smooth waveform in the live function color");
            Check(text > 180, "Shader composites readable dynamic function-name glyphs");
        }
        gpu.Resize(1000, 650); gpu.Render(ShaderState.From(model, 1000, 650, 8, 0, receiver.Packets));
        using (var image = gpu.Capture()) image.Save(Path.Combine(directory, "panel-small.png"), ImageFormat.Png);
        Check(gpu.Width == 1000 && gpu.Height == 650, "Renderer resize produces the minimum-size preview");
        var missing = gpu.Reload(Path.Combine(directory, "missing.hlsl"));
        Check(!missing && gpu.CompileError is not null && gpu.ShaderPath == shader, "Failed shader reload retains last working pipeline");
        Check(gpu.Reload(), "Working HLSL reload recovers after an error");
        gpu.ClearHistory(); Check(gpu.ReadHistory().All(h => h.Y == 0), "Clear resets only the GPU history");
        Check(model.Toys[0].Functions[0].Level > 0, "Clearing the graph does not zero held function levels");
        model.Draw(0.2f); gpu.StepHistory(ShaderState.From(model, 1000, 650, 9, 0, 0));
        history = gpu.ReadHistory();
        Check(history.Take(239).All(h => h.Y == 0) && history[239].Y == 1 && Math.Abs(history[239].X - 0.2f) < 0.0001, "First sample after Clear enters at NOW without stale history");

        var pointer = new PanelModel(); pointer.Upsert(new("pointer", "Pointer demo", true, 80, null, [new("v", "Vibration")]));
        pointer.ToggleVisible(0);
        pointer.DrawAt(new(PanelLayout.Graph.X + PanelLayout.Graph.Width * 0.31f, PanelLayout.Graph.Y + PanelLayout.Graph.Height * 0.37f));
        Check(Math.Abs(pointer.TouchPosition.X-0.31f)<0.0001 && Math.Abs(pointer.TouchPosition.Y-0.63f)<0.0001 && Math.Abs(pointer.Level-0.63f)<0.0001,
            "Touch position supplies independent normalized X and intensity Y");
        gpu.Resize(1240,800); gpu.ClearHistory();
        using(var blank = new Bitmap(1240,800)) gpu.UploadLabels(blank);
        Point CursorPoint() => new((int)Math.Round(PanelLayout.Graph.X + pointer.TouchPosition.X*PanelLayout.Graph.Width), (int)Math.Round(PanelLayout.Graph.Y + (1-pointer.TouchPosition.Y)*PanelLayout.Graph.Height));
        bool Bright(System.Drawing.Color color) => color.R>225 && color.G>225 && color.B>225;
        var firstPoint=CursorPoint();
        gpu.Render(ShaderState.From(pointer,1240,800,1,0,0));
        using(var image=gpu.Capture())
        {
            Check(Bright(image.GetPixel(firstPoint.X,firstPoint.Y)), "HLSL dot renders at the touch position instead of the right edge");
            var ring=image.GetPixel(firstPoint.X+11,firstPoint.Y);
            Check(Math.Max(ring.R,Math.Max(ring.G,ring.B))>140, "Larger HLSL touch ring is visible eleven pixels from the centre");
        }
        pointer.DrawAt(new(PanelLayout.Graph.X + PanelLayout.Graph.Width * 0.70f, PanelLayout.Graph.Y + PanelLayout.Graph.Height * 0.37f));
        Check(Math.Abs(pointer.Level-0.63f)<0.0001, "Sideways touch movement does not change intensity or edit time");
        gpu.Render(ShaderState.From(pointer,1240,800,1,0,0));
        using(var image=gpu.Capture()) Check(Bright(image.GetPixel(CursorPoint().X,CursorPoint().Y)) && !Bright(image.GetPixel(firstPoint.X,firstPoint.Y)), "GPU dot follows sideways movement and leaves its previous position");
        pointer.Release(); gpu.Render(ShaderState.From(pointer,1240,800,1,0,0));
        using(var image=gpu.Capture()) Check(!Bright(image.GetPixel(CursorPoint().X,CursorPoint().Y)) && Math.Abs(pointer.Toys[0].Functions[0].Level-0.63f)<0.0001,
            "Release hides touch feedback while preserving the function level");
        var held = new PanelModel();
        held.ReplaceInventory(Enumerable.Range(0,7).Select(i=>new ToyUpdate("held-"+i,"Held toy "+i,true,-1,i==6 ? 0.43f : 0)).ToArray());
        var heldPoint=new Point((int)PanelLayout.Graph.X+30,(int)Math.Round(PanelLayout.Graph.Y+PanelLayout.Graph.Height*0.57f));
        var heldState=ShaderState.From(held,1240,800,1,0,0);
        Check(held.Visible.All(f=>f.Level==0) && heldState.Functions[6].State.X==0.43f, "Off-page held function data reaches the shader independently of visible rows");
        gpu.Render(heldState);
        Color heldPixel;
        using(var image=gpu.Capture()) heldPixel=image.GetPixel(heldPoint.X,heldPoint.Y);
        held.StopAll(); gpu.Render(ShaderState.From(held,1240,800,1,0,0));
        using(var image=gpu.Capture())
        {
            var stopped=image.GetPixel(heldPoint.X,heldPoint.Y);
            Check(Math.Abs(heldPixel.R-stopped.R)+Math.Abs(heldPixel.G-stopped.G)+Math.Abs(heldPixel.B-stopped.B)>60, "GPU shows the off-page held colored line and Stop removes it");
        }

        // Review capture: two held sibling functions plus one live touch dot.
        model.ReplaceInventory(PanelModel.ParseInventory(OscListener.DemoJson)); model.StopAll(); model.ChangePage(-1000);
        foreach(var function in model.Functions) function.Function.Selected=false;
        model.ToggleVisible(1); model.Draw(0.5f); model.Release(); model.ToggleVisible(1);
        model.ToggleVisible(2); model.Draw(0.25f); model.Release(); model.ToggleVisible(2);
        model.ToggleVisible(4);
        gpu.ClearHistory();
        for(var i=0;i<240;i++) { model.Draw(0.45f+0.32f*MathF.Sin(i/17f)); gpu.StepHistory(ShaderState.From(model,1240,800,i/30f,0,0)); }
        model.DrawAt(new(PanelLayout.Graph.X+PanelLayout.Graph.Width*0.60f,PanelLayout.Graph.Y+PanelLayout.Graph.Height*0.35f));
        using(var labels=PanelLabels.Create(model,"OSC · 127.0.0.1:9002",gpu.CompileStatus,gpu.Backend,receiver.Packets,receiver.Rejected)) gpu.UploadLabels(labels);
        gpu.Render(ShaderState.From(model,1240,800,8,0,receiver.Packets));
        using(var image=gpu.Capture()) image.Save(Path.Combine(directory,"panel-preview.png"),ImageFormat.Png);
        gpu.Resize(1000,650); gpu.Render(ShaderState.From(model,1000,650,8,0,receiver.Packets));
        using(var image=gpu.Capture()) image.Save(Path.Combine(directory,"panel-small.png"),ImageFormat.Png);

        var patternPreview=PatternTests.Preview(); gpu.Resize(1240,800); gpu.UploadPattern(patternPreview.DisplayPattern); gpu.ClearHistory();
        using(var labels=PanelLabels.Create(patternPreview,"OSC · 127.0.0.1:9002",gpu.CompileStatus,gpu.Backend,receiver.Packets,receiver.Rejected)) gpu.UploadLabels(labels);
        var patternState=ShaderState.From(patternPreview,1240,800,5.4f,0,receiver.Packets);
        Check(patternState.PatternData.X==1 && Math.Abs(patternState.PatternData.Y-0.35f)<0.001,"Playback phase and recorded pattern mode reach the shader");
        gpu.Render(patternState);
        using(var image=gpu.Capture())
        {
            var waveform=0; var live=patternPreview.LiveColor;
            for(var y=232;y<580;y+=2) for(var x=408;x<1168;x+=2)
            { var pixel=image.GetPixel(x,y); if(Math.Abs(pixel.R-live.R)+Math.Abs(pixel.G-live.G)+Math.Abs(pixel.B-live.B)<25) waveform++; }
            Check(waveform>160 && gpu.ReadHistory().All(p=>p.Y==0),"HLSL renders the recorded waveform from memory independently of cleared live history");
            image.Save(Path.Combine(directory,"panel-preview.png"),ImageFormat.Png);
        }
        var phaseX=(int)Math.Round(PanelLayout.Graph.X+patternPreview.DisplayPhase*PanelLayout.Graph.Width);
        var phaseY=(int)Math.Round(PanelLayout.Graph.Y+(1-patternPreview.Level)*PanelLayout.Graph.Height);
        using(var image=gpu.Capture())
        { var pixel=image.GetPixel(phaseX,phaseY); var live=patternPreview.LiveColor; Check(Math.Abs(pixel.R-live.R)+Math.Abs(pixel.G-live.G)+Math.Abs(pixel.B-live.B)<40,"Recorded loop playhead follows elapsed phase on the GPU"); }
        patternPreview.AdvanceTo(6.4); gpu.Render(ShaderState.From(patternPreview,1240,800,6.4f,0,receiver.Packets));
        Check(Math.Abs(patternPreview.DisplayPhase-0.60)<0.001,"Playback advances independently of the rendered recording texture");
        gpu.Resize(1000,650); gpu.Render(patternState);
        using(var image=gpu.Capture()) image.Save(Path.Combine(directory,"panel-small.png"),ImageFormat.Png);
        Check(patternPreview.RecordArmed,"Record stays enabled after the rendered pattern completes");
        patternPreview.ToggleRecord();
        Check(ShaderState.From(patternPreview,1000,650,6.4f,0,0).PatternData.Z==0,"Explicit Record-off clears the shader button state");
        patternPreview.ToggleRecord(); gpu.Render(ShaderState.From(patternPreview,1000,650,6.4f,0,0));
        Check(ShaderState.From(patternPreview,1000,650,6.4f,0,0).PatternData.Z==1,"Armed Record state is supplied to its shader button");

        LoopVisualTests.Run(gpu,directory,Check);

        // Exercise the same native HWND/swap-chain path as the interactive app.
        // The temporary form is never shown and no user's window is controlled.
        using (var window = new Form { ClientSize = new Size(1240, 800), ShowInTaskbar = false })
        using (var surface = new RenderSurface())
        {
            window.Controls.Add(surface);
            _ = window.Handle; _ = surface.Handle;
            using var windowGpu = new GpuRenderer(shader, 1240, 800, surface.Handle, warp);
            using var labels = PanelLabels.Create(model, "OSC · 127.0.0.1:9002", windowGpu.CompileStatus, windowGpu.Backend, 1, 0);
            windowGpu.UploadLabels(labels);
            windowGpu.StepHistory(ShaderState.From(model, 1240, 800, 1, 0, 0));
            windowGpu.Render(ShaderState.From(model, 1240, 800, 1, 0, 0));
            Check(windowGpu.CompileError is null, "Native Windows swap chain compiles and presents HLSL");
            windowGpu.Resize(1000, 650); windowGpu.Render(ShaderState.From(model, 1000, 650, 1, 0, 0));
            Check(windowGpu.Width == 1000, "Native swap-chain resize/present succeeds");
        }

        File.WriteAllText(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(new { Passed = checks.Count, Backend = gpu.Backend, Checks = checks, PhysicalOutput = false, UnityProjectTouched = false }, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
