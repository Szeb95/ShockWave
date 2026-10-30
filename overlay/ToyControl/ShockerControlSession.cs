using ShockWave.ShaderLab;

namespace ShockWave.Overlay;

// One UI-thread state machine serves the desktop and avatar. It never owns API
// credentials and never queues or retries output. The bridge/receiver caps again.
internal sealed class ShockerControlSession
{
    internal sealed record Target(string Id, string Name, string Service, bool Available, int Maximum, int DurationMs, bool Remote, string Context="");
    public Func<IReadOnlyList<Target>> Inventory { get; set; } = () => [];
    public Func<Target,string,int,int,long,bool> Send { get; set; } = (_,_,_,_,_) => false;
    public Func<bool> IsTestMode { get; set; } = () => true;
    private sealed record SentShock(int Intensity,double Until);
    private SentShock[] sentShocks=[];
    private bool sentShockWasTest;
    // Newest submitted batch, not device telemetry. Deadlines never renew,
    // send or replay output; cancelling later input cannot unsend this batch.
    public string ShockFeedback(double now)
    {
        var minimum=101;var maximum=0;
        foreach(var shock in sentShocks) if(now<shock.Until) {minimum=Math.Min(minimum,shock.Intensity);maximum=Math.Max(maximum,shock.Intensity);}
        if(maximum==0) return "";
        var strength=minimum==maximum ? $"{maximum}%" : $"{minimum}–{maximum}%";
        return $"{(sentShockWasTest ? "Test shock" : "Shock sent")} · {strength}";
    }
    public PanelModel Model { get; } = new() { AdaptivePaging = true };
    private Target[] targets = [];
    private readonly HashSet<int> held = [];
    private readonly HashSet<string> selected = new(StringComparer.Ordinal);
    private Target[] armedTargets = [];
    private string signature = "";
    private double deadline;
    private long commandEpoch;
    private int armSource;
    private bool released;
    public int Intensity { get; private set; }
    public int DurationMs { get; private set; } = 300;
    public bool Armed => armedTargets.Length > 0;
    public bool ArmedFromVrc => Armed && armSource is 1 or 2 or 3;
    public bool ArmedFromNative => Armed && armSource==4;
    public bool ArmedFromDesktop => Armed && armSource==0;
    public float Remaining(double now) => Armed ? (float)Math.Clamp((deadline-now)/3,0,1) : 0;
    public int Revision { get; private set; }
    public string Notice { get; private set; } = "Choose shockers · set intensity and duration";
    public Target[] Selected => targets.Where(t => selected.Contains(t.Id)).ToArray();
    public bool Ready => selected.Count > 0 && Selected.All(t => t is { Available:true,Maximum:>0,DurationMs:>=300 }) && Intensity > 0;
    private string SelectionNotice => selected.Count == 0 ? "Choose shockers · set intensity and duration"
        : $"{selected.Count} shocker{(selected.Count == 1 ? "" : "s")} selected · set intensity and duration";
    public void Tick(double now)
    {
        static string Label(string value) { var clean=new string(value.Where(c=>!char.IsControl(c)).Take(96).ToArray()); return string.IsNullOrWhiteSpace(clean) ? "Shocker" : clean; }
        var next = Inventory().Take(64).Select(t=>t with { Name=Label(t.Name) }).ToArray();
        var nextSignature = System.Text.Json.JsonSerializer.Serialize(next);
        if (signature != nextSignature)
        {
            Cancel(); targets=next; signature=nextSignature;
            Model.ReplaceInventory(next.Select(t => new ToyUpdate(t.Id,t.Name,t.Available,-1,null,[new("control",t.Service)])).ToArray());
            selected.IntersectWith(next.Where(t=>t.Available).Select(t=>t.Id));
            SyncSelection(); Notice=SelectionNotice;
            Revision++;
        }
        Model.AdvanceTo(now);
        if(Armed && now>=deadline) { Cancel(); Notice="Confirmation expired · start with the right circle"; }
    }
    public void Cancel() { commandEpoch++; if(Armed) Revision++; armedTargets=[]; released=false; }
    public void ResetInput() { Cancel(); held.Clear(); }
    public void Select(int row)
    {
        Cancel(); var item=Model.Visible.ElementAtOrDefault(row);
        if(item is not { Connected:true }) { Notice="Shocker unavailable"; Revision++; return; }
        if(!selected.Add(item.Toy.Id)) selected.Remove(item.Toy.Id);
        SyncSelection(); Notice=SelectionNotice;
        Revision++;
    }
    private void SyncSelection()
    {
        foreach(var f in Model.Functions) f.Function.Selected=selected.Contains(f.Toy.Id) && f.Connected;
    }
    public void SelectPanelTarget(string id)
    {
        Cancel();if(!targets.Any(t=>t.Id==id && t.Available)) return;
        if(!selected.Add(id)) selected.Remove(id);SyncSelection();Notice=SelectionNotice;Revision++;
    }
    public void SetSlider(int hit, float value)
    {
        value=Math.Clamp(value,0,1);
        var intensity=hit==30 ? (int)Math.Round(value*100) : Intensity;
        var duration=hit==31 ? 300+(int)Math.Round(value*147)*100 : DurationMs;
        if(intensity==Intensity && duration==DurationMs) return;
        Cancel(); Intensity=intensity; DurationMs=duration; Revision++;
    }
    public void Release(int source) { held.Remove(source); if(held.Count==0 && Armed) released=true; }
    public void Press(int source,int hit,double now)
    {
        if(!held.Add(source) || held.Count>1) return;
        if(hit is >=1 and <=7) { Select(hit-1); return; }
        if(hit==32)
        {
            if(Armed || !Ready) return;
            armedTargets=Selected; armSource=source; deadline=now+3; released=false; Revision++;
            Notice="Release, then press the LEFT green circle within 3 seconds"; return;
        }
        if(hit==33)
        {
            var allowed=Armed && released && now<deadline && armedTargets.SequenceEqual(Selected) && Ready;
            Cancel(); if(allowed) Submit("Shock",now); return;
        }
        if(hit is 34 or 35) { Cancel(); Submit(hit==34 ? "Vibrate" : "Sound",now); }
    }
    private void Submit(string type,double now)
    {
        if(!Ready) { Notice="Choose available shockers with usable limits and an intensity above 0%"; return; }
        var group=Selected; var intensity=Intensity; var durationMs=DurationMs; var epoch=commandEpoch;
        var createdAt=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); var started=System.Diagnostics.Stopwatch.GetTimestamp();
        var submitted=0;
        var feedback=new List<SentShock>();var preview=type=="Shock" && IsTestMode();
        foreach(var target in group)
        {
            // One immediate attempt per selected target. A slow callback cannot
            // renew the original action, alter its values or add a new target.
            if(commandEpoch!=epoch || System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds>=250 ||
                !Ready || Intensity!=intensity || DurationMs!=durationMs || !group.SequenceEqual(Selected)) break;
            // Absolute caps, never multiplication: request 80%, maximum 40% => 40%.
            var percent=Math.Min(intensity,target.Maximum); var duration=Math.Min(durationMs,target.DurationMs);
            var sentAt=now+System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalSeconds;
            if(Send(target,type,percent,duration,createdAt))
            {
                submitted++;
                if(type=="Shock") feedback.Add(new(percent,sentAt+duration/1000d));
            }
        }
        if(feedback.Count>0) {sentShocks=feedback.ToArray();sentShockWasTest=preview;}
        Notice=submitted==group.Length ? $"{type} submitted once to {submitted} shocker{(submitted==1 ? "" : "s")} · individual limits apply"
            : $"{type} submitted to {submitted} of {group.Length} shockers · not retried; check connection / Test mode";
        Revision++;
    }
}
