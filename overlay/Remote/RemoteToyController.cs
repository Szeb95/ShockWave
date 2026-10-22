using System.Diagnostics;
using ShockWave.Overlay.Toys;

namespace ShockWave.Overlay.Toys;

internal sealed record RemoteToyFunction(string Id, string Name, string Type, int Maximum);
internal sealed record RemoteToyInfo(string Id, string Name, int? Battery, RemoteToyFunction[] Functions);
internal sealed record RemoteShockerInfo(string Id, string Name, string Service, int Maximum, int DurationMs);
internal sealed record RemoteInventory(RemoteToyInfo[] Toys, RemoteShockerInfo[] Shockers);
internal sealed record RemoteLevel(string Device, string Channel, int Percent, long Claim)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; init; }
}

// Session-only proxy: it has no Bluetooth/native transport and never saves activity.
internal sealed class RemoteToyController(Func<bool> testMode) : IToyInputController
{
    private sealed class Channel { public int Percent; public Guid Source; public long Generation, Deadline, Claim; }
    private readonly Dictionary<(string, string), Channel> channels = [];
    private long generation;
    public event Action<string>? Log;
    public int ActiveInputCount => channels.Values.Count(c => c.Source != Guid.Empty);
    public bool TestMode => testMode();
    public ToyAdapterSnapshot Snapshot { get; private set; } = new(ToyAdapterAvailability.Ready, false, "No remote toys", []);
    public void Replace(RemoteToyInfo[] toys)
    {
        var devices = toys.Select(t => new LocalToyDescriptor("remote/" + t.Id, "ShockWave remote", "Remote " + t.Name, "Remote toy",
            ToyConnectionState.Connected, t.Functions.Select(f => new ToyCapability(f.Id, Enum.TryParse<ToyCapabilityType>(f.Type, out var type) ? type : ToyCapabilityType.Unknown,
                f.Name + (f.Maximum < 100 ? $" · max {f.Maximum}%" : ""), 0, 100, 1, null, null)).ToArray(), true, t.Battery, DateTimeOffset.UtcNow, 30, false)).ToArray();
        Snapshot = new(ToyAdapterAvailability.Ready, false, "Remote app connected", devices);
        foreach (var key in channels.Keys.Where(k => !devices.Any(d => d.DeviceKey == k.Item1 && d.Capabilities.Any(c => c.ChannelId == k.Item2))).ToArray()) channels.Remove(key);
    }
    public void Disconnect() { channels.Clear(); Snapshot = Snapshot with { Devices = [], Message = "Remote app disconnected" }; }
    private Channel Get(string device, string function)
    {
        if (!Snapshot.Devices.Any(d => d.DeviceKey == device && d.Capabilities.Any(c => c.ChannelId == function))) throw new InvalidOperationException("Remote toy disconnected");
        if (!channels.TryGetValue((device, function), out var c)) channels[(device, function)] = c = new(); return c;
    }
    public ToyChannelState State(string device, string channel)
    {
        var c = Get(device, channel);
        return new(device, channel, new(device, channel), c.Percent, c.Percent, true, c.Source != Guid.Empty, "Remote request · owner limits apply")
        { InputSource = c.Source, InputGeneration = c.Generation };
    }
    public ToyInputLease BeginInput(string device, string channel, Guid source, int percent, TimeSpan lifetime)
    {
        if (source == Guid.Empty || percent is < 0 or > 100 || lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromSeconds(2)) throw new ArgumentOutOfRangeException();
        var c = Get(device, channel); c.Source = source; c.Generation = ++generation; c.Claim = c.Generation; c.Percent = percent;
        c.Deadline = Stopwatch.GetTimestamp() + (long)(lifetime.TotalSeconds * Stopwatch.Frequency);
        return new(device, channel, source, c.Generation);
    }
    public bool UpdateInput(ToyInputLease lease, int percent, TimeSpan lifetime)
    {
        if (percent is < 0 or > 100 || lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromSeconds(2)) return false;
        if (!channels.TryGetValue((lease.DeviceKey, lease.ChannelId), out var c) || c.Source != lease.Source || c.Generation != lease.Generation || Stopwatch.GetTimestamp() >= c.Deadline) return false;
        c.Percent = percent; c.Deadline = Stopwatch.GetTimestamp() + (long)(lifetime.TotalSeconds * Stopwatch.Frequency); return true;
    }
    public void EndInput(ToyInputLease lease)
    {
        if (channels.TryGetValue((lease.DeviceKey, lease.ChannelId), out var c) && c.Source == lease.Source && c.Generation == lease.Generation) Reset(c);
    }
    private void Reset(Channel c) { c.Percent = 0; c.Source = Guid.Empty; c.Generation = ++generation; }
    public void Stop(string? device = null) { foreach (var pair in channels.Where(p => device is null || p.Key.Item1 == device)) Reset(pair.Value); }
    public RemoteLevel[] Levels()
    {
        var expired = channels.Values.Where(c => c.Source != Guid.Empty && Stopwatch.GetTimestamp() >= c.Deadline).ToArray();
        foreach (var c in expired) Reset(c);
        if (expired.Length > 0) Log?.Invoke($"Local drawing heartbeat expired for {expired.Length} remote functions; output stopped, fresh input required.");
        return TestMode ? [] : channels.Where(p => p.Value.Source != Guid.Empty).Select(p => new RemoteLevel(p.Key.Item1[7..], p.Key.Item2, p.Value.Percent, p.Value.Claim)).ToArray();
    }
    public bool Reject(string device, string channel, long claim)
    {
        if (!channels.TryGetValue(("remote/" + device, channel), out var c) || c.Claim != claim || c.Source == Guid.Empty) return false;
        Reset(c); return true;
    }
}
