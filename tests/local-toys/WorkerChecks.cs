using System.Diagnostics;
using ShockWave.Overlay.Toys;

internal static partial class Program
{
    private static LovenseWorkerApi Worker(string mode = "normal", bool heartbeat = true, TimeSpan? timeout = null)
    {
        var info = LovenseWorkerApi.StartInfo("--ble-worker-fixture"); info.ArgumentList.Add(mode);
        return new(info, timeout, heartbeat);
    }
    private static LovenseDiscoveryAdapter WorkerAdapter(Func<ILovenseBleApi> create) => new(create,
        () => new LocalToyKeys(Enumerable.Repeat((byte)1, 32).ToArray()), scanDuration: TimeSpan.FromMilliseconds(70), responseTimeout: TimeSpan.FromSeconds(2));
    private static bool Exited(int id) { try { return Process.GetProcessById(id).HasExited; } catch (ArgumentException) { return true; } }

    private static async Task WorkerCallbacks()
    {
        using var worker = Worker();
        int? status = null; string? id = null, battery = null; var connected = false;
        var notifications = new System.Collections.Concurrent.ConcurrentQueue<int>();
        worker.BluetoothStatus += value => status = value; worker.ToyFound += (value, _, _) => id = value;
        worker.ConnectionChanged += (_, value) => connected = value; worker.Battery += (_, value) => battery = value;
        worker.Notification += (value, _) => notifications.Enqueue(value);
        worker.CheckBluetooth(); worker.StartScan(); await Until(() => status == 0 && id is not null);
        worker.StopScan(); worker.Connect(id!); await Until(() => connected);
        worker.ReadBattery(id!); await Until(() => battery == "72");
        worker.SendLevel(id!, 1, 20); worker.SendLevel(id!, 1, 0);
        await Until(() => notifications.Contains(220) && notifications.Contains(200));
        try { worker.SendLevel(id!, 1, 21); throw new Exception("Invalid scalar passed worker validation."); } catch (ArgumentOutOfRangeException) { }
        var processId = worker.WorkerProcessId; worker.Dispose(); await Until(() => Exited(processId)); worker.Dispose();
    }

    private static async Task WorkerPowerCycles()
    {
        LovenseWorkerApi? current = null;
        await using var adapter = new RememberedToyAdapter(WorkerAdapter(() => current = Worker("persistent")), new MemoryToyStore(),
            TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(10));
        await adapter.StartScanAsync(); var key = One(adapter).DeviceKey; await adapter.ConnectAsync(key);
        await using var controller = new LocalToyController(adapter, new());
        for (var cycle = 0; cycle < 3; cycle++)
        {
            controller.Arm(key); controller.Request(key, "v1", 50);
            var oldProcess = current!.WorkerProcessId;
            // Trigger this synthetic fault explicitly; ordinary automatic battery
            // reads must leave the fixture connected like a healthy real toy.
            current.ReadBattery("fixture-power-loss");
            await Until(() => One(adapter).ConnectionState == ToyConnectionState.Disconnected && !controller.State(key, "v1").Armed);
            Assert(One(adapter).ConnectionState == ToyConnectionState.Disconnected && !controller.State(key, "v1").Armed, "Power loss retained connection/arming.");
            if (cycle == 0) await adapter.ConnectAsync(key);
            else
            {
                await adapter.SetAutoConnectAsync(key, true);
                await Until(() => One(adapter).ConnectionState == ToyConnectionState.Connected);
            }
            Assert(current.WorkerProcessId != oldProcess && Exited(oldProcess), "Power-cycle recovery reused the poisoned worker process.");
            Assert(!controller.State(key, "v1").Armed && controller.State(key, "v1").RequestedPercent == 0, "Fresh native process restored output intent.");
            await adapter.SetAutoConnectAsync(key, false);
        }
    }

    private static async Task WorkerCrashRecovery()
    {
        LovenseWorkerApi? current = null;
        await using var adapter = WorkerAdapter(() => current = Worker());
        await adapter.StartScanAsync(); var key = One(adapter).DeviceKey; await adapter.ConnectAsync(key);
        await using var controller = new LocalToyController(adapter, new()); controller.Arm(key); controller.Request(key, "v1", 40);
        var oldProcess = current!.WorkerProcessId; using (var process = Process.GetProcessById(oldProcess)) process.Kill();
        await Until(() => adapter.Snapshot.Availability == ToyAdapterAvailability.Error && !controller.State(key, "v1").Armed);
        await adapter.StartScanAsync(); await adapter.ConnectAsync(key);
        Assert(current.WorkerProcessId != oldProcess && One(adapter).ConnectionState == ToyConnectionState.Connected && !controller.State(key, "v1").Armed,
            "Worker crash did not recover with a fresh, unarmed connection.");
    }

