namespace ShockWave.Overlay.SteamVr;

internal readonly record struct PanelFrameStamp(string Input,string Context);

// One replaceable display frame. Neither input nor output leases run here.
internal sealed class RasterPanelWorker : IDisposable
{
    private readonly object sync=new();
    private readonly AutoResetEvent ready=new(false);
    private readonly Thread worker;
    private readonly Func<PanelModel,string,Bitmap> render;
    private (PanelModel Model,PanelFrameStamp Stamp)? pending;
    private (Bitmap Image,PanelFrameStamp Stamp)? completed;
    private bool closed;
    private volatile Exception? failure;
    public Exception? Failure=>failure;
    public RasterPanelWorker(Func<PanelModel,string,Bitmap> render)
    {
        this.render=render;worker=new(Run) {IsBackground=true,Name="ShockWave panel display",Priority=ThreadPriority.BelowNormal};worker.Start();
    }
    public void Submit(PanelModel snapshot,PanelFrameStamp stamp) {lock(sync) {if(closed || Failure is not null) return;pending=(snapshot,stamp);ready.Set();}}
    public bool TryTake(out Bitmap? image,out PanelFrameStamp stamp)
    {
        lock(sync)
        {
            if(completed is not { } frame) {image=null;stamp=default;return false;}
            completed=null;image=frame.Image;stamp=frame.Stamp;return true;
        }
    }
    private void Run()
    {
        try
        {
            while(true)
            {
                ready.WaitOne();(PanelModel Model,PanelFrameStamp Stamp) next;
                lock(sync) {if(closed) return;if(pending is not { } frame) continue;next=frame;pending=null;}
                var bitmap=render(next.Model,PanelBuildInfo.Version);
                lock(sync) {if(closed) {bitmap.Dispose();return;}completed?.Image.Dispose();completed=(bitmap,next.Stamp);}
            }
        }
        catch(Exception error) {lock(sync) {failure=error;pending=null;}}
        finally {ready.Dispose();}
    }
    public void Dispose()
    {
        lock(sync) {if(closed) return;closed=true;pending=null;completed?.Image.Dispose();completed=null;try {ready.Set();} catch(ObjectDisposedException) {}}
    }
}
