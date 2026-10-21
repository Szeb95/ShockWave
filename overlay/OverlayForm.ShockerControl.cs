using System.Text.Json;

namespace ShockWave.Overlay;

internal sealed partial class OverlayForm
{
    private IReadOnlyList<ShockerControlSession.Target> ShockerControlInventory()
    {
        var running=bridge is { HasExited:false } && !stopRequested && !restartPending && !closing;
        var local=config.ActiveShockers.Where(s => s.Uuid.Length>0 || config.DryRun).Select(s =>
            new ShockerControlSession.Target("local/"+config.Provider+"/"+s.Key,s.Name,config.Provider,
                running && !s.Paused && s.ManualMinDurationMs<=15000 && (config.DryRun || ActiveApiToken.Length>0 && apiHealth?.Hub?.Shockers.FirstOrDefault(h=>h.Key==s.Key)?.State!="Offline"),
                s.ManualMaxIntensity,Math.Min(15000,s.ManualMaxDurationMs),false,
                $"{s.Uuid}/{s.ManualMinIntensity}/{s.ManualMinDurationMs}/{s.CooldownMs}/{config.DryRun}"));
        var remote=remoteSession.Connected ? remoteSession.Shockers.Select(s =>
            new ShockerControlSession.Target("remote/"+s.Id,"Remote "+s.Name,s.Service,true,s.Maximum,s.DurationMs,true)) : [];
        return local.Concat(remote).ToArray();
    }
    private bool SendPanelShocker(ShockerControlSession.Target target,string type,int percent,int durationMs)
    {
        if(target.Remote)
        {
            var sent=remoteSession.SendShock(target.Id[7..],type,percent,durationMs);
            if(sent) AppendLog("info",$"Shocker panel: {target.Name} · {type} · {percent}% · {durationMs/1000d:0.0}s submitted once to remote app");
            return sent;
        }
        var current=ShockerControlInventory().FirstOrDefault(t=>t.Id==target.Id && t.Available);
        if(current is null || bridge is not { HasExited:false }) return false;
        var key=target.Id[("local/"+config.Provider+"/").Length..];
        try
        {
            // A discrete, expiring owner command. Existing bridge limits, pause,
            // cooldown, Test mode and no-replay behavior remain authoritative.
            bridge.StandardInput.WriteLine(JsonSerializer.Serialize(new { kind="panelControl",key,type,intensity=percent,durationMs,
                expiresAt=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+250 }));
            AppendLog("info",$"Shocker panel: {target.Name} · {type} · {percent}% · {durationMs/1000d:0.0}s submitted once");
            return true;
        }
        catch(Exception e) when(e is IOException or InvalidOperationException) { AppendLog("error","Shocker panel command could not be submitted. It will not be retried."); return false; }
    }
}