    private static async Task WorkerHeartbeatExpiry()
    {
        using var worker = Worker(heartbeat: false); string? id = null;
        var stopped = false; worker.ToyFound += (value, _, _) => id = value;
        worker.Notification += (code, _) => { if (code == 200) stopped = true; };
        worker.StartScan(); await Until(() => id is not null); worker.StopScan(); worker.Connect(id!); worker.SendLevel(id!, 1, 20);
        var processId = worker.WorkerProcessId;
        await Until(() => stopped && Exited(processId));
        Assert(stopped, "Lost parent heartbeat did not issue fixture zero before exiting.");
    }

    private static Task WorkerHungCall()
    {
        using var worker = Worker("hang-write", timeout: TimeSpan.FromMilliseconds(350)); string? id = null;
        worker.ToyFound += (value, _, _) => id = value;
        worker.StartScan(); worker.StopScan(); worker.Connect(id!);
        var processId = worker.WorkerProcessId; var elapsed = Stopwatch.StartNew();
        try { worker.SendLevel(id!, 1, 20); throw new Exception("Blocked native write completed."); } catch (InvalidOperationException) { }
        Assert(elapsed.Elapsed < TimeSpan.FromSeconds(2) && Exited(processId), "Blocked native worker was not discarded within a bounded time.");
        try { worker.SendLevel(id!, 1, 20); throw new Exception("A dead worker accepted replay."); } catch (InvalidOperationException) { }
        return Task.CompletedTask;
    }

    private static Task WorkerExitDuringCall()
    {
        LovenseWorkerApi? worker=null;
        var adapter=new RememberedToyAdapter(WorkerAdapter(()=>worker=Worker("hang-write",timeout:TimeSpan.FromSeconds(5))),
            new MemoryToyStore(),retryInterval:TimeSpan.FromSeconds(1),pollInterval:TimeSpan.FromMilliseconds(20));
        using var page=new ShockWave.Overlay.LocalToysPage(_=>adapter); _=page.Handle;
        page.StartRememberedConnections();
        PumpUntil(()=>!page.Busy && page.Controller is not null && adapter.Snapshot.Devices.Any(d=>d.ConnectionState==ToyConnectionState.Connected));
        using var entered=new ManualResetEventSlim(); worker!.Notification+=(code,_)=> { if(code==399) entered.Set(); };
        var key=One(adapter).DeviceKey; page.SetTestMode(false); page.Controller!.Request(key,"v1",80);
        PumpUntil(()=>entered.IsSet);
        var processId=worker.WorkerProcessId; var elapsed=Stopwatch.StartNew(); var shutdown=page.ShutdownAsync();
        Assert(!page.Controller.State(key,"v1").Armed && page.Controller.State(key,"v1").RequestedPercent==0,
            "Exit released the helper before closing the owner output gate.");
        PumpUntil(()=>shutdown.IsCompleted); shutdown.GetAwaiter().GetResult();
        Assert(elapsed.Elapsed<TimeSpan.FromSeconds(2.5) && Exited(processId),
            "Exit waited for the normal five-second command timeout or left its worker alive.");
        try { page.Controller.Request(key,"v1",80); throw new Exception("Closed controller accepted replay."); }
        catch(InvalidOperationException) { }
        page.ShutdownAsync().GetAwaiter().GetResult(); return Task.CompletedTask;
    }

