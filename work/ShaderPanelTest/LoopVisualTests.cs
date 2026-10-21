using System.Drawing.Imaging;

namespace ShockWave.ShaderLab;

internal static class LoopVisualTests
{
    private static int Difference(Color a, Color b) => Math.Abs(a.R-b.R)+Math.Abs(a.G-b.G)+Math.Abs(a.B-b.B);
    private static Point CurvePoint(PanelModel model, FunctionBinding row, double x)
    {
        var duration=model.DisplayPattern?.Duration ?? 8;
        var playhead=model.DisplayPattern is null ? 1 : model.DisplayPhase;
        var clip=row.Function.Pattern!;
        var cycles=(model.Now-playhead*duration+x*duration-row.Function.LoopStarted)/clip.Duration;
        var level=clip.At((cycles-Math.Floor(cycles))*clip.Duration);
        return new((int)Math.Round(PanelLayout.Graph.X+x*PanelLayout.Graph.Width),
            (int)Math.Round(PanelLayout.Graph.Y+(1-level)*PanelLayout.Graph.Height));
    }
    private static PatternClip Wave(double duration,float low,float high,double shift=0) => new(
        Enumerable.Range(0,121).Select(i=>new PatternSample(duration*i/120.0,
            low+(high-low)*(float)(0.5+0.5*Math.Sin(i/120.0*Math.PI*2+shift)))));

