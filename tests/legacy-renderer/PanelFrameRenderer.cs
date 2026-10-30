using ShockWave.ShaderLab;

namespace ShockWave.Overlay;

// Value snapshots and immutable PatternClips only; no control, session or device references.
internal sealed record PanelRenderFrame(int Width,int Height,ShaderState State,int ClearRevision,
    PatternClip? Pattern,PatternClip?[] Loops,ToyControlLabels.Frame Labels,bool StepHistory,long InputRevision=0);

internal interface IPanelFrameRenderer : IDisposable
{
    void Draw(PanelRenderFrame frame);
}

internal sealed class GpuPanelFrameRenderer(string path,nint handle) : IPanelFrameRenderer
{
    private GpuRenderer? renderer;
    private int clearRevision=-1;
    private PatternClip? pattern;
    private ToyControlLabels.Frame? labels;
    public void Draw(PanelRenderFrame frame)
    {
        renderer ??= new(path,frame.Width,frame.Height,handle);
        renderer.Resize(frame.Width,frame.Height);
        if(clearRevision!=frame.ClearRevision) {renderer.ClearHistory();clearRevision=frame.ClearRevision;}
        if(!ReferenceEquals(pattern,frame.Pattern)) {renderer.UploadPattern(frame.Pattern);pattern=frame.Pattern;}
        renderer.UploadLoopPatterns(frame.Loops);
        if(!ReferenceEquals(labels,frame.Labels))
        {
            using var bitmap=frame.Labels.Draw();renderer.UploadLabels(bitmap);labels=frame.Labels;
        }
        if(frame.StepHistory) renderer.StepHistory(frame.State);
        renderer.Render(frame.State);
    }
    public void Dispose() {renderer?.Dispose();renderer=null;}
}
