using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShockWave.Overlay;
using ShockWave.Overlay.Toys;

internal static partial class Program
{
    private static void IntifaceChecks()
    {
        Run("Intiface address validation and separate settings preserve damaged files", IntifaceSettingsCheck);
        Run("Intiface DNS-SD decodes compressed records and rejects malformed/foreign replies", IntifaceDiscoveryCheck);
        Run("Intiface socket negotiates v3 and monitors servers with watchdog disabled", IntifaceSocketCheck);
        Run("Intiface nicknames survive new indexes; unnamed toys are session-scoped", IntifaceIdentityCheck);
        Run("Intiface duplicate nicknames revoke existing output and stay transient", IntifaceDuplicateCheck);
        Run("Intiface automatic socket recovery starts at zero with saved limits", IntifaceReconnectCheck);
        Run("Intiface ambiguous requests disarm without retry or replay", IntifaceTimeoutCheck);
        Run("Intiface transient preferences never enter saved settings", IntifaceTransientCheck);
        IntifaceUiCheck(); Console.WriteLine("PASS Intiface Bluetooth settings, discovery selection and saved startup"); passed++;
    }
    private static LocalToyKeys PhoneKeys() => new(Enumerable.Repeat((byte)12, 32).ToArray());
    private static object PhoneDevice(uint index, string? nickname = "My toy") => new
    {
        DeviceIndex = index, DeviceName = "Lovense Hush", DeviceDisplayName = nickname,
        DeviceMessages = new { ScalarCmd = new[] { new { ActuatorType = "Vibrate", StepCount = 20 } },
            SensorReadCmd = new[] { new { SensorType = "Battery", SensorRange = new[] { new[] { 0, 100 } } } } }
    };
    private static Task IntifaceSettingsCheck()
    {
        Assert(IntifaceConnectionSettings.ParseAddress("192.168.1.9").Port == 12345 &&
            IntifaceConnectionSettings.ParseAddress("ws://[::1]").Port == 12345 &&
            IntifaceConnectionSettings.ParseAddress("wss://phone.local:443/").Port == 443, "Phone addresses lost the default or explicit port.");
        foreach (var value in new[] { "https://example.com", "ws://user:password@phone:12345", "ws://phone:0", "ws://0.0.0.0:12345", "ws://phone:12345/?secret=x" })
        { try { _ = IntifaceConnectionSettings.ParseAddress(value); throw new Exception("Accepted invalid phone address."); } catch (InvalidOperationException) { } }
        var path = Path.Combine(AppContext.BaseDirectory, "intiface-settings-fixture.json");
        try
        {
            var store = new IntifaceConnectionSettings(path); store.Save(new(true, "ws://192.168.1.9:12345/", Guid.NewGuid().ToString("N")));
            var reopened = new IntifaceConnectionSettings(path); reopened.Load(); Assert(reopened.Value == store.Value, "Phone settings did not survive restart.");
            File.WriteAllText(path, "damaged fixture"); reopened = new(path); reopened.Load();
            try { reopened.Save(new()); throw new Exception("Overwrote damaged settings."); } catch (InvalidOperationException) { }
            Assert(File.ReadAllText(path) == "damaged fixture", "Original phone settings changed.");
        }
        finally { File.Delete(path); }
        return Task.CompletedTask;
    }
    private static Task IntifaceDiscoveryCheck()
    {
        // PTR uses a compression pointer back to the question; SRV/A arrive separately.
        var question = IntifaceDiscovery.Query(IntifaceDiscovery.Service, 12); question[2] = 0x84; question[7] = 1;
        var instance = Encoding.ASCII.GetBytes("Intiface fixture");
        var packet = question.Concat(new byte[] { 0xc0, 12, 0, 12, 0, 1, 0, 0, 0, 120, 0, (byte)(instance.Length + 3), (byte)instance.Length })
            .Concat(instance).Concat(new byte[] { 0xc0, 12 }).ToArray();
        var records = IntifaceDiscovery.ReadRecords(packet).ToList();
        Assert(records.Count == 1 && records[0].Target == "Intiface fixture." + IntifaceDiscovery.Service, "Compressed PTR was not decoded.");
        records.Add(new(records[0].Target, 33, 120, "fixture-phone.local", 12345, null));
        records.Add(new("fixture-phone.local", 1, 120, "", 0, IPAddress.Parse("192.168.1.9")));
        Assert(IntifaceDiscovery.Resolve(records).Single().Address.Port == 12345, "Split service records did not resolve.");
        records.Add(new(records[0].Target, 33, 0, "fixture-phone.local", 12345, null));
        Assert(IntifaceDiscovery.Resolve(records).Count == 0, "Goodbye service remained discoverable.");
        var loop = new byte[] { 0, 0, 0x80, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0xc0, 12 };
        Assert(IntifaceDiscovery.ReadRecords(loop).Count == 0 && IntifaceDiscovery.ReadRecords(packet[..^1]).Count == 0, "Malformed DNS reply was accepted.");
        Assert(IntifaceDiscovery.Resolve(new[] { new IntifaceDiscovery.Record("_other._tcp.local", 12, 120, records[0].Target, 0, null) }.Concat(records.Skip(1).Take(2))).Count == 0,
            "A foreign service was offered as Intiface.");
        return Task.CompletedTask;
    }
    private static async Task IntifaceSocketCheck()
    {
        await using var server = new FakeIntifaceServer(); server.Inventory = [PhoneDevice(3)];
        await using var session = new IntifaceWebSocketSession(server.Address, "fixture");
        await session.InitializeAsync(CancellationToken.None);
        await session.RequestAsync("RequestDeviceList", new { }, CancellationToken.None);
        Assert(session.Identity(3)?.StartsWith("named:") == true && session.PersistentIdentity(3), "Phone nickname identity was unavailable.");
        await Task.Delay(650);
        Assert(server.Requests.Count(value => value == "RequestDeviceList") >= 2 && !server.Requests.Contains("Ping"), "A zero-watchdog server received invalid pings.");
        var canceled = await session.RequestAsync("ScalarCmd", new { DeviceIndex = 3, Scalars = new[] { new { Index = 0, Scalar = .8, ActuatorType = "Vibrate" } } }, CancellationToken.None, () => false);
        Assert(canceled.ValueKind == JsonValueKind.Undefined && server.Levels.IsEmpty, "Canceled input crossed the phone socket.");
        var lost = false; session.Lost += () => lost = true; server.DropClients(); await ButtUntil(() => lost);
    }
    private static async Task IntifaceIdentityCheck()
    {
        await using var server = new FakeIntifaceServer(); server.Inventory = [PhoneDevice(1), PhoneDevice(2, null)];
        string named, transient;
        await using (var first = new IntifaceWebSocketSession(server.Address, "same-phone"))
        {
            await first.InitializeAsync(CancellationToken.None); await first.RequestAsync("RequestDeviceList", new { }, CancellationToken.None);
            named = PhoneKeys().ForDevice("intiface-phone", first.Identity(1)!); transient = first.Identity(2)!;
            Assert(!first.PersistentIdentity(2), "Unnamed toy claimed persistent hardware identity.");
        }
        server.Inventory = [PhoneDevice(17), PhoneDevice(18, null)];
        await using var next = new IntifaceWebSocketSession(server.Address, "same-phone");
        await next.InitializeAsync(CancellationToken.None); await next.RequestAsync("RequestDeviceList", new { }, CancellationToken.None);
        Assert(named == PhoneKeys().ForDevice("intiface-phone", next.Identity(17)!) && transient != next.Identity(18), "Phone settings followed a transient device index.");
    }
    private static async Task IntifaceDuplicateCheck()
    {
        await using var server = new FakeIntifaceServer(); server.Inventory = [PhoneDevice(1)];
        await using var adapter = new IntifaceToyAdapter(new(true, server.Address.AbsoluteUri, "fixture"), PhoneKeys);
        await adapter.StartScanAsync(); await ButtUntil(() => adapter.Snapshot.Devices.Count == 1);
        var old = One(adapter).DeviceKey;
        await using var controller = new LocalToyController(adapter, new(), automaticControls: true); controller.SetTestMode(false); controller.Request(old, "v", 60);
        await ButtUntil(() => server.Levels.Any(value => value > 0));
        server.Inventory = [PhoneDevice(1), PhoneDevice(2, "my TOY")];
        await ButtUntil(() => adapter.Snapshot.Availability == ToyAdapterAvailability.Error);
        Assert(!controller.State(old, "v").Active, "Nickname collision retained the previous output claim.");
        await adapter.StartScanAsync();
        await ButtUntil(() => adapter.Snapshot.Devices.Count(value => value.ConnectionState == ToyConnectionState.Connected) == 2);
        Assert(controller.SchedulerState.ActiveFunctions == 0 && adapter.Snapshot.Devices.Where(value => value.ConnectionState == ToyConnectionState.Connected).All(value => !value.PersistentIdentity),
            "A duplicate nickname inherited the original output or saved identity.");
    }
    private static async Task IntifaceReconnectCheck()
    {
        await using var server = new FakeIntifaceServer(); server.Inventory = [PhoneDevice(1)];
        await using var adapter = new RememberedToyAdapter(new IntifaceToyAdapter(new(true, server.Address.AbsoluteUri, "fixture"), PhoneKeys),
            new MemoryToyStore(), TimeSpan.FromMilliseconds(60), TimeSpan.FromMilliseconds(10));
        await adapter.EnableAutomaticConnectionsAsync(); await ButtUntil(() => adapter.Snapshot.Devices.Any(value => value.ConnectionState == ToyConnectionState.Connected));
        var key = adapter.Snapshot.Devices.Single(value => value.ConnectionState == ToyConnectionState.Connected).DeviceKey;
        await using var controller = new LocalToyController(adapter, new(), automaticControls: true);
        controller.SetPreference(new(key, "v", 100, 30)); controller.Request(key, "v", 100); await Task.Delay(70);
        Assert(server.Levels.IsEmpty, "Phone path bypassed Test mode."); controller.SetTestMode(false); controller.Request(key, "v", 100);
        await ButtUntil(() => server.Levels.Any()); Assert(server.Levels.Last() == .3, "Phone output bypassed owner cap.");
        server.DropClients(); await ButtUntil(() => !controller.State(key, "v").Active); server.Inventory = [PhoneDevice(8)];
        await ButtUntil(() => server.Connections >= 2 && adapter.Snapshot.Devices.Any(value => value.DeviceKey == key && value.ConnectionState == ToyConnectionState.Connected));
        var count = server.Levels.Count; await Task.Delay(180);
        Assert(server.Levels.Count == count && controller.State(key, "v").RequestedPercent == 0 && controller.State(key, "v").Preference.Maximum == 30,
            "Reconnect replayed output or lost the retained cap.");
        controller.Request(key, "v", 50); await ButtUntil(() => server.Levels.Count > count); controller.Stop(); await ButtUntil(() => server.Levels.Last() == 0);
    }
    private static async Task IntifaceTimeoutCheck()
    {
        await using var server = new FakeIntifaceServer { OmitResponse = "ScalarCmd", MaxPingTime = 1500 };
        await using var session = new IntifaceWebSocketSession(server.Address, "fixture"); var lost = false; session.Lost += () => lost = true;
        await session.InitializeAsync(CancellationToken.None);
        try { await session.RequestAsync("ScalarCmd", new { DeviceIndex = 1, Scalars = new[] { new { Index = 0, Scalar = .5, ActuatorType = "Vibrate" } } }, CancellationToken.None); throw new Exception("Unacknowledged output succeeded."); }
        catch (InvalidOperationException) { }
        Assert(lost && server.Requests.Count(value => value == "ScalarCmd") == 1 && server.Requests.Contains("Ping"), "Timed-out output was replayed or not disarmed.");
    }
    private static Task IntifaceTransientCheck()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "intiface-preference-fixture.json");
        try
        {
            var store = new LocalToyControlStore(path); store.Save(new("fixture-unnamed", "v", 80, 20), false);
            store.Save(new("fixture-named", "v", 90, 30));
            Assert(store.Get("fixture-unnamed", "v").Maximum == 20 && !File.ReadAllText(path).Contains("fixture-unnamed"), "Transient limits leaked into persisted device mappings.");
        }
        finally { File.Delete(path); }
        return Task.CompletedTask;
    }
    private static void IntifaceUiCheck()
    {
        var settings = new IntifaceConnectionSettings(); var creates = 0;
        using var host = new Form { Width = 750, Height = 480, ShowInTaskbar = false, StartPosition = FormStartPosition.Manual, Location = new Point(-20000, -20000) };
        using var page = new LocalToysPage(_ => { creates++; return new RememberedToyAdapter(ButtAdapter(new FakeButtplug()), new MemoryToyStore()); }, settings,
            _ => Task.FromResult<IReadOnlyList<IntifaceServer>>([new("Fixture phone", "fixture-phone.local", new Uri("ws://192.168.1.9:12345/"))]));
        host.Controls.Add(page); _ = host.Handle; _ = page.Handle;
        page.SourceInput.SelectedIndex = 4; PumpUntil(() => !page.Busy);
        Assert(page.PhoneSource && settings.Value.Enabled && page.PhoneOptions.Controls.Contains(page.FindPhoneButton), "Phone source was not available in Bluetooth options.");
        ClickOffscreen(page.FindPhoneButton); PumpUntil(() => page.PhoneAddressInput.Items.Count == 1 && page.FindPhoneButton.Enabled);
        ClickOffscreen(page.ConnectPhoneButton); PumpUntil(() => !page.Busy && settings.Value.Address.Length > 0);
        Assert(settings.Value.DiscoveryHost == "fixture-phone.local" && settings.Value.Scope.Length > 0 && creates >= 2, "Selecting a discovered phone did not save/recreate its source.");
        var scope = settings.Value.Scope; ClickOffscreen(page.ConnectPhoneButton); PumpUntil(() => !page.Busy);
        Assert(settings.Value.Scope == scope && settings.Value.DiscoveryHost == "fixture-phone.local", "Reapplying the same phone discarded saved identities or discovery fallback.");
        var closing = page.ShutdownAsync(); PumpUntil(() => closing.IsCompleted); closing.GetAwaiter().GetResult();
    }
}

