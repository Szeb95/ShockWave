using System.Text.Json;

namespace ShockWave.Overlay;

internal sealed partial class OverlayForm
{
    internal string RemoteLogText() => string.Join(Environment.NewLine,
        new[] { $"ShockWave {typeof(OverlayForm).Assembly.GetName().Version} remote diagnostics · copied {DateTimeOffset.Now:O}" }
        .Concat(recentLog.Where(line => line.Contains("  REMOTE  ", StringComparison.Ordinal))));
    private void UpdateRemoteGate()
    {
        if (bridge is not { HasExited: false } || stopRequested || closing) return;
        try { bridge.StandardInput.WriteLine(JsonSerializer.Serialize(new { kind = "remoteGate", session = remoteSession.Connected ? remoteSession.SessionId : "" })); }
        catch (Exception e) when (e is IOException or InvalidOperationException) { AppendLog("error", "Shocker bridge unavailable for remote control."); }
    }
    private void SendRemoteShocker(string key, string type, int percent, int durationMs, long expiresAt)
    {
        if (!remoteSession.Connected || bridge is not { HasExited: false } || stopRequested || restartPending || closing) return;
        try
        {
            bridge.StandardInput.WriteLine(JsonSerializer.Serialize(new { kind = "remoteControl", session = remoteSession.SessionId,
                key, type, intensity = percent, durationMs, expiresAt }));
        }
        catch (Exception e) when (e is IOException or InvalidOperationException) { remoteSession.Disconnect(); }
    }
}
