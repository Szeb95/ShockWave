using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using ShockWave.ShaderLab;

namespace ShockWave.Overlay.Sps;

internal sealed record SpsQueryFrame(string Avatar, OscMessage[] Messages, double RequestedAt, double ReceivedAt)
{
    public OscMessage[] DiscoveryMessages { get; init; }=[];
}

// Read-only local VRChat snapshots. No OSC sender, server, registration, token or
// actuator API. Only verified VRChat-owned loopback endpoints are discovered.
internal sealed class SpsOscQuery : IDisposable
{
    private readonly HttpClient http;
    private readonly Func<int[]> ports;
    private CancellationTokenSource cancel=new();
    private Task<SpsQueryFrame?>? pending;
    private Uri? endpoint;
    private double nextRead;
    private int expectedPort;
    private bool disposed;
    private int generation;
    public string Status { get; private set; }="Waiting for VRChat";
    public double LastFrameAt { get; private set; }=double.NegativeInfinity;
    public SpsOscQuery(HttpClient? client=null,Func<int[]>? portFinder=null)
    {
        http=client??new(new HttpClientHandler { UseProxy=false,AllowAutoRedirect=false }) { Timeout=TimeSpan.FromMilliseconds(750) };
        ports=portFinder??VrChatPorts;
    }
    public void Tick(bool enabled,int avatarPort,Action<SpsQueryFrame> receive)
    {
        if(disposed) return;
        if(!enabled) { if(pending is not null || endpoint is not null) Reset(); Status="OSC unavailable"; return; }
        if(expectedPort!=avatarPort) { Reset(); expectedPort=avatarPort; }
        var now=ToyOscRouter.Clock;
        if(pending is { IsCompleted:true })
        {
            var frame=pending.GetAwaiter().GetResult(); pending=null;
            if(frame is not null && now-frame.RequestedAt<=.25)
            { LastFrameAt=frame.RequestedAt; Status="Live VRChat contacts"; receive(frame); nextRead=now+.1; }
            else { Status="Waiting for VRChat contacts"; nextRead=now+1; }
        }
        if(pending is null && now>=nextRead) { var token=cancel.Token; pending=Task.Run(()=>ReadOnceAsync(avatarPort,token)); }
    }
    internal async Task<SpsQueryFrame?> ReadOnceAsync(int avatarPort,CancellationToken token=default)
    {
        if(disposed) return null;
        var epoch=Volatile.Read(ref generation);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(750); token=deadline.Token;
        try
        {
            token.ThrowIfCancellationRequested(); var selected=endpoint;
            // Revalidate process ownership every read; never follow redirects
            // or query another application's newly reused port.
            var candidates=ports().Where(p=>p is >0 and <=65535).Distinct().Take(16).ToArray();
            if(selected is not null && !candidates.Contains(selected.Port)) selected=null;
            if(selected is null)
                foreach(var port in candidates)
                {
                    var candidate=new Uri($"http://127.0.0.1:{port}/");
                    try
                    {
                        using var host=await Get(new(candidate,"?HOST_INFO"),32*1024,token).ConfigureAwait(false);
                        if(ValidHost(host.RootElement,avatarPort)) { selected=candidate; break; }
                    }
                    catch(Exception e) when(e is HttpRequestException or IOException or JsonException or InvalidDataException or OperationCanceledException)
                    { if(token.IsCancellationRequested) return null; }
                }
            if(selected is null || epoch!=Volatile.Read(ref generation)) return null;
            var requested=ToyOscRouter.Clock;
            using var document=await Get(new(selected,"avatar"),2*1024*1024,token).ConfigureAwait(false);
            var frame=Parse(document.RootElement,requested,ToyOscRouter.Clock);
            if(epoch!=Volatile.Read(ref generation) || token.IsCancellationRequested) return null;
            endpoint=selected; return frame;
        }
        catch(Exception e) when(e is HttpRequestException or IOException or JsonException or InvalidDataException or OperationCanceledException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { if(epoch==Volatile.Read(ref generation)) endpoint=null; return null; }
    }
    private async Task<JsonDocument> Get(Uri uri,int limit,CancellationToken token)
    {
        using var request=new HttpRequestMessage(HttpMethod.Get,uri);
        request.Headers.CacheControl=new() { NoCache=true,NoStore=true };
        using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if(response.Content.Headers.ContentLength>limit) throw new InvalidDataException("OSCQuery response too large.");
        await using var stream=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var content=new MemoryStream(); var buffer=new byte[8192];
        while(true)
        {
            var count=await stream.ReadAsync(buffer,token).ConfigureAwait(false); if(count==0) break;
            if(content.Length+count>limit) throw new InvalidDataException("OSCQuery response too large.");
            content.Write(buffer,0,count);
        }
        return JsonDocument.Parse(content.ToArray(),new() { MaxDepth=24 });
    }
    internal static bool ValidHost(JsonElement host,int avatarPort) => host.ValueKind==JsonValueKind.Object
        && host.TryGetProperty("NAME",out var name) && name.ValueKind==JsonValueKind.String && name.GetString()!.StartsWith("VRChat-",StringComparison.Ordinal)
        && host.TryGetProperty("OSC_IP",out var ip) && ip.ValueKind==JsonValueKind.String && IPAddress.TryParse(ip.GetString(),out var address) && IPAddress.IsLoopback(address)
        && host.TryGetProperty("OSC_PORT",out var port) && port.TryGetInt32(out var number) && number==avatarPort
        && host.TryGetProperty("OSC_TRANSPORT",out var transport) && transport.ValueKind==JsonValueKind.String && transport.GetString()=="UDP";
    internal static SpsQueryFrame Parse(JsonElement avatar,double requested,double received)
    {
        if(!avatar.TryGetProperty("CONTENTS",out var contents) || contents.ValueKind!=JsonValueKind.Object
            || !contents.TryGetProperty("change",out var change) || !change.TryGetProperty("VALUE",out var identity)
            || identity.ValueKind!=JsonValueKind.Array || identity.GetArrayLength()!=1 || identity[0].ValueKind!=JsonValueKind.String
            || !contents.TryGetProperty("parameters",out var parameters)) throw new InvalidDataException("Missing avatar snapshot.");
        var id=identity[0].GetString()!;
        if(id.Length is 0 or >256 || id.Any(char.IsControl)) throw new InvalidDataException("Invalid avatar identity.");
        var values=new Dictionary<string,OscMessage>(StringComparer.Ordinal);
        var discovered=new Dictionary<string,OscMessage>(StringComparer.Ordinal); var nodes=0;
        void Walk(JsonElement node,int depth)
        {
            if(++nodes>8192 || depth>16 || node.ValueKind!=JsonValueKind.Object) throw new InvalidDataException("OSCQuery tree too large.");
            if(node.TryGetProperty("FULL_PATH",out var path) && path.ValueKind==JsonValueKind.String && SpsSignals.Describe(path.GetString()!) is { } signal)
            {
                if(!node.TryGetProperty("ACCESS",out var access) || !access.TryGetInt32(out var flags) || (flags&1)==0) return;
                if(!node.TryGetProperty("VALUE",out var data) || data.ValueKind!=JsonValueKind.Array || data.GetArrayLength()!=1
                    || !node.TryGetProperty("TYPE",out var type) || type.ValueKind!=JsonValueKind.String) throw new InvalidDataException("Invalid contact snapshot.");
                object value=type.GetString() switch
                {
                    "f" when data[0].ValueKind==JsonValueKind.Number && data[0].TryGetSingle(out var f) && float.IsFinite(f) && f is >=0 and <=1 => f,
                    "i" when data[0].ValueKind==JsonValueKind.Number && data[0].TryGetInt32(out var i) && i is >=0 and <=1 => i,
                    "T" or "F" when data[0].ValueKind is JsonValueKind.True or JsonValueKind.False => data[0].GetBoolean(),
                    _=>throw new InvalidDataException("Invalid contact value.")
                };
                if(!values.TryAdd(signal.Address,new(signal.Address,[value])) || values.Count>256) throw new InvalidDataException("Duplicate or excessive contacts.");
            }
            else if(node.TryGetProperty("FULL_PATH",out var candidatePath) && candidatePath.ValueKind==JsonValueKind.String
                && SpsDiscovery.Describe(candidatePath.GetString()!) is not null
                && node.TryGetProperty("ACCESS",out var candidateAccess) && candidateAccess.TryGetInt32(out var candidateFlags) && (candidateFlags&1)!=0
                && node.TryGetProperty("VALUE",out var candidateData) && candidateData.ValueKind==JsonValueKind.Array && candidateData.GetArrayLength()==1
                && node.TryGetProperty("TYPE",out var candidateType) && candidateType.ValueKind==JsonValueKind.String)
            {
                object? candidateValue=candidateType.GetString() switch
                {
                    "f" when candidateData[0].ValueKind==JsonValueKind.Number && candidateData[0].TryGetSingle(out var f) && float.IsFinite(f) && Math.Abs(f)<=1000=>f,
                    "i" when candidateData[0].ValueKind==JsonValueKind.Number && candidateData[0].TryGetInt32(out var i) && Math.Abs((long)i)<=1000=>i,
                    "T" or "F" when candidateData[0].ValueKind is JsonValueKind.True or JsonValueKind.False=>candidateData[0].GetBoolean(), _=>null
                };
                if(candidateValue is not null && discovered.Count<256) discovered.TryAdd(candidatePath.GetString()!,new(candidatePath.GetString()!,[candidateValue]));
            }
            if(node.TryGetProperty("CONTENTS",out var children))
            {
                if(children.ValueKind!=JsonValueKind.Object) throw new InvalidDataException("Invalid contact tree.");
                foreach(var child in children.EnumerateObject()) Walk(child.Value,depth+1);
            }
        }
        Walk(parameters,0); return new(id,values.Values.ToArray(),requested,received) { DiscoveryMessages=discovered.Values.ToArray() };
    }
    public void Reset()
    {
        if(disposed) return;
        Interlocked.Increment(ref generation); cancel.Cancel(); cancel.Dispose(); cancel=new(); pending=null; endpoint=null; nextRead=0; LastFrameAt=double.NegativeInfinity;
    }
    public void Dispose()
    {
        if(disposed) return; disposed=true; Interlocked.Increment(ref generation);
        pending=null; endpoint=null; LastFrameAt=double.NegativeInfinity;
        cancel.Cancel(); cancel.Dispose(); http.Dispose();
    }

    [DllImport("iphlpapi.dll")]
    private static extern uint GetExtendedTcpTable(IntPtr table,ref int size,bool ordered,int family,int tableClass,uint reserved);
    internal static int[] VrChatPorts()
    {
        var pids=new HashSet<int>();
        foreach(var process in Process.GetProcessesByName("VRChat")) { using(process) pids.Add(process.Id); }
        if(pids.Count==0) return [];
        var size=0; _=GetExtendedTcpTable(IntPtr.Zero,ref size,false,2,3,0);
        if(size is <4 or >1024*1024) return [];
        var memory=Marshal.AllocHGlobal(size);
        try
        {
            if(GetExtendedTcpTable(memory,ref size,false,2,3,0)!=0) return [];
            var count=Marshal.ReadInt32(memory); if(count<0 || count>(size-4)/24) return [];
            var found=new List<int>();
            for(var n=0;n<count;n++)
            {
                var row=IntPtr.Add(memory,4+n*24);
                if(!pids.Contains(Marshal.ReadInt32(row,20)) || Marshal.ReadInt32(row,4)!=0x0100007f) continue;
                var port=Marshal.ReadInt32(row,8); found.Add(((port&255)<<8)|((port>>8)&255));
            }
            return found.Distinct().ToArray();
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
}
