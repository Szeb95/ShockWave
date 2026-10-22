using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ShockWave.Overlay.Sps;

[Flags]
internal enum SpsSources { OtherSockets = 1, OtherBody = 2, OtherPlugs = 4, MySockets = 8, MyBody = 16, MyPlugs = 32, All = 63 }
internal sealed record SpsHole(string Id, string Name, string Kind, string Stem, float X, float Y, SpsSources Sources, List<string> Inputs)
{
    public bool DiscoveryOnly { get; init; }
}
internal sealed record SpsWire
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
    public string HoleId { get; init; } = "";
    public SpsTarget Target { get; init; } = new("", "");
    public bool Enabled { get; init; } = true;
    public SpsResponse Response { get; init; }
    public double InputMin { get; init; }
    public double InputMax { get; init; } = 1;
    public double Gain { get; init; } = 1;
    public int DeadZone { get; init; }
    public int SmoothingMs { get; init; }
    public int TimeoutMs { get; init; } = 2000;
    // Empty means every permitted interaction. Imported exact mappings stay exact.
    public List<string> ExactInputs { get; init; } = [];
}
internal sealed record SpsBoardDocument(int Version, bool HolesLocked, List<SpsHole> Holes, List<SpsWire> Wires)
{
    public bool AllToysToAllHoles { get; init; }
    public SpsSources? AutomaticSources { get; init; }
    public List<SpsWire> AutomaticToyOptions { get; init; } = [];
    public SpsBoardDocument Copy() => this with { Holes = Holes.Select(h => h with { Inputs = h.Inputs.ToList() }).ToList(), Wires = Wires.Select(w => w with { ExactInputs = w.ExactInputs.ToList() }).ToList(), AutomaticToyOptions=AutomaticToyOptions.Select(w=>w with { ExactInputs=[] }).ToList() };
}
internal static class SpsBoardModel
{
    // Older layouts inherit one shared set without overwriting their manual choices.
    public static SpsSources EffectiveAutomaticSources(SpsBoardDocument board) => board.AutomaticSources ??
        (board.Holes.Count==0 ? SpsSources.OtherSockets|SpsSources.OtherBody|SpsSources.OtherPlugs :
            board.Holes.Aggregate((SpsSources)0,(sources,hole)=>sources|hole.Sources));
    public static string Key(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];
    public static string Stem(string address) => SpsSignals.Describe(address) is null ? address : address[..address.LastIndexOf('/')];
    public static SpsHole Hole(string address, int index = 0)
    {
        var signal = SpsSignals.Describe(address); var stem = Stem(address); var name = signal?.Zone ?? address[19..];
        var n = name.ToLowerInvariant(); var x = .5f; var y = .28f + index % 6 * .09f;
        if (new[] { "mouth", "oral", "head", "blowjob" }.Any(n.Contains)) y = .145f;
        else if (new[] { "anal", "anus", "ass", "back", "rear" }.Any(n.Contains)) { x = .58f; y = .70f; }
        else if (new[] { "vagin", "pussy", "front", "groin", "penis", "cock", "genital" }.Any(n.Contains)) y = .68f;
        else if (new[] { "chest", "breast", "nipple" }.Any(n.Contains)) { x = n.Contains("left") ? .45f : n.Contains("right") ? .55f : .5f; y = .33f; }
        return new(Key(stem), name[..Math.Min(name.Length, 80)], signal?.Kind ?? "Custom", stem, x, y,
            SpsSources.OtherSockets | SpsSources.OtherBody | SpsSources.OtherPlugs, [address]);
    }
    public static SpsSources Source(SpsSignal signal)
    {
        var field = signal.Address[(signal.Address.LastIndexOf('/') + 1)..]; var self = signal.Interaction == "Self";
        if (signal.Kind is "Touch" or "Custom" || field.StartsWith("Touch", StringComparison.Ordinal)) return self ? SpsSources.MyBody : SpsSources.OtherBody;
        var plug = field.StartsWith("Frot", StringComparison.Ordinal) ? signal.Kind == "Plug" : signal.Kind == "Socket";
        return plug ? self ? SpsSources.MyPlugs : SpsSources.OtherPlugs : self ? SpsSources.MySockets : SpsSources.OtherSockets;
    }
    public static SpsBoardDocument FromRules(List<SpsRule> rules)
    {
        var board = new SpsBoardDocument(1, false, [], []);
        foreach (var rule in rules)
        {
            var prototype = Hole(rule.Address, board.Holes.Count);
            var hole = board.Holes.FirstOrDefault(h => h.Id == prototype.Id);
            if (hole is null) { hole = prototype with { Sources = SpsSources.All }; board.Holes.Add(hole); }
            else if (!hole.Inputs.Contains(rule.Address)) hole.Inputs.Add(rule.Address);
            foreach (var target in rule.Targets) board.Wires.Add(new()
            {
                Id = Key(rule.Id + "/" + target.DeviceKey + "/" + target.ChannelId), HoleId = hole.Id, Target = target,
                Enabled = rule.Enabled, Response = rule.Response, InputMin = rule.InputMin, InputMax = rule.InputMax,
                Gain = rule.Gain, DeadZone = rule.DeadZone, SmoothingMs = rule.SmoothingMs, TimeoutMs = rule.TimeoutMs, ExactInputs = [rule.Address]
            });
        }
        return board;
    }
    public static bool Discover(SpsBoardDocument board, SpsSignal signal)
    {
        var prototype = Hole(signal.Address, board.Holes.Count); var hole = board.Holes.FirstOrDefault(h => h.Id == prototype.Id);
        if (hole is null) { if (board.Holes.Count >= 64) return false; board.Holes.Add(prototype); return true; }
        if (hole.Inputs.Contains(signal.Address) || hole.Inputs.Count >= 16) return false;
        hole.Inputs.Add(signal.Address); return true;
    }
    public static List<SpsRule> Compile(SpsBoardDocument board, IReadOnlyList<SpsTarget>? allTargets = null)
    {
        if (board.AllToysToAllHoles)
        {
            var targets = allTargets?.Where(t=>SpsSignals.LocalDevice(t.DeviceKey)).Distinct().OrderBy(t => t.DeviceKey, StringComparer.Ordinal).ThenBy(t => t.ChannelId, StringComparer.Ordinal).ToList() ?? [];
            if (targets.Count > 256) throw new InvalidDataException("Use at most 256 toy functions with All toys to all holes.");
            if (targets.Count == 0) return [];
            // Shared rules per contact keep automatic routing bounded. Saved
            // manual wires never become automatic wires.
            var compiled=new List<SpsRule>();
            var options=targets.Select(t=>board.AutomaticToyOptions.FirstOrDefault(o=>o.Target==t)??new SpsWire { Target=t });
            foreach(var group in options.GroupBy(o=>(o.Enabled,o.Response,o.InputMin,o.InputMax,o.Gain,o.DeadZone,o.SmoothingMs,o.TimeoutMs)))
            {
                var setting=group.First(); var groupId=Key(JsonSerializer.Serialize(group.Key, new JsonSerializerOptions { IncludeFields=true }));
                var automatic=board with { AllToysToAllHoles=false,
                    Holes=board.Holes.Select(h=>h with { Sources=EffectiveAutomaticSources(board) }).ToList(),Wires=board.Holes.Select(h=>setting with
                    { Id=Key("all/"+groupId+"/"+h.Id),HoleId=h.Id,ExactInputs=[] }).ToList() };
                compiled.AddRange(Compile(automatic).Select(r=>r with { Targets=group.Select(o=>o.Target).ToList() }));
            }
            SpsStore.Validate(compiled, 2048, 256); return compiled;
        }
        var rules = new List<SpsRule>();
        foreach (var wire in board.Wires)
        {
            if(!SpsSignals.LocalDevice(wire.Target.DeviceKey)) continue;
            var hole = board.Holes.Single(h => h.Id == wire.HoleId);
            if(hole.DiscoveryOnly) continue; // Unverified contact metadata is never an output rule.
            var inputs = wire.ExactInputs.Count > 0 ? wire.ExactInputs : hole.Inputs;
            foreach (var address in inputs)
            {
                var signal = SpsSignals.Describe(address);
                if (signal is not null && !hole.Sources.HasFlag(Source(signal))) continue;
                var field = address[(address.LastIndexOf('/') + 1)..]; string? root = null;
                if (wire.ExactInputs.Count == 0 && signal is not null)
                {
                    // Prefer continuous contacts over their binary Close companions.
                    if (field.EndsWith("Close", StringComparison.Ordinal)) continue;
                    if (field.EndsWith("NewRoot", StringComparison.Ordinal)) continue;
                    if (field.EndsWith("NewTip", StringComparison.Ordinal)) root = address[..^3] + "Root";
                    if ((field is "PenOthers" or "PenSelf") && inputs.Any(a => a == address + "NewTip" || a == address + "NewRoot")) continue;
                }
                rules.Add(new()
                {
                    Id = Key(wire.Id + "/" + address), Name = hole.Name, Address = address, RootAddress = root,
                    Enabled = wire.Enabled, Response = wire.Response, InputMin = wire.InputMin, InputMax = wire.InputMax,
                    Gain = wire.Gain, DeadZone = wire.DeadZone, SmoothingMs = wire.SmoothingMs, TimeoutMs = wire.TimeoutMs, Targets = [wire.Target]
                });
            }
            // A root can be received before the matching tip; wait for both rather
            // than routing the raw one-metre root proximity as insertion depth.
            if (wire.ExactInputs.Count == 0)
                foreach (var address in inputs.Where(a => a.EndsWith("NewRoot", StringComparison.Ordinal)))
                {
                    var tip = address[..^4] + "Tip"; if (inputs.Contains(tip)) continue;
                    if (SpsSignals.Describe(address) is not { } signal || !hole.Sources.HasFlag(Source(signal))) continue;
                    rules.Add(new() { Id = Key(wire.Id + "/" + tip), Name = hole.Name, Address = tip, RootAddress = address,
                        Enabled = wire.Enabled, Response = wire.Response, InputMin = wire.InputMin, InputMax = wire.InputMax, Gain = wire.Gain, DeadZone = wire.DeadZone,
                        SmoothingMs = wire.SmoothingMs, TimeoutMs = wire.TimeoutMs, Targets = [wire.Target] });
                }
        }
        SpsStore.Validate(rules, 2048); return rules;
    }
}
internal sealed class SpsBoardStore(string file)
{
    public string PathName { get; } = file;
    public string? Warning { get; private set; }
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    public SpsBoardDocument Load(List<SpsRule> legacy,bool legacyFileExists=false)
    {
        if (!File.Exists(PathName)) return legacy.Count==0 && !legacyFileExists
            ? new(1,false,[],[]) { AllToysToAllHoles=true } : SpsBoardModel.FromRules(legacy);
        try { return Read(PathName); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        { Warning = "Saved SPS layout could not be read. It is preserved; existing exact mappings are still available."; return SpsBoardModel.FromRules(legacy); }
    }
    public static SpsBoardDocument Read(string path)
    {
        if (new FileInfo(path).Length > 512 * 1024) throw new InvalidDataException("SPS layout is too large.");
        var board = JsonSerializer.Deserialize<SpsBoardDocument>(File.ReadAllText(path), Json) ?? throw new InvalidDataException("Missing SPS layout.");
        Validate(board); return board;
    }
    public static void Validate(SpsBoardDocument board)
    {
        if (board.Version != 1 || board.Holes is null || board.Wires is null || (board.AutomaticSources is {} sources && ((int)sources & ~63)!=0) || board.AutomaticToyOptions is null || board.AutomaticToyOptions.Count>256 || board.Holes.Count > 64 || board.Wires.Count > 1024
            || board.Holes.Any(h => h is null) || board.Wires.Any(w => w is null)
            || board.Holes.Select(h => h.Id).Distinct().Count() != board.Holes.Count || board.Wires.Select(w => w.Id).Distinct().Count() != board.Wires.Count)
            throw new InvalidDataException("Invalid SPS layout version, capacity or duplicate IDs.");
        foreach (var hole in board.Holes)
            if (string.IsNullOrWhiteSpace(hole.Name) || hole.Name.Length > 80 || hole.Name.Any(char.IsControl) || hole.Stem is null
                || hole.Id != SpsBoardModel.Key(hole.Stem) || !float.IsFinite(hole.X) || !float.IsFinite(hole.Y) || hole.X is < 0 or > 1 || hole.Y is < 0 or > 1
                || hole.Inputs is null || hole.Inputs.Count is 0 or > 16 || hole.Inputs.Distinct().Count() != hole.Inputs.Count
                || ((int)hole.Sources & ~63) != 0 || hole.Inputs.Any(a => a is null || !SpsSignals.SafeAddress(a)
                    || (hole.DiscoveryOnly ? SpsDiscovery.Describe(a)?.Stem != hole.Stem : SpsBoardModel.Stem(a) != hole.Stem)))
                throw new InvalidDataException("Check the hole identity, position and inputs.");
        foreach (var wire in board.Wires)
        {
            var hole = board.Holes.FirstOrDefault(h => h.Id == wire.HoleId);
            if (string.IsNullOrWhiteSpace(wire.Id) || wire.Id.Length > 64 || hole is null || wire.Target is null || wire.ExactInputs is null
                || wire.ExactInputs.Count > 16 || wire.ExactInputs.Distinct().Count() != wire.ExactInputs.Count || wire.ExactInputs.Any(a => !hole.Inputs.Contains(a)))
                throw new InvalidDataException("Check the connection identity, hole and inputs.");
            SpsStore.Validate([new() { Id = wire.Id, Name = hole.Name, Address = hole.Inputs[0], Targets = [wire.Target], Enabled = wire.Enabled,
                Response = wire.Response, InputMin = wire.InputMin, InputMax = wire.InputMax, Gain = wire.Gain, DeadZone = wire.DeadZone,
                SmoothingMs = wire.SmoothingMs, TimeoutMs = wire.TimeoutMs }]);
        }
        if(board.AutomaticToyOptions.Any(o=>o is null) || board.AutomaticToyOptions.Select(o=>o.Target).Distinct().Count()!=board.AutomaticToyOptions.Count)
            throw new InvalidDataException("Use one automatic setting per toy function.");
        foreach(var option in board.AutomaticToyOptions) ValidateAutomatic(option);
        _ = SpsBoardModel.Compile(board with { AllToysToAllHoles = false });
    }
    internal static void ValidateAutomatic(SpsWire option)
    {
        if(option.HoleId!="" || option.ExactInputs is null || option.ExactInputs.Count!=0 || string.IsNullOrWhiteSpace(option.Id) || option.Id.Length>64
            || !SpsStore.ValidTarget(option.Target) || !SpsStore.ValidResponse(option.Response,option.InputMin,option.InputMax,option.Gain,option.DeadZone,option.SmoothingMs,option.TimeoutMs))
            throw new InvalidDataException("Check the automatic toy function and response settings.");
    }
    public void Save(SpsBoardDocument board)
    {
        Validate(board); var json = JsonSerializer.Serialize(board, Json);
        if (Encoding.UTF8.GetByteCount(json) > 512 * 1024) throw new InvalidDataException("SPS layout is too large.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(PathName))!);
        if (Warning is not null && File.Exists(PathName)) File.Copy(PathName, PathName + ".preserved-" + Guid.NewGuid().ToString("N"));
        var temporary = PathName + ".tmp-" + Guid.NewGuid().ToString("N");
        try { File.WriteAllText(temporary, json); File.Move(temporary, PathName, true); Warning = null; }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static void Export(string path, SpsBoardDocument board) => new SpsBoardStore(path).Save(board);
}
