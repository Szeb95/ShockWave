using System.Diagnostics;

namespace ShockWave.Overlay.Toys;

internal sealed record ToyChannelState(string DeviceKey, string ChannelId, ToyControlPreference Preference,
    int RequestedPercent, double SentPercent, bool Armed, bool Active, string Detail)
{
    public double WaitMilliseconds { get; init; }
    public double SubmitMilliseconds { get; init; }
    public Guid InputSource { get; init; }
    public long InputGeneration { get; init; }
    public double OwnershipChangedAt { get; init; }
    public bool Stopping { get; init; }
}

internal sealed record ToyInputLease(string DeviceKey, string ChannelId, Guid Source, long Generation);

internal sealed record ToySchedulerState(int PendingFunctions, int SubmittingFunctions, int ActiveFunctions, double PerToyTarget,
    double SharedTarget, long Submitted, long Replaced, long CanceledBeforeSubmit,
    double LastWaitMilliseconds, double LastSubmitMilliseconds);

// The owner output gate. Remote sessions use expiring input leases; no restored
// activity or physical command retries are allowed.
internal sealed class LocalToyController : IAsyncDisposable, IToyInputController
{
    private sealed class Channel(ToyControlPreference preference)
    {
        public ToyControlPreference Preference = preference;
        public int Requested, SentNative, InFlightNative;
        public bool Active, Pending, StopPending, StopInFlight, WriteInFlight;
        public bool StopUnconfirmed;
        public long Deadline;
        public Guid InputSource;
        public long InputGeneration, SourceExpires, OwnershipChangedAt;
        public long LastSent, InputAt, Revision;
        public double WaitMilliseconds, SubmitMilliseconds;
        public string Detail = "Off";
    }
    private readonly object sync = new();
    private readonly ILocalToyAdapter adapter;
    private readonly ILocalToyOutput output;
    private readonly LocalToyControlStore store;
    private readonly Dictionary<(string Device, string Channel), Channel> channels = [];
    private readonly HashSet<string> armed = new(StringComparer.Ordinal);
    private readonly HashSet<string> failedDevices = new(StringComparer.Ordinal);
    private readonly bool automaticControls;
    private readonly Dictionary<string, long> nextSend = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> lastDeviceStart = new(StringComparer.Ordinal), deviceTurn = new(StringComparer.Ordinal);
    private readonly HashSet<string> batchDevices = new(StringComparer.Ordinal);
    private readonly Queue<long> recentSubmissions = new();
    private readonly Dictionary<string, ToyConnectionState> connections = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim wake = new(0, 1);
    private readonly Task worker;
    private Task? disposeTask;
    private bool testMode = true, disposed;
    private double perToyTarget = 5, lastWaitMilliseconds, lastSubmitMilliseconds;
    private long nextAdapterSend, turn, submitted, replaced, canceledBeforeSubmit;
    private const double SharedMaximum = 20;
    private const int MaximumBatchToys = 4;
    public event Action? Changed;
    public bool TestMode { get { lock (sync) return testMode; } }
    // Check current connected output, including submissions and fresh requests.
    // Offline history and test-mode preview percentages are not active connections.
    public bool HasPotentialOutput
    {
        get
        {
            lock (sync) return channels.Any(pair =>
            {
                var device = Device(pair.Key.Device);
                var cap = device?.Capabilities.FirstOrDefault(cap => cap.ChannelId == pair.Key.Channel);
                if (device?.ConnectionState != ToyConnectionState.Connected || device.IsSimulated || cap?.IsSetting == true) return false;
                var state = pair.Value;
                return state.StopUnconfirmed || state.SentNative > 0 || state.WriteInFlight && state.InFlightNative > 0 ||
                    state.StopPending || state.StopInFlight || !testMode && state.Active && cap is not null && NativeValue(state, cap) > 0;
            });
        }
    }
    public string? StorageWarning => store.Warning;

    public LocalToyController(ILocalToyAdapter adapter, LocalToyControlStore store, bool automaticControls = false)
    {
        this.adapter = adapter; this.automaticControls = automaticControls;
        output = adapter as ILocalToyOutput ?? throw new InvalidOperationException("Toy output is unavailable.");
        this.store = store; store.Load();
        adapter.Changed += InventoryChanged; output.OutputFailed += OutputFailed;
        InventoryChanged();
        worker = Task.Run(WorkAsync);
    }

