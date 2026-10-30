using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace ShockWave.Overlay.Toys;

// Connects outward to the selected phone. It never starts/kills an engine or retries a command.
internal sealed class IntifaceWebSocketSession(Uri address, string scope) : IButtplugSession
{
    private readonly ClientWebSocket socket = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private readonly ConcurrentDictionary<uint, JsonElement> devices = new();
    private readonly string sessionId = Guid.NewGuid().ToString("N");
    private Task reader = Task.CompletedTask, heartbeat = Task.CompletedTask;
    private int nextId, closed, failed;
    public event Action<string, JsonElement>? Message;
    public event Action? Lost;
    public async Task InitializeAsync(CancellationToken token)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        startup.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            socket.Options.Proxy = null;
            await socket.ConnectAsync(address, startup.Token);
            reader = Task.Run(ReadAsync);
            var info = await RequestAsync("RequestServerInfo", new { ClientName = "ShockWave", MessageVersion = 3 }, startup.Token);
            if (info.GetProperty("MessageVersion").GetInt32() != 3 || !info.TryGetProperty("MaxPingTime", out var ping) ||
                !ping.TryGetInt32(out var maximum) || maximum is < 0 or > 60000)
                throw new InvalidOperationException("This Intiface server returned unsupported connection settings. Update Intiface and try again.");
            // Intiface Central defaults to a disabled protocol watchdog (zero). In that
            // case monitor the socket using read-only inventory requests, never actuator renewals.
            heartbeat = Task.Run(() => HeartbeatAsync(maximum == 0 ? 500 : Math.Clamp(maximum / 3, 20, 500), maximum > 0));
        }
        catch (Exception error)
        {
            Abort();
            if (error is OperationCanceledException && token.IsCancellationRequested) throw;
            if (error is InvalidOperationException) throw;
            throw new InvalidOperationException("Cannot reach Intiface. Open it on your phone, start its server and check that both devices are on the same home network.");
        }
    }

    // v3 does not expose a hardware identity. Only an explicit unique nickname is persistent.
    // Unnamed/duplicate toys get session identities, never a guessed index-to-hardware association.
    public string? Identity(uint index)
    {
        if (!devices.TryGetValue(index, out var device)) return null;
        var label = Nickname(device);
        return label is not null && devices.Values.Count(value => string.Equals(Nickname(value), label, StringComparison.OrdinalIgnoreCase)) == 1
            ? "named:" + scope + ":" + device.GetProperty("DeviceName").GetString() + ":" + label
            : "session:" + scope + ":" + sessionId + ":" + index;
    }
    private static string? Nickname(JsonElement device) => device.TryGetProperty("DeviceDisplayName", out var label) &&
        label.ValueKind == JsonValueKind.String && label.GetString()?.Trim() is { Length: > 0 and <= 128 } value ? value : null;
    public bool PersistentIdentity(uint index) => Identity(index)?.StartsWith("named:", StringComparison.Ordinal) == true;
    private void Track(string kind, JsonElement payload)
    {
        var old = devices.Keys.ToDictionary(index => index, Identity);
        if (kind == "DeviceList")
        {
            var list = payload.GetProperty("Devices");
            if (list.GetArrayLength() > 256) throw new InvalidDataException();
            devices.Clear();
            foreach (var device in list.EnumerateArray()) devices[device.GetProperty("DeviceIndex").GetUInt32()] = device.Clone();
        }
        else if (kind == "DeviceAdded")
        {
            if (devices.Count >= 256) throw new InvalidDataException();
            devices[payload.GetProperty("DeviceIndex").GetUInt32()] = payload;
        }
        else if (kind == "DeviceRemoved") devices.TryRemove(payload.GetProperty("DeviceIndex").GetUInt32(), out _);
        else return;
        // A nickname collision/change cannot transfer a live control to another identity.
        // Close the old ownership session before reconnecting with the new inventory.
        foreach (var (index, identity) in old)
            if (!devices.ContainsKey(index))
                Message?.Invoke("DeviceRemoved", JsonSerializer.SerializeToElement(new { Id = 0, DeviceIndex = index }));
            else if (identity != Identity(index)) { Fail(); return; }
        if (kind == "DeviceList") Message?.Invoke(kind, payload);
    }
    public async Task<JsonElement> RequestAsync(string kind, object fields, CancellationToken token, Func<bool>? isCurrent = null)
    {
        if (Volatile.Read(ref closed) != 0 || Volatile.Read(ref failed) != 0 || socket.State != WebSocketState.Open)
            throw new InvalidOperationException("Phone connection is unavailable.");
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        bounded.CancelAfter(TimeSpan.FromSeconds(2));
        var id = Interlocked.Increment(ref nextId);
        var parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(fields))!;
        parameters["Id"] = JsonSerializer.SerializeToElement(id);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { new Dictionary<string, object> { [kind] = parameters } });
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (pending.Count >= 64) throw new InvalidOperationException("Phone connection is busy.");
        pending[id] = completion;
        var submitted = false;
        try
        {
            await writes.WaitAsync(bounded.Token);
            try
            {
                if (isCurrent is not null && !isCurrent()) return default;
                bounded.Token.ThrowIfCancellationRequested();
                submitted = true;
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, bounded.Token);
            }
            finally { writes.Release(); }
            var response = await completion.Task.WaitAsync(bounded.Token);
            return response;
        }
        catch (Exception)
        {
            // A cancellation, partial write or missing response can leave output uncertain.
            // Drop this socket and its ownership. A configured server watchdog supplies
            // the phone-side deadline; a server advertising zero provides no such bound.
            if (submitted) Fail();
            throw new InvalidOperationException("Phone request failed; output is disarmed and the connection will recover automatically.");
        }
        finally { pending.TryRemove(id, out _); }
    }
    private async Task ReadAsync()
    {
        try
        {
            var buffer = new byte[8192];
            while (!lifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(buffer, lifetime.Token);
                    if (result.MessageType != WebSocketMessageType.Text || message.Length + result.Count > 1024 * 1024) throw new InvalidDataException();
                    message.Write(buffer, 0, result.Count);
                } while (!result.EndOfMessage);
                using var document = JsonDocument.Parse(message.ToArray());
                if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > 256) throw new InvalidDataException();
                foreach (var item in document.RootElement.EnumerateArray())
                {
                    if (item.EnumerateObject().Count() != 1) throw new InvalidDataException();
                    var property = item.EnumerateObject().Single(); var payload = property.Value.Clone();
                    Track(property.Name, payload);
                    if (Volatile.Read(ref failed) != 0) return;
                    if (payload.TryGetProperty("Id", out var id) && id.TryGetInt32(out var value) && value > 0 && pending.TryGetValue(value, out var completion))
                    {
                        if (property.Name == "Error") completion.TrySetException(new InvalidOperationException("Intiface rejected the request."));
                        else completion.TrySetResult(payload);
                    }
                    else Message?.Invoke(property.Name, payload);
                }
            }
        }
        catch (Exception) { if (!lifetime.IsCancellationRequested) Fail(); }
    }
    private async Task HeartbeatAsync(int interval, bool ping)
    {
        try { while (!lifetime.IsCancellationRequested) { await Task.Delay(interval, lifetime.Token); await RequestAsync(ping ? "Ping" : "RequestDeviceList", new { }, lifetime.Token); } }
        catch (Exception) { if (!lifetime.IsCancellationRequested) Fail(); }
    }
    private void Abort()
    {
        lifetime.Cancel(); socket.Abort();
        foreach (var item in pending.Values) item.TrySetException(new InvalidOperationException("Phone connection was lost."));
    }
    private void Fail()
    {
        if (Interlocked.Exchange(ref failed, 1) != 0 || Volatile.Read(ref closed) != 0) return;
        Abort(); try { Lost?.Invoke(); } catch (Exception) { }
    }
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref closed) != 0) return;
        try { using var stop = new CancellationTokenSource(400); await RequestAsync("StopAllDevices", new { }, stop.Token); } catch (Exception) { }
        if (Interlocked.Exchange(ref closed, 1) != 0) return;
        Abort();
        try { await Task.WhenAll(reader, heartbeat).WaitAsync(TimeSpan.FromMilliseconds(700)); } catch (Exception) { }
        socket.Dispose();
    }
}
