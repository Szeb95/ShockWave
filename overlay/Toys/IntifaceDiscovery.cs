using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace ShockWave.Overlay.Toys;

internal sealed record IntifaceServer(string Name, string Host, Uri Address)
{
    public override string ToString() => Name + " · " + Address.Authority;
}

// Bounded DNS-SD browse of Intiface's official service. No subnet/port scanning.
internal static class IntifaceDiscovery
{
    internal const string Service = "_intiface_engine._tcp.local";
    internal sealed record Record(string Name, ushort Type, uint Ttl, string Target, int Port, IPAddress? Address);
    public static async Task<IReadOnlyList<IntifaceServer>> FindAsync(CancellationToken token, TimeSpan? duration = null)
    {
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(duration ?? TimeSpan.FromSeconds(3));
        var records = new List<Record>(); var sync = new object();
        var addresses = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback && nic.SupportsMulticast)
            .SelectMany(nic => nic.GetIPProperties().UnicastAddresses)
            .Where(value => value.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(value => value.Address).Distinct().Take(16).ToArray();
        await Task.WhenAll(addresses.Select(async local =>
        {
            try
            {
                using var client = new UdpClient(AddressFamily.InterNetwork);
                client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                try { client.Client.Bind(new IPEndPoint(IPAddress.Any, 5353)); }
                catch (SocketException) { client.Client.Bind(new IPEndPoint(local, 0)); }
                client.JoinMulticastGroup(IPAddress.Parse("224.0.0.251"), local);
                client.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                client.Ttl = 255;
                var destination = new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353);
                await client.SendAsync(Query(Service, 12), destination, bounded.Token);
                var queried = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { Service };
                for (var packets = 0; packets < 256; packets++)
                {
                    var packet = await client.ReceiveAsync(bounded.Token);
                    var found = ReadRecords(packet.Buffer);
                    lock (sync)
                    {
                        if (records.Count + found.Count <= 4096) records.AddRange(found);
                    }
                    // Responders may split PTR, SRV and address records into separate replies.
                    foreach (var record in found)
                    {
                        var target = record.Type == 12 && record.Name.Equals(Service, StringComparison.OrdinalIgnoreCase) ? record.Target :
                            record.Type == 33 && record.Name.EndsWith("." + Service, StringComparison.OrdinalIgnoreCase) ? record.Target : null;
                        if (target is null || !target.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || queried.Count >= 64 || !queried.Add(target)) continue;
                        await client.SendAsync(Query(target, record.Type == 12 ? (ushort)33 : (ushort)1), destination, bounded.Token);
                    }
                }
            }
            catch (Exception error) when (error is SocketException or OperationCanceledException or IOException or ArgumentException) { }
        }));
        token.ThrowIfCancellationRequested();
        lock (sync) return Resolve(records);
    }
    internal static IReadOnlyList<IntifaceServer> Resolve(IEnumerable<Record> input)
    {
        var records = input.ToArray(); var result = new List<IntifaceServer>();
        foreach (var ptr in records.Where(value => value.Type == 12 && value.Ttl > 0 && value.Name.Equals(Service, StringComparison.OrdinalIgnoreCase)))
        {
            if (!ptr.Target.EndsWith("." + Service, StringComparison.OrdinalIgnoreCase)) continue;
            var srv = records.LastOrDefault(value => value.Type == 33 && value.Name.Equals(ptr.Target, StringComparison.OrdinalIgnoreCase));
            if (srv is null || srv.Ttl == 0 || srv.Port is < 1 or > 65535 || !srv.Target.EndsWith(".local", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var address in records.Where(value => value.Type == 1 && value.Ttl > 0 && value.Name.Equals(srv.Target, StringComparison.OrdinalIgnoreCase)))
            {
                if (address.Address is not { } ip || IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.GetAddressBytes()[0] >= 224) continue;
                var name = ptr.Target[..^(Service.Length + 1)];
                if (name.Any(char.IsControl)) continue;
                result.Add(new(name, srv.Target, new UriBuilder("ws", ip.ToString(), srv.Port).Uri));
            }
        }
        return result.DistinctBy(value => value.Address.AbsoluteUri).OrderBy(value => value.Name, StringComparer.OrdinalIgnoreCase).Take(32).ToArray();
    }
    internal static byte[] Query(string name, ushort type)
    {
        using var data = new MemoryStream();
        data.Write(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 });
        foreach (var label in name.Split('.'))
        {
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length is 0 or > 63) throw new ArgumentException("Invalid service name.");
            data.WriteByte((byte)bytes.Length); data.Write(bytes);
        }
        data.Write(new byte[] { 0, (byte)(type >> 8), (byte)type, 0x80, 1 });
        return data.ToArray();
    }
    internal static IReadOnlyList<Record> ReadRecords(byte[] data)
    {
        var result = new List<Record>();
        try
        {
            if (data.Length is < 12 or > 9000 || (data[2] & 0x80) == 0) return result;
            var position = 12; var questions = Word(data, 4);
            var count = Word(data, 6) + Word(data, 8) + Word(data, 10);
            if (questions > 64 || count > 256) return result;
            for (var i = 0; i < questions; i++) { _ = Name(data, ref position); position += 4; }
            for (var i = 0; i < count; i++)
            {
                var name = Name(data, ref position); var type = Word(data, position);
                var cls = Word(data, position + 2) & 0x7fff;
                var ttl = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position + 4, 4));
                var length = Word(data, position + 8); position += 10; var end = checked(position + length);
                if (end > data.Length) throw new InvalidDataException();
                if (cls == 1)
                {
                    if (type == 12) { var target = Name(data, ref position); if (position > end) throw new InvalidDataException(); result.Add(new(name, type, ttl, target, 0, null)); }
                    else if (type == 33 && length >= 7) { var port = Word(data, position + 4); position += 6; var target = Name(data, ref position); if (position > end) throw new InvalidDataException(); result.Add(new(name, type, ttl, target, port, null)); }
                    else if (type == 1 && length == 4) result.Add(new(name, type, ttl, "", 0, new IPAddress(data.AsSpan(position, 4))));
                }
                position = end;
            }
        }
        catch (Exception error) when (error is InvalidDataException or ArgumentException or IndexOutOfRangeException or OverflowException) { result.Clear(); }
        return result;
    }
    private static ushort Word(byte[] data, int position) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(position, 2));
    private static string Name(byte[] data, ref int position)
    {
        var labels = new List<string>(); var cursor = position; var jumped = false; var length = 0;
        for (var steps = 0; steps < 128; steps++)
        {
            var size = data[cursor++];
            if ((size & 0xc0) == 0xc0)
            {
                var target = ((size & 0x3f) << 8) | data[cursor++];
                if (!jumped) position = cursor;
                jumped = true; cursor = target; continue;
            }
            if (size == 0) { if (!jumped) position = cursor; return string.Join('.', labels); }
            if (size > 63 || (length += size + 1) > 255) throw new InvalidDataException();
            labels.Add(Encoding.UTF8.GetString(data, cursor, size)); cursor += size;
        }
        throw new InvalidDataException();
    }
}