    private static long Now => Stopwatch.GetTimestamp();
    private static long Ticks(double seconds) => (long)(seconds * Stopwatch.Frequency);
    private void Publish(bool wakeWorker = true)
    {
        if (wakeWorker && !lifetime.IsCancellationRequested)
            try { if (wake.CurrentCount == 0) wake.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }
        try { Changed?.Invoke(); } catch (Exception) { }
    }
    public ToySchedulerState SchedulerState
    {
        get
        {
            lock (sync) return new(channels.Values.Count(c => c.Pending), channels.Values.Count(c => c.WriteInFlight), channels.Values.Count(c => c.Active),
                perToyTarget, SharedMaximum, submitted, replaced, canceledBeforeSubmit, lastWaitMilliseconds, lastSubmitMilliseconds);
        }
    }
    public void SetUpdateRate(double rate)
    {
        if (!double.IsFinite(rate) || rate is < 1 or > 10) throw new ArgumentOutOfRangeException(nameof(rate));
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            perToyTarget = rate;
            foreach (var (key, started) in lastDeviceStart)
                if (Device(key) is { } device) nextSend[key] = started + Ticks(1 / Math.Min(rate, device.ConservativeCommandsPerSecond));
        }
        Publish();
    }
    private LocalToyDescriptor? Device(string key) => adapter.Snapshot.Devices.FirstOrDefault(d => d.DeviceKey == key);
    private bool ScanBlocksOutput => adapter.Snapshot is { IsScanning: true, SupportsOutputWhileScanning: false };
    private ToyCapability Capability(string device, string channel) => Device(device)?.Capabilities.FirstOrDefault(c => c.ChannelId == channel)
        ?? throw new InvalidOperationException("This toy function is unavailable.");
    private Channel Get(string device, string channel)
    {
        if (!channels.TryGetValue((device, channel), out var state))
        {
            _ = Capability(device, channel);
            channels[(device, channel)] = state = new(store.Get(device, channel));
        }
        return state;
    }

    public ToyChannelState State(string device, string channel)
    {
        lock (sync)
        {
            var state = Get(device, channel); var cap = Capability(device, channel);
            return new(device, channel, state.Preference, state.Requested,
                (testMode && state.Active ? NativeValue(state, cap) : state.SentNative) * 100d / (cap.NativeMaximum ?? 1), armed.Contains(device), state.Active, state.Detail)
            {
                WaitMilliseconds = state.Pending ? Stopwatch.GetElapsedTime(state.InputAt).TotalMilliseconds : state.WaitMilliseconds,
                SubmitMilliseconds = state.SubmitMilliseconds,
                InputSource = state.InputSource, InputGeneration = state.InputGeneration,
                OwnershipChangedAt = state.OwnershipChangedAt / (double)Stopwatch.Frequency,
                Stopping = state.StopPending || state.StopInFlight,
            };
        }
    }

    public void SetTestMode(bool enabled)
    {
        lock (sync)
        {
            if (testMode == enabled) return;
            DisarmCore(null, enabled ? "Test mode · output blocked" : automaticControls ? "Ready · waiting for input" : "Enable controls before starting");
            testMode = enabled;
            RefreshAutomaticControlsCore();
        }
        Publish();
    }

    public void Arm(string key)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var device = Device(key);
            if (device?.ConnectionState != ToyConnectionState.Connected || ScanBlocksOutput)
                throw new InvalidOperationException("Connect the toy and stop scanning first.");
            if (!device.CapabilitiesKnown || device.Capabilities.Count == 0)
                throw new InvalidOperationException("This toy's functions are unknown.");
            if (channels.Any(pair => pair.Key.Device == key && (pair.Value.StopPending || pair.Value.StopInFlight)))
                throw new InvalidOperationException("Wait for the stop request to finish before enabling controls.");
            armed.Add(key);
        }
        Publish();
    }

    public void SetPreference(ToyControlPreference preference)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            var state = Get(preference.DeviceKey, preference.ChannelId);
            try { store.Save(preference); }
            catch (Exception)
            {
                failedDevices.Add(preference.DeviceKey);
                DisarmCore(preference.DeviceKey, "Settings were not saved · output stopped");
                Publish();
                throw;
            }
            state.Preference = preference;
            if (state.Active)
            {
                if (state.Pending || state.WriteInFlight) replaced++;
                state.Revision++; state.InputAt = Now;
                if (NativeValue(state, Capability(preference.DeviceKey, preference.ChannelId)) == 0)
                    StopCore(preference.DeviceKey, preference.ChannelId, "Off");
                else state.Pending = true; // Re-scale the latest input, preserving its original expiry.
            }
        }
        Publish();
    }

    public void Request(string device, string channel, int percent)
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
            ReleaseSource(Get(device, channel));
            RequestCore(device, channel, percent, allowPendingStop: true);
        }
        Publish();
    }

    // A local controller claims a function only on fresh touch/Play. Renewals
    // cannot take it back after an owner edit, Stop, reset or expired lease.
    public ToyInputLease BeginInput(string device, string channel, Guid source, int percent, TimeSpan lifetime)
    {
        if (source == Guid.Empty || lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromSeconds(2)) throw new ArgumentOutOfRangeException();
        ToyInputLease lease;
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            RefreshAutomaticControlsCore();
            if (!armed.Contains(device) || ScanBlocksOutput) throw new InvalidOperationException("Toy controls are unavailable.");
            var state = Get(device, channel);
            RequestCore(device, channel, percent, allowPendingStop: state.InputSource == source && Now < state.SourceExpires);
            state.InputSource = source; state.InputGeneration++; state.OwnershipChangedAt = Now; state.SourceExpires = Now + Ticks(lifetime.TotalSeconds);
            if (state.Active) state.Deadline = state.SourceExpires;
            lease = new(device, channel, source, state.InputGeneration);
        }
        Publish(); return lease;
    }
    public bool UpdateInput(ToyInputLease lease, int percent, TimeSpan lifetime)
    {
        if (lifetime <= TimeSpan.Zero || lifetime > TimeSpan.FromSeconds(2)) throw new ArgumentOutOfRangeException(nameof(lifetime));
        lock (sync)
        {
            if (disposed || !channels.TryGetValue((lease.DeviceKey, lease.ChannelId), out var state) ||
                state.InputSource != lease.Source || state.InputGeneration != lease.Generation || Now >= state.SourceExpires) return false;
            try { RequestCore(lease.DeviceKey, lease.ChannelId, percent, allowPendingStop: true, renewal: true); }
            catch (InvalidOperationException) { ReleaseSource(state); StopCore(lease.DeviceKey, lease.ChannelId, "Controller unavailable · off"); return false; }
            state.SourceExpires = Now + Ticks(lifetime.TotalSeconds);
            if (state.Active) state.Deadline = state.SourceExpires;
        }
        Publish(); return true;
    }
    public void EndInput(ToyInputLease lease)
    {
        lock (sync)
            if (!disposed && channels.TryGetValue((lease.DeviceKey, lease.ChannelId), out var state) &&
                state.InputSource == lease.Source && state.InputGeneration == lease.Generation)
            { ReleaseSource(state); StopCore(lease.DeviceKey, lease.ChannelId, "Controller stopped · fresh input required"); }
        Publish();
    }
    private static void ReleaseSource(Channel state) { state.InputSource = Guid.Empty; state.InputGeneration++; state.OwnershipChangedAt = Now; state.SourceExpires = 0; }
    private void RequestCore(string device, string channel, int percent, bool allowPendingStop = false, bool renewal = false)
    {
            RefreshAutomaticControlsCore();
            if (percent is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(percent));
            var state = Get(device, channel);
            // Renewing a held input extends its lease below; it is not a new
            // write. Do not cancel an identical submission already waiting on
            // the adapter/worker lock. New values and owner requests still
            // replace pending work through Revision and the transport guard.
            if (renewal && percent == state.Requested &&
                (percent == 0 && !state.Active || state.Active && armed.Contains(device) &&
                 (!automaticControls || !failedDevices.Contains(device)) &&
                 Device(device)?.ConnectionState == ToyConnectionState.Connected && !ScanBlocksOutput &&
                 Capability(device, channel).NativeMaximum is not null)) return;
            if (state.Pending || state.WriteInFlight) replaced++;
            state.Revision++; state.InputAt = Now;
            if (percent == 0) { StopCore(device, channel, "Off"); }
            else
            {
                if (!armed.Contains(device) || automaticControls && failedDevices.Contains(device) || Device(device)?.ConnectionState != ToyConnectionState.Connected || ScanBlocksOutput)
                    throw new InvalidOperationException(automaticControls ? "Toy controls are unavailable. Wait for connection/Stop or select Reset Bluetooth after an error." : "Enable this toy's controls first.");
                var cap = Capability(device, channel);
                if (cap.NativeMaximum is null) throw new InvalidOperationException("This function's range is unknown.");
                // A zero within the same live source is a curve value, not an
                // owner Stop. Keep only its newest rise while the zero finishes;
                // SendNext still waits for all required zeros. Owner Stop/edit,
                // disconnect and expiry invalidate the source before this gate.
                // A fresh owner edit can replace that waiting value, but an
                // explicit Stop disarms first and still fails the armed check.
                if ((state.StopPending || state.StopInFlight) && !allowPendingStop) throw new InvalidOperationException("Wait for the stop request to finish.");
                state.Requested = percent;
                if (NativeValue(state, cap) == 0) StopCore(device, channel, "Off");
                else
                {
                    state.Active = state.Pending = true;
                    state.Deadline = automaticControls ? long.MaxValue : Now + Ticks(state.Preference.DurationMs / 1000d);
                    state.Detail = testMode ? "Test preview · no hardware output" : "Queued";
                }
            }
    }

    private static int NativeValue(Channel state, ToyCapability capability)
    {
        var percent = Math.Min(state.Requested * state.Preference.Sensitivity / 100d, state.Preference.Maximum);
        // Floor quantization guarantees the native result never exceeds the owner's cap.
        return (int)Math.Floor(percent * (capability.NativeMaximum ?? 0) / 100d + 1e-9);
    }

    private void StopCore(string device, string channel, string reason)
    {
        var state = Get(device, channel);
        state.Revision++; state.InputAt = Now;
        state.StopPending |= state.SentNative > 0 || state.WriteInFlight && state.InFlightNative > 0;
        state.Active = state.Pending = false; state.Requested = 0; state.Deadline = 0; state.Detail = reason;
        if (channel is "t" or "d" or "pos")
        {
            foreach (var related in new[] { "t", "d", "pos" }.Where(c => c != channel))
            {
                var motion = channels.GetValueOrDefault((device, related));
                if (motion is null) continue;
                ReleaseSource(motion);
                motion.Revision++;
                motion.StopPending |= motion.SentNative > 0 || motion.WriteInFlight && motion.InFlightNative > 0;
                motion.Active = motion.Pending = false; motion.Requested = 0; motion.Deadline = 0; motion.Detail = reason;
            }
        }
    }

    public void Stop(string? device = null)
    {
        lock (sync)
        {
            DisarmCore(device, automaticControls ? "Stopped · waiting for fresh input" : "Stopped · enable controls to restart");
            RefreshAutomaticControlsCore();
        }
        Publish();
    }
    private void DisarmCore(string? device, string reason)
    {
        if (device is null) armed.Clear(); else armed.Remove(device);
        foreach (var key in channels.Keys.ToArray())
            if (device is null || key.Device == device) { ReleaseSource(channels[key]); StopCore(key.Device, key.Channel, reason); }
    }

    private void InventoryChanged()
    {
        lock (sync)
        {
            var snapshot = adapter.Snapshot;
            foreach (var device in snapshot.Devices)
            {
                foreach (var cap in device.Capabilities) Get(device.DeviceKey, cap.ChannelId);
                if (device.ConnectionState == ToyConnectionState.Connected &&
                    connections.GetValueOrDefault(device.DeviceKey) != ToyConnectionState.Connected) failedDevices.Remove(device.DeviceKey);
                if (device.ConnectionState != ToyConnectionState.Connected ||
                    connections.GetValueOrDefault(device.DeviceKey) != ToyConnectionState.Connected || snapshot.IsScanning && !snapshot.SupportsOutputWhileScanning)
                    DisarmCore(device.DeviceKey, automaticControls ? "Waiting for connection/input" : "Enable controls after connecting / scanning");
                connections[device.DeviceKey] = device.ConnectionState;
            }
            foreach (var key in connections.Keys.Except(snapshot.Devices.Select(d => d.DeviceKey)).ToArray())
            {
                armed.Remove(key); connections.Remove(key);
                foreach (var pair in channels.Where(p => p.Key.Device == key))
                {
                    pair.Value.StopUnconfirmed |= pair.Value.SentNative > 0 || pair.Value.WriteInFlight && pair.Value.InFlightNative > 0;
                    ReleaseSource(pair.Value); pair.Value.Active = pair.Value.Pending = pair.Value.StopPending = false; pair.Value.SentNative = pair.Value.Requested = 0;
                }
            }
            RefreshAutomaticControlsCore();
        }
        Publish();
    }

    private void OutputFailed(string device)
    {
        // Vendor notifications have no channel/request ID. Fail the entire toy closed, never guess an acknowledgement.
        lock (sync) { failedDevices.Add(device); DisarmCore(device, "Native command failed · Reset Bluetooth; physical stop unconfirmed"); }
        Publish();
    }

    private bool RefreshAutomaticControlsCore()
    {
        if (!automaticControls || disposed || store.Warning is not null || ScanBlocksOutput) return false;
        var changed = false;
        foreach (var device in adapter.Snapshot.Devices.Where(device => device.ConnectionState == ToyConnectionState.Connected &&
            device.CapabilitiesKnown && device.Capabilities.Count > 0 && !failedDevices.Contains(device.DeviceKey)))
        {
            if (channels.Any(pair => pair.Key.Device == device.DeviceKey && (pair.Value.StopPending || pair.Value.StopInFlight))) continue;
            if (!armed.Add(device.DeviceKey)) continue;
            changed = true;
            foreach (var pair in channels.Where(pair => pair.Key.Device == device.DeviceKey && !pair.Value.Active))
                pair.Value.Detail = "Ready · waiting for input";
        }
        return changed;
    }

    private async Task WorkAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var changed = false;
                lock (sync)
                {
                    foreach (var (key, state) in channels.ToArray())
                    {
                        if (state.InputSource != Guid.Empty && Now >= state.SourceExpires)
                        { ReleaseSource(state); StopCore(key.Device, key.Channel, "Controller heartbeat expired · off"); changed = true; }
                        else if (state.Active && Now >= state.Deadline) { StopCore(key.Device, key.Channel, "Duration expired · off"); changed = true; }
                    }
                    foreach (var (key, state) in channels)
                    {
                        if (!state.Pending || !state.Active || !armed.Contains(key.Device)) continue;
                        if (testMode || NativeValue(state, Capability(key.Device, key.Channel)) == state.SentNative)
                        {
                            // Preview and unchanged native steps consume no Bluetooth budget.
                            state.Pending = false;
                            state.Detail = testMode ? "Test preview · no hardware output" : "Sent · unchanged native step";
                            changed = true;
                        }
                    }
                }
                // One ordinary write per pass. Revisit expiry and all zeros before selecting another.
                var stopped = SendStops();
                if (stopped) lock (sync) RefreshAutomaticControlsCore();
                changed |= stopped;
                var attempted = SendNext();
                if (changed || attempted) Publish(wakeWorker: false);
                if (!attempted) await wake.WaitAsync(TimeSpan.FromMilliseconds(10), lifetime.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            lock (sync)
            {
                DisarmCore(null, "Output controller failed · controls paused");
                disposed = true;
            }
            SendStops();
            Publish();
        }
    }

    private bool SendNext()
    {
        (string Device, string Channel) key = (string.Empty, string.Empty);
        Channel? state = null;
        int native = 0;
        long revision = 0, inputAt = 0, checkedAt = 0;
        lock (sync)
        {
            if (disposed || testMode || channels.Values.Any(c => c.StopPending || c.StopInFlight)) return false;
            var now = Now;
            while (recentSubmissions.TryPeek(out var sentAt) && now-sentAt >= Ticks(1)) recentSubmissions.Dequeue();
            if (recentSubmissions.Count >= SharedMaximum) return false;
            if (now >= nextAdapterSend) batchDevices.Clear();
            if (batchDevices.Count >= MaximumBatchToys) return false;
            var groups = channels.Where(p => p.Value.Pending && p.Value.Active && armed.Contains(p.Key.Device))
                .GroupBy(p => p.Key.Device).OrderBy(g => deviceTurn.GetValueOrDefault(g.Key));
            foreach (var group in groups)
            {
                if (batchDevices.Contains(group.Key) || now < nextSend.GetValueOrDefault(group.Key)) continue;
                if (Device(group.Key)?.ConnectionState != ToyConnectionState.Connected || ScanBlocksOutput)
                { DisarmCore(group.Key, "Disconnected / scanning · off"); continue; }
                var candidate = group.Where(p => !p.Value.StopPending && Now < p.Value.Deadline)
                    .OrderBy(p => p.Value.LastSent).FirstOrDefault();
                if (candidate.Value is null) continue;
                key = candidate.Key; state = candidate.Value;
                native = NativeValue(state, Capability(key.Device, key.Channel));
                // A new request can arrive after WorkAsync's unchanged-value
                // scan. Recheck here so it cannot consume a grouped slot for
                // an identical native step.
                if (native == state.SentNative)
                {
                    state.Pending = false; state.Detail = "Sent · unchanged native step";
                    return true;
                }
                revision = state.Revision; inputAt = state.InputAt;
                state.Pending = false; state.WriteInFlight = true; state.InFlightNative = native;
                break;
            }
        }
        if (state is null) return false;
        bool IsCurrent()
        {
            lock (sync)
            {
                checkedAt = Now;
                return !disposed && !testMode && state.Active && state.Revision == revision && armed.Contains(key.Device) &&
                    !state.StopPending && !state.StopInFlight && checkedAt < state.Deadline;
            }
        }
        try
        {
            // Guards run again after adapter and worker locks, immediately before pipe submission.
            if (!output.TrySendLevel(key.Device, key.Channel, native, IsCurrent))
            {
                lock (sync) canceledBeforeSubmit++;
                return true;
            }
            var finished = Now;
            lock (sync)
            {
                state.LastSent = checkedAt;
                state.SentNative = native;
                state.WaitMilliseconds = lastWaitMilliseconds = Stopwatch.GetElapsedTime(inputAt, checkedAt).TotalMilliseconds;
                state.SubmitMilliseconds = lastSubmitMilliseconds = Stopwatch.GetElapsedTime(checkedAt, finished).TotalMilliseconds;
                // Up to four different ready toys share a short group of serial
                // calls. Each consumes its shared allowance from submission,
                // not completion; time spent inside the call already counts.
                // The next group waits for that allowance, with no idle credit.
                // A rolling window also caps successful normal writes at 20/s.
                batchDevices.Add(key.Device);
                recentSubmissions.Enqueue(checkedAt);
                nextAdapterSend = Math.Max(nextAdapterSend, checkedAt) + Ticks(1 / SharedMaximum);
                lastDeviceStart[key.Device] = checkedAt;
                var rate = Math.Min(perToyTarget, Device(key.Device)?.ConservativeCommandsPerSecond ?? perToyTarget);
                nextSend[key.Device] = checkedAt + Ticks(1 / Math.Clamp(rate, .1, 10));
                deviceTurn[key.Device] = ++turn; submitted++;
                if (state.Active) state.Detail = state.Pending ? "Queued · latest input" : "Sent · physical response unconfirmed";
            }
        }
        catch (Exception)
        {
            lock (sync) { state.StopUnconfirmed |= native > 0; failedDevices.Add(key.Device); DisarmCore(key.Device, "Command failed · Reset Bluetooth; physical stop unconfirmed; no retry"); }
        }
        finally { lock (sync) state.WriteInFlight = false; }
        return true;
    }

    private bool SendStops()
    {
        List<((string Device, string Channel) Key, Channel State)> stops = [];
        lock (sync)
            foreach (var (key, state) in channels)
                if (state.StopPending)
                {
                    state.StopPending = false;
                    state.StopInFlight = true;
                    state.StopUnconfirmed = true;
                    state.SentNative = 0; // A failed zero cannot enqueue an endless stop loop.
                    stops.Add((key, state));
                }
        foreach (var (key, state) in stops)
        {
            string? error = null;
            var started = Now;
            try
            {
                if (Device(key.Device)?.ConnectionState == ToyConnectionState.Connected)
                    output.SendLevel(key.Device, key.Channel, 0);
                else error = "Disconnected · physical stop unconfirmed";
            }
            catch (Exception) { error = "Stop could not be sent · turn off the toy if still active"; }
            lock (sync)
            {
                state.StopInFlight = false;
                state.StopUnconfirmed = error is not null;
                state.SubmitMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                // Zero bypasses normal pacing. Its completed call must not add
                // another shared pause to unrelated toys; existing debt remains.
                if (error is not null) { failedDevices.Add(key.Device); DisarmCore(key.Device, error); }
            }
        }
        return stops.Count > 0;
    }

    public ValueTask DisposeAsync()
    {
        lock (sync) return new ValueTask(disposeTask ??= DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        lock (sync)
        {
            disposed = true; DisarmCore(null, "Closing · off");
        }
        lifetime.Cancel(); await worker.ConfigureAwait(false);
        await Task.Run(() => SendStops()).ConfigureAwait(false);
        adapter.Changed -= InventoryChanged; output.OutputFailed -= OutputFailed;
        wake.Dispose();
        lifetime.Dispose();
    }
}