    private static async Task WorkerPipeClosure()
    {
        var info = LovenseWorkerApi.StartInfo("--ble-worker-fixture"); info.ArgumentList.Add("normal");
        using var process = new Process { StartInfo = info }; process.Start();
        try
        {
            async Task<BleWorkerMessage> Read() => System.Text.Json.JsonSerializer.Deserialize<BleWorkerMessage>(
                (await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)))!)!;
            Assert((await Read()).Kind == "ready", "Child fixture did not initialize.");
            var id = "worker-fixture-" + process.Id;
            await process.StandardInput.WriteLineAsync(System.Text.Json.JsonSerializer.Serialize(new BleWorkerMessage("level", 1, 1, id, Value: 20)));
            await process.StandardInput.FlushAsync();
            while ((await Read()).Code != 220) { }
            process.StandardInput.Close();
            var stopped = false;
            while (await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(3)) is { } line)
                if (System.Text.Json.JsonSerializer.Deserialize<BleWorkerMessage>(line)?.Code == 200) stopped = true;
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3));
            Assert(stopped, "Closing the parent pipe left fixture output active.");
        }
        finally { if (!process.HasExited) process.Kill(); }
    }

    private static Task WorkerStartupAndCleanup()
    {
        try { using var missing = Worker("missing"); throw new Exception("Worker startup failure was hidden."); } catch (LovenseLibraryException) { }
        var worker = Worker("hang-cleanup"); var processId = worker.WorkerProcessId; var elapsed = Stopwatch.StartNew();
        worker.Dispose(); Assert(elapsed.Elapsed < TimeSpan.FromSeconds(3) && Exited(processId), "Native cleanup hung instead of ending the worker.");
        return Task.CompletedTask;
    }

    private static Task WorkerStartupDiagnostics()
    {
        var missingExe = LovenseWorkerApi.StartInfo("--ble-worker-fixture");
        missingExe.FileName = Path.Combine(AppContext.BaseDirectory, "missing-worker-" + Guid.NewGuid() + ".exe");
        try { using var worker = new LovenseWorkerApi(missingExe); throw new Exception("Missing EXE started."); }
        catch (LovenseLibraryException error) { Assert(error.Message.Contains("Windows error 2"), "Launch failure lost its Windows error code."); }

        try { using var worker = Worker("exit-startup"); throw new Exception("Exited worker became ready."); }
        catch (LovenseLibraryException error) { Assert(error.Message.Contains("exited during startup") && error.Message.Contains("37"), "Premature exit lost its startup stage/exit code."); }

        var elapsed = Stopwatch.StartNew();
        try { using var worker = Worker("hang-startup"); throw new Exception("Hung startup became ready."); }
        catch (LovenseLibraryException error) { Assert(error.Message.Contains("startup timed out"), "Hung startup did not identify timeout."); }
        Assert(elapsed.Elapsed < TimeSpan.FromSeconds(8), "Startup timeout did not dispose the blocked worker promptly.");
        return Task.CompletedTask;
    }
}

// Process-scoped poisoned state reproduces why rebuilding a wrapper can fail while a restart succeeds.
internal sealed class WorkerFixtureApi : ILovenseBleApi
{
    private readonly FakeBle inner = new();
    private readonly string mode;
    private static bool poisoned;
    public WorkerFixtureApi(string mode)
    {
        if (mode == "exit-startup") Environment.Exit(37);
        if (mode == "hang-startup") Thread.Sleep(Timeout.Infinite);
        if (mode == "missing") throw new LovenseLibraryException("fixture missing library");
        this.mode = mode;
        inner.OnScan = () => inner.Found("worker-fixture-" + Environment.ProcessId);
        inner.OnOutput = value => inner.Notify(200 + value, "worker-fixture-" + Environment.ProcessId);
    }
    public event Action<int>? BluetoothStatus { add => inner.BluetoothStatus += value; remove => inner.BluetoothStatus -= value; }
    public event Action<string, string, string>? ToyFound { add => inner.ToyFound += value; remove => inner.ToyFound -= value; }
    public event Action<string, bool>? ConnectionChanged { add => inner.ConnectionChanged += value; remove => inner.ConnectionChanged -= value; }
    public event Action<string, string>? Battery { add => inner.Battery += value; remove => inner.Battery -= value; }
    public event Action<int, string>? Notification { add => inner.Notification += value; remove => inner.Notification -= value; }
    public void CheckBluetooth() => inner.CheckBluetooth();
    public void StartScan() => inner.StartScan();
    public void StopScan() => inner.StopScan();
    public void Connect(string id) { if (poisoned) inner.Notify(8, id); else inner.Connect(id); }
    public void Disconnect(string id) => inner.Disconnect(id);
    public void ReadBattery(string id)
    {
        if (mode == "slow-battery") { inner.Notify(301, id); Thread.Sleep(350); }
        if (mode == "persistent" && id == "fixture-power-loss")
        { poisoned = true; inner.Connection("worker-fixture-" + Environment.ProcessId, false); }
        else inner.ReadBattery(id);
    }
    public void SendLevel(string id, int command, int value)
    {
        if (mode == "hang-write" && value > 0) { inner.Notify(399,id); Thread.Sleep(Timeout.Infinite); }
        inner.SendLevel(id, command, value);
    }
    public void Dispose() { if (mode == "hang-cleanup") Thread.Sleep(Timeout.Infinite); inner.Dispose(); }
}
