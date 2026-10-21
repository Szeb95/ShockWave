namespace ShockWave.Overlay.Toys;

internal static class LovenseModelCatalog
{
    // Facts normalized from LovenseToySupportConfig.csv v1.6, inspected 2026-10-07.
    // Shared symbols identify families, not a firmware revision. Intersect ambiguous functions.
    private static readonly Dictionary<string, (string Model, string Functions)> Models = new(StringComparer.OrdinalIgnoreCase)
    {
        ["l"] = ("Ambi", "v"), ["v"] = ("Mission family", "v"), ["ca"] = ("Mission family", "v"),
        ["su"] = ("C20", "v"), ["t"] = ("Calor", "v"), ["r"] = ("Diamo", "v"),
        ["j"] = ("Dolce", "v1 v2"), ["w"] = ("Domi", "v"), ["p"] = ("Edge", "v1 v2"),
        ["ef"] = ("Exomoon", "v"), ["x"] = ("Ferri", "v"), ["ei"] = ("Flexer", "v f"),
        ["n"] = ("Gemini", "v1 v2"), ["ea"] = ("Gravity", "v t"), ["ed"] = ("Gush", "v"),
        ["ba"] = ("Solace Pro", "t pos"), ["z"] = ("Hush", "v"), ["eb"] = ("Hyphy", "v"),
        ["u"] = ("Lapis", "v1 v2 v3"), ["s"] = ("Lush family", "v"),
        ["b"] = ("Max family", "v p"), ["a"] = ("Nora", "v r"), ["c"] = ("Nora", "v r"),
        ["toyb"] = ("Max family", "v p"), ["toya"] = ("Nora", "v r"), ["toyc"] = ("Nora", "v r"),
        ["o"] = ("Osci family", "v"), ["oc"] = ("Osci family", "v"), ["qa"] = ("QA01", "v1 v2"),
        ["el"] = ("Ridge", "v r"), ["h"] = ("Solace", "t d"), ["q"] = ("Tenera family", "v s"),
        ["sd"] = ("Vulse", "v"), ["f"] = ("XMachine", "v t"), ["ez"] = ("Gush 2", "o"),
        ["an"] = ("Lush Anal", "v"), ["fs"] = ("Mini XMachine", "v t"), ["qb"] = ("Fizz", "v s"),
        ["wd"] = ("Spinel", "v t"), ["cb"] = ("Velvo", "v r"),
    };

    public static (string Model, IReadOnlyList<ToyCapability> Capabilities, bool Known) Identify(string symbol)
    {
        if (!Models.TryGetValue(symbol.Trim('\0', '0', ' ').ToLowerInvariant(), out var entry))
            return ("Unknown Lovense model", Array.Empty<ToyCapability>(), false);
        return (entry.Model, Array.AsReadOnly(entry.Functions.Split(' ').Select(Capability).ToArray()), true);
    }

    public static (string Model, IReadOnlyList<ToyCapability> Capabilities, bool Known) IdentifyModel(string model)
    {
        var symbol = Models.FirstOrDefault(entry => entry.Value.Model == model).Key;
        return Identify(symbol ?? "");
    }

    private static ToyCapability Capability(string function)
    {
        var (type, name) = function switch
        {
            "v" => (ToyCapabilityType.Vibration, "Vibration"),
            "v1" => (ToyCapabilityType.Vibration, "Vibration 1"),
            "v2" => (ToyCapabilityType.Vibration, "Vibration 2"),
            "v3" => (ToyCapabilityType.Vibration, "Vibration 3"),
            "r" => (ToyCapabilityType.Rotation, "Rotation"),
            "p" => (ToyCapabilityType.Pump, "Pump"),
            "t" => (ToyCapabilityType.Thrusting, "Thrusting speed"),
            "f" => (ToyCapabilityType.Fingering, "Fingering"),
            "s" => (ToyCapabilityType.Suction, "Suction"),
            "d" => (ToyCapabilityType.Depth, "Depth"),
            "pos" => (ToyCapabilityType.Position, "Position"),
            "o" => (ToyCapabilityType.Oscillation, "Oscillation"),
            _ => (ToyCapabilityType.Unknown, "Unknown function"),
        };
        // Documented native function ranges, rechecked 2026-10-08. Physical behavior remains unmeasured.
        var maximum = type switch
        {
            ToyCapabilityType.Pump or ToyCapabilityType.Depth => 3,
            ToyCapabilityType.Position => 100,
            ToyCapabilityType.Unknown => (int?)null,
            _ => 20,
        };
        return new ToyCapability(function, type, name, maximum.HasValue ? 0 : null,
            maximum, maximum.HasValue ? 1 : null, null, null);
    }

    public static int Command(string channel) => channel switch
    {
        "v" => 0, "v1" => 1, "v2" => 2, "v3" => 3, "r" => 4, "p" => 5,
        "t" => 6, "f" => 7, "s" => 8, "d" => 9, "pos" => 10, "o" => 11,
        _ => throw new InvalidOperationException("This toy function is not supported."),
    };
}
