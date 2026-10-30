using System.Diagnostics;

namespace ShockWave.Overlay;

internal readonly record struct PanelRenderMetrics(long Frames,long Skipped,long MaximumFrameMs);

// Owns only graphics. It cannot read/renew input, send commands or change session state.
// A slow GPU replaces pending visual frames instead of queuing work on the UI thread.
internal sealed class PanelRenderWorker : IDisposable
{
    private readonly object sync=new();
    private readonly AutoResetEvent signal=new(false);
    private PanelRenderFrame? pending;
    private bool stopped;
    private Exception? failure;
    private int ready,completed;
    private long frames,skipped,maximumFrame;
    private long inputRevision=-1;
    public bool Ready=>Volatile.Read(ref ready)!=0;
    public bool Completed=>Volatile.Read(ref completed)!=0;
    public Exception? Failure=>Volatile.Read(ref failure);
    public bool AcceptsInput(long revision)=>Ready && Interlocked.Read(ref inputRevision)==revision;
    public PanelRenderWorker(Func<IPanelFrameRenderer> createRenderer)
    {
        var thread=new Thread(()=>Run(createRenderer)) {IsBackground=true,Name="ShockWave panel graphics",Priority=ThreadPriority.BelowNormal};
        thread.Start();
    }
    public void Submit(PanelRenderFrame frame)
    {
        lock(sync)
        {
            if(stopped) return;
            if(pending is not null) Interlocked.Increment(ref skipped);
            pending=frame;signal.Set();
        }
    }
    private void Run(Func<IPanelFrameRenderer> createRenderer)
    {
        try
        {
            using var renderer=createRenderer();
            while(true)
            {
                signal.WaitOne();PanelRenderFrame? frame;
                lock(sync) {if(stopped) break;frame=pending;pending=null;}
                if(frame is null) continue;
                var at=Stopwatch.GetTimestamp();renderer.Draw(frame);
                var elapsed=(long)Math.Ceiling(Stopwatch.GetElapsedTime(at).TotalMilliseconds);
                long previous;
                do {previous=Interlocked.Read(ref maximumFrame);if(elapsed<=previous) break;}
                while(Interlocked.CompareExchange(ref maximumFrame,elapsed,previous)!=previous);
                Interlocked.Increment(ref frames);
                lock(sync) {if(stopped) break;Interlocked.Exchange(ref inputRevision,frame.InputRevision);Volatile.Write(ref ready,1);}
            }
        }
        catch(Exception error) {Volatile.Write(ref failure,error);}
        finally
        {
            lock(sync) {stopped=true;pending=null;Volatile.Write(ref ready,0);}
            signal.Dispose();Volatile.Write(ref completed,1);
        }
    }
    public PanelRenderMetrics TakeMetrics()=>new(Interlocked.Exchange(ref frames,0),Interlocked.Exchange(ref skipped,0),Interlocked.Exchange(ref maximumFrame,0));
    public void Pause() {lock(sync) pending=null;}
    public void Dispose()
    {
        // Never join a graphics driver from the UI. The owning thread releases
        // its resources after its current call, with no pending frames retained.
        lock(sync) {if(stopped) return;stopped=true;pending=null;signal.Set();}
    }
}
