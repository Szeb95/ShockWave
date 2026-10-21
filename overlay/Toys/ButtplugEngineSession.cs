using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace ShockWave.Overlay.Toys;

internal interface IButtplugSession : IAsyncDisposable
{
    event Action<string, JsonElement>? Message;
    event Action? Lost;
    Task InitializeAsync(CancellationToken token);
    Task<JsonElement> RequestAsync(string kind, object fields, CancellationToken token, Func<bool>? isCurrent = null);
    string? Identity(uint index);
}

// One app-owned engine, reverse WebSocket on a private loopback URL. No remote
// listener, shared Intiface server, raw commands, or automatic command retry.
internal sealed class ButtplugEngineSession(bool bluetooth = true, string? executable = null) : IButtplugSession
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writes = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    private Process? process;
    private SafeFileHandle? job;
    private TcpListener? listener;
    private TcpClient? connection;
    private WebSocket? socket;
    private string? runtimeDirectory, configPath;
    private Task reader = Task.CompletedTask, heartbeat = Task.CompletedTask;
    private int nextId, closing, failed;
    public event Action<string, JsonElement>? Message;
    public event Action? Lost;
    internal int ProcessId => process?.Id ?? 0;

    public async Task InitializeAsync(CancellationToken token)
    {
        using var startup = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        startup.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            var path = executable ?? Path.Combine(AppContext.BaseDirectory, "native", "buttplug", "intiface-engine.exe");
            if (!File.Exists(path)) throw new InvalidOperationException("Bundled Bluetooth engine is missing. Keep the complete program folder together, or choose Lovense Bluetooth (legacy).");
            runtimeDirectory = Path.Combine(Path.GetTempPath(), "ShockWave-Buttplug-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(runtimeDirectory);
            configPath = Path.Combine(runtimeDirectory, "devices.json");
            // Engine's private identity/index map is transient. Only opaque
            // installation keys reach the existing encrypted remembered store.
            await File.WriteAllTextAsync(configPath, "{\"version\":{\"major\":5,\"minor\":0},\"user_configs\":{\"devices\":[]}}", startup.Token);
            listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(1);
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var secretPath = "/" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var info = new ProcessStartInfo(path)
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(path)!,
            };
            foreach (var argument in new[] { "--websocket-client-address", $"ws://127.0.0.1:{port}{secretPath}",
                "--server-name", "ShockWave private Bluetooth", "--max-ping-time", "2000",
                "--device-config-file", Path.Combine(Path.GetDirectoryName(path)!, "lovense-devices.json"),
                "--user-device-config-file", configPath }) info.ArgumentList.Add(argument);
            if (bluetooth) info.ArgumentList.Add("--use-bluetooth-le");
            // Other managers are opt-in in this pinned engine. No serial/dongle,
            // network device manager, mDNS, REST or external Lovense app.
            process = new Process { StartInfo = info, EnableRaisingEvents = true };
            process.Exited += (_, _) => Fail();
            if (!process.Start()) throw new InvalidOperationException("Could not start the bundled Bluetooth engine.");
            job = OwnProcess(process);
            process.OutputDataReceived += (_, _) => { }; process.ErrorDataReceived += (_, _) => { };
            process.BeginOutputReadLine(); process.BeginErrorReadLine(); // Never publish vendor identities/logs.
            connection = await listener.AcceptTcpClientAsync(startup.Token);
            var stream = connection.GetStream();
            var header = new StringBuilder(); var one = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            {
                if (header.Length >= 8192 || await stream.ReadAsync(one, startup.Token) != 1) throw new IOException();
                header.Append((char)one[0]);
            }
            var lines = header.ToString().Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            if (lines[0] != $"GET {secretPath} HTTP/1.1") throw new IOException();
            var key = lines.FirstOrDefault(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase))?.Split(':', 2)[1].Trim();
            if (key is null || Convert.FromBase64String(key).Length != 16) throw new IOException();
            var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept + "\r\n\r\n"), startup.Token);
            listener.Stop();
            socket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(5));
            reader = Task.Run(ReadAsync);
            var response = await RequestAsync("RequestServerInfo", new { ClientName = "ShockWave", MessageVersion = 3 }, startup.Token);
            if (!response.TryGetProperty("MessageVersion", out var version) || version.GetInt32() != 3) throw new IOException();
            heartbeat = Task.Run(HeartbeatAsync);
        }
        catch (Exception error)
        {
            await DisposeAsync();
            if (error is OperationCanceledException && token.IsCancellationRequested) throw;
            if (error is InvalidOperationException) throw;
            throw new InvalidOperationException("Bundled Bluetooth engine could not start. Try Reset Bluetooth or choose Lovense Bluetooth (legacy).");
        }
    }

    public string? Identity(uint index)
    {
        if (configPath is null) return null;
        try
        {
            using var file = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (file.Length > 1024 * 1024) return null;
            using var document = JsonDocument.Parse(file);
            var devices = document.RootElement.GetProperty("user_configs").GetProperty("devices");
            if (devices.GetArrayLength() > 256) return null;
            foreach (var device in devices.EnumerateArray())
            {
                if (device.GetProperty("identifier").GetProperty("protocol").GetString() != "lovense") continue;
                var config = device.GetProperty("config");
                if (config.GetProperty("user_config").GetProperty("index").GetUInt32() == index)
                    return device.GetProperty("identifier").GetProperty("address").GetString();
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or InvalidOperationException) { }
        return null;
    }

    public async Task<JsonElement> RequestAsync(string kind, object fields, CancellationToken token, Func<bool>? isCurrent = null)
    {
        if (Volatile.Read(ref failed) != 0 || Volatile.Read(ref closing) != 0 && kind != "StopAllDevices" || socket?.State != WebSocketState.Open)
            throw new InvalidOperationException("Bluetooth engine connection is unavailable.");
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        bounded.CancelAfter(TimeSpan.FromSeconds(2));
        var id = Interlocked.Increment(ref nextId);
        var parameters = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(fields))!;
        parameters["Id"] = JsonSerializer.SerializeToElement(id);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { new Dictionary<string, object> { [kind] = parameters } });
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        var submitted = false;
        try
        {
            await writes.WaitAsync(bounded.Token);
            try
            {
                if (isCurrent is not null && !isCurrent()) return default;
                if (Volatile.Read(ref closing) != 0 && kind != "StopAllDevices") throw new OperationCanceledException();
                bounded.Token.ThrowIfCancellationRequested();
                submitted = true; // A canceled/partial send is also ambiguous.
                await socket.SendAsync(bytes, WebSocketMessageType.Text, true, bounded.Token);
            }
            finally { writes.Release(); }
            return await completion.Task.WaitAsync(bounded.Token);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && !lifetime.IsCancellationRequested)
        {
            // An ambiguous submission is never retried. Kill/disarm this engine
            // rather than keep a socket which might execute it later.
            if (submitted) Fail();
            throw new InvalidOperationException("Bluetooth engine request timed out; output was disarmed. Reset Bluetooth to recover.");
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
                WebSocketReceiveResult received;
                do
                {
                    received = await socket!.ReceiveAsync(buffer, lifetime.Token);
                    if (received.MessageType != WebSocketMessageType.Text || message.Length + received.Count > 1024 * 1024) throw new IOException();
                    message.Write(buffer, 0, received.Count);
                } while (!received.EndOfMessage);
                using var document = JsonDocument.Parse(message.ToArray());
                if (document.RootElement.GetArrayLength() > 256) throw new IOException();
                foreach (var item in document.RootElement.EnumerateArray())
                    foreach (var property in item.EnumerateObject())
                    {
                        var payload = property.Value.Clone();
                        if (payload.TryGetProperty("Id", out var id) && id.TryGetInt32(out var value) && value > 0 && pending.TryGetValue(value, out var completion))
                        {
                            if (property.Name == "Error") completion.TrySetException(new InvalidOperationException("Bluetooth engine rejected the request."));
                            else completion.TrySetResult(payload);
                        }
                        else Message?.Invoke(property.Name, payload);
                    }
            }
        }
        catch (Exception) { if (!lifetime.IsCancellationRequested) Fail(); }
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            { await Task.Delay(500, lifetime.Token); await RequestAsync("Ping", new { }, lifetime.Token); }
        }
        catch (Exception) { if (!lifetime.IsCancellationRequested) Fail(); }
    }

    private void Fail()
    {
        if (Volatile.Read(ref closing) != 0 || Interlocked.Exchange(ref failed, 1) != 0) return;
        foreach (var completion in pending.Values) completion.TrySetException(new InvalidOperationException("Bluetooth engine connection was lost."));
        // Closing the owned job kills the helper even after a hung request.
        job?.Dispose();
        try { Lost?.Invoke(); } catch (Exception) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref closing, 1) != 0) return;
        // Best effort stop before closing the owned process. Local owner gates
        // are already disarmed by page/controller cleanup.
        try { using var stop = new CancellationTokenSource(500); await RequestAsync("StopAllDevices", new { }, stop.Token); } catch (Exception) { }
        lifetime.Cancel(); listener?.Stop(); socket?.Abort(); connection?.Dispose(); job?.Dispose();
        foreach (var completion in pending.Values) completion.TrySetCanceled();
        try { await Task.WhenAll(reader, heartbeat).WaitAsync(TimeSpan.FromSeconds(1)); } catch (Exception) { }
        if (process is not null)
        {
            try { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(1)); } } catch (Exception) { }
            process.Dispose();
        }
        socket?.Dispose();
        try { if (configPath is not null) File.Delete(configPath); if (runtimeDirectory is not null) Directory.Delete(runtimeDirectory, false); } catch (IOException) { }
    }

    // Windows closes this job handle when the parent terminates, including a
    // forced app exit. The engine cannot outlive its owner and accept old input.
    private static SafeFileHandle OwnProcess(Process child)
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new IOException();
        var limits = new JobLimits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()) || !AssignProcessToJobObject(handle, child.Handle))
        { handle.Dispose(); throw new IOException(); }
        return handle;
    }
    [StructLayout(LayoutKind.Sequential)] private struct BasicLimits
    { public long ProcessTime, JobTime; public uint Flags; public UIntPtr Minimum, Maximum; public uint ActiveProcesses; public UIntPtr Affinity; public uint Priority, Scheduling; }
    [StructLayout(LayoutKind.Sequential)] private struct IoCounters
    { public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes; }
    [StructLayout(LayoutKind.Sequential)] private struct JobLimits
    { public BasicLimits Basic; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(SafeFileHandle job, int info, ref JobLimits limits, uint size);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr child);
}
