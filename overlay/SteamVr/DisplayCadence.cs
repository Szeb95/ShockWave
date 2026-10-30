#nullable enable
namespace ShockWave.Overlay.SteamVr;

internal sealed class DisplayCadence(double interval)
{
    private double deadline=double.NaN;
    public void Reset()=>deadline=double.NaN;
    public bool Due(double now,bool force=false)
    {
        if(!double.IsFinite(now)) return false;
        if(force || !double.IsFinite(deadline)) {deadline=now+interval;return true;}
        if(now<deadline) return false;
        // Preserve the cadence across coarse/uneven timer ticks, but render only
        // one complete frame after a stall instead of replaying missed frames.
        deadline+=(Math.Floor((now-deadline)/interval)+1)*interval;
        return true;
    }
}
