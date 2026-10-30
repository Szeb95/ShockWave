using System.Buffers.Binary;
using System.Text;

// Production codec only. The standalone lab listener stays in the lab.
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

