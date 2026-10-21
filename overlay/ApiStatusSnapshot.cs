namespace ShockWave.Overlay;

internal sealed class ApiStatusSnapshot
{
    public string Kind { get; set; } = "";
    public long RetryAt { get; set; }
    public List<ShockerApiStatus> Shockers { get; set; } = [];
    public HubApiStatus? Hub { get; set; }
    public ShockerApiStatus? Connection { get; set; }
}

internal sealed class HubApiStatus
{
    public string State { get; set; } = "Unknown";
    public int Total { get; set; }
    public int Online { get; set; }
    public string Detail { get; set; } = "";
    public List<ShockerHubStatus> Shockers { get; set; } = [];
}

internal sealed class ShockerHubStatus
{
    public string Key { get; set; } = "";
    public string State { get; set; } = "Unknown";
}

internal sealed class ShockerApiStatus
{
    public string Provider { get; set; } = "OpenShock";
    public string Key { get; set; } = "";
    public string State { get; set; } = "Checking";
    public int StatusCode { get; set; }
    public string Detail { get; set; } = "";

    public string Label => State switch
    {
        "Connected" => "API connected",
        "TokenNeeded" => "Token needed",
        "UuidNeeded" => Provider == "PiShock" ? "Device needed" : "UUID needed",
        "RateLimited" => "API waiting to retry",
        "Error" => StatusCode switch
        {
            401 => "Token rejected",
            403 => "No access",
            404 => Provider == "PiShock" ? "Device not found" : "UUID not found",
            >= 500 => "API server error",
            _ => "API error",
        },
        _ => "API connecting",
    };

    public string ShortLabel => State switch
    {
        "Connected" => "Connected",
        "RateLimited" => "Retry wait",
        "Error" => StatusCode switch
        {
            401 => "Token error",
            403 => "No access",
            404 => Provider == "PiShock" ? "Device error" : "UUID error",
            >= 500 => "Server error",
            _ => "API error",
        },
        "TokenNeeded" => "Token needed",
        "UuidNeeded" => Provider == "PiShock" ? "Device needed" : "UUID needed",
        _ => "Connecting",
    };
}
