namespace ShockWave.ShaderLab;

internal readonly record struct PatternSample(double Time, float Level);

internal sealed class PatternClip
{
    private readonly PatternSample[] samples;
    public double Duration { get; }
    public int SampleCount => samples.Length;
    public PatternClip(IEnumerable<PatternSample> points)
    {
        samples = points.ToArray();
        if (samples.Length is < 2 or > 1802 || samples[0].Time != 0) throw new ArgumentException("Invalid pattern samples");
        for (var i=0;i<samples.Length;i++)
            if (!double.IsFinite(samples[i].Time) || !float.IsFinite(samples[i].Level) || samples[i].Level is < 0 or > 1 ||
                (i>0 && samples[i].Time<=samples[i-1].Time)) throw new ArgumentException("Invalid pattern timing/value");
        Duration=samples[^1].Time;
        if (Duration<PanelModel.MinimumRecordingSeconds || Duration>PanelModel.MaximumRecordingSeconds) throw new ArgumentException("Invalid pattern duration");
    }
    public float At(double time)
    {
        time=Math.Clamp(time,0,Duration);
        var low=0; var high=samples.Length-1;
        while(high-low>1) { var middle=(low+high)/2; if(samples[middle].Time<=time) low=middle; else high=middle; }
        var a=samples[low]; var b=samples[high];
        return a.Level+(b.Level-a.Level)*(float)((time-a.Time)/(b.Time-a.Time));
    }
    public double Phase(double elapsed) => Math.Max(0,elapsed)%Duration/Duration;
    public float LoopAt(double elapsed) => At(Phase(elapsed)*Duration);
}

internal readonly record struct RecordingTarget(string ToyId, string FunctionId, Guid Generation);
internal sealed class PatternRecording
{
    public RecordingTarget[] Targets;
    public readonly double Started;
    private readonly List<PatternSample> samples;
    private int nextSample=1;
    public float Latest;
    public PatternRecording(double started,float level,RecordingTarget[] targets)
    {
        Started=started; Latest=level; Targets=targets; samples=[new(0,level)];
    }
    public void Capture(double now)
    {
        var elapsed=Math.Clamp(now-Started,0,PanelModel.MaximumRecordingSeconds);
        while(nextSample/30.0<=elapsed && nextSample<=1800) { samples.Add(new(nextSample/30.0,Latest)); nextSample++; }
    }
    public PatternClip Finish(double now)
    {
        Capture(now);
        var duration=Math.Clamp(now-Started,0,PanelModel.MaximumRecordingSeconds);
        if(Math.Abs(samples[^1].Time-duration)<0.0000001) samples[^1]=new(duration,Latest);
        else samples.Add(new(duration,Latest));
        return new(samples);
    }
}

internal sealed partial class PanelModel
{
    public const double MinimumRecordingSeconds=0.2, MaximumRecordingSeconds=60;
    public double Now { get; private set; }
    public bool RecordArmed { get; private set; }
    private PatternRecording? recording;
    private bool blockDrawingUntilRelease;
    public bool Recording => recording is not null;
    public double RecordingSeconds => recording is null ? 0 : Math.Clamp(Now-recording.Started,0,MaximumRecordingSeconds);
    public string PatternNotice { get; private set; } = "Select functions, then Record";
    public int LoopCount => Functions.Count(f=>f.Connected && f.Function.LoopRunning);
    public FunctionBinding? DisplayLoop => Functions.FirstOrDefault(f=>f.Connected && f.Selected && f.Function.LoopRunning);
    public PatternClip? DisplayPattern => DisplayLoop?.Function.Pattern;
    public double DisplayPhase => DisplayLoop is { } row ? row.Function.Pattern!.Phase(Now-row.Function.LoopStarted) : 0;
    public string PatternStatus => Recording ? $"Recording {RecordingSeconds:0.0} / 60s" : RecordArmed ? $"Recording enabled · {SelectedCount} selected" :
        DisplayPattern is { } clip ? $"Loop {clip.Duration:0.0}s · {LoopCount} running" : LoopCount>0 ? $"{LoopCount} loops running · {SelectedCount} selected" : PatternNotice;

