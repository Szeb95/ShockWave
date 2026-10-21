using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace ShockWave.Overlay.Toys;

// Called only in a dedicated child process. Its process lifetime owns the vendor DLL lifetime.
internal static class LovenseWorkerHost
{
    public static int Run(Func<ILovenseBleApi> createApi) => RunAsync(createApi).GetAwaiter().GetResult();
    private static async Task<int> RunAsync(Func<ILovenseBleApi> createApi)
    {
        // WinExe children have redirected pipes but no console. Changing Console.InputEncoding
        // calls SetConsoleCP on Windows and fails before readiness with an invalid console handle.
        using var inputReader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false),
            detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
        using var outputWriter = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false),
            bufferSize: 1024, leaveOpen: true) { AutoFlush = true };
        var writerLock = new object();
        void Report(BleWorkerMessage message)
        {
            lock (writerLock) outputWriter.WriteLine(JsonSerializer.Serialize(message));
        }
        ILovenseBleApi api;
        try { api = createApi(); }
        catch (Exception error) { Report(new("fault", Code: error is LovenseLibraryException ? 1 : 0)); return 1; }
        api.BluetoothStatus += code => Report(new("bluetooth", Code: code));
        api.ToyFound += (id, address, symbol) => Report(new("found", Id: id, Address: address, Symbol: symbol));
        api.ConnectionChanged += (id, connected) => Report(new("connection", Id: id, Connected: connected));
        api.Battery += (id, value) => Report(new("battery", Id: id, Text: value));
        api.Notification += (code, id) => Report(new("notify", Code: code, Id: id));
        using var lifetime = new CancellationTokenSource();
        var nativeGate = new SemaphoreSlim(1, 1);
        var active = new ConcurrentDictionary<(string Id, int Command), int>();
        var commands = Channel.CreateBounded<BleWorkerMessage>(8);
        long lastHeartbeat = Stopwatch.GetTimestamp();
        var revoked = 0;
        void StopOutputs(string? id = null)
        {
            foreach (var (key, value) in active.ToArray())
            {
                if (value == 0 || id is not null && key.Id != id) continue;
                active[key] = 0;
                try { api.SendLevel(key.Id, key.Command is 9 or 10 ? 6 : key.Command, 0); } catch (Exception) { }
            }
        }
        var input = Task.Run(async () =>
        {
            try
            {
                while (await inputReader.ReadLineAsync(lifetime.Token).ConfigureAwait(false) is { } line)
                {
                    if (line.Length > 16384) break;
                    var command = JsonSerializer.Deserialize<BleWorkerMessage>(line);
                    if (command is null || command.Id.Length > 4096) break;
                    if (command.Kind == "heartbeat") { Interlocked.Exchange(ref lastHeartbeat, Stopwatch.GetTimestamp()); continue; }
                    await commands.Writer.WriteAsync(command, lifetime.Token).ConfigureAwait(false);
                    if (command.Kind == "quit") return;
                }
            }
            catch (Exception) { }
            finally { Interlocked.Exchange(ref revoked, 1); commands.Writer.TryComplete(); }
        });
        _ = Task.Run(async () =>
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(100).ConfigureAwait(false);
                if (lifetime.IsCancellationRequested) return;
                if (!input.IsCompleted && Stopwatch.GetElapsedTime(Interlocked.Read(ref lastHeartbeat)) < TimeSpan.FromMilliseconds(1500)) continue;
                Interlocked.Exchange(ref revoked, 1);
                // Parent/pipe loss never retains output permission. A blocked DLL still defeats physical-stop guarantees.
                try
                {
                    await Task.Run(async () =>
                    {
                        await nativeGate.WaitAsync().ConfigureAwait(false);
                        try { StopOutputs(); api.Dispose(); } finally { nativeGate.Release(); }
                    }).WaitAsync(TimeSpan.FromMilliseconds(700)).ConfigureAwait(false);
                }
                catch (Exception) { }
                Environment.Exit(0); // Discards all vendor state, including a stuck native call.
            }
        });
        Report(new("ready", Code: 1));
        try
        {
            await foreach (var command in commands.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await nativeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    switch (command.Kind)
                    {
                        case "check": api.CheckBluetooth(); break;
                        case "scan": StopOutputs(); api.StartScan(); break;
                        case "stop": api.StopScan(); break;
                        case "connect": api.Connect(command.Id); break;
                        case "disconnect": StopOutputs(command.Id); api.Disconnect(command.Id); break;
                        case "battery": api.ReadBattery(command.Id); break;
                        case "level":
                            if (command.Value > 0 && Volatile.Read(ref revoked) != 0) throw new InvalidOperationException("Parent output gate closed.");
                            if (command.Code is < 0 or > 11 || command.Value < 0 || command.Value > (command.Code == 10 ? 100 : command.Code is 5 or 9 ? 3 : 20)) throw new InvalidDataException();
                            if (command.Value == 0 && command.Code == 6)
                                foreach (var key in active.Keys.Where(key => key.Id == command.Id && key.Command is 6 or 9 or 10)) active[key] = 0;
                            active[(command.Id, command.Code)] = command.Value;
                            api.SendLevel(command.Id, command.Code, command.Value); break;
                        case "quit": StopOutputs(); return 0;
                        default: throw new InvalidDataException();
                    }
                    Report(new("ack", Request: command.Request));
                }
                catch (Exception) { StopOutputs(); Report(new("fault", Request: command.Request)); return 1; }
                finally { nativeGate.Release(); }
            }
            return 0;
        }
        finally
        {
            // The parent kills this process if native cleanup does not finish in one second.
            await nativeGate.WaitAsync().ConfigureAwait(false);
            try { StopOutputs(); api.Dispose(); }
            finally { lifetime.Cancel(); nativeGate.Release(); }
        }
    }
}
