using System.Text.Json;
using System.Text.RegularExpressions;
using ShockWave.Overlay.Toys;

namespace ShockWave.Overlay.Sps;

internal sealed record SpsTarget(string DeviceKey, string ChannelId);
internal enum SpsResponse { Level, Motion }
internal sealed record SpsRule
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string Name { get; init; } = "New mapping";
    public bool Enabled { get; init; } = true;
    public string Address { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore]
    public string? RootAddress { get; init; }
    public SpsResponse Response { get; init; }
    public double InputMin { get; init; }
    public double InputMax { get; init; } = 1;
    public double Gain { get; init; } = 1;
    public int DeadZone { get; init; }
    public int SmoothingMs { get; init; }
    public int TimeoutMs { get; init; } = 2000;
    public List<SpsTarget> Targets { get; init; } = [];
}
internal sealed record SpsConfiguration(int Version, List<SpsRule> Rules);
internal sealed record SpsSignal(string Address, string Kind, string Zone, string Interaction,
    string Measurement, double Value, double ReceivedAt, double Rate, double RateFrom = double.NegativeInfinity);

internal static class SpsSignals
{
    public static bool LocalDevice(string key)=>!key.StartsWith("remote/",StringComparison.Ordinal);
    private const string Prefix = "/avatar/parameters/";
    private static readonly Regex Known = new(@"^(?:VF\d+_)?(?:(OGB)/(Pen|Orf)/([^/]+)/([^/]+)|(VFH)/Zone/Touch/([^/]+)/(Self|Others))$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    { "TouchSelf", "TouchOthers", "TouchSelfClose", "TouchOthersClose", "PenSelf", "PenOthers",
      "PenOthersClose", "PenSelfNewRoot", "PenOthersNewRoot", "PenSelfNewTip", "PenOthersNewTip", "FrotOthers", "FrotOthersClose" };
    public static bool SafeAddress(string address) => address.Length is > 19 and <= 256 && address.StartsWith(Prefix, StringComparison.Ordinal)
        && !address.Any(c => char.IsControl(c) || c is '*' or '?' or '[' or ']' or '{' or '}')
        && !Regex.IsMatch(address[Prefix.Length..], @"^(?:VF\d+_)?ShockWave", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
        && address[Prefix.Length..] != "OGB_ENABLED"
        && !address.Contains("Feedback", StringComparison.OrdinalIgnoreCase)
        && !address.Contains("/Rx", StringComparison.OrdinalIgnoreCase) && !address.Contains("_Rx", StringComparison.OrdinalIgnoreCase);
    public static SpsSignal? Describe(string address)
    {
        if (!SafeAddress(address)) return null;
        var match = Known.Match(address[Prefix.Length..]);
        if (!match.Success) return null;
        if (match.Groups[5].Success)
            return new(address, "Touch", match.Groups[6].Value, match.Groups[7].Value, "Touch proximity", 0, double.NegativeInfinity, 0);
        var field = match.Groups[4].Value;
        if (!Fields.Contains(field)) return null;
        var measurement = field.EndsWith("NewRoot", StringComparison.Ordinal) ? "Root proximity (1 m)"
            : field.EndsWith("NewTip", StringComparison.Ordinal) ? "Tip proximity (1 m)"
            : field.StartsWith("Pen", StringComparison.Ordinal) ? "Penetration proximity"
            : field.StartsWith("Frot", StringComparison.Ordinal) ? "Rubbing proximity" : "Touch proximity";
        if (field.EndsWith("Close", StringComparison.Ordinal)) measurement += " · close";
        return new(address, match.Groups[2].Value == "Pen" ? "Plug" : "Socket", match.Groups[3].Value,
            field.Contains("Others", StringComparison.Ordinal) ? "Others" : "Self", measurement, 0, double.NegativeInfinity, 0);
    }
    public static string Label(SpsSignal signal)
    {
        var label = $"{signal.Zone} · {signal.Interaction} · {signal.Measurement}";
        return label[..Math.Min(label.Length, 80)];
    }
    public static bool Scalar(ToyCapability capability) => !capability.IsSetting && capability.Type != ToyCapabilityType.Unknown && capability.NativeMaximum is > 0;
    public static double Normalize(SpsRule rule, double value) => Math.Clamp((value - rule.InputMin) / (rule.InputMax - rule.InputMin), 0, 1);
    public static double Map(SpsRule rule, double normalized, double motion = 0)
    {
        var input = rule.Response == SpsResponse.Motion ? Math.Abs(motion) : normalized;
        var dead = rule.DeadZone / 100d;
        return input <= dead ? 0 : Math.Clamp((input - dead) / (1 - dead) * rule.Gain, 0, 1);
    }
}

internal sealed class SpsStore(string? filePath = null)
{
    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SzebsToys", "sps-mappings.json");
    public string PathName { get; } = filePath ?? DefaultPath;
    public string? Warning { get; private set; }
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public List<SpsRule> Load()
    {
        if (!File.Exists(PathName)) return [];
        try { return Read(PathName); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        { Warning = "SPS mappings could not be read. The saved file was kept; repair it or explicitly save replacement mappings."; return []; }
    }
    public static List<SpsRule> Read(string file)
    {
        if (new FileInfo(file).Length > 256 * 1024) throw new InvalidDataException("Mapping file is too large.");
        var config = JsonSerializer.Deserialize<SpsConfiguration>(File.ReadAllText(file), Json);
        if (config is null || config.Version != 1 || config.Rules is null) throw new InvalidDataException("Unsupported SPS mapping version.");
        Validate(config.Rules); return config.Rules;
    }
    public static void Validate(IReadOnlyList<SpsRule> rules, int maximum = 64, int maximumTargets = 16)
    {
        if (rules.Count > maximum || rules.Any(r => r is null) || rules.Select(r => r.Id).Distinct(StringComparer.Ordinal).Count() != rules.Count)
            throw new InvalidDataException($"Use at most {maximum} mappings with unique IDs.");
        foreach (var rule in rules)
        {
            if (string.IsNullOrWhiteSpace(rule.Id) || rule.Id.Length > 64 || string.IsNullOrWhiteSpace(rule.Name) || rule.Name.Length > 80 || rule.Name.Any(char.IsControl)
                || rule.Address is null || !SpsSignals.SafeAddress(rule.Address) || !Enum.IsDefined(rule.Response)
                || !double.IsFinite(rule.InputMin) || !double.IsFinite(rule.InputMax) || rule.InputMin < -1000 || rule.InputMax > 1000 || rule.InputMin >= rule.InputMax
                || !double.IsFinite(rule.Gain) || rule.Gain is < 0 or > 10 || rule.DeadZone is < 0 or > 99
                || rule.SmoothingMs is < 0 or > 2000 || rule.TimeoutMs is < 250 or > 10000 || rule.Targets is null || rule.Targets.Count > maximumTargets
                || rule.Targets.Any(t => t is null || string.IsNullOrWhiteSpace(t.DeviceKey) || t.DeviceKey.Length > 128 || string.IsNullOrWhiteSpace(t.ChannelId) || t.ChannelId.Length > 64
                    || t.DeviceKey.Any(char.IsControl) || t.ChannelId.Any(char.IsControl)) || rule.Targets.Distinct().Count() != rule.Targets.Count)
                throw new InvalidDataException("Check the mapping name, exact input address, ranges and target functions.");
            if (rule.RootAddress is not null && (SpsSignals.Describe(rule.RootAddress) is not { Kind: "Socket" }
                || !rule.RootAddress.EndsWith("NewRoot", StringComparison.Ordinal) || rule.Address != rule.RootAddress[..^4] + "Tip"))
                throw new InvalidDataException("Socket depth needs the matching root and tip contacts.");
        }
    }
    internal static bool ValidTarget(SpsTarget? t) => t is not null && !string.IsNullOrWhiteSpace(t.DeviceKey) && t.DeviceKey.Length<=128
        && !string.IsNullOrWhiteSpace(t.ChannelId) && t.ChannelId.Length<=64 && !t.DeviceKey.Any(char.IsControl) && !t.ChannelId.Any(char.IsControl);
    internal static bool ValidResponse(SpsResponse response,double min,double max,double gain,int dead,int smoothing,int timeout) => Enum.IsDefined(response)
        && double.IsFinite(min) && double.IsFinite(max) && min>=-1000 && max<=1000 && min<max
        && double.IsFinite(gain) && gain is >=0 and <=10 && dead is >=0 and <=99 && smoothing is >=0 and <=2000 && timeout is >=250 and <=10000;
    public void Save(List<SpsRule> rules) { Write(PathName, rules); Warning = null; }
    public static void Write(string file, List<SpsRule> rules)
    {
        Validate(rules); Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        var json = JsonSerializer.Serialize(new SpsConfiguration(1, rules), Json);
        if (System.Text.Encoding.UTF8.GetByteCount(json) > 256 * 1024) throw new InvalidDataException("Mapping file is too large. Reduce the number of targets or mappings.");
        var temporary = file + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, json); File.Move(temporary, file, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
