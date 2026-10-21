using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using ShockWave.VrDemo.Shared;

namespace ShockWave.ShaderLab;

internal sealed class VrcDemoLink : IDisposable
{
    private const string Address="/avatar/parameters/";
    private readonly PanelModel model;
    private readonly OscListener receiver;
    private readonly UdpClient sender=new(AddressFamily.InterNetwork);
    private readonly IPEndPoint destination;
    private readonly Dictionary<string,bool> bools=[];
    private readonly Dictionary<string,float> floats=[];
    private readonly Dictionary<string,float> sent=[];
    private readonly List<string> actions=[];
    private readonly VrFeedback feedback=new();
    private double lastBeat=double.NegativeInfinity,lastSend=-1,lastFull=-1,lastSenderBeat=-1;
    private bool visible,hadBeat,previousTouch,blockedTouch,senderBeat;
    public bool IsLive { get; private set; }
    public int ReceivePort => receiver.Port;
    public int SendPort => destination.Port;
    public int Received => receiver.Packets;
    public int Rejected => receiver.Rejected;
    public int SentPackets { get; private set; }
    public string Status => receiver.Fault ?? (IsLive ? "VR OSC · connected" : "VR OSC · waiting for visible panel / heartbeat");
    public VrcDemoLink(PanelModel model,int receivePort=VrPanelContract.DefaultReceivePort,int sendPort=VrPanelContract.DefaultSendPort)
    {
        this.model=model; destination=new(IPAddress.Loopback,sendPort);
        var allowed=VrPanelContract.BoolInputs.Concat(VrPanelContract.FloatInputs).Select(n=>Address+VrPanelContract.Prefix+n).ToHashSet(StringComparer.Ordinal);
        receiver=new(receivePort,m=>m.Address=="/avatar/change" || allowed.Contains(m.Address));
    }
    public void Tick(double now)
    {
        model.AdvanceTo(now);
        receiver.Drain(message=>Apply(message,now));
        var live=visible && hadBeat && now-lastBeat<2 && receiver.Fault is null;
        if(IsLive && !live) Invalidate();
        IsLive=live;
        if(live)
        {
            foreach(var action in actions) Act(action);
            var touch=Bool("InTouch") || Bool("InTestTouch");
            if(!touch) blockedTouch=false;
            if(!blockedTouch)
            {
                if(touch)
                {
                    var x=Float(Bool("InTestTouch") ? "InTestX" : "InX"); var y=Float(Bool("InTestTouch") ? "InTestY" : "InY");
                    if(Bool("InFlipX")) x=1-x; if(Bool("InFlipY")) y=1-y;
                    if(!previousTouch && model.RecordArmed) { feedback.Clear(); ClearRequested=true; }
                    model.DrawAt(new(PanelLayout.Graph.X+x*PanelLayout.Graph.Width,PanelLayout.Graph.Y+(1-y)*PanelLayout.Graph.Height));
                }
                else if(previousTouch) model.Release();
            }
            previousTouch=touch;
        }
        actions.Clear();
        if(now-lastSend>=0.05) { lastSend=now; SendFrame(now); }
    }
    private bool Apply(OscMessage message,double now)
    {
        if(message.Address=="/avatar/change")
        {
            if(message.Arguments.Length!=1 || message.Arguments[0] is not string) throw new FormatException("Avatar change requires an id string");
            Invalidate(); visible=false; hadBeat=false; return true;
        }
        var name=message.Address.Substring((Address+VrPanelContract.Prefix).Length);
        if(message.Arguments.Length!=1) throw new FormatException("VR parameter requires one value");
        if(VrPanelContract.FloatInputs.Contains(name))
        {
            if(message.Arguments[0] is not float value || !float.IsFinite(value) || value<0 || value>1) throw new FormatException("VR coordinate must be finite 0..1");
            floats[name]=value; return true;
        }
        var current=message.Arguments[0] switch { bool b=>b,int i when i is 0 or 1=>i==1,_=>throw new FormatException("VR button requires bool") };
        var previous=Bool(name); bools[name]=current;
        if(name=="Visible") { visible=current; if(!current) { Invalidate(); hadBeat=false; } return true; }
        if(name=="InBeat") { if(!hadBeat || current!=previous) { hadBeat=true; lastBeat=now; } return true; }
        if(name=="InTouch" || name=="InTestTouch") { if(!current && !Bool("InTouch") && !Bool("InTestTouch")) blockedTouch=false; return true; }
        if(current && !previous && name.StartsWith("In") && name is not ("InFlipX" or "InFlipY")) actions.Add(name);
        return true;
    }
    private bool Bool(string name) => bools.GetValueOrDefault(name);
    private float Float(string name) => floats.GetValueOrDefault(name);
    private void Invalidate()
    {
        blockedTouch=Bool("InTouch") || Bool("InTestTouch"); previousTouch=false; actions.Clear();
        model.Release(commitRecording:false); model.StopAll();
        foreach(var row in model.Functions) row.Function.Selected=false;
        feedback.Clear();
    }
    private void Act(string name)
    {
        if(name.StartsWith("InRow") && int.TryParse(name[5..],out var row)) { if(!previousTouch) model.ToggleVisible(row); return; }
        switch(name)
        {
            case "InRecord": model.ToggleRecord(); break;
            case "InPlay": model.PlaySelected(); break;
            case "InHold": model.HoldSelected(); blockedTouch=Bool("InTouch")||Bool("InTestTouch"); break;
            case "InZero": model.ZeroSelected(); blockedTouch=Bool("InTouch")||Bool("InTestTouch"); break;
            case "InStop": model.StopAll(); blockedTouch=Bool("InTouch")||Bool("InTestTouch"); previousTouch=false; break;
            case "InClear": feedback.Clear(); ClearRequested=true; break;
            case "InPrev": if(!previousTouch) model.ChangePage(-1); break;
            case "InNext": if(!previousTouch) model.ChangePage(1); break;
            case "InOwn": model.ChangeRoute(0); break;
            case "InRemote": model.ChangeRoute(1); break;
            case "InBoth": model.ChangeRoute(2); break;
            case "InDemo":
                model.Release(commitRecording:false); model.StopAll(); previousTouch=false; blockedTouch=Bool("InTouch")||Bool("InTestTouch");
                model.ReplaceInventory(PanelModel.ParseInventory(OscListener.DemoJson)); feedback.Clear(); sent.Clear(); break;
        }
    }
    public bool ClearRequested { get; set; }
    private void SendFrame(double now)
    {
        var frame=feedback.Frame(model,IsLive);
        var full=now-lastFull>=1; if(full) lastFull=now;
        List<byte[]> messages=[];
        foreach(var (name,value) in frame)
        {
            if(!full && sent.TryGetValue(name,out var prior) && prior==value) continue;
            sent[name]=value; messages.Add(OscCodec.Encode(Address+VrPanelContract.ReceivePrefix+name,value));
        }
        if(now-lastSenderBeat>=0.5)
        {
            lastSenderBeat=now; senderBeat=!senderBeat; messages.Add(OscCodec.Encode(Address+VrPanelContract.Prefix+"RxBeat",senderBeat));
        }
        // Small immediate bundles. No input-address echo, retries or queued replay.
        foreach(var chunk in messages.Chunk(16))
        {
            var packet=Bundle(chunk); sender.Send(packet,packet.Length,destination); SentPackets++;
        }
    }
    internal static byte[] Bundle(IEnumerable<byte[]> messages)
    {
        using var stream=new MemoryStream(); stream.Write(System.Text.Encoding.ASCII.GetBytes("#bundle\0"));
        stream.Write(new byte[] {0,0,0,0,0,0,0,1});
        Span<byte> length=stackalloc byte[4];
        foreach(var message in messages) { BinaryPrimitives.WriteInt32BigEndian(length,message.Length); stream.Write(length); stream.Write(message); }
        return stream.ToArray();
    }
    public void Dispose() { Invalidate(); receiver.Dispose(); sender.Dispose(); }
}