    public static void Run(GpuRenderer gpu,string directory,Action<bool,string> check)
    {
        gpu.Resize(1240,800); gpu.ClearHistory(); gpu.UploadPattern(null);
        using(var labels=new Bitmap(1240,800)) gpu.UploadLabels(labels);
        var model=new PanelModel();
        model.ReplaceInventory(Enumerable.Range(0,7).Select(i=>new ToyUpdate("loop-view-"+i,"Visual toy "+i,true,80,i==0 ? 0.63f : 0)).ToArray());
        var row=model.Functions.Last();
        var clip=new PatternClip([new(0,0.2f),new(1,0.8f),new(2,0.5f),new(3,0.1f),new(4,0.2f)]);
        row.Function.Pattern=clip; row.Function.LoopRunning=true; row.Function.LoopStarted=0;
        model.AdvanceTo(17);
        check(model.SelectedCount==0 && model.DisplayPattern is null && model.Visible.All(f=>!f.Function.LoopRunning),"Deselected loop test includes an active function outside the visible page");
        check(gpu.UploadLoopPatterns(model) && !gpu.UploadLoopPatterns(model),"Recorded curve atlas uploads on clip changes and reuses unchanged memory");
        var state=ShaderState.From(model,1240,800,17,0,0);
        check(state.Functions[6].State.W==1 && Math.Abs(state.Functions[6].LoopView.X-0.25f)<0.001 && state.Functions[6].LoopView.Y==2,"Off-page deselected loop supplies phase and two repeats across the live eight-second window");
        var point=CurvePoint(model,row,0.15);
        var flatPoint=new Point(point.X,(int)Math.Round(PanelLayout.Graph.Y+(1-row.Level)*PanelLayout.Graph.Height));
        var heldPoint=new Point((int)(PanelLayout.Graph.X+0.20*PanelLayout.Graph.Width),(int)Math.Round(PanelLayout.Graph.Y+0.37*PanelLayout.Graph.Height));
        gpu.Render(state);
        Color curvePixel,flatPixel,heldPixel;
        using(var image=gpu.Capture()) { curvePixel=image.GetPixel(point.X,point.Y); flatPixel=image.GetPixel(flatPoint.X,flatPoint.Y); heldPixel=image.GetPixel(heldPoint.X,heldPoint.Y); }
        model.AdvanceTo(17.5); var advancedPoint=CurvePoint(model,row,0.15);
        gpu.Render(ShaderState.From(model,1240,800,17.5f,0,0));
        Color advancedPixel;
        using(var image=gpu.Capture()) advancedPixel=image.GetPixel(advancedPoint.X,advancedPoint.Y);
        model.Upsert(new("loop-view-6","Renamed loop",true,79,null));
        check(!gpu.UploadLoopPatterns(model) && model.Functions.Last().Function.Pattern==clip,"Metadata-only refresh retains the atlas clip without resampling or restarting it");
        model.StopAll(); gpu.Render(ShaderState.From(model,1240,800,17.5f,0,0));
        using(var image=gpu.Capture())
        {
            check(Difference(curvePixel,image.GetPixel(point.X,point.Y))>70,"GPU draws the recorded off-page waveform when no functions are selected");
            check(Difference(flatPixel,image.GetPixel(flatPoint.X,flatPoint.Y))<20,"A running loop no longer leaves a false horizontal line at its current value");
            check(Math.Abs(advancedPoint.Y-point.Y)>40 && Difference(advancedPixel,image.GetPixel(advancedPoint.X,advancedPoint.Y))>70,"Deselected waveform scrolls at its independent current phase");
            check(Difference(heldPixel,image.GetPixel(heldPoint.X,heldPoint.Y))>60,"Non-looping held input keeps its straight colored line alongside loop curves");
        }

        var zero=new PanelModel(); zero.Upsert(new("zero-loop","Zero crossing",true,-1,null));
        var zeroRow=zero.Functions.Single(); zeroRow.Function.Pattern=new([new(0,0),new(1,0.8f),new(2,0)]); zeroRow.Function.LoopRunning=true;
        zero.AdvanceTo(6); gpu.UploadLoopPatterns(zero);
        var zeroPoint=CurvePoint(zero,zeroRow,0.15); gpu.Render(ShaderState.From(zero,1240,800,6,0,0));
        Color zeroPixel; using(var image=gpu.Capture()) zeroPixel=image.GetPixel(zeroPoint.X,zeroPoint.Y);
        zero.Upsert(new("zero-loop","Zero crossing",false,-1,null)); gpu.UploadLoopPatterns(zero); gpu.Render(ShaderState.From(zero,1240,800,6,0,0));
        using(var image=gpu.Capture()) check(zeroRow.Level==0 && Difference(zeroPixel,image.GetPixel(zeroPoint.X,zeroPoint.Y))>70,"Loop curves remain visible at a zero crossing and disappear on disconnect");

        var full=new PanelModel();
        full.ReplaceInventory(Enumerable.Range(0,128).Select(i=>new ToyUpdate("capacity-"+i,"Capacity toy",true,-1,null,[new("a","First"),new("b","Second")])).ToArray());
        var last=full.Functions.Last(); last.Function.Pattern=clip; last.Function.LoopRunning=true; full.AdvanceTo(17);
        gpu.UploadLoopPatterns(full); var lastPoint=CurvePoint(full,last,0.15); gpu.Render(ShaderState.From(full,1240,800,17,0,0));
        Color lastPixel; using(var image=gpu.Capture()) lastPixel=image.GetPixel(lastPoint.X,lastPoint.Y);
        last.Function.LoopRunning=false; last.Function.Level=0; gpu.Render(ShaderState.From(full,1240,800,17,0,0));
        using(var image=gpu.Capture()) check(Difference(lastPixel,image.GetPixel(lastPoint.X,lastPoint.Y))>70,"The last of 256 function rows renders from its own bounded atlas row");
        last.Function.LoopRunning=true;
        var selected=full.Functions.First(); selected.Function.Pattern=Wave(5,0.1f,0.3f); selected.Function.LoopRunning=true; full.ToggleVisible(0); full.AdvanceTo(17);
        gpu.UploadLoopPatterns(full); gpu.UploadPattern(full.DisplayPattern);
        var selectedState=ShaderState.From(full,1240,800,17,0,0);
        check(Math.Abs(selectedState.Functions[255].LoopView.X-0.75f)<0.001 && selectedState.Functions[255].LoopView.Y==1.25f && Math.Abs(selectedState.Functions[255].LoopView.Z-0.4f)<0.001,"Background loops align with the selected pattern's duration and current-time playhead");
        var sharedPoint=CurvePoint(full,last,0.25); gpu.Render(selectedState);
        Color sharedPixel; using(var image=gpu.Capture()) sharedPixel=image.GetPixel(sharedPoint.X,sharedPoint.Y);
        last.Function.LoopRunning=false; last.Function.Level=0; gpu.Render(ShaderState.From(full,1240,800,17,0,0));
        using(var image=gpu.Capture()) check(Difference(sharedPixel,image.GetPixel(sharedPoint.X,sharedPoint.Y))>70,"GPU keeps a deselected independent loop visible behind the selected full-pattern preview");

        var review=new PanelModel(); review.ReplaceInventory(PanelModel.ParseInventory(OscListener.DemoJson));
        var reviewRows=review.Functions.ToArray(); reviewRows[0].Function.Level=0.82f; reviewRows[3].Function.Level=0.38f;
        foreach(var (index,duration,low,high,shift) in new (int,double,float,float,double)[] {(1,4,0.15f,0.75f,0),(2,5.2,0.25f,0.65f,1.2),(4,3.2,0.05f,0.48f,2.4),(8,6,0.60f,0.92f,0.4)})
        { var f=reviewRows[index].Function; f.Pattern=Wave(duration,low,high,shift); f.LoopRunning=true; f.LoopStarted=index; }
        review.AdvanceTo(20.75); gpu.UploadLoopPatterns(review); gpu.UploadPattern(null); gpu.ClearHistory();
        using(var labels=PanelLabels.Create(review,"OSC · 127.0.0.1:9002",gpu.CompileStatus,gpu.Backend,1,0)) gpu.UploadLabels(labels);
        gpu.Render(ShaderState.From(review,1240,800,20.75f,0,1));
        using(var image=gpu.Capture()) image.Save(Path.Combine(directory,"panel-preview.png"),ImageFormat.Png);
        gpu.Resize(1000,650); gpu.Render(ShaderState.From(review,1000,650,20.75f,0,1));
        using(var image=gpu.Capture()) image.Save(Path.Combine(directory,"panel-small.png"),ImageFormat.Png);
        review.ToggleVisible(5); review.DrawAt(new(PanelLayout.Graph.X+0.61f*PanelLayout.Graph.Width,PanelLayout.Graph.Y+0.24f*PanelLayout.Graph.Height));
        gpu.Resize(1240,800); using(var labels=PanelLabels.Create(review,"OSC · 127.0.0.1:9002",gpu.CompileStatus,gpu.Backend,1,0)) gpu.UploadLabels(labels);
        gpu.Render(ShaderState.From(review,1240,800,20.75f,0,1));
        using(var image=gpu.Capture()) image.Save(Path.Combine(directory,"panel-drawing.png"),ImageFormat.Png);
    }
}
