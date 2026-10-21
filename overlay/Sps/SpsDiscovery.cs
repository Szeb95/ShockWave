using System.Text.RegularExpressions;
using ShockWave.ShaderLab;

namespace ShockWave.Overlay.Sps;

internal sealed record SpsDiscoveryIdentity(string Stem,string Name,string Kind);
internal sealed record SpsDiscoveryCandidate(string Id,string Stem,string Name,string Kind,string[] Inputs,string[] SupportedInputs,double SeenAt);
internal sealed record SpsDiscoveryRow(string Id,string Name,string Kind,string Stem,int Inputs,bool Added,bool Supported);

// Positive SPS namespaces only. Anatomical words in ordinary avatar parameters
// are not evidence of a contact (for example MouthOpen from face tracking).
internal sealed class SpsDiscovery
{
    private const string Prefix="/avatar/parameters/";
    private static readonly Regex Family=new(@"^(?:VF\d+_)?(?:OGB|TPS_Internal|SPS)/(Pen|Orf|Plug|Socket)/([^/]+)/([^/]+)$",RegexOptions.CultureInvariant);
    private static readonly Regex ZoneFamily=new(@"^(?:VF\d+_)?VFH/Zone/(Pen|Orf|Plug|Socket)/([^/]+)/([^/]+)$",RegexOptions.CultureInvariant);
    private sealed class Entry(SpsDiscoveryIdentity identity)
    {
        public SpsDiscoveryIdentity Identity=identity;
        public readonly Dictionary<string,double> Inputs=[];
        public double SeenAt;
    }
    private readonly Dictionary<string,Entry> entries=[];
    public static SpsDiscoveryIdentity? Describe(string address)
    {
        if(!SpsSignals.SafeAddress(address)) return null;
        var parameter=address[Prefix.Length..]; var match=Family.Match(parameter);
        if(!match.Success) match=ZoneFamily.Match(parameter);
        if(!match.Success) return null;
        var kind=match.Groups[1].Value is "Pen" or "Plug" ? "Plug" : "Socket";
        var name=match.Groups[2].Value; if(string.IsNullOrWhiteSpace(name)) return null; if(name.Length>80) name=name[..80];
        return new(address[..address.LastIndexOf('/')],name,kind);
    }
    public static bool ValidValue(OscMessage input)
    {
        if(input.Arguments.Length!=1) return false;
        return input.Arguments[0] switch
        {
            bool=>true, float f=>float.IsFinite(f) && Math.Abs(f)<=1000,
            int i=>Math.Abs((long)i)<=1000, _=>false
        };
    }
    public void Observe(OscMessage input,double at)
    {
        if(!double.IsFinite(at) || Describe(input.Address) is not { } identity || !ValidValue(input)) return;
        if(SpsSignals.Describe(input.Address) is not null && input.Arguments[0] is float f && f is <0 or >1) return;
        if(SpsSignals.Describe(input.Address) is not null && input.Arguments[0] is int i && i is <0 or >1) return;
        if(!entries.TryGetValue(identity.Stem,out var entry))
        {
            if(entries.Count>=64) { _=Candidates(at); if(entries.Count>=64) return; }
            entries[identity.Stem]=entry=new(identity);
        }
        if(entry.Inputs.TryGetValue(input.Address,out var old) && at<old) return;
        if(entry.Inputs.Count>=16 && !entry.Inputs.ContainsKey(input.Address))
        {
            if(SpsSignals.Describe(input.Address) is null) return;
            var unverified=entry.Inputs.FirstOrDefault(p=>SpsSignals.Describe(p.Key) is null).Key;
            if(unverified is null) return;
            entry.Inputs.Remove(unverified);
        }
        entry.Inputs[input.Address]=at; entry.SeenAt=Math.Max(entry.SeenAt,at);
    }
    public IReadOnlyList<SpsDiscoveryCandidate> Candidates(double now)
    {
        foreach(var pair in entries.ToArray())
        {
            foreach(var input in pair.Value.Inputs.Where(p=>now-p.Value>120).Select(p=>p.Key).ToArray()) pair.Value.Inputs.Remove(input);
            if(pair.Value.Inputs.Count==0) entries.Remove(pair.Key);
        }
        return entries.Values.OrderBy(e=>e.Identity.Kind,StringComparer.Ordinal).ThenBy(e=>e.Identity.Name,StringComparer.Ordinal)
            .ThenBy(e=>e.Identity.Stem,StringComparer.Ordinal).Select(e=>new SpsDiscoveryCandidate(
                SpsBoardModel.Key(e.Identity.Stem),e.Identity.Stem,e.Identity.Name,e.Identity.Kind,
                e.Inputs.Keys.Order(StringComparer.Ordinal).ToArray(),e.Inputs.Keys.Where(a=>SpsSignals.Describe(a) is not null).Order(StringComparer.Ordinal).ToArray(),e.SeenAt)).ToArray();
    }
    public void Reset()=>entries.Clear();
}
