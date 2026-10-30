namespace ShockWave.Overlay.SteamVr;

internal static class PanelBuildInfo
{
    public static string Version => typeof(PanelBuildInfo).Assembly.GetName().Version?.ToString(3) ?? "ShockWave";
}