    public void AdvanceTo(double now)
    {
        if (!double.IsFinite(now) || now<Now) throw new ArgumentOutOfRangeException(nameof(now));
        Now=now;
        if(recording is not null)
        {
            recording.Capture(now);
            if(now-recording.Started>=MaximumRecordingSeconds) { FinishRecording(); Touching=false; blockDrawingUntilRelease=true; }
        }
        var changed=false;
        foreach(var row in Functions.Where(f=>f.Connected && f.Function.LoopRunning))
        {
            var value=row.Function.Pattern!.LoopAt(now-row.Function.LoopStarted);
            changed |= Math.Abs(row.Function.Level-value)>0.00001;
            row.Function.Level=value;
        }
        if(!Touching && DisplayLoop is { } displayed)
        {
            changed |= Math.Abs(Level-displayed.Level)>0.00001; Level=displayed.Level;
        }
        if(changed) Revision++;
    }
    public void ToggleRecord()
    {
        if(RecordArmed) { RecordArmed=false; CancelRecording("Record off · holding"); return; }
        if(Touching) { PatternNotice="Release touch before enabling Record"; Revision++; return; }
        RecordArmed=true; PatternNotice="Recording enabled · touch to start"; Revision++;
    }
    private bool BeginDrawing(float level)
    {
        if(blockDrawingUntilRelease) return false;
        if(!Touching)
        {
            var targets=Functions.Where(f=>f.Selected && f.Connected).ToArray();
            foreach(var row in targets) row.Function.LoopRunning=false;
            if(RecordArmed && targets.Length>0)
            {
                recording=new(Now,level,targets.Select(f=>new RecordingTarget(f.Toy.Id,f.Function.Id,f.Function.Generation)).ToArray());
                PatternNotice="Recording";
            }
            else if(RecordArmed) PatternNotice="Select a connected function first";
        }
        return true;
    }
    private IEnumerable<FunctionBinding> DrawingTargets => recording is null ? Functions.Where(f=>f.Selected && f.Connected) :
        Functions.Where(f=>f.Connected && recording.Targets.Any(t=>t.ToyId==f.Toy.Id && t.FunctionId==f.Function.Id && t.Generation==f.Function.Generation));
    private void FinishRecording()
    {
        var session=recording;
        if(session is null) return;
        recording=null;
        if(Now-session.Started<MinimumRecordingSeconds) { PatternNotice="Touch too short · holding"; Revision++; return; }
        var clip=session.Finish(Now);
        foreach(var target in session.Targets)
        {
            var row=Functions.FirstOrDefault(f=>f.Connected && f.Toy.Id==target.ToyId && f.Function.Id==target.FunctionId && f.Function.Generation==target.Generation);
            if(row is null) continue;
            row.Function.Pattern=clip; row.Function.LoopStarted=Now; row.Function.LoopRunning=true; row.Function.Level=clip.At(0);
        }
        PatternNotice=$"Recorded {clip.Duration:0.0}s · looping"; Revision++;
    }
    private void CancelRecording(string notice)
    {
        recording=null; PatternNotice=notice; Revision++;
    }
    private void ReconcileRecording()
    {
        if(recording is null) return;
        recording.Targets=recording.Targets.Where(t=>Functions.Any(f=>f.Connected && f.Toy.Id==t.ToyId && f.Function.Id==t.FunctionId && f.Function.Generation==t.Generation)).ToArray();
        if(recording.Targets.Length==0) CancelRecording("Recording cancelled · target changed");
    }
    public void PlaySelected()
    {
        if(Touching || Recording) { PatternNotice="Release touch before playback"; Revision++; return; }
        var count=0;
        foreach(var row in Functions.Where(f=>f.Connected && f.Selected && f.Function.Pattern is not null))
        {
            row.Function.LoopStarted=Now; row.Function.LoopRunning=true; row.Function.Level=row.Function.Pattern!.At(0); count++;
        }
        PatternNotice=count==0 ? "Record a pattern first" : $"Playing {count} recorded functions";
        AdvanceTo(Now); Revision++;
    }
    public void HoldSelected()
    {
        CancelRecording("Selected functions holding"); Touching=false;
        foreach(var row in Functions.Where(f=>f.Selected)) row.Function.LoopRunning=false;
        Revision++;
    }
}
