using System.Text.RegularExpressions;
using ShockWave.ShaderLab;

namespace ShockWave.Overlay.Sps;

// Display-only VRCFury menu switches. They never enter SpsSession or renew output.
internal sealed class SpsActivation
{
    private readonly Dictionary<string,bool> values=new(StringComparer.Ordinal);
    private double received=double.NegativeInfinity;
    internal static IReadOnlyDictionary<string,bool> Resolve(IEnumerable<OscMessage> contacts,IReadOnlyList<OscMessage> toggles)
    {
        var result=new Dictionary<string,bool>(StringComparer.Ordinal);
        var sockets=contacts.Select(m=>SpsSignals.Describe(m.Address)).Where(s=>s is { Kind:"Socket" }).Cast<SpsSignal>()
            .Select(s=>(s.Zone,Stem:SpsBoardModel.Stem(s.Address))).Distinct().GroupBy(s=>s.Zone);
        foreach(var socket in sockets.Take(64))
        {
            if(socket.Count()!=1) continue;
            var matches=toggles.Where(m=>m.Arguments is [bool] && SpsSignals.SafeAddress(m.Address)
                && Regex.IsMatch(m.Address,@"^/avatar/parameters/VF\d+_",RegexOptions.CultureInvariant)
                && Regex.Replace(m.Address,@"^/avatar/parameters/VF\d+_","",RegexOptions.CultureInvariant).Replace('/','_')==socket.Key).ToArray();
            if(matches.Length==1) result[socket.Single().Stem]=(bool)matches[0].Arguments[0];
        }
        return result;
    }
    internal void Apply(IReadOnlyDictionary<string,bool> states,double at)
    {
        if(!double.IsFinite(at) || at<received) return;
        values.Clear(); received=at;
        foreach(var pair in states.Take(64))
            if(SpsSignals.Describe(pair.Key+"/PenSelfNewTip") is { Kind:"Socket" }) values[pair.Key]=pair.Value;
    }
    internal bool? Get(string stem,double now) => now>=received && now-received<=2 && values.TryGetValue(stem,out var enabled)?enabled:null;
    internal void Reset() { values.Clear(); received=double.NegativeInfinity; }
}
