using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using ShockWave.Overlay;
using ShockWave.Overlay.Toys;

internal static class Program
{
    private static int passed;
    private static string artifacts = "";
    [STAThread] private static int Main(string[] args)
    {
        artifacts = Path.GetFullPath("work/remote-checks"); Directory.CreateDirectory(artifacts);
        Application.EnableVisualStyles();
        try
        {
            Receiver(); Inventory(); Panel();
            if (args.Contains("--package-startup")) SharingCode();
            if (args.Contains("--internet")) Internet();
            File.WriteAllText(Path.Combine(artifacts, "native-report.json"), JsonSerializer.Serialize(new { passed, PhysicalOutput = false, PublicHelper = args.Contains("--internet"), PackagedStartup = args.Contains("--package-startup") }));
            Console.WriteLine($"Remote connection: {passed} checks passed"); return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); File.WriteAllText(Path.Combine(artifacts,"native-failure.txt"),e.ToString()); return 1; }
    }
    private static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, RemoteSession.Json);
    private static void Check(bool value, string reason) { if (!value) throw new Exception(reason); passed++; Console.WriteLine("PASS " + reason); }
    private static void Wait(Func<bool> condition, string reason, int timeout = 3000)
    { var until = Environment.TickCount64 + timeout; while (!condition()) { if (Environment.TickCount64 > until) throw new Exception(reason); Thread.Sleep(10); } }
    private static void Receiver()
    {
        using var f = new Fixture(); var r = f.Session; var c = f.Controller;
        c.SetPreference(c.State("fixture", "v1").Preference with { Sensitivity = 50, Maximum = 30 });
        var info = r.PublishInventory(); var t = info.Toys.Single(); var v = t.Functions[0];
        Check(t.Id != "fixture" && v.Id != "v1" && t.Id.Length == 32, "session aliases hide hardware/local keys");
        var frame = new RemoteLevel(t.Id,v.Id,80,1);
        r.Receive("state", Element(new[] { frame }));
        Wait(() => f.Adapter.Writes.Any(w => w.Value == 6), "receiver applies owner caps");
        Check(c.State("fixture","v1").RequestedPercent == 80 && c.State("fixture","v1").SentPercent == 30, "remote input passes actual owner sensitivity and maximum gate");
        c.Request("fixture","v1",14);
        r.Receive("state", Element(new[] { frame with { Percent = 100 } }));
        r.Receive("state", Element(new[] { frame with { Percent = 100 } }));
        Check(c.State("fixture","v1").RequestedPercent == 14, "owner edit rejects old peer claim without stopping owner input");
        r.Disconnect(); Check(c.State("fixture","v1").RequestedPercent == 14, "disconnect ends only peer-owned leases");
        c.Stop(); Wait(() => !c.State("fixture","v1").Stopping, "zero settles");
        info = r.PublishInventory(); t = info.Toys[0]; v = t.Functions[0]; frame = new(t.Id,v.Id,70,2);
        r.Receive("state", Element(new[] { frame }), 100);
        Wait(() => c.State("fixture","v1").RequestedPercent == 0, "remote lease expires without UI/network tick"); passed++;
        r.Receive("state", Element(new[] { frame })); Check(c.State("fixture","v1").RequestedPercent == 0, "expired claim cannot rearm itself");
        Wait(() => !c.State("fixture","v1").Stopping, "expiry zero settles");
        r.Receive("state", Element(new[] { frame with { Claim = 3 } })); Check(c.State("fixture","v1").RequestedPercent == 70, "fresh peer gesture can claim again without extra permission dialog");
        c.Stop(); r.Receive("state", Element(new[] { frame with { Claim = 3, Percent = 100 } }));
        Check(c.State("fixture","v1").RequestedPercent == 0, "owner Stop cannot be undone by old remote state");
        Wait(() => !c.State("fixture","v1").Stopping, "stop settles");
        r.Receive("state", Element(new[] { frame with { Claim = 4 }, new RemoteLevel(new string('a',32),new string('b',32),100,4) }));
        Check(c.State("fixture","v1").RequestedPercent == 0, "snapshot with unknown target is atomically rejected");
        bool invalid = false; try { r.Receive("state", Element(new[] { frame with { Percent = 101 } })); } catch (JsonException) { invalid = true; }
        Check(invalid && c.State("fixture","v1").RequestedPercent == 0, "out-of-range remote state has no output");
        f.Config.DryRun = true; c.SetTestMode(true); var writes = f.Adapter.Writes.Count;
        r.Receive("state", Element(new[] { frame with { Claim = 5 } })); Thread.Sleep(60);
        Check(f.Adapter.Writes.Count == writes, "receiver Test mode blocks physical submissions");
        r.Disconnect(); Check(c.State("fixture","v1").RequestedPercent == 0, "disconnect clears incoming activity");
    }
    private static void Inventory()
    {
        using var f = new Fixture(); var r = f.Session;
        var toy = new RemoteToyInfo(new string('a',32),"Friend toy",60,[new(new string('b',32),"Vibration","Vibration",45)]);
        r.Receive("inventory", Element(new RemoteInventory([toy], [new(new string('c',32),"Friend shocker","PiShock",30,500)])));
        Check(r.Toys.Snapshot.Devices.Single().DeviceKey.StartsWith("remote/") && r.Shockers.Single().Maximum == 30, "remote toy inventory and future shocker inventory remain separate");
        using var panel = new ToyControlSession(() => f.Adapter.Snapshot, () => f.Controller) { Remote = r.Toys };
        panel.Model.ChangeRoute(2); panel.Tick(ToyOscRouter.Clock);
        Check(panel.Model.FunctionCount == 3, "unified list displays local functions and friend's remote function");
        panel.Action("InRemote", false); panel.Tick(ToyOscRouter.Clock);
        Check(panel.Model.FunctionCount == 3 && panel.Model.Toys.Last().Name=="Remote Friend toy", "legacy destination controls preserve the unified list and Remote name prefix");
        panel.Action("InRow2",false); panel.Draw(new(600,300),false,true); panel.Tick(ToyOscRouter.Clock);
        Check(r.Toys.Levels().Length == 1 && f.Controller.State("fixture","v1").RequestedPercent == 0, "drawing a remote toy produces app state without touching local Bluetooth");
        r.Toys.Stop(); panel.Tick(ToyOscRouter.Clock);
        Check(panel.Model.Functions.All(row => row.Function.Level == 0), "remote rejection/Stop invalidates panel input");
        using(var noBluetooth = new ToyControlSession(()=>null,()=>null) { Remote = r.Toys })
        {
            noBluetooth.Model.ChangeRoute(1); noBluetooth.Tick(ToyOscRouter.Clock);
            noBluetooth.Action("InRow0",false); noBluetooth.Draw(new(600,300),false,true); noBluetooth.Tick(ToyOscRouter.Clock);
            Check(!noBluetooth.TestMode && r.Toys.Levels().Length==1,"controller without a Bluetooth backend can operate remote toys and shows the actual Test mode");
        }
        r.Disconnect(); panel.Tick(ToyOscRouter.Clock); Check(panel.Model.FunctionCount == 2, "disconnect removes remote functions while preserving local toy inventory");
        bool invalid = false; try { r.Receive("inventory",Element(new RemoteInventory([toy,toy],[]))); } catch (JsonException) { invalid=true; }
        Check(invalid && r.Toys.Snapshot.Devices.Count == 0, "duplicate inventory is rejected before partial replacement");
        foreach (var raw in new[] { "{\"toys\":[null],\"shockers\":[]}", "{\"toys\":[],\"shockers\":[null]}" })
        {
            bool rejected=false; using var doc=JsonDocument.Parse(raw);
            try { r.Receive("inventory",doc.RootElement); } catch(JsonException) { rejected=true; }
            Check(rejected,"null inventory elements are rejected without crashing or partial replacement");
        }
        using(var doc=JsonDocument.Parse("[null]"))
        {
            bool rejected=false; try { r.Receive("state",doc.RootElement); } catch(JsonException) { rejected=true; }
            Check(rejected,"null state elements are rejected before output");
        }
    }
    private static void Panel()
    {
        using var toys = new LocalToysPage(); using var form = new OverlayForm(new BridgeConfig(),initialSaveConfig:_=>{},initialLocalToys:toys);
        Button Nav(string name) => (Button)typeof(OverlayForm).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(form)!;
        Check(Nav("remoteNav").Top > Nav("spsNav").Top && Nav("remoteNav").Top < Nav("logsNav").Top, "Connect tab is between SPS and Log");
        using var f = new Fixture(); using var page = new RemotePage(f.Session,()=>"node.exe",Path.GetFullPath("bridge"));
        using var preview = new Form { Width=1050,Height=870,ShowInTaskbar=false,Location=new(-32000,-32000),StartPosition=FormStartPosition.Manual };
        preview.Controls.Add(page); preview.Show(); Application.DoEvents();
        using var bitmap = new Bitmap(page.Width,page.Height); page.DrawToBitmap(bitmap,page.ClientRectangle); bitmap.Save(Path.Combine(artifacts,"connect-page.png")); preview.Hide();
        Check(!f.Session.Enabled && !f.Session.Connected, "opening Connect page starts no helper, account, Bluetooth or output");
    }
    private static void SharingCode()
    {
        var package = Environment.GetEnvironmentVariable("SHOCKWAVE_REMOTE_PACKAGE")
            ?? throw new Exception("Set SHOCKWAVE_REMOTE_PACKAGE for the published sharing-code check.");
        using var fixture = new Fixture();
        using var page = new RemotePage(fixture.Session, () => Path.Combine(package, "node.exe"), Path.Combine(package, "bridge"));
        using var preview = new Form { Width=1050,Height=870,ShowInTaskbar=false,Location=new(-32000,-32000),StartPosition=FormStartPosition.Manual };
        preview.Controls.Add(page); preview.Show(); Application.DoEvents();
        T Field<T>(string name) => (T)typeof(RemotePage).GetField(name, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(page)!;
        Check(Field<TextBox>("own").Text.Length == 0 && !Field<Button>("copy").Enabled, "sharing code is initially empty without starting a connection");
        Field<Button>("connect").PerformClick();
        Wait(() => { Application.DoEvents(); return Field<TextBox>("own").Text.StartsWith("SW1."); }, "Connect button displays the generated sharing code", 15000);
        Check(fixture.Session.Enabled && Field<TextBox>("own").Text == fixture.Session.Invitation && Field<Button>("copy").Enabled,
            "actual Connect button fills the code field and enables Copy my code using packaged dependencies");
        Check(fixture.Adapter.Writes.IsEmpty, "generating a sharing code causes no toy output");
        Field<Button>("disconnect").PerformClick();
        Check(!fixture.Session.Enabled && !fixture.Session.Connected && Field<Button>("connect").Enabled,
            "Disconnect after sharing-code generation restores Connect without starting hardware");
        preview.Hide();
    }
    private static void Internet()
    {
        using var a = new Fixture(4); using var b = new Fixture(4);
        a.Session.Diagnostic += text => Console.WriteLine("A " + text); b.Session.Diagnostic += text => Console.WriteLine("B " + text);
        var package = Environment.GetEnvironmentVariable("SHOCKWAVE_REMOTE_PACKAGE");
        var node = package is null ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"nodejs","node.exe") : Path.Combine(package,"node.exe");
        var directory = package is null ? Path.GetFullPath("bridge") : Path.Combine(package,"bridge");
        a.Session.Connect("",node,directory); bool joined=false;
        Wait(() =>
        {
            a.Session.Tick(); b.Session.Tick();
            if (!joined && a.Session.Invitation.Length>0) { joined=true; b.Session.Connect(a.Session.Invitation,node,directory); }
            return a.Session.Connected && b.Session.Connected && a.Session.Toys.Snapshot.Devices.Count>0 && b.Session.Toys.Snapshot.Devices.Count>0;
        },"two native sessions connect through public helper",55000);
        Check(true,"two native app sessions automatically exchange inventory over direct WebRTC");
        var oldId = a.Session.Toys.Snapshot.Devices[0].DeviceKey;
        var outgoing = a.Session.Toys.Snapshot.Devices.Select(t => a.Session.Toys.BeginInput(t.DeviceKey,t.Capabilities[0].ChannelId,Guid.NewGuid(),65,TimeSpan.FromSeconds(2))).ToArray();
        var reverse = b.Session.Toys.Snapshot.Devices.Select(t => b.Session.Toys.BeginInput(t.DeviceKey,t.Capabilities[1].ChannelId,Guid.NewGuid(),45,TimeSpan.FromSeconds(2))).ToArray();
        Wait(() =>
        {
            a.Session.Tick(); b.Session.Tick();
            foreach(var lease in outgoing) a.Session.Toys.UpdateInput(lease,65,TimeSpan.FromSeconds(2));
            foreach(var lease in reverse) b.Session.Toys.UpdateInput(lease,45,TimeSpan.FromSeconds(2));
            return b.Adapter.Snapshot.Devices.All(t => b.Controller.State(t.DeviceKey,"v1").RequestedPercent==65) &&
                a.Adapter.Snapshot.Devices.All(t => a.Controller.State(t.DeviceKey,"pump").RequestedPercent==45);
        },"remote UI state reaches actual receiver gate",6000);
        Check(outgoing.Length==4 && reverse.Length==4,"four toys work simultaneously in both directions through simulated receiver gates");
        Check(a.Session.PublishInventory().Toys.Length==4,"remote inventory is never forwarded as locally owned toys");
        b.Session.Disconnect();
        Wait(() => { a.Session.Tick(); return !a.Session.Connected && b.Controller.State("fixture","v1").RequestedPercent==0; },"disconnect revokes both session directions",5000);
        Check(a.Session.Toys.Snapshot.Devices.Count==0,"either side disconnect clears peer inventory and incoming output");
        b.Session.Connect(a.Session.Invitation,node,directory);
        try { Wait(() => { a.Session.Tick(); b.Session.Tick(); return a.Session.Connected && b.Session.Connected && a.Session.Toys.Snapshot.Devices.Count==4; },"reconnect with both switches on",15000); }
        catch { Console.WriteLine("A status: " + a.Session.Status + "; B status: " + b.Session.Status); throw; }
        Check(a.Session.Toys.Snapshot.Devices[0].DeviceKey!=oldId && a.Session.Toys.Levels().Length==0 && b.Adapter.Snapshot.Devices.All(t=>b.Controller.State(t.DeviceKey,"v1").RequestedPercent==0),
            "reconnect automatically restores devices with fresh aliases and no old levels or loops");
    }
    private sealed class Fixture : IDisposable
    {
        public Adapter Adapter { get; } public BridgeConfig Config { get; } = new(); public LocalToyController Controller { get; }
        public RemoteSession Session { get; }
        public Fixture(int count=1) { Adapter=new(count); Controller=new(Adapter,new LocalToyControlStore(),true); Controller.SetTestMode(false); Session=new(()=>Adapter.Snapshot,()=>Controller,()=>Config,(_,_,_,_,_)=>{}); }
        public void Dispose() { Session.Dispose(); Controller.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
    private sealed class Adapter : ILocalToyAdapter, ILocalToyOutput
    {
        public event Action? Changed { add {} remove {} } public event Action<string>? OutputFailed { add {} remove {} }
        public ConcurrentQueue<(string Channel,int Value)> Writes { get; } = new();
        public ToyAdapterSnapshot Snapshot { get; }
        public Adapter(int count) { Snapshot = new(ToyAdapterAvailability.Ready,false,"Fixture",Enumerable.Range(0,count).Select(i=>new LocalToyDescriptor(i==0 ? "fixture" : "fixture-"+i,"simulation","Fixture "+i,"Mock",ToyConnectionState.Connected,
            [new("v1",ToyCapabilityType.Vibration,"Vibration",0,20,1,null,null),new("pump",ToyCapabilityType.Pump,"Pump",0,3,1,null,null)],true,70,DateTimeOffset.UtcNow,10,true)).ToArray()); }
        public void SendLevel(string device,string channel,int value) => Writes.Enqueue((channel,value));
        public Task StartScanAsync(CancellationToken c=default)=>Task.CompletedTask; public Task StopScanAsync(CancellationToken c=default)=>Task.CompletedTask;
        public Task ConnectAsync(string key,CancellationToken c=default)=>Task.CompletedTask; public Task DisconnectAsync(string key,CancellationToken c=default)=>Task.CompletedTask;
        public Task ReadBatteryAsync(string key,CancellationToken c=default)=>Task.CompletedTask; public ValueTask DisposeAsync()=>ValueTask.CompletedTask;
    }
}
