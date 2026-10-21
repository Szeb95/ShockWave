using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace ShockWave.ShaderLab;

internal sealed record OscMessage(string Address, object[] Arguments);

internal static class OscCodec
{
    private static readonly UTF8Encoding Utf8 = new(false, true);
    public static List<OscMessage> Decode(byte[] packet)
    {
        if (packet.Length is 0 or > 65507) throw new FormatException("Invalid OSC packet size");
        List<OscMessage> messages = [];
        Parse(0, packet.Length, 0);
        return messages;
        void Parse(int start, int end, int depth)
        {
            if (depth > 8 || messages.Count >= 256) throw new FormatException("OSC bundle limit");
            var offset = start;
            var address = ReadString(ref offset, end);
            if (address == "#bundle")
            {
                if (offset + 8 > end) throw new FormatException("Truncated timetag");
                var timetag = BinaryPrimitives.ReadUInt64BigEndian(packet.AsSpan(offset, 8)); offset += 8;
                if (timetag > 1) throw new FormatException("Scheduled bundles are unsupported; use immediate");
                while (offset < end)
                {
                    if (offset + 4 > end) throw new FormatException("Truncated bundle length");
                    var size = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(offset, 4)); offset += 4;
                    if (size <= 0 || size > end - offset) throw new FormatException("Invalid bundle element");
                    Parse(offset, offset + size, depth + 1); offset += size;
                }
                return;
            }
            if (!address.StartsWith('/') || address.Length > 160) throw new FormatException("Invalid OSC address");
            var tags = ReadString(ref offset, end);
            if (!tags.StartsWith(',') || tags.Length > 33) throw new FormatException("Invalid OSC type tags");
            List<object> args = [];
            foreach (var tag in tags.AsSpan(1))
            {
                switch (tag)
                {
                    case 's': args.Add(ReadString(ref offset, end)); break;
                    case 'T': args.Add(true); break;
                    case 'F': args.Add(false); break;
                    case 'i': case 'f':
                        if (offset + 4 > end) throw new FormatException("Truncated OSC argument");
                        var raw = BinaryPrimitives.ReadInt32BigEndian(packet.AsSpan(offset, 4)); offset += 4;
                        if (tag == 'i') args.Add(raw); else args.Add(BitConverter.Int32BitsToSingle(raw));
                        break;
                    default: throw new FormatException("Unsupported OSC argument");
                }
            }
            if (offset != end) throw new FormatException("Trailing OSC bytes");
            messages.Add(new(address, args.ToArray()));
        }
        string ReadString(ref int offset, int end)
        {
            var zero = Array.IndexOf(packet, (byte)0, offset, end - offset);
            if (zero < 0) throw new FormatException("Unterminated OSC string");
            var next = offset + ((zero - offset + 4) & ~3);
            if (next > end || packet.AsSpan(zero, next - zero).IndexOfAnyExcept((byte)0) >= 0) throw new FormatException("Invalid OSC padding");
            var result = Utf8.GetString(packet, offset, zero - offset); offset = next; return result;
        }
    }
    public static byte[] Encode(string address, params object[] arguments)
    {
        using var stream = new MemoryStream();
        WriteString(address);
        WriteString("," + string.Concat(arguments.Select(a => a switch { string => "s", int => "i", float => "f", bool b => b ? "T" : "F", _ => throw new ArgumentException("Unsupported type") })));
        Span<byte> word = stackalloc byte[4];
        foreach (var a in arguments)
        {
            if (a is string s) WriteString(s);
            else if (a is int or float)
            {
                BinaryPrimitives.WriteInt32BigEndian(word, a is int i ? i : BitConverter.SingleToInt32Bits((float)a)); stream.Write(word);
            }
        }
        return stream.ToArray();
        void WriteString(string s)
        {
            var bytes = Utf8.GetBytes(s); stream.Write(bytes); stream.WriteByte(0);
            while (stream.Length % 4 != 0) stream.WriteByte(0);
        }
    }
}

