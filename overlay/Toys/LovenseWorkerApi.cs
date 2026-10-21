using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace ShockWave.Overlay.Toys;

// Private parent/child pipes, not a network endpoint. No command survives a worker replacement.
internal sealed record BleWorkerMessage(string Kind, long Request = 0, int Code = 0, string Id = "",
    string Address = "", string Symbol = "", string Text = "", bool Connected = false, int Value = 0);

internal sealed class LovenseWorkerApi : ILovenseBleApi
{
    private readonly Process process;
    private readonly SemaphoreSlim calls = new(1, 1), writes = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<bool>> pending = new();
    private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource lifetime = new();
    private readonly TimeSpan callTimeout;
    private long sequence;
    private int disposed, failed;
    internal int WorkerProcessId => process.Id;
    public event Action<int>? BluetoothStatus;
    public event Action<string, string, string>? ToyFound;
    public event Action<string, bool>? ConnectionChanged;
    public event Action<string, string>? Battery;
    public event Action<int, string>? Notification;

    internal static ProcessStartInfo StartInfo(string mode)
    {
        var executable = Environment.ProcessPath ?? throw new LovenseLibraryException("The Bluetooth worker could not locate the app.");
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, Assembly.GetEntryAssembly()!.GetName().Name + ".dll"));
        info.ArgumentList.Add(mode);
        info.Environment.Remove("OPENSHOCK_TOKEN");
        return info;
    }

    public LovenseWorkerApi(ProcessStartInfo? startInfo = null, TimeSpan? callTimeout = null, bool heartbeat = true)
    {
        this.callTimeout = callTimeout ?? TimeSpan.FromSeconds(5);
        process = new() { StartInfo = startInfo ?? StartInfo("--lovense-worker") };
        try
        {
            if (!process.Start()) throw new InvalidOperationException();
            _ = Task.Run(ReadAsync);
            _ = Task.Run(DrainDiagnosticsAsync); // Never expose native diagnostics/private IDs as app logs.
            ready.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            if (heartbeat) _ = Task.Run(HeartbeatAsync);
        }
        catch (Exception error)
        {
            var startupError = StartupError(error);
            Dispose();
            throw startupError;
        }
    }

    private LovenseLibraryException StartupError(Exception error)
    {
        if (error is LovenseLibraryException libraryError) return libraryError;
        if (error is System.ComponentModel.Win32Exception windowsError)
            return new($"Bluetooth worker could not launch (Windows error {windowsError.NativeErrorCode}). Check access to the app folder.");
        if (error is TimeoutException)
            return new("Bluetooth worker startup timed out. Select Reset Bluetooth; if it repeats, reopen the app.");
        int? exitCode = null;
        try { if (process.WaitForExit(100)) exitCode = process.ExitCode; } catch (Exception) { }
        return new("Bluetooth worker exited during startup" + (exitCode is null ? "" : $" (code {exitCode})") +
            ". Select Reset Bluetooth; if it repeats, report this error.");
    }

    private async Task DrainDiagnosticsAsync()
    {
        var buffer = new char[1024];
        try
        {
            while (await process.StandardError.ReadAsync(buffer.AsMemory(), lifetime.Token).ConfigureAwait(false) > 0) { }
        }
        catch (Exception) { }
    }

    private async Task ReadAsync()
    {
        try
        {
            while (await process.StandardOutput.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                if (Volatile.Read(ref disposed) != 0) break;
                if (line.Length > 32768) throw new InvalidDataException();
                BleWorkerMessage? message;
                try { message = JsonSerializer.Deserialize<BleWorkerMessage>(line); }
                catch (JsonException) { continue; } // Ignore vendor stdout; never forward it to logs.
                if (message is null) continue;
                if (message.Kind == "ready") { if (message.Code == 1) ready.TrySetResult(true); else ready.TrySetException(new InvalidDataException()); continue; }
                if (message.Kind == "ack") { if (pending.TryGetValue(message.Request, out var waiter)) waiter.TrySetResult(true); continue; }
                if (message.Kind == "fault")
                {
                    var error = new LovenseLibraryException(message.Code == 1 ? "Lovense library missing or incompatible. Check native/lovense beside the EXE." :
                        "Bluetooth worker failed. Select Reset Bluetooth, then reconnect.");
                    ready.TrySetException(error);
                    if (pending.TryGetValue(message.Request, out var waiter)) waiter.TrySetException(error);
                    Fail(); continue;
                }
                try
                {
                    switch (message.Kind)
                    {
                        case "bluetooth": BluetoothStatus?.Invoke(message.Code); break;
                        case "found": ToyFound?.Invoke(message.Id, message.Address, message.Symbol); break;
                        case "connection": ConnectionChanged?.Invoke(message.Id, message.Connected); break;
                        case "battery": Battery?.Invoke(message.Id, message.Text); break;
                        case "notify": Notification?.Invoke(message.Code, message.Id); break;
                    }
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
        finally { Fail(); }
    }

    private void Fail()
    {
        if (Volatile.Read(ref disposed) != 0 || Interlocked.Exchange(ref failed, 1) != 0) return;
        // After early EOF, allow Windows to publish the child's real exit code before cleanup.
        if (!ready.Task.IsCompleted) try { process.WaitForExit(100); } catch (Exception) { }
        var error = new InvalidOperationException("Bluetooth worker stopped responding. Select Reset Bluetooth, then reconnect.");
        ready.TrySetException(error);
        foreach (var waiter in pending.Values) waiter.TrySetException(error);
        Kill();
        try { Notification?.Invoke(-1, ""); } catch (Exception) { }
    }

    private async Task<bool> WriteAsync(BleWorkerMessage message, CancellationToken token, Func<bool>? isCurrent = null)
    {
        await writes.WaitAsync(token).ConfigureAwait(false);
        try
        {
            if (isCurrent is not null && !isCurrent()) return false;
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
            return true;
        }
        finally { writes.Release(); }
    }

    private bool Call(string kind, string id = "", int command = 0, int value = 0, Func<bool>? isCurrent = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (Volatile.Read(ref failed) != 0) throw new InvalidOperationException("Bluetooth worker needs a reset. Select Reset Bluetooth.");
        if (id.Length > 4096) throw new ArgumentException("Invalid toy identity.");
        calls.Wait(lifetime.Token);
        var request = Interlocked.Increment(ref sequence);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[request] = completion;
        try
        {
            // Recheck after earlier management calls release the pipe; never submit old owner intent.
            if (isCurrent is not null && !isCurrent()) return false;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(callTimeout);
            if (!WriteAsync(new(kind, request, command, id, Value: value), timeout.Token, isCurrent).GetAwaiter().GetResult()) return false;
            completion.Task.WaitAsync(callTimeout, lifetime.Token).GetAwaiter().GetResult();
            return true;
        }
        catch (Exception)
        {
            Fail();
            throw new InvalidOperationException("Bluetooth worker did not complete the request. Select Reset Bluetooth, then reconnect.");
        }
        finally { pending.TryRemove(request, out _); calls.Release(); }
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
            while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                timeout.CancelAfter(TimeSpan.FromSeconds(1));
                await WriteAsync(new("heartbeat"), timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception) { if (Volatile.Read(ref disposed) == 0) Fail(); }
    }

    public void CheckBluetooth() => Call("check");
    public void StartScan() => Call("scan");
    public void StopScan() => Call("stop");
    public void Connect(string id) => Call("connect", id);
    public void Disconnect(string id) => Call("disconnect", id);
    public void ReadBattery(string id) => Call("battery", id);
    public void SendLevel(string id, int command, int value)
    {
        if (command is < 0 or > 11 || value < 0 || value > (command == 10 ? 100 : command is 5 or 9 ? 3 : 20)) throw new ArgumentOutOfRangeException(nameof(value));
        Call("level", id, command, value);
    }
    public bool TrySendLevel(string id, int command, int value, Func<bool> isCurrent)
    {
        if (command is < 0 or > 11 || value < 0 || value > (command == 10 ? 100 : command is 5 or 9 ? 3 : 20)) throw new ArgumentOutOfRangeException(nameof(value));
        return Call("level", id, command, value, isCurrent);
    }

    private void Kill() { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (Exception) { } }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        foreach (var waiter in pending.Values) waiter.TrySetCanceled();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
            WriteAsync(new("quit"), timeout.Token).GetAwaiter().GetResult();
            if (!process.WaitForExit(1000)) Kill();
        }
        catch (Exception) { Kill(); }
        try { process.WaitForExit(1000); } catch (Exception) { }
        process.Dispose();
    }
}
