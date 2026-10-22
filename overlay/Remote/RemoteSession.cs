using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using ShockWave.Overlay.Toys;

namespace ShockWave.Overlay;

// Owned by the UI thread. Hardware lease expiry remains in LocalToyController,
// independent of this thread, the helper, and the network worker.
internal sealed class RemoteSession(Func<ToyAdapterSnapshot?> inventory, Func<LocalToyController?> controller,
    Func<BridgeConfig> config, Action<string, string, int, int, long> shock, IRemoteFriendStore? friendStore = null) : IDisposable
{
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true };
    internal const int StateLifetimeMs = 500;
    private sealed record Alias(string Device, string Function, LocalToyController Controller);
    private sealed record OwnedLease(ToyInputLease Lease, LocalToyController Controller, long Claim, long OwnerRevision, long AcceptedAt, double RemainingMs);
    private readonly Dictionary<(string, string), string> aliases = [];
    private readonly Dictionary<(string, string), Alias> targets = [];
    private readonly Dictionary<string, string> shockTargets = [];
    private readonly Dictionary<(string, string), OwnedLease> leases = [];
    private readonly Dictionary<(string, string), long> claims = [];
    private readonly HashSet<string> shockIds = [];
    private readonly Dictionary<(string, string), long> loggedRejections = [];
    private readonly Guid source = Guid.NewGuid();
    private RemoteWorker? worker;
    private readonly RemoteFriendBook friendBook = new(friendStore ?? new MemoryRemoteFriendStore());
    private string pairingPeer = "", pendingFriendName = "", friendNotice = "";
    private string session = "", lastInventory = "";
    private long heartbeatAt, inventoryAt, stateAt;
    private long tickAt, maximumTickGap, logWindow;
    private int logCount, suppressedLogs, outgoingCount = -1;
    private int unknownSnapshots;
    private bool lastTestMode;
    public RemoteToyController Toys { get; } = new(() => config().DryRun);
    public RemoteShockerInfo[] Shockers { get; private set; } = [];
    public bool Enabled { get; private set; }
    public bool Connected { get; private set; }
    public string SessionId => session;
    public string Invitation { get; private set; } = "";
    public string Status { get; private set; } = "Disconnected · Connect is off";
    public double RoundTripMs { get; private set; }
    public IReadOnlyList<RemoteFriend> Friends => friendBook.Friends;
    public string SelectedFriendId { get; private set; } = "";
    public bool CanRememberFriend { get; private set; }
    public bool RememberingFriend { get; private set; }
    public string FriendNotice => friendBook.Notice.Length > 0 ? friendBook.Notice : friendNotice;
    public event Action? Changed, Paired;
    public event Action<string>? Log;
    internal event Action<string>? Diagnostic;
    public void Connect(string code, string node, string directory)
        => StartConnection(code, null, node, directory);
    public void ConnectFriend(string id, string node, string directory)
    {
        var friend = Friends.FirstOrDefault(f => f.Id == id);
        if (friend is null) { friendNotice = "Choose a saved friend, or pair using a new code."; Changed?.Invoke(); return; }
        StartConnection("", friend, node, directory);
    }
    private void StartConnection(string code, RemoteFriend? friend, string node, string directory)
    {
        EndSession();
        if (!friendBook.Prepare()) { Disconnect(); Status = friendBook.Notice; Changed?.Invoke(); return; }
        SelectedFriendId = friend?.Id ?? ""; friendNotice = ""; Invitation = "";
        try
        {
            worker ??= new(node, directory); Enabled = true;
            worker.Send(new { kind = "enable", code, identity = friendBook.Id, friend = friend is null ? null : new { friend.Id, friend.Secret } });
            Status = friend is null ? "Connecting · direct app-to-app · TURN disabled" : "Waiting for saved friend · both Connect switches must be on";
            Report("Connect enabled · encrypted direct app-to-app · TURN disabled.");
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { Enabled = false; Status = "Connection helper could not start. Use the complete ShockWave folder."; worker?.Dispose(); worker = null; }
        Changed?.Invoke();
    }
    public void Disconnect()
    { if (Enabled || Connected) Report("Connection closed · Connect is off."); Enabled = false; worker?.Send(new { kind = "disable" }); EndSession(); Status = "Disconnected · Connect is off"; Changed?.Invoke(); }
    private void EndSession()
    {
        foreach (var lease in leases.Values) lease.Controller.EndInput(lease.Lease);
        leases.Clear(); claims.Clear(); targets.Clear(); shockTargets.Clear(); aliases.Clear(); shockIds.Clear();
        Toys.Disconnect(); Shockers = []; Connected = false; session = lastInventory = "";
        loggedRejections.Clear(); outgoingCount = -1; maximumTickGap = 0; unknownSnapshots = 0;
        CanRememberFriend = RememberingFriend = false; pairingPeer = "";
    }
    public void RememberFriend(string name)
    {
        if (!Connected || !CanRememberFriend || RememberingFriend) return;
        if (name.Length > 48 || name.Any(char.IsControl)) { friendNotice = "Use a friend name of up to 48 characters."; Changed?.Invoke(); return; }
        pendingFriendName = name.Trim(); RememberingFriend = true; friendNotice = "Saving this pairing on both apps…";
        worker?.Send(new { kind = "remember-friend" }); Changed?.Invoke();
    }
    public void RenameFriend(string id, string name)
    {
        if (friendBook.Rename(id, name)) friendNotice = "Friend name saved on this app.";
        Changed?.Invoke();
    }
    public void ForgetFriend(string id)
    {
        if (!Friends.Any(f => f.Id == id)) return;
        // Kill the worker too: it must not retain a forgotten key or old invitation in RAM.
        Disconnect(); worker?.Dispose(); worker = null;
        if (friendBook.Forget(id)) { SelectedFriendId = ""; Invitation = ""; friendNotice = "Friend forgotten. A new code is required to pair again."; }
        Changed?.Invoke();
    }
    private void FriendEvent(JsonElement item)
    {
        if (!Enabled || !Connected || !item.TryGetProperty("session", out var bound) || bound.GetString() != session) return;
        switch (item.GetProperty("kind").GetString())
        {
            case "friend-ready":
                pairingPeer = item.GetProperty("id").GetString() ?? "";
                CanRememberFriend = RemoteFriendStore.ValidId(pairingPeer) && item.GetProperty("available").GetBoolean() && !Friends.Any(f => f.Id == pairingPeer);
                break;
            case "friend-save":
                var request = item.GetProperty("request").GetString() ?? "";
                var id = item.GetProperty("id").GetString() ?? "";
                if (!Regex.IsMatch(request, "^[a-f0-9]{32}$") || id != pairingPeer) return;
                var name = item.GetProperty("outgoing").GetBoolean() ? pendingFriendName : "";
                var saved = friendBook.Remember(id, item.GetProperty("secret").GetString() ?? "", name);
                worker?.Send(new { kind = "friend-stored", request, ok = saved });
                if (!saved) { RememberingFriend = false; friendNotice = "Pairing was not saved on both apps."; }
                break;
            case "friend-saved":
                var friendId = item.GetProperty("id").GetString() ?? "";
                if (!Friends.Any(f => f.Id == friendId)) return;
                SelectedFriendId = friendId; CanRememberFriend = RememberingFriend = false;
                pendingFriendName = ""; friendNotice = "Friend saved on both apps. Next time, choose this friend and press Connect.";
                Report("Friend pairing saved; future connections still start with no activity.");
                break;
            case "friend-failed":
                RememberingFriend = false; friendNotice = "Friend save was not confirmed. Check both apps and try again, or use a new code.";
                break;
        }
        Changed?.Invoke();
    }
    private void Send(string type, object payload) => worker?.Send(new { kind = "send", type, payload, createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
    public void Tick()
    {
        if (worker is null) return;
        if (worker.Failed) { Disconnect(); worker.Dispose(); worker = null; Status = "Connection helper stopped · use Connect to try again"; Report("Connection helper stopped; app heartbeat, queue or process failure."); Changed?.Invoke(); return; }
        var now = Environment.TickCount64;
        var gap = tickAt > 0 ? now - tickAt : 0; tickAt = now;
        if (Connected && (leases.Count > 0 || Toys.ActiveInputCount > 0))
        {
            maximumTickGap = Math.Max(maximumTickGap, gap);
            if (gap >= 150) Report($"App remote updates delayed {gap} ms while toy input was active.");
        }
        if (now - heartbeatAt >= 250) { heartbeatAt = now; worker.Send(new { kind = "heartbeat" }); }
        foreach (var item in worker.Drain())
        {
            try
            {
                switch (item.GetProperty("kind").GetString())
                {
                    case "diagnostic": Diagnostic?.Invoke(item.GetRawText()); break;
                    case "health": ReportHealth(item); break;
                    case "friend-ready": case "friend-save": case "friend-saved": case "friend-failed": FriendEvent(item); break;
                    case "invitation": Invitation = item.GetProperty("code").GetString() ?? ""; Changed?.Invoke(); break;
                    case "status": if (!Connected) { Status = item.GetProperty("detail").GetString() ?? "Waiting"; Changed?.Invoke(); } break;
                    case "error": Enabled = false; EndSession(); Status = item.GetProperty("detail").GetString() ?? "Connection failed"; Report("Connection helper reported an error; Connect is off."); Changed?.Invoke(); break;
                    case "connected":
                        if (!Enabled) break; EndSession(); Connected = true; session = item.GetProperty("session").GetString()!;
                        Status = "Connected · encrypted direct app-to-app · TURN disabled"; inventoryAt = 0; lastTestMode = config().DryRun;
                        Report("Peer connected; fresh session, no old activity restored.");
                        Paired?.Invoke(); Changed?.Invoke(); break;
                    case "disconnected":
                        var detail = item.TryGetProperty("detail", out var reason) ? reason.GetString() : "Peer disconnected";
                        Report("Peer connection ended; " + (Enabled ? "automatic direct retry is on." : "Connect is off."));
                        EndSession(); Status = Enabled ? detail + " · Connect is on · retrying direct" : "Disconnected · Connect is off"; Changed?.Invoke(); break;
                    case "timing": RoundTripMs = item.GetProperty("milliseconds").GetDouble(); break;
                    case "message":
                        var message = item.GetProperty("message");
                        if (!Connected || message.GetProperty("session").GetString() != session) break;
                        var elapsed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - item.GetProperty("receivedAt").GetInt64();
                        var type = message.GetProperty("kind").GetString();
                        var lifetime = RemainingLifetime(type!, item.GetProperty("remainingMs").GetDouble(), elapsed);
                        if (lifetime <= 0) { if (type == "state") Report($"Incoming toy state expired in app queue after {Math.Max(0, elapsed)} ms; ignored."); break; }
                        Receive(type!, message.GetProperty("payload"), lifetime); break;
                }
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException or OverflowException)
            { Disconnect(); Status = "Invalid remote data · disconnected"; Report("Invalid remote data; connection closed."); Changed?.Invoke(); }
        }
        if (!Connected) return;
        if (lastTestMode != config().DryRun) { lastTestMode = config().DryRun; Toys.Stop(); foreach (var l in leases.Values) l.Controller.EndInput(l.Lease); leases.Clear(); }
        if (now - inventoryAt >= 500) { inventoryAt = now; PublishInventory(); }
        if (now - stateAt >= 33)
        {
            stateAt = now; var levels = Toys.Levels();
            if (levels.Length != outgoingCount) { outgoingCount = levels.Length; Report($"Sending {outgoingCount} remote toy functions."); }
            Send("state", levels);
        }
    }
    private string AliasFor(string device, string function) => aliases.TryGetValue((device, function), out var id) ? id : aliases[(device, function)] = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
    internal RemoteInventory PublishInventory()
    {
        targets.Clear(); shockTargets.Clear();
        var local = controller(); var toys = new List<RemoteToyInfo>();
        if (local is not null)
        foreach (var device in (inventory()?.Devices ?? []).Where(d => d.ConnectionState == ToyConnectionState.Connected && d.CapabilitiesKnown).Take(32))
        {
            var id = AliasFor(device.DeviceKey, ""); var functions = new List<RemoteToyFunction>();
            foreach (var f in device.Capabilities.Where(f => !f.IsSetting && f.Type != ToyCapabilityType.Unknown && f.NativeMaximum is > 0).Take(8))
            {
                var channel = AliasFor(device.DeviceKey, f.ChannelId); targets[(id, channel)] = new(device.DeviceKey, f.ChannelId, local);
                functions.Add(new(channel, Clean(f.DisplayName), f.Type.ToString(), local.State(device.DeviceKey, f.ChannelId).Preference.Maximum));
            }
            if (functions.Count > 0) toys.Add(new(id, Clean(device.DisplayName), device.BatteryPercent, functions.ToArray()));
        }
        var shocks = config().ActiveShockers.Where(s => !s.Paused && s.Uuid.Length > 0).Take(32).Select(s =>
        {
            var id = AliasFor("shock/" + s.Key, ""); shockTargets[id] = s.Key;
            return new RemoteShockerInfo(id, Clean(s.Name), config().Provider, s.MaxIntensity, Math.Min(15000, s.MaxDurationMs));
        }).ToArray();
        foreach (var key in leases.Keys.Where(k => !targets.ContainsKey(k)).ToArray()) End(key);
        var next = new RemoteInventory(toys.ToArray(), shocks); var text = JsonSerializer.Serialize(next, Json);
        if (text != lastInventory) { lastInventory = text; Send("inventory", next); }
        return next;
    }
    // Consume worker-to-UI time too; never round an expired packet up to a new lease.
    internal static double RemainingLifetime(string type, double remainingMs, long elapsed)
    {
        var maximum = type == "state" ? StateLifetimeMs : type == "shock" ? 250 : 3000;
        if (elapsed < -10 || !double.IsFinite(remainingMs)) return 0;
        return Math.Max(0, Math.Min(remainingMs, maximum) - Math.Max(0, elapsed));
    }
    internal void Receive(string type, JsonElement payload, double remainingMs = StateLifetimeMs)
    {
        if (type == "inventory")
        {
            var data = payload.Deserialize<RemoteInventory>(Json) ?? throw new JsonException();
            if (data.Toys is null || data.Shockers is null || data.Toys.Any(t => t is null) || data.Shockers.Any(s => s is null) || data.Toys.Length > 32 || data.Shockers.Length > 32 || data.Toys.Sum(t => t.Functions?.Length ?? 1000) > 256) throw new JsonException();
            var ids = new HashSet<string>();
            foreach (var toy in data.Toys)
            {
                if (!Id(toy.Id) || !ids.Add(toy.Id) || !Text(toy.Name) || toy.Battery is < 0 or > 100 || toy.Functions is null || toy.Functions.Length is < 1 or > 8) throw new JsonException();
                var functions = new HashSet<string>();
                foreach (var f in toy.Functions) if (f is null || !Id(f.Id) || !functions.Add(f.Id) || !Text(f.Name) || !Enum.TryParse<ToyCapabilityType>(f.Type, out var ft) || !Enum.IsDefined(ft) || ft is ToyCapabilityType.Unknown or ToyCapabilityType.Depth or ToyCapabilityType.Position || f.Maximum is < 0 or > 100) throw new JsonException();
            }
            foreach (var s in data.Shockers) if (!Id(s.Id) || !ids.Add(s.Id) || !Text(s.Name) || s.Service is not ("OpenShock" or "PiShock") || s.Maximum is < 0 or > 100 || s.DurationMs is < 1 or > 15000) throw new JsonException();
            Toys.Replace(data.Toys); Shockers = data.Shockers; Changed?.Invoke();
        }
        else if (type == "state")
        {
            var values = payload.Deserialize<RemoteLevel[]>(Json) ?? throw new JsonException();
            if (values.Length > 256 || values.Any(v => v is null || !Id(v.Device) || !Id(v.Channel) || v.Percent is < 0 or > 100 || v.Claim < 1) || values.Select(v => (v.Device, v.Channel)).Distinct().Count() != values.Length) throw new JsonException();
            if (values.Any(v => !targets.ContainsKey((v.Device, v.Channel))))
            {
                if (unknownSnapshots++ == 0) Report("Receiver ignored a state snapshot containing an unavailable toy function.");
                return; // Atomic rejection; no guessed targets.
            }
            var present = values.Select(v => (v.Device, v.Channel)).ToHashSet();
            foreach (var key in leases.Keys.Where(k => !present.Contains(k)).ToArray()) End(key);
            foreach (var value in values)
            {
                var key = (value.Device, value.Channel); var target = targets[key]; var life = TimeSpan.FromMilliseconds(remainingMs);
                try
                {
                    if (leases.TryGetValue(key, out var existing) && existing.Claim == value.Claim)
                    {
                        if (!ReferenceEquals(existing.Controller, target.Controller)) { End(key); Reject(value, "controller-changed", existing, remainingMs); }
                        else if (!target.Controller.UpdateInput(existing.Lease, value.Percent, life))
                        {
                            var state = target.Controller.State(target.Device, target.Function);
                            var failure = state.OwnerRevision == existing.OwnerRevision && state.InputSource == Guid.Empty && state.LastInputSource == source
                                ? "input-expired" : state.InputSource != Guid.Empty && state.InputSource != source ? "other-input" : "owner-or-safety-reset";
                            End(key); Reject(value, failure, existing, remainingMs);
                        }
                        else leases[key] = existing with { AcceptedAt = Environment.TickCount64, RemainingMs = remainingMs };
                    }
                    else if (value.Claim > claims.GetValueOrDefault(key))
                    {
                        // A fresh gesture may replace this same live source directly.
                        // Ending it first queues an unnecessary zero and can make the
                        // new gesture reject itself while that zero is still in flight.
                        if (leases.TryGetValue(key, out var previous) && !ReferenceEquals(previous.Controller, target.Controller)) End(key);
                        claims[key] = value.Claim;
                        var lease = target.Controller.BeginInput(target.Device, target.Function, source, value.Percent, life);
                        leases[key] = new(lease, target.Controller, value.Claim, target.Controller.State(target.Device, target.Function).OwnerRevision, Environment.TickCount64, remainingMs);
                    }
                    else Reject(value, "old-claim");
                }
                catch (InvalidOperationException) { claims[key] = Math.Max(claims.GetValueOrDefault(key), value.Claim); End(key); Reject(value, "controller-unavailable"); }
            }
        }
        else if (type == "stop") { foreach (var key in leases.Keys.ToArray()) End(key); }
        else if (type == "result")
        {
            var value = payload.Deserialize<RemoteLevel>(Json);
            if (value is not null && Id(value.Device) && Id(value.Channel) && value.Claim > 0 && Toys.Reject(value.Device, value.Channel, value.Claim))
                Report($"Friend stopped toy input {value.Claim}: {ReasonText(value.Reason)}; fresh drawing or Play required.");
        }
        else if (type == "shock")
        {
            var id = payload.GetProperty("commandId").GetString()!; var target = payload.GetProperty("device").GetString()!;
            var mode = payload.GetProperty("type").GetString()!; var intensity = payload.GetProperty("percent").GetInt32(); var duration = payload.GetProperty("durationMs").GetInt32();
            if (!Id(id) || !Id(target) || mode is not ("Shock" or "Vibrate" or "Sound") || intensity is < 1 or > 100 || duration is < 1 or > 15000 || !shockTargets.TryGetValue(target, out var key) || shockIds.Contains(id)) return;
            if (shockIds.Count >= 4096) { Disconnect(); return; } shockIds.Add(id);
            shock(key, mode, intensity, duration, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + (long)remainingMs); // One submission, never retried or replayed.
        }
    }
    internal bool SendShock(string id, string type, int percent, int durationMs)
    {
        if (!Connected || config().DryRun || !Shockers.Any(s => s.Id == id) || type is not ("Shock" or "Vibrate" or "Sound") || percent is < 1 or > 100 || durationMs is < 1 or > 15000) return false;
        Send("shock", new { commandId = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(), device = id, type, percent, durationMs }); return true;
    }
    private void Reject(RemoteLevel value, string reason, OwnedLease? previous = null, double remainingMs = 0)
    {
        Send("result", value with { Reason = reason });
        var key = (value.Device, value.Channel);
        if (loggedRejections.GetValueOrDefault(key) == value.Claim) return;
        loggedRejections[key] = value.Claim;
        var timing = previous is null ? "" : $" Last accepted {Math.Max(0, Environment.TickCount64-previous.AcceptedAt)} ms ago; previous lease {previous.RemainingMs:0} ms; new state remaining {remainingMs:0} ms.";
        Report($"Receiver stopped toy input {value.Claim}: {ReasonText(reason)}.{timing} RTT {RoundTripMs:0} ms; app gap {maximumTickGap} ms.");
    }
    private static string ReasonText(string? reason) => reason switch
    {
        "input-expired" => "input freshness expired", "other-input" => "another input owns this function",
        "owner-or-safety-reset" => "owner action or safety reset", "controller-changed" => "local toy controller changed",
        "controller-unavailable" => "toy unavailable or required Stop pending", "old-claim" => "old or expired input claim",
        _ => "receiver rejected input (no reason supplied)"
    };
    private void Report(string message)
    {
        var now = Environment.TickCount64;
        if (now-logWindow >= 1000) { logWindow = now; logCount = 0; if (suppressedLogs > 0) { Log?.Invoke($"{suppressedLogs} additional remote diagnostic events suppressed."); suppressedLogs = 0; } }
        if (logCount++ < 32) Log?.Invoke(message); else suppressedLogs++;
    }
    internal void ReportHealth(JsonElement item)
    {
        static double Number(JsonElement data, string name) => data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var n) && double.IsFinite(n) ? Math.Clamp(n,0,1e9) : 0;
        Report($"Network 5s: sent {Number(item,"sent"):0}, received {Number(item,"received"):0}, expired {Number(item,"expired"):0}, future {Number(item,"future"):0}, blocked {Number(item,"blocked"):0}, pipe expired {Number(item,"ipcExpired"):0}, unknown targets {unknownSnapshots}; max state age {Number(item,"maxAgeMs"):0} ms, gap {Number(item,"maxGapMs"):0} ms, min remaining {Number(item,"minRemainingMs"):0} ms; max RTT {Number(item,"rttMs"):0} ms, clock uncertainty {Number(item,"uncertaintyMs"):0} ms; app gap {maximumTickGap} ms.");
        maximumTickGap = 0; unknownSnapshots = 0;
    }
    private void End((string, string) key) { if (leases.Remove(key, out var lease)) lease.Controller.EndInput(lease.Lease); }
    private static bool Id(string? value) => value is not null && Regex.IsMatch(value, "^[a-f0-9]{32}$", RegexOptions.CultureInvariant);
    private static bool Text(string? value) => value is { Length: > 0 and <= 96 } && !value.Any(char.IsControl);
    private static string Clean(string value) => new(value.Where(c => !char.IsControl(c)).Take(96).ToArray());
    public void Dispose() { Disconnect(); worker?.Dispose(); worker = null; }
}
