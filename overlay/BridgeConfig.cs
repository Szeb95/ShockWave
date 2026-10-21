using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;

namespace ShockWave.Overlay;

internal sealed class BridgeConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public string ApiBaseUrl { get; set; } = "https://api.openshock.app";
    public string Provider { get; set; } = "OpenShock";
    public string PiShockUsername { get; set; } = "";
    public bool RememberPiShockApiKey { get; set; } = true;
    public List<ShockerConfig> PiShockShockers { get; set; } = [];
    public bool PiShockSelfTestEnabled { get; set; }
    public List<string> PiShockSelfTestShockerKeys { get; set; } = [];
    [JsonIgnore] public bool IsPiShock => Provider == "PiShock";
    [JsonIgnore] public List<ShockerConfig> ActiveShockers => IsPiShock ? PiShockShockers : Shockers;
    [JsonIgnore] public bool ActiveSelfTestEnabled { get => IsPiShock ? PiShockSelfTestEnabled : SelfTestEnabled; set { if (IsPiShock) PiShockSelfTestEnabled = value; else SelfTestEnabled = value; } }
    [JsonIgnore] public List<string> ActiveSelfTestShockerKeys { get => IsPiShock ? PiShockSelfTestShockerKeys : SelfTestShockerKeys; set { if (IsPiShock) PiShockSelfTestShockerKeys = value; else SelfTestShockerKeys = value; } }
    public bool DryRun { get; set; }
    public bool SelfTestEnabled { get; set; }
    public List<string> SelfTestShockerKeys { get; set; } = [];
    public string SelfTestAvatarGroup { get; set; } = "PiCtrl";
    public bool RememberApiToken { get; set; } = true;
    public int PollIntervalMs { get; set; } = 2000;
    public List<ShockerConfig> Shockers { get; set; } = [];
    public OscConfig Osc { get; set; } = new();

    public static string ConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SzebsToys", "bridge-config.json");

    public static BridgeConfig Load(string bridgeDirectory)
    {
        var source = File.Exists(ConfigPath)
            ? ConfigPath
            : File.Exists(Path.Combine(bridgeDirectory, "config.json"))
                ? Path.Combine(bridgeDirectory, "config.json")
                : Path.Combine(bridgeDirectory, "config.example.json");
        var json = File.ReadAllText(source);
        var result = Parse(json);
        if (!File.Exists(ConfigPath) || !json.Contains("\"shockers\"")) result.Save();
        return result;
    }

    public static BridgeConfig Parse(string json)
    {
        var result = JsonSerializer.Deserialize<BridgeConfig>(json, JsonOptions)
            ?? throw new InvalidDataException("The bridge configuration is empty.");
        result.Osc ??= new OscConfig();
        result.Shockers ??= [];
        result.SelfTestShockerKeys ??= [];
        result.PiShockShockers ??= [];
        result.PiShockSelfTestShockerKeys ??= [];
        foreach (var shocker in result.PiShockShockers) { shocker.Service = "PiShock"; shocker.NameSyncPending = false; }
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("shockers", out _))
        {
            string Text(string key, string fallback = "") => root.TryGetProperty(key, out var item) ? item.GetString() ?? fallback : fallback;
            int Number(string key, int fallback) => root.TryGetProperty(key, out var item) ? item.GetInt32() : fallback;
            result.Shockers.Add(new ShockerConfig
            {
                Key = "legacy-shocker", Name = "My shocker", Uuid = Text("shockerId"),
                MaxIntensity = Number("maxIntensity", 100),
                MaxDurationMs = Number("maxDurationMs", 15000),
                CooldownMs = Number("minCommandGapMs", 1000),
                DurationInputMax = Number("durationInputMax", 100),
                DurationAtInputMaxMs = Number("durationAtInputMaxMs", 15000),
                IntensityAddress = result.Osc.IntensityAddress,
                DurationAddress = result.Osc.DurationAddress,
                ActionAddress = result.Osc.ActionAddress,
                FeedbackEventAddress = result.Osc.FeedbackEventAddress,
                FeedbackActiveAddress = result.Osc.FeedbackActiveAddress,
                FeedbackIntensityAddress = result.Osc.FeedbackIntensityAddress,
                FeedbackModeAddress = result.Osc.FeedbackModeAddress,
            });
        }
        var savedShockers = root.TryGetProperty("shockers", out var saved) && saved.ValueKind == JsonValueKind.Array
            ? saved.EnumerateArray().ToArray() : [];
        for (var index = 0; index < result.Shockers.Count; index++)
        {
            var shocker = result.Shockers[index];
            bool Has(string name) => index < savedShockers.Length && savedShockers[index].EnumerateObject()
                .Any(property => string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase));
            if (!Has("manualMinIntensity")) shocker.ManualMinIntensity = shocker.MinIntensity;
            if (!Has("manualMaxIntensity")) shocker.ManualMaxIntensity = shocker.MaxIntensity;
            if (!Has("manualMinDurationMs")) shocker.ManualMinDurationMs = shocker.MinDurationMs;
            if (!Has("manualMaxDurationMs")) shocker.ManualMaxDurationMs = shocker.MaxDurationMs;
        }
        return result;
    }

    public string ToJson(bool compact = false) => JsonSerializer.Serialize(this,
        compact ? new JsonSerializerOptions(JsonOptions) { WriteIndented = false } : JsonOptions);

    public void Save(string? destination = null)
    {
        var path = destination ?? ConfigPath;
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, ToJson() + Environment.NewLine);
        File.Move(temporary, path, true);
    }

    public BridgeConfig Clone() => Parse(ToJson());

    public string? Validate(string token, bool requireToken = true)
    {
        if (Provider is not ("OpenShock" or "PiShock")) return "Choose OpenShock or PiShock.";
        if (PiShockUsername is null || PiShockUsername.Length > 256 || PiShockUsername.Any(char.IsControl)) return "Enter a valid PiShock username.";
        if (!Uri.TryCreate(ApiBaseUrl, UriKind.Absolute, out var uri) ||
            !(uri.Scheme == Uri.UriSchemeHttps ||
              (uri.Scheme == Uri.UriSchemeHttp && (uri.Host == "localhost" || uri.Host == "127.0.0.1"))))
            return "The API URL must use HTTPS, except for a local test server.";
        if (requireToken && !DryRun && ActiveShockers.Any(s => s.Uuid.Length > 0) && token.Length == 0) return $"Live {Provider} control requires an API key/token.";
        if (requireToken && IsPiShock && !DryRun && ActiveShockers.Any(s => s.Uuid.Length > 0) && string.IsNullOrWhiteSpace(PiShockUsername)) return "Enter your PiShock username in Settings.";
        if (PollIntervalMs < 500) return "Connection retry interval must be at least 500 ms.";
        if (Osc.ListenPort is < 1 or > 65535 || Osc.SendPort is < 1 or > 65535)
            return "OSC ports must be 1–65535.";
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var uuids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var shocker in Shockers.Concat(PiShockShockers))
        {
            var error = shocker.Validate();
            if (error is not null) return $"{shocker.Name}: {error}";
            if (!keys.Add(shocker.Key)) return "Each shocker must have a unique panel key.";
            var identity = shocker.IsPiShock ? string.Join('/', shocker.Uuid.Split('/').Take(2)) : shocker.Uuid;
            if (shocker.Uuid.Length > 0 && !uuids.Add(shocker.Service + ":" + identity)) return "That device is already assigned to another panel.";
        }
        if (!Regex.IsMatch(SelfTestAvatarGroup, @"^[A-Za-z0-9_]+$"))
            return "VRChat test group may contain letters, numbers, and underscores.";
        if (SelfTestShockerKeys.Concat(PiShockSelfTestShockerKeys).Distinct(StringComparer.Ordinal).Count() != SelfTestShockerKeys.Count + PiShockSelfTestShockerKeys.Count ||
            SelfTestShockerKeys.Any(key => !Shockers.Any(s => s.Key == key)) || PiShockSelfTestShockerKeys.Any(key => !PiShockShockers.Any(s => s.Key == key)))
            return "Choose existing devices for the self test.";
        if (ActiveSelfTestEnabled && ActiveSelfTestShockerKeys.Count == 0)
            return "Select at least one of your own test devices under Debug.";
        if (ActiveSelfTestEnabled && !DryRun && ActiveShockers.Any(s => ActiveSelfTestShockerKeys.Contains(s.Key) && s.Uuid.Length == 0))
            return "Each selected test device needs its own device identifier under Options.";
        return null;
    }
}

