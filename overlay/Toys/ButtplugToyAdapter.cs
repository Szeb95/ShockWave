using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShockWave.Overlay.Toys;

// Intiface Engine connects recognized toys while discovery continues. A scan
// never zeros/disarms an unrelated connected toy on this backend.
internal sealed class ButtplugToyAdapter : ILocalToyAdapter, ILocalToyOutput, ILocalToyExit, ILocalToyConnectionFilter
{
    private sealed record Feature(string Channel, string Kind, string Actuator, int Index, int Steps);
    private sealed record Entry(uint Index, LocalToyDescriptor Descriptor, Feature[] Features, int? BatteryIndex);
    private readonly Func<IButtplugSession> createSession;
    private readonly Func<LocalToyKeys> createKeys;
    private readonly object sync = new();
    private readonly SemaphoreSlim management = new(1, 1), output = new(1, 1);
    private readonly CancellationTokenSource lifetime = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, JsonElement> announced = new();
    private readonly HashSet<string> disconnected = new(StringComparer.Ordinal);
    private IButtplugSession? session;
    private LocalToyKeys? keys;
    private bool scanning, closed;
    private string message = "Bundled Bluetooth engine · ready to discover toys.";
    private string? lastError;
    private ToyAdapterAvailability availability = ToyAdapterAvailability.Ready;
    private CancellationTokenSource? scanLifetime;
    private Task inventoryLoop = Task.CompletedTask;
    private Task? cleanup;
    private readonly TimeSpan scanDuration;
    public Func<string, bool> AllowConnection { private get; set; } = _ => true;
    public event Action? Changed;
    public event Action<string>? OutputFailed;
    public ButtplugToyAdapter(Func<LocalToyKeys> createKeys, Func<IButtplugSession>? createSession = null, TimeSpan? scanDuration = null)
    { this.createKeys = createKeys; this.createSession = createSession ?? (() => new ButtplugEngineSession()); this.scanDuration = scanDuration ?? TimeSpan.FromSeconds(15); }
    public ToyAdapterSnapshot Snapshot
    {
        get { lock (sync) return new(availability, scanning, message, Array.AsReadOnly(entries.Values.Select(value => value.Descriptor)
            .OrderBy(device => device.DisplayName).ThenBy(device => device.DeviceKey).ToArray())) { LastError = lastError, SupportsOutputWhileScanning = true }; }
    }
    private void Publish()
    { if (Changed is { } handlers) foreach (Action handler in handlers.GetInvocationList()) try { handler(); } catch (Exception) { } }
    private async Task Manage(Func<CancellationToken, Task> action, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        await management.WaitAsync(linked.Token);
        try { linked.Token.ThrowIfCancellationRequested(); await action(linked.Token); }
        finally { management.Release(); }
    }
    private async Task EnsureAsync(CancellationToken token)
    {
        if (session is not null) return;
        keys = createKeys(); session = createSession();
        session.Message += OnMessage; session.Lost += OnLost;
        try
        {
            await session.InitializeAsync(token);
            var devices = await session.RequestAsync("RequestDeviceList", new { }, token);
            OnMessage("DeviceList", devices);
            inventoryLoop = Task.Run(InventoryLoopAsync);
        }
        catch (Exception)
        {
            lock (sync) { availability = ToyAdapterAvailability.Error; lastError = "Bundled Bluetooth engine unavailable. Reset Bluetooth or choose Lovense Bluetooth (legacy)."; }
            Publish(); throw;
        }
    }
    private void OnMessage(string kind, JsonElement payload)
    {
        lock (sync)
        {
            if (closed) return;
            if (kind == "DeviceAdded" && announced.Count < 256) announced[payload.GetProperty("DeviceIndex").GetUInt32()] = payload.Clone();
            else if (kind == "DeviceList")
            {
                foreach (var device in payload.GetProperty("Devices").EnumerateArray())
                    if (announced.Count < 256) announced[device.GetProperty("DeviceIndex").GetUInt32()] = device.Clone();
            }
            else if (kind == "DeviceRemoved")
            {
                var index = payload.GetProperty("DeviceIndex").GetUInt32(); announced.Remove(index);
                foreach (var (key, entry) in entries.Where(pair => pair.Value.Index == index).ToArray())
                    entries[key] = entry with { Descriptor = entry.Descriptor with { ConnectionState = ToyConnectionState.Disconnected, BatteryPercent = null } };
            }
            else if (kind == "ScanningFinished") { scanning = false; scanLifetime?.Cancel(); message = "Discovery finished. Connected toys remain available."; }
        }
        Publish();
    }
    private void OnLost()
    {
        string[] affected;
        lock (sync)
        {
            if (closed) return;
            scanning = false; announced.Clear(); availability = ToyAdapterAvailability.Error;
            lastError = message = "Bluetooth engine connection lost · output disarmed. Reset Bluetooth to recover.";
            affected = entries.Keys.ToArray();
            foreach (var key in affected) entries[key] = entries[key] with { Descriptor = entries[key].Descriptor with
                { ConnectionState = ToyConnectionState.Unavailable, BatteryPercent = null } };
        }
        Publish(); foreach (var key in affected) OutputFailed?.Invoke(key);
    }
    private async Task InventoryLoopAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                KeyValuePair<uint, JsonElement>[] pending;
                lock (sync) pending = announced.Where(pair => !entries.Values.Any(entry => entry.Index == pair.Key &&
                    entry.Descriptor.ConnectionState == ToyConnectionState.Connected)).ToArray();
                foreach (var (index, device) in pending)
                {
                    var name = device.GetProperty("DeviceName").GetString() ?? "Unknown toy";
                    if (!name.StartsWith("Lovense ", StringComparison.OrdinalIgnoreCase) || name.Length > 128) continue;
                    var identity = session!.Identity(index);
                    if (identity is null || identity.Length is 0 or > 4096) continue;
                    // Match the original SDK keys when Windows exposes the same
                    // target MAC. Adapter MACs may precede it in a WinRT ID.
                    var addresses = Regex.Matches(identity, @"(?i)(?:[0-9a-f]{2}[:-]){5}[0-9a-f]{2}");
                    var normalized = addresses.Count > 0 ? Regex.Replace(addresses[^1].Value, "[^0-9A-Fa-f]", "") : null;
                    var key = normalized is not null ? keys!.ForDevice("lovense-ble", "address:" + normalized) : keys!.ForDevice("buttplug-ble", identity);
                    if (!AllowConnection(key))
                    {
                        await session.RequestAsync("StopDeviceCmd", new { DeviceIndex = index }, lifetime.Token);
                        lock (sync) announced.Remove(index);
                        continue;
                    }
                    var (features, capabilities, battery) = ParseFeatures(device);
                    var label = name[8..];
                    lock (sync)
                    {
                        if (closed || !announced.ContainsKey(index) || disconnected.Contains(key)) continue;
                        if (entries.TryGetValue(key, out var existing) && existing.Index == index && existing.Descriptor.ConnectionState == ToyConnectionState.Connected) continue;
                        var descriptor = new LocalToyDescriptor(key, "buttplug-ble", label, label,
                            ToyConnectionState.Connected, capabilities, capabilities.Count > 0, null, DateTimeOffset.UtcNow, 10, false);
                        entries[key] = new(index, descriptor, features, battery);
                        message = "Bundled Bluetooth · connected toys stay usable during discovery.";
                    }
                    Publish();
                }
                await Task.Delay(50, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception) { OnLost(); if (session is not null) await session.DisposeAsync(); }
    }
    private static (Feature[] Features, IReadOnlyList<ToyCapability> Capabilities, int? Battery) ParseFeatures(JsonElement device)
    {
        var messages = device.GetProperty("DeviceMessages"); var features = new List<Feature>(); var capabilities = new List<ToyCapability>();
        if (messages.TryGetProperty("ScalarCmd", out var scalars))
        {
            var vibrators = scalars.EnumerateArray().Count(value => value.GetProperty("ActuatorType").GetString() == "Vibrate"); var motor = 0; var index = 0;
            foreach (var scalar in scalars.EnumerateArray())
            {
                var type = scalar.GetProperty("ActuatorType").GetString()!; var steps = scalar.GetProperty("StepCount").GetInt32();
                var (channel, capability, label) = type switch
                {
                    "Vibrate" => (vibrators == 1 ? "v" : "v" + (++motor), ToyCapabilityType.Vibration, vibrators == 1 ? "Vibration" : "Vibration " + motor),
                    "Constrict" => ("p", ToyCapabilityType.Pump, "Pump"),
                    "Oscillate" => ("o", ToyCapabilityType.Oscillation, "Oscillation"),
                    _ => ("", ToyCapabilityType.Unknown, "Unsupported function"),
                };
                if (channel.Length > 0 && !(capability == ToyCapabilityType.Vibration && motor > 3) && steps is > 0 and <= 10000 && !features.Any(feature => feature.Channel == channel))
                { features.Add(new(channel, "ScalarCmd", type, index, steps)); capabilities.Add(new(channel, capability, label, 0, steps, 1, null, null)); }
                index++;
            }
        }
        if (messages.TryGetProperty("RotateCmd", out var rotations) && rotations.GetArrayLength() == 1)
        {
            var steps = rotations[0].GetProperty("StepCount").GetInt32();
            if (steps is > 0 and <= 10000) { features.Add(new("r", "RotateCmd", "Rotate", 0, steps)); capabilities.Add(new("r", ToyCapabilityType.Rotation, "Rotation", 0, steps, 1, null, null)); }
        }
        int? battery = null;
        if (messages.TryGetProperty("SensorReadCmd", out var sensors))
        {
            var index = 0;
            foreach (var sensor in sensors.EnumerateArray()) { if (sensor.GetProperty("SensorType").GetString() == "Battery") { battery = index; break; } index++; }
        }
        return (features.ToArray(), capabilities.AsReadOnly(), battery);
    }
    public Task StartScanAsync(CancellationToken cancellationToken = default) => Manage(async token =>
    {
        await EnsureAsync(token);
        lock (sync) if (scanning) return;
        lock (sync)
        {
            scanning = true; message = "Scanning · connected toys remain controllable.";
            scanLifetime?.Cancel(); scanLifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        }
        Publish();
        try { await session!.RequestAsync("StartScanning", new { }, token); }
        catch
        {
            // A canceled request may already have started native discovery.
            // Stop it with the owner's lifetime token, not the canceled request.
            try { await session!.RequestAsync("StopScanning", new { }, lifetime.Token); }
            catch (Exception) { OnLost(); await session!.DisposeAsync(); }
            lock (sync) { scanning = false; scanLifetime?.Cancel(); }
            Publish(); throw;
        }
        _ = ScanDeadlineAsync(scanLifetime.Token);
    }, cancellationToken);
    private async Task ScanDeadlineAsync(CancellationToken token)
    {
        try { await Task.Delay(scanDuration, token); await StopScanAsync(lifetime.Token); }
        catch (OperationCanceledException) { }
        catch (Exception) { OnLost(); }
    }
    public Task StopScanAsync(CancellationToken cancellationToken = default) => Manage(async token =>
    {
        lock (sync) if (!scanning) return;
        await session!.RequestAsync("StopScanning", new { }, token);
        lock (sync) { scanning = false; scanLifetime?.Cancel(); message = "Discovery stopped. Connected toys remain available."; }
        Publish();
    }, cancellationToken);
    public Task ConnectAsync(string deviceKey, CancellationToken cancellationToken = default) => Manage(_ =>
    {
        lock (sync)
        {
            disconnected.Remove(deviceKey);
            if (!entries.TryGetValue(deviceKey, out var entry) || entry.Descriptor.ConnectionState != ToyConnectionState.Connected)
                throw new InvalidOperationException("Toy is offline. Turn it on nearby and scan; the bundled engine connects it automatically.");
        }
        return Task.CompletedTask;
    }, cancellationToken);
    public Task DisconnectAsync(string deviceKey, CancellationToken cancellationToken = default) => Manage(async token =>
    {
        Entry entry; lock (sync) entry = entries[deviceKey];
        await session!.RequestAsync("StopDeviceCmd", new { DeviceIndex = entry.Index }, token);
        lock (sync) { disconnected.Add(deviceKey); entries[deviceKey] = entry with { Descriptor = entry.Descriptor with
            { ConnectionState = ToyConnectionState.Disconnected, BatteryPercent = null } }; }
        Publish();
    }, cancellationToken);
    public Task ReadBatteryAsync(string deviceKey, CancellationToken cancellationToken = default) => Manage(async token =>
    {
        Entry entry; lock (sync) entry = entries[deviceKey];
        if (entry.Descriptor.ConnectionState != ToyConnectionState.Connected || entry.BatteryIndex is not int sensor) return;
        var response = await session!.RequestAsync("SensorReadCmd", new { DeviceIndex = entry.Index, SensorIndex = sensor, SensorType = "Battery" }, token);
        int? percent = null;
        if (response.TryGetProperty("DeviceIndex", out var responseIndex) && responseIndex.GetUInt32() == entry.Index &&
            response.GetProperty("SensorType").GetString() == "Battery" && response.GetProperty("SensorIndex").GetInt32() == sensor &&
            response.GetProperty("Data") is var data && data.GetArrayLength() == 1 && data[0].TryGetInt32(out var reading) && reading is >= 0 and <= 100) percent = reading;
        lock (sync)
            if (entries.TryGetValue(deviceKey, out var current) && current.Index == entry.Index && current.Descriptor.ConnectionState == ToyConnectionState.Connected)
                entries[deviceKey] = current with { Descriptor = current.Descriptor with { BatteryPercent = percent } };
        Publish();
    }, cancellationToken);
    public void SendLevel(string deviceKey, string channelId, int nativeValue) => TrySendLevel(deviceKey, channelId, nativeValue, () => true);
    public bool TrySendLevel(string deviceKey, string channelId, int nativeValue, Func<bool> isCurrent)
    {
        output.Wait(lifetime.Token);
        try
        {
            if (!isCurrent()) return false;
            Entry entry; Feature feature;
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(closed, this);
                if (!entries.TryGetValue(deviceKey, out entry!) || entry.Descriptor.ConnectionState != ToyConnectionState.Connected)
                    throw new InvalidOperationException("Connect the toy before using its controls.");
                feature = entry.Features.FirstOrDefault(value => value.Channel == channelId) ?? throw new InvalidOperationException("This function is unsupported by the bundled engine.");
                if (nativeValue < 0 || nativeValue > feature.Steps) throw new ArgumentOutOfRangeException(nameof(nativeValue));
            }
            object fields = feature.Kind == "RotateCmd"
                ? new { DeviceIndex = entry.Index, Rotations = new[] { new { Index = feature.Index, Speed = nativeValue / (double)feature.Steps, Clockwise = true } } }
                : new { DeviceIndex = entry.Index, Scalars = new[] { new { Index = feature.Index, Scalar = nativeValue / (double)feature.Steps, ActuatorType = feature.Actuator } } };
            var response = session!.RequestAsync(feature.Kind, fields, lifetime.Token, () =>
            {
                if (!isCurrent()) return false;
                lock (sync) return !closed && entries.TryGetValue(deviceKey, out var current) && current.Index == entry.Index &&
                    current.Descriptor.ConnectionState == ToyConnectionState.Connected;
            }).GetAwaiter().GetResult();
            return response.ValueKind != JsonValueKind.Undefined;
        }
        finally { output.Release(); }
    }
    public Task CloseTransportForExitAsync() => session?.DisposeAsync().AsTask() ?? Task.CompletedTask;
    public ValueTask DisposeAsync() { lock (sync) return new(cleanup ??= Task.Run(CleanupAsync)); }
    private async Task CleanupAsync()
    {
        lock (sync) { closed = true; scanning = false; }
        lifetime.Cancel(); scanLifetime?.Cancel();
        if (session is not null) { session.Message -= OnMessage; session.Lost -= OnLost; await session.DisposeAsync(); }
        try { await inventoryLoop; } catch (OperationCanceledException) { }
        lock (sync)
        {
            availability = ToyAdapterAvailability.Disposed;
            foreach (var key in entries.Keys.ToArray()) entries[key] = entries[key] with { Descriptor = entries[key].Descriptor with { ConnectionState = ToyConnectionState.Unavailable, BatteryPercent = null } };
        }
        Publish();
    }
}
