using ShockWave.ShaderLab;
using ShockWave.Overlay.Toys;

namespace ShockWave.Overlay.Sps;

// UI-thread model. Positive output always goes through the existing controller's
// generation lease, limits, Test mode and scheduler. Saved assignments accept fresh
// input automatically; cached values are never replayed after setup or reconnect.
internal sealed class SpsSession(Func<ToyAdapterSnapshot?> inventory, Func<LocalToyController?> getController) : IDisposable
{
    private readonly Dictionary<string, SpsSignal> signals = new(StringComparer.Ordinal);
    private sealed record Binding(ToyInputLease Lease, int Percent);
    private readonly Dictionary<SpsTarget, Binding> bindings = [];
    private readonly Dictionary<SpsTarget, double> blocked = [];
    private readonly Dictionary<SpsTarget, (long Generation, long OwnerRevision)> observedOwnership = [];
    private readonly Dictionary<SpsTarget, double> unavailableSince = [];
    private readonly Dictionary<string, double> levels = [];
    private readonly Dictionary<string, double> configuredAt = [];
    private readonly Dictionary<string, SpsDepth> depths = [];
    private readonly Guid source = Guid.NewGuid();
    private LocalToyController? controller;
    private double startAt = double.NegativeInfinity, lastTick;
    private bool disposed;
    public IReadOnlyList<SpsRule> Rules { get; private set; } = [];
    public IReadOnlyCollection<SpsSignal> Signals => signals.Values;
    public int Rejected { get; private set; }
    public int ActiveTargets => bindings.Values.Count(b => b.Percent > 0);
    public string Notice { get; private set; } = "Automatic · waiting for assigned SPS input";
    public bool TestMode => controller?.TestMode ?? true;
    public void Configure(List<SpsRule> rules, double now)
    {
        SpsStore.Validate(rules, 2048, 256);
        var previous=Rules.ToDictionary(r=>r.Id); var replacement=rules.ToDictionary(r=>r.Id);
        var changed = Rules.Where(old => !replacement.TryGetValue(old.Id,out var next) || !Same(old,next))
            .Concat(rules.Where(next => !previous.TryGetValue(next.Id,out var old) || !Same(old,next))).ToArray();
        var affected=changed.SelectMany(r=>r.Targets).ToHashSet();
        foreach(var old in Rules)
        {
            if(!replacement.TryGetValue(old.Id,out var next) || !Same(old,next)) continue;
            affected.UnionWith(old.Targets.Except(next.Targets)); affected.UnionWith(next.Targets.Except(old.Targets));
        }
        foreach (var target in affected)
        {
            End(target); unavailableSince[target]=now;
            if (!blocked.ContainsKey(target)) observedOwnership.Remove(target);
        }
        foreach (var rule in changed) { levels.Remove(rule.Id); configuredAt[rule.Id] = now; }
        Rules = rules.Select(r => r with { Targets = r.Targets.ToList() }).ToArray();
        foreach (var id in configuredAt.Keys.Where(id => !Rules.Any(r => r.Id == id)).ToArray()) configuredAt.Remove(id);
        foreach (var target in blocked.Keys.Concat(unavailableSince.Keys).Distinct().ToArray())
            if (!Rules.Any(r => r.Targets.Contains(target))) { blocked.Remove(target); observedOwnership.Remove(target); unavailableSince.Remove(target); }
        foreach (var target in observedOwnership.Keys.Where(t => !Rules.Any(r => r.Targets.Contains(t))).ToArray()) observedOwnership.Remove(target);
        Notice = "Automatic · waiting for fresh SPS input";
    }
    private static bool Same(SpsRule a, SpsRule b) => a.Id == b.Id && a.Name == b.Name && a.Address == b.Address && a.RootAddress == b.RootAddress && a.Enabled == b.Enabled
        && a.Response == b.Response && a.InputMin == b.InputMin && a.InputMax == b.InputMax && a.Gain == b.Gain && a.DeadZone == b.DeadZone
        && a.SmoothingMs == b.SmoothingMs && a.TimeoutMs == b.TimeoutMs;
    private double AcceptAfter(SpsRule rule) => Math.Max(startAt, configuredAt.GetValueOrDefault(rule.Id, double.NegativeInfinity));
    public void Stop()
    {
        foreach (var target in bindings.Keys.ToArray()) End(target);
        levels.Clear(); Notice = "SPS output cleared";
    }
    public void BlockAll(double now)
    {
        Stop();
        foreach (var target in Rules.Where(r => r.Enabled).SelectMany(r => r.Targets).Distinct()) BlockOwner(target, now);
    }
    private ToyChannelState? State(SpsTarget target)
    {
        try { return controller?.State(target.DeviceKey, target.ChannelId); }
        catch (InvalidOperationException) { return null; }
    }
    private void BlockOwner(SpsTarget target, double now, double? ownerChangedAt = null)
    {
        var state = State(target);
        var changedAt = ownerChangedAt ?? state?.OwnershipChangedAt ?? 0;
        blocked[target] = changedAt > 0 ? Math.Min(now, changedAt) : now;
    }
    private void BlockInput(SpsTarget target, double now)
    {
        blocked[target] = now;
    }
    public void Reset(double now, string reason)
    {
        foreach (var target in bindings.Keys.ToArray()) End(target);
        signals.Clear(); levels.Clear(); depths.Clear(); startAt = now;
        foreach (var target in observedOwnership.Keys.Where(t => !blocked.ContainsKey(t)).ToArray()) observedOwnership.Remove(target);
        Notice = reason + " · waiting for fresh SPS input";
    }
    public void StopRule(string id, double now)
    {
        foreach (var target in Rules.Where(r => r.Id == id).SelectMany(r => r.Targets)) { End(target); BlockInput(target, now); }
        Notice = "Mapping stopped · release its inputs before restarting";
    }
    public bool Apply(OscMessage message, double now)
    {
        if (disposed || !double.IsFinite(now)) return false;
        if (message.Address == "/avatar/change")
        {
            if (message.Arguments.Length != 1 || message.Arguments[0] is not string) return false;
            Reset(now, "Avatar changed"); return true;
        }
        var known = SpsSignals.Describe(message.Address);
        if (known is null && !Rules.Any(r => r.Address == message.Address)) return false;
        if (!SpsSignals.SafeAddress(message.Address)) return false;
        double? value = message.Arguments.Length == 1 ? message.Arguments[0] switch
        { float f => f, int i => i, bool b => b ? 1 : 0, _ => null } : null;
        if (value is null || !double.IsFinite(value.Value) || Math.Abs(value.Value) > 1000 || known is not null && (value < 0 || value > 1))
        {
            signals.Remove(message.Address); Rejected++;
            foreach (var rule in Rules.Where(r => r.Address == message.Address || r.RootAddress == message.Address))
            { depths.Remove(rule.Address); foreach (var target in rule.Targets) { End(target); BlockInput(target, now); } }
            return false;
        }
        var prior = signals.GetValueOrDefault(message.Address);
        if(prior is not null && now<prior.ReceivedAt) return false;
        if (prior is null && signals.Count >= 256) { Rejected++; return false; }
        var dt = prior is null ? 0 : now - prior.ReceivedAt;
        var rate = dt is >= .005 and <= .5 ? (value.Value - prior!.Value) / dt : 0;
        signals[message.Address] = (known ?? new(message.Address, "Custom", message.Address[19..], "Defined by avatar", "Calibrated value", 0, 0, 0))
            with { Value = value.Value, ReceivedAt = now, Rate = rate, RateFrom = prior?.ReceivedAt ?? double.NegativeInfinity };
        foreach (var target in Rules.Where(r => r.Address == message.Address || r.RootAddress == message.Address).SelectMany(r => r.Targets).Where(t => !Ready(t))) unavailableSince[target] = now;
        return true;
    }
    private void MonitorDepth(double now)
    {
        foreach(var tip in signals.Values.Where(s=>s.Kind=="Socket" && s.Address.EndsWith("NewTip",StringComparison.Ordinal)))
        {
            var address=tip.Address[..^3]+"Root";
            if(now-tip.ReceivedAt>2 || !signals.TryGetValue(address,out var root) || now-root.ReceivedAt>2) continue;
            if(!depths.TryGetValue(tip.Address,out var depth)) depths[tip.Address]=depth=new();
            depth.Update(root,tip);
        }
    }
    public double Level(SpsRule rule, double now)
    {
        if (!signals.TryGetValue(rule.Address, out var signal) || now - signal.ReceivedAt > rule.TimeoutMs / 1000d) return 0;
        if (rule.RootAddress is not null)
        {
            if (!signals.TryGetValue(rule.RootAddress, out var root) || now - root.ReceivedAt > rule.TimeoutMs / 1000d) return 0;
            if (!depths.TryGetValue(rule.Address, out var depth)) depths[rule.Address] = depth = new();
            depth.Update(root, signal);
            return SpsSignals.Map(rule, SpsSignals.Normalize(rule, depth.Value), depth.RateFrom > AcceptAfter(rule) && now-Math.Max(root.ReceivedAt,signal.ReceivedAt)<=.15 ? depth.Rate/(rule.InputMax-rule.InputMin) : 0);
        }
        var motion = now - signal.ReceivedAt <= .15 && signal.RateFrom > AcceptAfter(rule) ? signal.Rate / (rule.InputMax - rule.InputMin) : 0;
        return SpsSignals.Map(rule, SpsSignals.Normalize(rule, signal.Value), motion);
    }
    public string RuleState(SpsRule rule, double now)
    {
        if (!rule.Enabled) return "Disabled";
        if (!signals.TryGetValue(rule.Address, out var signal)) return "No signal";
        if (now - signal.ReceivedAt > rule.TimeoutMs / 1000d) return "Stale · off";
        if (signal.ReceivedAt <= AcceptAfter(rule)) return "Waiting for fresh input";
        if (rule.RootAddress is not null && (!signals.TryGetValue(rule.RootAddress, out var root) || root.ReceivedAt <= AcceptAfter(rule)
            || now - root.ReceivedAt > rule.TimeoutMs / 1000d)) return "Waiting for root + tip";
        if (rule.RootAddress is not null && (!depths.TryGetValue(rule.Address, out var depth) || depth.Length is null)) return "Waiting for depth calibration";
        if (rule.Targets.Any(t => blocked.ContainsKey(t))) return "Release input to rearm";
        if (rule.Targets.Any(t => State(t) is { InputSource: var owner } && owner != Guid.Empty && owner != source)) return "Paused · another controller";
        if (rule.Targets.Count == 0) return "Choose toy function";
        if (rule.Targets.All(t => !Ready(t))) return "Toy disconnected";
        if (rule.Targets.All(t => signal.ReceivedAt <= unavailableSince.GetValueOrDefault(t, double.NegativeInfinity))) return "Waiting for fresh input";
        return rule.Targets.Any(t => bindings.TryGetValue(t, out var b) && b.Percent > 0) ? TestMode ? "Test preview" : "Routing" : "Idle / target unavailable";
    }
    public double RoutedLevel(string ruleId) => levels.GetValueOrDefault(ruleId);
    private bool Ready(SpsTarget target)
    {
        var snapshot = inventory();
        var device = snapshot?.Devices.FirstOrDefault(d => d.DeviceKey == target.DeviceKey);
        return snapshot is not null && (!snapshot.IsScanning || snapshot.SupportsOutputWhileScanning) && device?.ConnectionState == ToyConnectionState.Connected && device.CapabilitiesKnown
            && device.Capabilities.Any(c => c.ChannelId == target.ChannelId && SpsSignals.Scalar(c));
    }
    public void Tick(double now, bool listening)
    {
        if (disposed) return;
        MonitorDepth(now);
        var next = getController();
        if (!ReferenceEquals(controller, next))
        {
            if (controller is not null) Reset(now, "Bluetooth controller reset");
            controller = next;
            observedOwnership.Clear();
        }
        var dt = Math.Clamp(now - lastTick, 0, .25); lastTick = now;
        if (!listening)
        {
            if (signals.Count > 0 || bindings.Count > 0) Reset(now, "OSC unavailable");
            Notice = "OSC unavailable · reconnect in Toy control"; return;
        }
        if (controller is null) return;
        // A change-only neutral packet may arrive between owner Stop and this
        // timer. Compare it with the actual ownership change, not observation
        // time, so that single deliberate release is sufficient to rearm.
        foreach (var target in Rules.Where(r => r.Enabled).SelectMany(r => r.Targets).Union(bindings.Keys).Distinct().ToArray())
        {
            if (!Ready(target)) { End(target); observedOwnership.Remove(target); continue; }
            if (State(target) is not { } state) continue;
            if (observedOwnership.TryGetValue(target, out var prior))
            {
                if (state.OwnerRevision != prior.OwnerRevision)
                {
                    bindings.Remove(target); BlockOwner(target, now, state.OwnerChangedAt);
                }
                else if (state.InputGeneration != prior.Generation && state.InputSource != source)
                {
                    if (state.InputSource != Guid.Empty || state.LastInputSource != source)
                    {
                        // Interactive ownership pauses only this target. Its release
                        // requires a later fresh contact, never cached positive input.
                        bindings.Remove(target);
                        unavailableSince[target] = Math.Max(unavailableSince.GetValueOrDefault(target, double.NegativeInfinity), state.OwnershipChangedAt);
                    }
                    else if (bindings.Remove(target)) BlockOwner(target, now);
                }
            }
            observedOwnership[target] = (state.InputGeneration, state.OwnerRevision);
        }
        var desired = new Dictionary<SpsTarget, (double Level, double Until)>();
        var allTargets = Rules.Where(r => r.Enabled).SelectMany(r => r.Targets).Distinct().ToArray();
        foreach (var rule in Rules)
        {
            var fresh = rule.Enabled && signals.TryGetValue(rule.Address, out var signal) && signal.ReceivedAt > AcceptAfter(rule)
                && now - signal.ReceivedAt <= rule.TimeoutMs / 1000d;
            if (rule.RootAddress is not null) fresh &= signals.TryGetValue(rule.RootAddress, out var root) && root.ReceivedAt > AcceptAfter(rule) && now - root.ReceivedAt <= rule.TimeoutMs / 1000d;
            var input = fresh ? Level(rule, now) : 0;
            var previous = levels.GetValueOrDefault(rule.Id);
            var level = input == 0 ? 0 : rule.SmoothingMs == 0 ? input : previous + (input - previous) * (1 - Math.Exp(-dt * 1000 / rule.SmoothingMs));
            levels[rule.Id] = level;
            if (!fresh) continue;
            var until = signals[rule.Address].ReceivedAt + rule.TimeoutMs / 1000d;
            if (rule.RootAddress is not null) until = Math.Min(until, signals[rule.RootAddress].ReceivedAt + rule.TimeoutMs / 1000d);
            foreach (var target in rule.Targets)
            {
                if (signals[rule.Address].ReceivedAt <= unavailableSince.GetValueOrDefault(target, double.NegativeInfinity)) continue;
                if (rule.RootAddress is not null && signals[rule.RootAddress].ReceivedAt <= unavailableSince.GetValueOrDefault(target, double.NegativeInfinity)) continue;
                var old = desired.GetValueOrDefault(target);
                // The strongest value keeps its own expiry. A weaker fresh rule
                // must not extend a stronger stale request if the UI stalls.
                desired[target] = level > old.Level ? (level, until) : level == old.Level ? (level, Math.Max(old.Until, until)) : old;
            }
        }
        foreach (var target in allTargets.Union(bindings.Keys).ToArray())
        {
            if (!Ready(target)) { End(target); unavailableSince[target] = now; continue; }
            if (State(target) is { InputSource: var owner } && owner != Guid.Empty && owner != source) continue;
            if (blocked.TryGetValue(target, out var since))
            {
                var inputs = Rules.Where(r => r.Enabled && r.Targets.Contains(target)).ToArray();
                if (inputs.Length > 0 && inputs.All(r => signals.TryGetValue(r.Address, out var s) && s.ReceivedAt > since
                    && now - s.ReceivedAt <= r.TimeoutMs / 1000d
                    && (r.RootAddress is null ? SpsSignals.Normalize(r, s.Value) <= r.DeadZone / 100d
                        : signals.TryGetValue(r.RootAddress, out var root) && root.ReceivedAt > since && now - root.ReceivedAt <= r.TimeoutMs / 1000d && Level(r with { Response = SpsResponse.Level }, now) == 0))) blocked.Remove(target);
                else continue;
            }
            var request = desired.GetValueOrDefault(target);
            var percent = (int)Math.Round(request.Level * 100);
            var remaining = Math.Min(2, request.Until - now);
            if (remaining <= 0) { End(target); continue; }
            var life = TimeSpan.FromSeconds(remaining);
            if (bindings.TryGetValue(target, out var binding))
            {
                // Fresh neutral is immediately off, but keeps the live source
                // until the exact contact deadline. A rapid rise can then wait
                // behind its zero without being mistaken for owner Stop. Never
                // allocate a new source for neutral or renew expired input.
                if (!controller.UpdateInput(binding.Lease, percent, life))
                { bindings.Remove(target); BlockOwner(target, now); Notice = "Owner override / Stop · release input to rearm"; }
                else bindings[target] = binding with { Percent = percent };
            }
            else if (percent > 0)
            {
                try
                {
                    var lease = controller.TryBeginAutomaticInput(target.DeviceKey, target.ChannelId, source, percent, life);
                    if (lease is not null) bindings[target] = new(lease, percent);
                }
                catch (InvalidOperationException)
                {
                    // A rearmed, still-fresh rise may wait behind an unfinished
                    // required zero. Keep the consumed release while waiting;
                    // the controller gate remains shut until zero succeeds. A
                    // later ownership change still requires a later release.
                    if (State(target) is not { Stopping: true }) BlockOwner(target, now);
                    Notice = "Waiting for toy / Stop to finish";
                }
            }
        }
        Notice = ActiveTargets > 0 ? TestMode ? "Test mode · previewing mapped output" : "SPS routing · owner limits apply"
            : "Automatic · waiting for connected toy and fresh assigned input";
    }
    private void End(SpsTarget target)
    {
        if (bindings.Remove(target, out var binding)) controller?.EndInput(binding.Lease);
    }
    public void Dispose() { if (disposed) return; Stop(); disposed = true; }
}
