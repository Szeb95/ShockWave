namespace ShockWave.Overlay;

internal sealed class DesktopPanelWorker : IDisposable
{
    private readonly object sync=new();
    private readonly AutoResetEvent ready=new(false);
    private readonly Func<DesktopPanelFrame,Bitmap> render;
    private DesktopPanelFrame? pending;
    private (Bitmap Image,string Cookie)? completed;
    private bool closed;
    private volatile Exception? failure;
    public Exception? Failure=>failure;
    public DesktopPanelWorker(Func<DesktopPanelFrame,Bitmap> render)
    {this.render=render;new Thread(Run) {IsBackground=true,Name="ShockWave desktop display",Priority=ThreadPriority.BelowNormal}.Start();}
    public void Submit(DesktopPanelFrame frame) {lock(sync) {if(closed || failure is not null) return;pending=frame;ready.Set();}}
    public bool TryTake(out Bitmap? image,out string cookie)
    {
        lock(sync) {if(completed is not { } frame) {image=null;cookie="";return false;}completed=null;image=frame.Image;cookie=frame.Cookie;return true;}
    }
    private void Run()
    {
        try
        {
            while(true)
            {
                ready.WaitOne();DesktopPanelFrame frame;
                lock(sync) {if(closed) return;if(pending is null) continue;frame=pending;pending=null;}
                var image=render(frame);
                lock(sync) {if(closed) {image.Dispose();return;}completed?.Image.Dispose();completed=(image,frame.Cookie);}
            }
        }
        catch(Exception error) {lock(sync) {failure=error;pending=null;}}
        finally {ready.Dispose();}
    }
    public void Dispose() {lock(sync) {if(closed) return;closed=true;pending=null;completed?.Image.Dispose();completed=null;try {ready.Set();} catch(ObjectDisposedException) {}}}
}
