namespace ShockWave.Overlay;

internal sealed class ShockerSyncSnapshot
{
    public string Kind { get; set; } = "";
    public string Key { get; set; } = "";
    public string Uuid { get; set; } = "";
    public string Name { get; set; } = "";
    public string RequestId { get; set; } = "";
    public bool Success { get; set; }
    public string Detail { get; set; } = "";
}
