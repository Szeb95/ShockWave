using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ShockWave.Overlay;

internal sealed class RemoteWorker : IDisposable
{
    private readonly Process process;
    private readonly SafeFileHandle job;
    private readonly Channel<string> writes = Channel.CreateBounded<string>(64);
    private readonly ConcurrentQueue<JsonElement> events = new();
    private JsonElement? latestState;
    private readonly object sync = new();
    private bool disposed;
    public bool Failed { get; private set; }
    public RemoteWorker(string node, string directory)
    {
        var start = new ProcessStartInfo(node) { WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(directory, "remote-worker.mjs"));
        start.Environment.Remove("OPENSHOCK_TOKEN"); start.Environment.Remove("PISHOCK_API_KEY");
        process = new() { StartInfo = start, EnableRaisingEvents = true };
        process.Exited += (_, _) => Failed = true;
        process.Start();
        try { job = OwnProcess(process); }
        catch { try { process.Kill(true); } catch { } process.Dispose(); throw; }
        _ = Task.Run(async () =>
        {
            try { await foreach (var line in writes.Reader.ReadAllAsync()) await process.StandardInput.WriteLineAsync(line); }
            catch (Exception) { Failed = true; }
        });
        _ = Task.Run(async () =>
        {
            try
            {
                while (await process.StandardOutput.ReadLineAsync() is { } line)
                {
                    if (line.Length > 65000) { Failed = true; break; }
                    using var doc = JsonDocument.Parse(line); var value = doc.RootElement.Clone();
                    if (value.GetProperty("kind").GetString() == "message" && value.GetProperty("message").GetProperty("kind").GetString() == "state")
                        lock (sync) latestState = value;
                    else if (events.Count < 64) events.Enqueue(value);
                    else { Failed = true; break; }
                }
            }
            catch (Exception) { Failed = true; }
        });
        _ = Task.Run(async () => { try { while (await process.StandardError.ReadLineAsync() is not null) { } } catch { } });
    }
    public bool Send(object value)
    {
        if (disposed || Failed) return false;
        if (writes.Writer.TryWrite(JsonSerializer.Serialize(value, RemoteSession.Json))) return true;
        Failed = true; return false;
    }
    public IEnumerable<JsonElement> Drain()
    {
        while (events.TryDequeue(out var value)) yield return value;
        JsonElement? state; lock (sync) { state = latestState; latestState = null; }
        if (state is { } newest) yield return newest;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true; writes.Writer.TryComplete();
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        process.Dispose();
        job.Dispose();
    }
    private static SafeFileHandle OwnProcess(Process child)
    {
        var handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new IOException("Could not own connection helper");
        var limits = new JobLimits { Basic = new BasicLimits { Flags = 0x2000 } };
        if (!SetInformationJobObject(handle, 9, ref limits, (uint)Marshal.SizeOf<JobLimits>()) || !AssignProcessToJobObject(handle, child.Handle))
        { handle.Dispose(); throw new IOException("Could not own connection helper"); }
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