internal sealed class FakeIntifaceServer : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentBag<WebSocket> sockets = [];
    private readonly ConcurrentBag<Task> clients = [];
    private readonly Task accept;
    public object[] Inventory = [];
    public string? OmitResponse;
    public int MaxPingTime, Connections;
    public readonly ConcurrentQueue<string> Requests = new();
    public readonly ConcurrentQueue<double> Levels = new();
    public Uri Address { get; }
    public FakeIntifaceServer()
    { listener.Start(); Address = new Uri($"ws://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/"); accept = Task.Run(AcceptAsync); }
    private async Task AcceptAsync()
    {
        try { while (!lifetime.IsCancellationRequested) { var tcp = await listener.AcceptTcpClientAsync(lifetime.Token); clients.Add(Task.Run(() => ServeAsync(tcp))); } }
        catch (Exception) when (lifetime.IsCancellationRequested) { }
    }
    private async Task ServeAsync(TcpClient tcp)
    {
        using (tcp)
        try
        {
            var stream = tcp.GetStream(); var header = new StringBuilder(); var one = new byte[1];
            while (!header.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal))
            { if (header.Length > 8192 || await stream.ReadAsync(one, lifetime.Token) != 1) return; header.Append((char)one[0]); }
            var key = header.ToString().Split("\r\n").Single(line => line.StartsWith("Sec-WebSocket-Key:", StringComparison.OrdinalIgnoreCase)).Split(':', 2)[1].Trim();
            var hash = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + hash + "\r\n\r\n"), lifetime.Token);
            using var socket = WebSocket.CreateFromStream(stream, true, null, TimeSpan.FromSeconds(10)); sockets.Add(socket); Interlocked.Increment(ref Connections);
            var buffer = new byte[8192];
            while (!lifetime.IsCancellationRequested)
            {
                using var message = new MemoryStream(); WebSocketReceiveResult result;
                do { result = await socket.ReceiveAsync(buffer, lifetime.Token); if (result.MessageType != WebSocketMessageType.Text) return; message.Write(buffer, 0, result.Count); } while (!result.EndOfMessage);
                using var parsed = JsonDocument.Parse(message.ToArray());
                foreach (var element in parsed.RootElement.EnumerateArray())
                {
                    var request = element.EnumerateObject().Single(); var kind = request.Name; var payload = request.Value; var id = payload.GetProperty("Id").GetInt32(); Requests.Enqueue(kind);
                    if (kind == "ScalarCmd") foreach (var scalar in payload.GetProperty("Scalars").EnumerateArray()) Levels.Enqueue(scalar.GetProperty("Scalar").GetDouble());
                    if (kind == OmitResponse) continue;
                    var responseKind = kind == "RequestServerInfo" ? "ServerInfo" : kind == "RequestDeviceList" ? "DeviceList" : kind == "SensorReadCmd" ? "SensorReading" : "Ok";
                    object response = kind == "RequestServerInfo" ? new { Id = id, MessageVersion = 3, MaxPingTime, ServerName = "Fixture phone" } :
                        kind == "RequestDeviceList" ? new { Id = id, Devices = Inventory } :
                        kind == "SensorReadCmd" ? new { Id = id, DeviceIndex = payload.GetProperty("DeviceIndex").GetUInt32(), SensorIndex = 0, SensorType = "Battery", Data = new[] { 72 } } : new { Id = id };
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(new[] { new Dictionary<string, object> { [responseKind] = response } });
                    // Exercise real fragmented WebSocket receive handling.
                    var middle = bytes.Length / 2;
                    await socket.SendAsync(bytes.AsMemory(0, middle), WebSocketMessageType.Text, false, lifetime.Token);
                    await socket.SendAsync(bytes.AsMemory(middle), WebSocketMessageType.Text, true, lifetime.Token);
                }
            }
        }
        catch (Exception) { }
    }
    public void DropClients() { foreach (var socket in sockets) try { socket.Abort(); } catch (ObjectDisposedException) { } }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); listener.Stop(); DropClients();
        try { await Task.WhenAll(clients.Append(accept)).WaitAsync(TimeSpan.FromSeconds(2)); } catch (Exception) { }
    }
}