internal sealed class ShockerConfig
{
    public string Service { get; set; } = "OpenShock";
    [JsonIgnore] public bool IsPiShock => Service == "PiShock";
    public string Key { get; set; } = Guid.NewGuid().ToString("D");
    public string Name { get; set; } = "My shocker";
    public bool NameSyncPending { get; set; }
    public string Model { get; set; } = "";
    public string Uuid { get; set; } = "";
    public bool Paused { get; set; }
    public int MinIntensity { get; set; }
    public int MaxIntensity { get; set; } = 100;
    public int MinDurationMs { get; set; } = 300;
    public int MaxDurationMs { get; set; } = 15000;
    public int ManualMinIntensity { get; set; }
    public int ManualMaxIntensity { get; set; } = 100;
    public int ManualMinDurationMs { get; set; } = 300;
    public int ManualMaxDurationMs { get; set; } = 15000;
    public int CooldownMs { get; set; } = 1000;
    public int DurationInputMax { get; set; } = 100;
    public int DurationAtInputMaxMs { get; set; } = 15000;
    public string AvatarGroup { get; set; } = "PiCtrl";
    public string? IntensityAddress { get; set; }
    public string? DurationAddress { get; set; }
    public string? ActionAddress { get; set; }
    public string? FeedbackEventAddress { get; set; }
    public string? FeedbackActiveAddress { get; set; }
    public string? FeedbackIntensityAddress { get; set; }
    public string? FeedbackModeAddress { get; set; }

