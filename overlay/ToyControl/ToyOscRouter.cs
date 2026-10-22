using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ShockWave.ShaderLab;
using ShockWave.ToyControl.Shared;

namespace ShockWave.Overlay;

// One local input socket for toys and PiCtrl. No inventory network API, private
// device IDs, or controller-output addresses are accepted as toy input.
internal sealed class ToyOscRouter : IDisposable
{
    private sealed record Packet(byte[] Bytes, OscMessage[] Messages, long Received, bool Local);
    private readonly ConcurrentQueue<Packet> pending = new();
    private readonly UdpClient receiver, sender = new(AddressFamily.InterNetwork);
    private readonly CancellationTokenSource cancel = new();
    private readonly Task worker;
    private readonly IPEndPoint destination;
    private readonly VrFeedback feedback = new();
    private readonly Dictionary<string, float> sent = [];
    private double lastSend = -1, lastFull = -1, lastBeat = -1;
    private bool beat, disposed;
    private bool spsPresence;
    private double lastSpsPresence = -1;
    private int clearRevision=-1;
    public string? Fault { get; private set; }
    public int ListenPort { get; }
    public int Received { get; private set; }
    public int Rejected { get; private set; }
    public int Dropped { get; private set; }
    public static double Clock => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
    public ToyOscRouter(int listenPort, int sendPort, string listenHost="127.0.0.1")
    {
        destination = new(IPAddress.Loopback, sendPort);
        receiver = new(AddressFamily.InterNetwork);
        try { var address=IPAddress.TryParse(listenHost,out var parsed) ? parsed : Dns.GetHostAddresses(listenHost).First(a=>a.AddressFamily==AddressFamily.InterNetwork); receiver.Client.ExclusiveAddressUse = true; receiver.Client.Bind(new IPEndPoint(address, listenPort)); }
        catch { receiver.Dispose(); sender.Dispose(); cancel.Dispose(); throw; }
        ListenPort = ((IPEndPoint)receiver.Client.LocalEndPoint!).Port;
        worker = Task.Run(Receive);
    }
    private async Task Receive()
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var packet = await receiver.ReceiveAsync(cancel.Token);
                try
                {
                    var decoded = OscCodec.Decode(packet.Buffer).ToArray();
                    if (pending.Count >= 128) { Dropped++; continue; }
                    pending.Enqueue(new(packet.Buffer, decoded, Stopwatch.GetTimestamp(),IPAddress.IsLoopback(packet.RemoteEndPoint.Address)));
                }
                catch (Exception e) when (e is FormatException or DecoderFallbackException) { Rejected++; }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException) { if (!cancel.IsCancellationRequested) Fault = "OSC listener unavailable"; }
    }
    public void Tick(ToyControlSession session, Action<byte[], OscMessage[]> forward, Action<OscMessage, double>? localInput = null)
    {
        var now = Clock;
        if (Fault is not null) { session.Vrc.Reset(); return; }
        for (var n = 0; n < 128 && pending.TryDequeue(out var packet); n++)
        {
            if (Stopwatch.GetElapsedTime(packet.Received).TotalSeconds > .25) { Dropped++; continue; }
            Received++;
            if(packet.Local) foreach (var message in packet.Messages) { session.Vrc.Apply(message, now); localInput?.Invoke(message, packet.Received / (double)Stopwatch.Frequency); }
            forward(packet.Bytes, packet.Messages);
        }
    }
    public void Send(ToyControlSession session)
    {
        var now = Clock;
        if (disposed || Fault is not null) return;
        if(now-lastSend<.05) { SendPointerCursor(session); return; }
        lastSend = now;
        // Send freshness before the larger display update. Keep the two-second
        // expiry unchanged, but give dropped display batches more pulse margin.
        try
        {
            if(now-lastBeat>=.25)
            {
                beat=!beat; lastBeat=now;
                var pulse=OscCodec.Encode("/avatar/parameters/"+ToyControlContract.Prefix+"RxBeat",beat);
                sender.Send(pulse,pulse.Length,destination);
            }
        }
        catch(SocketException) { Fault="OSC feedback unavailable"; session.Vrc.Reset(); return; }
        SendPointerCursor(session);
        if(clearRevision!=session.ClearRevision) { feedback.Clear(); clearRevision=session.ClearRevision; }
        var frame = feedback.Frame(session.DisplayModel, session.Vrc.IsLive);
        frame["ShockerPage"]=session.ShockerPage ? 1 : 0;
        frame["ShockIntensity"]=session.Shockers.Intensity/100f;
        frame["ShockDuration"]=(session.Shockers.DurationMs-300)/14700f;
        frame["ShockArmed"]=session.Shockers.Armed ? 1 : 0;
        frame["ShockRemaining"]=session.Shockers.Remaining(now);
        frame["ShockReady"]=session.Shockers.Ready ? 1 : 0;
        frame["Online"] = session.Vrc.IsLive ? session.TestMode ? .5f : 1 : 0;
        frame["Pointer"] = session.PointerMode ? 1 : 0;
        for(var hand=0;hand<2;hand++)
        {
            var p=session.Vrc.PointerPoints[hand]; var prefix="Pointer"+(hand==0 ? "L" : "R");
            frame[prefix+"X"]=Math.Clamp(p.X/1240,0,1); frame[prefix+"Y"]=Math.Clamp(1-(p.Y-100)/608,0,1);
            frame[prefix+"Valid"]=session.PointerMode && session.Vrc.IsLive && session.Vrc.PointerValid[hand] ? 1 : 0;
        }
        var full = now - lastFull >= 1; if (full) lastFull = now;
        List<byte[]> messages = [];
        foreach (var (name, value) in frame)
        {
            if (!full && sent.TryGetValue(name, out var before) && before == value) continue;
            sent[name] = value; messages.Add(OscCodec.Encode("/avatar/parameters/" + ToyControlContract.ReceivePrefix + name, value));
        }
        try { foreach (var chunk in messages.Chunk(16)) { var packet = Bundle(chunk); sender.Send(packet, packet.Length, destination); } }
        catch (SocketException) { Fault = "OSC feedback unavailable"; session.Vrc.Reset(); }
    }
    private void SendPointerCursor(ToyControlSession session)
    {
        // Cursor updates bypass the large graph-frame throttle (up to the UI's
        // 30 Hz). Selection and the drawn cursor use the same projected point.
        List<byte[]> packets=[];
        for(var hand=0;hand<2;hand++)
        {
            var p=session.Vrc.PointerPoints[hand]; var prefix="Pointer"+(hand==0 ? "L" : "R");
            Put(prefix+"X",Math.Clamp(p.X/1240,0,1)); Put(prefix+"Y",Math.Clamp(1-(p.Y-100)/608,0,1));
            Put(prefix+"Valid",session.PointerMode && session.Vrc.IsLive && session.Vrc.PointerValid[hand] ? 1 : 0);
        }
        if(packets.Count==0) return;
        try {var packet=Bundle(packets);sender.Send(packet,packet.Length,destination);}
        catch(SocketException) {Fault="Pointer feedback unavailable";session.Vrc.Reset();}
        void Put(string name,float value)
        {
            if(sent.TryGetValue(name,out var old) && old==value) return;
            sent[name]=value;packets.Add(OscCodec.Encode("/avatar/parameters/"+ToyControlContract.ReceivePrefix+name,value));
        }
    }
    internal static byte[] Bundle(IEnumerable<byte[]> messages)
    {
        using var stream = new MemoryStream(); stream.Write(Encoding.ASCII.GetBytes("#bundle\0")); stream.Write(new byte[] { 0,0,0,0,0,0,0,1 });
        Span<byte> length = stackalloc byte[4];
        foreach (var message in messages) { BinaryPrimitives.WriteInt32BigEndian(length, message.Length); stream.Write(length); stream.Write(message); }
        return stream.ToArray();
    }
    public void SendSpsPresence(bool enabled)
    {
        if (disposed || Fault is not null || enabled == spsPresence && (!enabled || Clock - lastSpsPresence < 1)) return;
        try
        {
            var packet = OscCodec.Encode("/avatar/parameters/OGB_ENABLED", enabled);
            sender.Send(packet, packet.Length, destination); spsPresence = enabled; lastSpsPresence = Clock;
        }
        catch (SocketException) { Fault = "SPS avatar signal link unavailable"; }
    }
    public void Dispose()
    {
        if (disposed) return;
        SendSpsPresence(false);
        disposed = true; cancel.Cancel(); receiver.Dispose(); sender.Dispose();
        // ReceiveAsync is cancelled; do not block the UI on a native/network wait.
        _ = worker.ContinueWith(_ => cancel.Dispose(), TaskScheduler.Default);
    }
}
