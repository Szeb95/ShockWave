using ShockWave.ShaderLab;

namespace ShockWave.Overlay;

// One UI-thread state machine serves the desktop and avatar. It never owns API
// credentials and never queues or retries output. The bridge/receiver caps again.
internal sealed class ShockerControlSession
{
    internal sealed record Target(string Id, string Name, string Service, bool Available, int Maximum, int DurationMs, bool Remote, string Context="");
    public Func<IReadOnlyList<Target>> Inventory { get; set; } = () => [];
    public Func<Target,string,int,int,bool> Send { get; set; } = (_,_,_,_) => false;
    public PanelModel Model { get; } = new() { AdaptivePaging = true };
    private Target[] targets = [];
    private readonly HashSet<int> held = [];
    private string signature = "", selected = "", armedTarget = "";
    private double deadline;
    private int armSource;
    private bool released;
    public int Intensity { get; private set; }
    public int DurationMs { get; private set; } = 300;
    public bool Armed => armedTarget.Length > 0;
    public bool ArmedFromVrc => Armed && armSource!=0;
    public float Remaining(double now) => Armed ? (float)Math.Clamp((deadline-now)/3,0,1) : 0;
    public int Revision { get; private set; }
    public string Notice { get; private set; } = "Choose a shocker · set intensity and duration";
    public Target? Selected => targets.FirstOrDefault(t => t.Id == selected);
    public bool Ready => Selected is { Available:true,Maximum:>0,DurationMs:>=300 } && Intensity > 0;
    public void Tick(double now)
    {
        static string Label(string value) { var clean=new string(value.Where(c=>!char.IsControl(c)).Take(96).ToArray()); return string.IsNullOrWhiteSpace(clean) ? "Shocker" : clean; }
        var next = Inventory().Take(64).Select(t=>t with { Name=Label(t.Name) }).ToArray();
        var nextSignature = System.Text.Json.JsonSerializer.Serialize(next);
        if (signature != nextSignature)
        {
            Cancel(); targets=next; signature=nextSignature;
            Model.ReplaceInventory(next.Select(t => new ToyUpdate(t.Id,t.Name,t.Available,-1,null,[new("control",t.Service)])).ToArray());
            foreach(var f in Model.Functions) f.Function.Selected=f.Toy.Id==selected && f.Connected;
            if(Selected is not { Available:true }) selected="";
            Revision++;
        }
        Model.AdvanceTo(now);
        if(Armed && now>=deadline) { Cancel(); Notice="Confirmation expired · start with the right circle"; }
    }
    public void Cancel() { if(Armed) Revision++; armedTarget=""; released=false; }
    public void ResetInput() { Cancel(); held.Clear(); }
    public void Select(int row)
    {
        Cancel(); var item=Model.Visible.ElementAtOrDefault(row);
        selected=item is { Connected:true } ? item.Toy.Id : "";
        foreach(var f in Model.Functions) f.Function.Selected=f.Toy.Id==selected;
        Notice=Selected is { } target ? target.Name+" · set intensity and duration" : "Shocker unavailable";
        Revision++;
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
            armedTarget=selected; armSource=source; deadline=now+3; released=false; Revision++;
            Notice="Release, then press the LEFT green circle within 3 seconds"; return;
        }
        if(hit==33)
        {
            var allowed=Armed && released && now<deadline && armedTarget==selected && Ready;
            Cancel(); if(allowed) Submit("Shock"); return;
        }
        if(hit is 34 or 35) { Cancel(); Submit(hit==34 ? "Vibrate" : "Sound"); }
    }
    private void Submit(string type)
    {
        if(!Ready || Selected is not { } target) { Notice="Choose an available shocker and an intensity above 0%"; return; }
        // Absolute caps, never multiplication: request 80%, maximum 40% => 40%.
        var percent=Math.Min(Intensity,target.Maximum); var duration=Math.Min(DurationMs,target.DurationMs);
        Notice=percent>0 && duration>=300 && Send(target,type,percent,duration)
            ? $"{type} submitted once · {percent}% · {duration/1000d:0.0}s" : "Command not submitted · check connection / Test mode";
        Revision++;
    }
}
