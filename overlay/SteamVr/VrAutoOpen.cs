namespace ShockWave.Overlay.SteamVr;

// App-start intent only. Waiting never starts SteamVR or sends device commands.
internal sealed class VrAutoOpen
{
    public bool Pending {get;private set;}
    public int Attempts {get;private set;}
    public double NextCheck {get;private set;}
    private bool detected;
    public void Arm(bool enabled) {Pending=enabled;Attempts=0;NextCheck=0;detected=false;}
    public void Cancel()=>Pending=false;
    public void Tick(double now,bool available,Func<bool> open)
    {
        if(!Pending || !double.IsFinite(now) || now<NextCheck) return;
        if(!available) {detected=false;NextCheck=now+1;return;}
        if(!detected) {detected=true;NextCheck=now+1;return;}
        Attempts++;
        if(open() || Attempts>=3) {Pending=false;return;}
        NextCheck=now+(Attempts==1 ? 2 : 5);
    }
}
