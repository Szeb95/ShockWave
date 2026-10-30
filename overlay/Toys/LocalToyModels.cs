using System.Security.Cryptography;
using System.Text;

namespace ShockWave.Overlay.Toys;

internal enum ToyConnectionState { Discovered, Connecting, Connected, Disconnecting, Disconnected, Unavailable }
internal enum ToyAdapterAvailability { Ready, BluetoothOff, BluetoothUnavailable, LibraryMissing, Error, Disposed }
internal enum ToyCapabilityType { Vibration, Rotation, Pump, Thrusting, Fingering, Suction, Depth, Position, Oscillation, Unknown }

internal interface IToyInputController
{
    bool TestMode { get; }
    ToyChannelState State(string device, string channel);
    ToyInputLease BeginInput(string device, string channel, Guid source, int percent, TimeSpan lifetime);
    bool UpdateInput(ToyInputLease lease, int percent, TimeSpan lifetime);
    void EndInput(ToyInputLease lease);
    void Stop(string? device = null);
}

// These are local inventory descriptors, never remote grants. Nullable fields mean unverified/unknown.
internal sealed record ToyCapability(string ChannelId, ToyCapabilityType Type, string DisplayName,
    int? NativeMinimum, int? NativeMaximum, int? NativeStep, bool? SupportsStop, bool? SupportsStatus)
{
    public bool IsSetting => Type is ToyCapabilityType.Depth or ToyCapabilityType.Position;
}

// Local transport only. Every caller must use LocalToyController's owner gate; never expose this to peers.
internal interface ILocalToyOutput
{
    event Action<string>? OutputFailed;
    void SendLevel(string deviceKey, string channelId, int nativeValue);
    bool TrySendLevel(string deviceKey, string channelId, int nativeValue, Func<bool> isCurrent)
    {
        if (!isCurrent()) return false;
        SendLevel(deviceKey, channelId, nativeValue);
        return true;
    }
}

internal sealed record LocalToyDescriptor(string DeviceKey, string Adapter, string DisplayName, string Model,
    ToyConnectionState ConnectionState, IReadOnlyList<ToyCapability> Capabilities, bool CapabilitiesKnown,
    int? BatteryPercent, DateTimeOffset LastSeenUtc, double ConservativeCommandsPerSecond, bool IsSimulated)
{
    public int DescriptorVersion => 1;
    public bool IsRemembered { get; init; }
    public bool AutoConnect { get; init; }
    public bool AutoConnectPaused { get; init; }
    public bool PersistentIdentity { get; init; } = true;
}

internal sealed record ToyAdapterSnapshot(ToyAdapterAvailability Availability, bool IsScanning,
    string Message, IReadOnlyList<LocalToyDescriptor> Devices)
{
    public string? LastError { get; init; }
    // True only for a backend which keeps established toy sessions usable
    // during discovery. Legacy Lovense/simulation retain their scan gate.
    public bool SupportsOutputWhileScanning { get; init; }
}

internal interface ILocalToyAdapter : IAsyncDisposable
{
    event Action? Changed;
    ToyAdapterSnapshot Snapshot { get; }
    Task StartScanAsync(CancellationToken cancellationToken = default);
    Task StopScanAsync(CancellationToken cancellationToken = default);
    Task ConnectAsync(string deviceKey, CancellationToken cancellationToken = default);
    Task DisconnectAsync(string deviceKey, CancellationToken cancellationToken = default);
    Task ReadBatteryAsync(string deviceKey, CancellationToken cancellationToken = default);
    // Stage 01 deliberately has no actuator/output command API.
}

internal interface IRememberedToyAdapter : ILocalToyAdapter
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task EnableAutomaticConnectionsAsync(CancellationToken cancellationToken = default, Func<bool>? canScan = null);
    Task SetAutoConnectAsync(string deviceKey, bool enabled, CancellationToken cancellationToken = default);
    Task ForgetAsync(string deviceKey, CancellationToken cancellationToken = default);
}

// App exit only: release an isolated transport after its owner has disarmed.
internal interface ILocalToyExit
{
    Task CloseTransportForExitAsync();
}

// Auto-connecting backends must honor the wrapper's session-only Forget pause.
internal interface ILocalToyConnectionFilter
{
    Func<string, bool> AllowConnection { set; }
}

internal sealed class LocalToyKeys
{
    private readonly byte[] secret;
    public LocalToyKeys(byte[] secret)
    {
        if (secret.Length != 32) throw new ArgumentException("An installation key must contain 32 bytes.");
        this.secret = secret.ToArray();
    }

    public static LocalToyKeys LoadOrCreate(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            file.Write(RandomNumberGenerator.GetBytes(32));
        }
        catch (IOException) when (File.Exists(path)) { }
        return new LocalToyKeys(File.ReadAllBytes(path));
    }

    public string ForDevice(string adapter, string privateIdentity) => "toy-" +
        Convert.ToHexString(HMACSHA256.HashData(secret,
            Encoding.UTF8.GetBytes(adapter + "\n" + privateIdentity.Trim().ToUpperInvariant()))).ToLowerInvariant();
}