    public ShockerConfig Clone() => (ShockerConfig)MemberwiseClone();

    public void UseAvatarGroup(string group)
    {
        if (group == AvatarGroup) return;
        AvatarGroup = group;
        IntensityAddress = DurationAddress = ActionAddress = null;
        FeedbackEventAddress = FeedbackActiveAddress = FeedbackIntensityAddress = FeedbackModeAddress = null;
    }

    public string? Validate()
    {
        if (string.IsNullOrWhiteSpace(Key)) return "Missing panel key.";
        if (string.IsNullOrWhiteSpace(Name)) return "Enter a toy name.";
        if (NameSyncPending && !ValidSyncedName(Name)) return "Synced names must contain 1–64 characters without line breaks.";
        if (Service is not ("OpenShock" or "PiShock")) return "Unknown shocker service.";
        if (Uuid.Length > 0 && (IsPiShock ? !ValidPiShockTarget(Uuid) : !Guid.TryParseExact(Uuid, "D", out _)))
            return IsPiShock ? "Choose a PiShock device with Add shocker, or enter hub/shocker[/share] IDs." : "Enter a valid shocker UUID.";
        if (MinIntensity < 0 || MaxIntensity > 100 || MinIntensity > MaxIntensity) return "Intensity range must be within 0–100%.";
        if (MinDurationMs < 300 || MaxDurationMs > 65535 || MinDurationMs > MaxDurationMs) return "Duration range must be within 0.3–65.535 seconds.";
        if (ManualMinIntensity < 0 || ManualMaxIntensity > 100 || ManualMinIntensity > ManualMaxIntensity) return "Your button intensity range must be within 0–100%.";
        if (ManualMinDurationMs < 300 || ManualMaxDurationMs > 65535 || ManualMinDurationMs > ManualMaxDurationMs) return "Your button duration range must be within 0.3–65.535 seconds.";
        if (CooldownMs is < 0 or > 60000) return "Cooldown must be within 0–60 seconds.";
        if (DurationInputMax is < 1 or > 100 || DurationAtInputMaxMs is < 300 or > 65535) return "Invalid avatar duration mapping.";
        if (!Regex.IsMatch(AvatarGroup, @"^[A-Za-z0-9_]+$")) return "Avatar group may contain letters, numbers, and underscores.";
        foreach (var address in new[] { IntensityAddress, DurationAddress, ActionAddress, FeedbackEventAddress,
                     FeedbackActiveAddress, FeedbackIntensityAddress, FeedbackModeAddress })
            if (address is not null && !address.StartsWith('/')) return "Avatar addresses must start with /.";
        return null;
    }

    public static bool ValidSyncedName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 64 && !name.Any(char.IsControl);
    public static bool ValidPiShockTarget(string? value) => value is not null && Regex.IsMatch(value, @"^[1-9][0-9]{0,9}/[1-9][0-9]{0,9}(?:/[1-9][0-9]{0,9})?$") && value.Split('/').All(part => int.TryParse(part, out var number) && number > 0);
    public bool MatchesControlAddress(string address) => address == (IntensityAddress ?? $"/avatar/parameters/{AvatarGroup}_Intensity") ||
        address == (DurationAddress ?? $"/avatar/parameters/{AvatarGroup}_Duration") || address == (ActionAddress ?? $"/avatar/parameters/{AvatarGroup}_Action");
}

internal sealed class OscConfig
{
    public string ListenHost { get; set; } = "127.0.0.1";
    public int ListenPort { get; set; } = 9001;
    public string SendHost { get; set; } = "127.0.0.1";
    public int SendPort { get; set; } = 9000;
    public string IntensityAddress { get; set; } = "/avatar/parameters/PiCtrl_Intensity";
    public string DurationAddress { get; set; } = "/avatar/parameters/PiCtrl_Duration";
    public string ActionAddress { get; set; } = "/avatar/parameters/PiCtrl_Action";
    public string FeedbackEventAddress { get; set; } = "/avatar/parameters/PiCtrl_FeedbackEvent";
    public string FeedbackActiveAddress { get; set; } = "/avatar/parameters/PiCtrl_FeedbackActive";
    public string FeedbackIntensityAddress { get; set; } = "/avatar/parameters/PiCtrl_FeedbackIntensity";
    public string FeedbackModeAddress { get; set; } = "/avatar/parameters/PiCtrl_FeedbackMode";
}