internal sealed class OscListener : IDisposable
{
    private readonly UdpClient udp;
    private readonly CancellationTokenSource cancel = new();
    private readonly ConcurrentQueue<OscMessage[]> pending = new();
    private readonly Task receiver;
    private readonly Func<OscMessage,bool>? accept;
    private int packets, rejected, dropped;
    public int Port { get; }
    public int Packets => Volatile.Read(ref packets);
    public int Rejected => Volatile.Read(ref rejected);
    public int Dropped => Volatile.Read(ref dropped);
    public string? Fault { get; private set; }
    public OscListener(int port,Func<OscMessage,bool>? accept=null)
    {
        this.accept=accept;
        udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.ExclusiveAddressUse = true;
        udp.Client.Bind(new IPEndPoint(IPAddress.Loopback, port));
        Port = ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        receiver = Task.Run(Receive);
    }
    private async Task Receive()
    {
        try
        {
            while (!cancel.IsCancellationRequested)
            {
                var packet = await udp.ReceiveAsync(cancel.Token);
                Interlocked.Increment(ref packets);
                try
                {
                    var messages = OscCodec.Decode(packet.Buffer).Where(m=>accept is null || accept(m)).ToArray();
                    if(messages.Length==0) continue;
                    if (pending.Count >= 128) { Interlocked.Increment(ref dropped); continue; }
                    pending.Enqueue(messages);
                }
                catch (Exception e) when (e is FormatException or DecoderFallbackException) { Interlocked.Increment(ref rejected); }
            }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (SocketException e) { if (!cancel.IsCancellationRequested) Fault = "OSC socket failed: " + e.SocketErrorCode; }
    }
    public int Drain(PanelModel model)
    {
        return Drain(message => { if (!Apply(model,message)) return false; model.Source="OSC inventory"; return true; });
    }
    public int Drain(Func<OscMessage,bool> apply)
    {
        var accepted = 0;
        for (var n = 0; n < 128 && pending.TryDequeue(out var messages); n++)
            foreach (var message in messages)
            {
                try
                {
                    if (apply(message)) accepted++;
                }
                catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException or KeyNotFoundException or OverflowException) { Interlocked.Increment(ref rejected); }
            }
        return accepted;
    }
    public static bool Apply(PanelModel model, OscMessage message)
    {
        var a = message.Arguments;
        switch (message.Address)
        {
            case "/szebs/shader/inventory":
                if (a.Length != 1 || a[0] is not string json) throw new FormatException("Inventory requires a JSON string");
                model.ReplaceInventory(PanelModel.ParseInventory(json)); return true;
            case "/szebs/shader/toy":
                if (a.Length is not (4 or 5) || a[0] is not string id || a[1] is not string name || a[3] is not int battery) throw new FormatException("Toy requires id, name, connected, battery[, level]");
                var connected = a[2] switch { bool b => b, int i when i is 0 or 1 => i == 1, _ => throw new FormatException("Invalid connection flag") };
                float? level = a.Length == 5 ? a[4] switch { float f => f, int i when i is 0 or 1 => i, _ => throw new FormatException("Level requires 0..1") } : null;
                model.Upsert(PanelModel.Validate(id, name, connected, battery, level)); return true;
            case "/szebs/shader/remove":
                if (a.Length != 1 || a[0] is not string alias) throw new FormatException("Remove requires a toy alias");
                model.Remove(alias); return true;
            default: return false;
        }
    }
    public void SendDemo()
    {
        using var sender = new UdpClient();
        var bytes = OscCodec.Encode("/szebs/shader/inventory", DemoJson);
        sender.Send(bytes, bytes.Length, new IPEndPoint(IPAddress.Loopback, Port));
    }
    public const string DemoJson = """
        [{"id":"demo-lush","name":"Lush family","battery":82,"functions":[{"id":"v","name":"Vibration"}]},
         {"id":"demo-nora","name":"Nora","battery":67,"functions":[{"id":"v","name":"Vibration"},{"id":"r","name":"Rotation"}]},
         {"id":"demo-domi","name":"Domi","battery":94,"functions":[{"id":"v","name":"Vibration"}]},
         {"id":"demo-edge","name":"Edge","battery":56,"functions":[{"id":"v1","name":"Vibration 1"},{"id":"v2","name":"Vibration 2"}]},
         {"id":"demo-custom","name":"My custom toy ✨","battery":-1,"functions":[{"id":"custom","name":"Custom function"}]},
         {"id":"demo-future","name":"Future model","battery":73,"functions":[{"id":"future","name":"Future function"}]},
         {"id":"demo-second","name":"Lush family #2","battery":91,"functions":[{"id":"v","name":"Vibration"}]},
         {"id":"demo-remote","name":"Remote toy (demo)","battery":45,"functions":[{"id":"v","name":"Vibration (preview)"}]}]
        """;
    public void Dispose()
    {
        cancel.Cancel(); udp.Dispose();
        try { receiver.GetAwaiter().GetResult(); } catch (OperationCanceledException) { }
        cancel.Dispose();
    }
}
