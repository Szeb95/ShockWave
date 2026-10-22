using System.Globalization;
using ShockWave.ShaderLab;
using ShockWave.Overlay.Sps;
using ShockWave.Overlay.Toys;

namespace ShockWave.Overlay;

internal sealed class SpsPage : UserControl
{
    private readonly LocalToysPage toys;
    private readonly Func<bool> listening;
    private readonly SpsStore legacyStore;
    private readonly SpsBoardStore store;
    private readonly SpsBoard board = new();
    private readonly Label message = new() { Dock=DockStyle.Bottom,Height=0,ForeColor=Color.FromArgb(255,183,77),BackColor=Color.FromArgb(30,31,34),Padding=new(8,2,8,2),Font=new("Segoe UI",9) };
    private readonly System.Windows.Forms.Timer timer = new() { Interval=33 };
    private SpsBoardDocument document;
    private bool closed, metadataDirty;
    private double lastSave, lastPaint;
    private readonly Func<int> avatarPort;
    private SpsOscQuery? query;
    private string? queryAvatar;
    private double inputResetAt=double.NegativeInfinity;
    private SpsTarget[] routingTargets = [];
    private readonly Dictionary<string,(object Value,double At)> unrecognized = new(StringComparer.Ordinal);
    private readonly SpsDiscovery discovery = new();
    private readonly SpsActivation activation=new();
    internal IReadOnlyList<SpsDiscoveryCandidate> DiscoveryCandidates=>discovery.Candidates(ToyOscRouter.Clock);
    internal IReadOnlyCollection<string> UnrecognizedInputs => unrecognized.Keys;
    public SpsSession Session { get; }
    public bool SignalsEnabled => !closed;
    internal SpsBoard Board => board;
    internal SpsBoardDocument Document => document;
    internal Action<Form>? OptionsPresenter { get; set; }
    public event Action<string,string>? Log;
    private static readonly Color[] Colors = [Color.FromArgb(130,214,75),Color.FromArgb(74,128,255),Color.FromArgb(255,170,84),Color.FromArgb(192,129,238),Color.FromArgb(74,206,190),Color.FromArgb(242,117,150)];

    public SpsPage(LocalToysPage toys,Func<bool> listening,SpsStore? store=null,Func<int>? avatarPort=null)
    {
        this.toys=toys; this.listening=listening; this.avatarPort=avatarPort??(()=>9000); legacyStore=store??new SpsStore();
        this.store=new(Path.ChangeExtension(legacyStore.PathName,".board.json")); document=this.store.Load(legacyStore.Load(),File.Exists(legacyStore.PathName));
        if(document.AllToysToAllHoles && document.AutomaticSources is null) document=document with { AutomaticSources=SpsBoardModel.EffectiveAutomaticSources(document) };
        Session=new(()=>toys.Snapshot,()=>toys.Controller); Session.Configure(SpsBoardModel.Compile(document),ToyOscRouter.Clock);
        Dock=DockStyle.Fill; BackColor=Color.FromArgb(30,31,34); Controls.Add(board); Controls.Add(message);
        board.Document=document; board.HoleLevel=HoleLevel; board.HoleState=HoleState; board.HoleActive=HoleActive;
        board.HoleEnabled=h=>activation.Get(h.Stem,ToyOscRouter.Clock);
        board.AutomaticSourcesRequested+=SetAutomaticSources;
        board.ToyOptionsRequested+=AutomaticToyOptions;
        board.HoleMoved+=(id,x,y)=> { var next=document.Copy(); var index=next.Holes.FindIndex(h=>h.Id==id); next.Holes[index]=next.Holes[index] with { X=x,Y=y }; Commit(next,false); };
        board.LockRequested+=()=>Commit(document with { HolesLocked=!document.HolesLocked },false);
        board.AllToysRequested+=()=>Commit(document with { AllToysToAllHoles=!document.AllToysToAllHoles });
        board.ConnectRequested+=Connect;
        board.WireOptionsRequested+=WireOptions;
        board.HoleOptionsRequested+=HoleOptions;
        board.StopRequested+=()=> { toys.Controller?.Stop(); Session.BlockAll(ToyOscRouter.Clock); SetMessage("All local toys stopped. Release SPS inputs before continuing."); };
        board.ClearRequested+=()=>Commit(document with { Wires=[] });
        board.SetupRequested+=Setup;
        board.DiscoveryRequested+=()=> { if(closed) return; board.SetDiscoveryMode(!board.DiscoveryMode); PaintBoard(); };
        board.DiscoveryAddRequested+=AddDiscovered;
        timer.Tick+=(_,_)=>Tick();
        if((this.store.Warning??(!File.Exists(this.store.PathName)?legacyStore.Warning:null)) is { } warning) SetMessage(warning);
        PaintBoard();
    }
    public void StartMonitoring() { if(closed) return; query??=new SpsOscQuery(); timer.Start(); }
    public void Receive(OscMessage input,double now)
    {
        if(!SignalsEnabled) return;
        if(now>=inputResetAt && ToyOscRouter.Clock-now<=.25) discovery.Observe(input,now);
        if(!Session.Apply(input,now)) { ObserveUnrecognized(input,now); return; }
        if(input.Address=="/avatar/change") { inputResetAt=now; queryAvatar=(string)input.Arguments[0]; query?.Reset(); unrecognized.Clear(); discovery.Reset(); activation.Reset(); }
        var signal=Session.Signals.FirstOrDefault(s=>s.Address==input.Address);
        if(signal is null || document.Holes.All(h=>h.DiscoveryOnly || h.Id!=SpsBoardModel.Key(SpsBoardModel.Stem(signal.Address)))
            || !SpsBoardModel.Discover(document,signal)) return;
        metadataDirty=true; board.Document=document; ConfigureRouting(now);
    }
    public void ResetInput() { if(closed) return; inputResetAt=ToyOscRouter.Clock; queryAvatar=null; query?.Reset(); unrecognized.Clear(); discovery.Reset(); activation.Reset(); Session.Reset(inputResetAt,"OSC link reset"); }
    private void ObserveUnrecognized(OscMessage input,double now)
    {
        if(!double.IsFinite(now) || !SpsSignals.SafeAddress(input.Address) || SpsSignals.Describe(input.Address) is not null || input.Arguments.Length!=1) return;
        var value=input.Arguments[0];
        if(value is not (float or int or bool) || value is float f && (!float.IsFinite(f) || Math.Abs(f)>1000) || value is int i && Math.Abs((long)i)>1000) return;
        if(!unrecognized.ContainsKey(input.Address) && unrecognized.Count>=64) unrecognized.Remove(unrecognized.MinBy(p=>p.Value.At).Key);
        unrecognized[input.Address]=(value,now);
    }
    private IEnumerable<LocalToyDescriptor> LocalDevices=>(toys.Snapshot?.Devices??[]).Where(d=>SpsSignals.LocalDevice(d.DeviceKey));
    private SpsTarget[] AutomaticTargets() => LocalDevices.SelectMany(d=>d.Capabilities.Where(SpsSignals.Scalar)
        .Select(c=>new SpsTarget(d.DeviceKey,c.ChannelId))).Distinct().OrderBy(t=>t.DeviceKey,StringComparer.Ordinal).ThenBy(t=>t.ChannelId,StringComparer.Ordinal).ToArray();
    private bool ConfigureRouting(double now)
    {
        routingTargets=AutomaticTargets();
        try { Session.Configure(SpsBoardModel.Compile(document,routingTargets),now); return true; }
        catch(InvalidDataException e) { Session.Configure([],now); SetMessage("SPS routing paused: "+e.Message); return false; }
    }
    internal void ReceiveQuery(SpsQueryFrame frame)
    {
        if(closed || frame.RequestedAt<=inputResetAt || ToyOscRouter.Clock-frame.RequestedAt>.25) return;
        if(queryAvatar!=frame.Avatar)
        { queryAvatar=frame.Avatar; inputResetAt=frame.RequestedAt; unrecognized.Clear(); discovery.Reset(); activation.Reset(); Session.Reset(inputResetAt,"Avatar contacts refreshed"); }
        activation.Apply(frame.SocketEnabled,frame.RequestedAt);
        // Query inventory is readable even when another OSC app owns the UDP
        // port. Keep those observations out of the routing session until the
        // shared listener is healthy; never promote cached metadata on recovery.
        if(listening()) foreach(var value in frame.Messages) Receive(value,frame.RequestedAt);
        else foreach(var value in frame.Messages) discovery.Observe(value,frame.RequestedAt);
        foreach(var value in frame.DiscoveryMessages) discovery.Observe(value,frame.RequestedAt);
    }
    internal void Tick()
    {
        if(closed) return; var now=ToyOscRouter.Clock; query?.Tick(SignalsEnabled,avatarPort(),ReceiveQuery);
        if(document.AllToysToAllHoles && !routingTargets.SequenceEqual(AutomaticTargets())) ConfigureRouting(now);
        Session.Tick(now,listening());
        if(metadataDirty && now-lastSave>=1 && store.Warning is null && (legacyStore.Warning is null || File.Exists(store.PathName)))
        {
            try { store.Save(document); metadataDirty=false; lastSave=now; }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidDataException) { lastSave=now+5; SetMessage("SPS layout could not be saved: "+e.Message); }
        }
        if(now-lastPaint<.03 || !Visible) return; lastPaint=now; PaintBoard();
    }
    private double HoleLevel(SpsHole hole)
    {
        var rules=Session.Rules.Where(r=>SpsBoardModel.Stem(r.Address)==hole.Stem).ToArray();
        if(rules.Length>0) return rules.Max(r=>Session.RoutedLevel(r.Id));
        return Session.Signals.Where(s=>SpsBoardModel.Stem(s.Address)==hole.Stem && ToyOscRouter.Clock-s.ReceivedAt<=2
            && HoleSources(hole).HasFlag(SpsBoardModel.Source(s)) && !s.Address.EndsWith("NewTip",StringComparison.Ordinal) && !s.Address.EndsWith("NewRoot",StringComparison.Ordinal)).Select(s=>s.Value).DefaultIfEmpty(0).Max();
    }
    private SpsSources HoleSources(SpsHole hole)=>document.AllToysToAllHoles?SpsBoardModel.EffectiveAutomaticSources(document):hole.Sources;
    private bool HoleActive(SpsHole hole)=>Session.Signals.Any(s=>SpsBoardModel.Stem(s.Address)==hole.Stem && ToyOscRouter.Clock-s.ReceivedAt<=2
        && s.Value>0 && HoleSources(hole).HasFlag(SpsBoardModel.Source(s)));
    private string HoleState(SpsHole hole)
    {
        if(hole.DiscoveryOnly) return "Unrecognized SPS format · input setup needed";
        var enabled=activation.Get(hole.Stem,ToyOscRouter.Clock);
        if(enabled==false) return "Off";
        if(enabled==true && !HoleActive(hole)) return "On · waiting for contact";
        var rules=Session.Rules.Where(r=>SpsBoardModel.Stem(r.Address)==hole.Stem && r.Enabled).ToArray();
        if(rules.Length==0) return HoleActive(hole)?"Contact received · connect a toy":"Waiting for contact";
        var states=rules.Select(r=>Session.RuleState(r,ToyOscRouter.Clock)).ToArray();
        if(states.Any(s=>s is "Routing" or "Test preview")) return $"{HoleLevel(hole)*100:0}% · {(Session.TestMode?"Test mode":"Output")}";
        if(states.Contains("Waiting for depth calibration")) return "Withdraw and approach to calibrate";
        if(states.Contains("Release input to rearm")) return "Release inputs after Stop";
        return states[0];
    }
    private void PaintBoard()
    {
        var rows=new List<SpsToyRow>();
        foreach(var device in LocalDevices)
            foreach(var capability in device.Capabilities.Where(SpsSignals.Scalar))
            {
                var target=new SpsTarget(device.DeviceKey,capability.ChannelId); var connected=device.ConnectionState==ToyConnectionState.Connected;
                var percent=0; var sent=0;
                try
                {
                    var state=toys.Controller?.State(device.DeviceKey,capability.ChannelId);
                    percent=state?.RequestedPercent??0; sent=(int)Math.Round(state?.SentPercent??0);
                }
                catch(InvalidOperationException) { }
                var color=Colors[rows.Count%Colors.Length];
                rows.Add(new(target,device.DisplayName,capability.DisplayName,
                    !connected?"Disconnected":$"{(toys.Controller?.TestMode!=false?"Preview":"Output")} {sent}%"+(device.BatteryPercent is int battery ? $" · battery {battery}%" : ""),color,connected,percent));
            }
        foreach(var target in document.Wires.Select(w=>w.Target).Distinct().Where(t=>SpsSignals.LocalDevice(t.DeviceKey) && rows.All(r=>r.Target!=t)))
        {
            var capability=toys.Snapshot?.Devices.FirstOrDefault(d=>d.DeviceKey==target.DeviceKey)?.Capabilities.FirstOrDefault(c=>c.ChannelId==target.ChannelId);
            if(capability is not null && !SpsSignals.Scalar(capability)) continue;
            rows.Add(new(target,"Saved toy",capability?.DisplayName??"Saved function","Unavailable · assignment retained",Colors[rows.Count%Colors.Length],false,0));
        }
        board.Rows=rows; board.Document=document;
        board.Discoveries=DiscoveryCandidates.Select(c=>new SpsDiscoveryRow(c.Id,c.Name,c.Kind,c.Stem,c.Inputs.Length,
            document.Holes.Any(h=>h.Id==c.Id && (!h.DiscoveryOnly || c.SupportedInputs.Length==0)),c.SupportedInputs.Length>0)).ToArray();
        board.Status=!listening()?"OSC UNAVAILABLE":Session.Signals.Count==0?"WAITING FOR SPS":Session.Signals.Any(s=>ToyOscRouter.Clock-s.ReceivedAt<=2)?"SPS CONNECTED":"SPS INPUT EXPIRED";
        board.StatusDetail=!listening()?"Toy output paused · check OSC port":Session.TestMode?"Test mode · no toy output":$"{(document.AllToysToAllHoles?"All holes":"Manual links")} · {Session.ActiveTargets} active functions";
        board.Invalidate();
    }
    private bool Commit(SpsBoardDocument next,bool routing=true)
    {
        try
        {
            if(next.AllToysToAllHoles && next.AutomaticSources is null) next=next with { AutomaticSources=SpsBoardModel.EffectiveAutomaticSources(next) };
            store.Save(next); document=next; metadataDirty=false;
            var ready=!routing || ConfigureRouting(ToyOscRouter.Clock);
            board.Document=document; PaintBoard(); if(ready) SetMessage(""); return true;
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidDataException)
        { SetMessage("SPS change could not be saved: "+e.Message); board.Invalidate(); return false; }
    }
    private void Connect(SpsTarget target,string holeId)
    {
        if(document.AllToysToAllHoles || !SpsSignals.LocalDevice(target.DeviceKey)) return;
        if(document.Holes.FirstOrDefault(h=>h.Id==holeId)?.DiscoveryOnly==true) { SetMessage("This possible SPS contact needs a supported input format before connecting a toy."); return; }
        if(document.Wires.FirstOrDefault(w=>w.Target==target && w.HoleId==holeId) is { } existing) { WireOptions(existing.Id); return; }
        var next=document.Copy(); next.Wires.Add(new() { HoleId=holeId,Target=target });
        if(Commit(next)) Log?.Invoke("sps","Toy function connected to "+document.Holes.Single(h=>h.Id==holeId).Name+".");
    }
    internal void AddDiscovered(string id)
    {
        if(closed) return;
        var candidate=DiscoveryCandidates.FirstOrDefault(c=>c.Id==id); if(candidate is null) return;
        var next=document.Copy(); var index=next.Holes.FindIndex(h=>h.Id==id);
        if(index>=0 && (!next.Holes[index].DiscoveryOnly || candidate.SupportedInputs.Length==0)) return;
        if(index<0 && next.Holes.Count>=64) { SetMessage("The body map can contain up to 64 zones."); return; }
        var supported=candidate.SupportedInputs.Length>0;
        var inputs=(supported ? candidate.SupportedInputs : candidate.Inputs).Take(16).ToList();
        var position=SpsBoardModel.Hole(inputs[0],next.Holes.Count);
        var hole=new SpsHole(id,candidate.Name,candidate.Kind,candidate.Stem,position.X,position.Y,
            SpsSources.OtherSockets|SpsSources.OtherBody|SpsSources.OtherPlugs,inputs) { DiscoveryOnly=!supported };
        if(index>=0)
        {
            var previous=next.Holes[index]; next.Holes[index]=hole with { X=previous.X,Y=previous.Y,Sources=previous.Sources };
            // Imported inactive metadata may contain saved wire settings. Add
            // must not silently enable those when the format becomes recognized.
            for(var wireIndex=0;wireIndex<next.Wires.Count;wireIndex++)
                if(next.Wires[wireIndex].HoleId==id) next.Wires[wireIndex]=next.Wires[wireIndex] with { Enabled=false };
        }
        else next.Holes.Add(hole);
        if(Commit(next)) SetMessage(!supported ? "Added to the body map. This format needs input setup before it can control a toy." :
            document.AllToysToAllHoles?"Added. The shared hole options apply when fresh signals arrive.":"Added to the body map. Turn Discovery off to place and connect it.");
    }
    private void Present(Form options)
    { if(OptionsPresenter is not null) OptionsPresenter(options); else options.ShowDialog(FindForm()); }
    private void WireOptions(string id)
    {
        if(document.AllToysToAllHoles) return;
        if(document.Wires.FirstOrDefault(w=>w.Id==id) is not { } wire) return;
        if(!SpsSignals.LocalDevice(wire.Target.DeviceKey)) return;
        var hole=document.Holes.Single(h=>h.Id==wire.HoleId); var row=board.Rows.FirstOrDefault(r=>r.Target==wire.Target);
        using var options=new SpsWireOptions(wire,hole,row?.Function??"Toy function"); Present(options);
        if(options.DialogResult!=DialogResult.OK) return;
        var next=document.Copy();
        if(options.Disconnect) next.Wires.RemoveAll(w=>w.Id==id);
        else next.Wires[next.Wires.FindIndex(w=>w.Id==id)]=options.Value;
        Commit(next);
    }
    private void HoleOptions(string id)
    {
        if(document.Holes.FirstOrDefault(h=>h.Id==id) is not { } hole) return;
        using var options=new SpsHoleOptions(hole); Present(options); if(options.DialogResult!=DialogResult.OK) return;
        var next=document.Copy(); var index=next.Holes.FindIndex(h=>h.Id==id); next.Holes[index]=next.Holes[index] with { Sources=options.Value.Sources }; Commit(next);
    }
    private void SetAutomaticSources(SpsSources sources)
    {
        if(closed || !document.AllToysToAllHoles) return;
        Commit(document with { AutomaticSources=sources });
    }
    private void AutomaticToyOptions(SpsTarget target)
    {
        if(closed || !document.AllToysToAllHoles || !SpsSignals.LocalDevice(target.DeviceKey)) return;
        var setting=document.AutomaticToyOptions.FirstOrDefault(o=>o.Target==target)??new SpsWire { Id=SpsBoardModel.Key("auto/"+target.DeviceKey+"/"+target.ChannelId),Target=target };
        var row=board.Rows.FirstOrDefault(r=>r.Target==target);
        using var options=new SpsWireOptions(setting,null,row?.Function??"Toy function",automatic:true); Present(options);
        if(options.DialogResult!=DialogResult.OK) return;
        var next=document.Copy(); next.AutomaticToyOptions.RemoveAll(o=>o.Target==target); next.AutomaticToyOptions.Add(options.Value); Commit(next);
    }
    private void Setup()
    {
        var menu=new ContextMenuStrip { BackColor=Color.FromArgb(41,43,47),ForeColor=Color.White };
        menu.Items.Add("Add custom input…",null,(_,_)=>CustomInput());
        var received=new ToolStripMenuItem("Add received input…");
        foreach(var item in unrecognized.Where(p=>ToyOscRouter.Clock-p.Value.At<=120).OrderByDescending(p=>p.Value.At))
        {
            var address=item.Key; received.DropDownItems.Add(address[19..]+" = "+Convert.ToString(item.Value.Value,CultureInfo.InvariantCulture),null,(_,_)=>CustomInput(address));
        }
        if(received.DropDownItems.Count==0) received.DropDownItems.Add("No other scalar inputs received in the last two minutes").Enabled=false;
        menu.Items.Add(received);
        menu.Items.Add("Import connections…",null,(_,_)=>Import());
        menu.Items.Add("Export connections…",null,(_,_)=>Export());
        menu.Closed+=(_,_)=>menu.Dispose(); menu.Show(board,new Point(24,board.Height-40));
    }
    private void CustomInput(string? receivedAddress=null)
    {
        using var form=new Form { Text="Custom SPS input",ClientSize=new(520,200),BackColor=Color.FromArgb(41,43,47),ForeColor=Color.White,StartPosition=FormStartPosition.CenterParent,
            FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,ShowInTaskbar=false };
        var name=new TextBox { Text=receivedAddress is null?"Custom input":receivedAddress[19..][..Math.Min(80,receivedAddress.Length-19)],Bounds=new(18,30,480,26),MaxLength=80 };
        var address=new TextBox { Text=receivedAddress??"/avatar/parameters/MySpsInput",Bounds=new(18,80,480,26),MaxLength=256 };
        var caption=new Label { Text="Name / exact exported OSC parameter",Bounds=new(18,8,480,22),ForeColor=Color.White };
        var button=new Button { Text="Add input",Bounds=new(375,140,123,36),DialogResult=DialogResult.OK };
        form.Controls.AddRange([caption,name,address,button]); form.AcceptButton=button; Present(form); if(form.DialogResult!=DialogResult.OK) return;
        try
        {
            var rule=new SpsRule { Name=name.Text.Trim(),Address=address.Text.Trim() }; SpsStore.Validate([rule]); var next=document.Copy();
            var hole=SpsBoardModel.Hole(rule.Address,next.Holes.Count) with { Name=rule.Name }; if(next.Holes.All(h=>h.Id!=hole.Id)) next.Holes.Add(hole); Commit(next);
        }
        catch(InvalidDataException e) { SetMessage(e.Message); }
    }
    private void Import()
    {
        using var dialog=new OpenFileDialog { Filter="SPS connections (*.json)|*.json",Title="Import SPS connections" }; if(dialog.ShowDialog(this)!=DialogResult.OK) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 512 * 1024) throw new InvalidDataException("SPS layout is too large.");
            using var json=System.Text.Json.JsonDocument.Parse(File.ReadAllText(dialog.FileName));
            var next=json.RootElement.TryGetProperty("holes",out _)?SpsBoardStore.Read(dialog.FileName):SpsBoardModel.FromRules(SpsStore.Read(dialog.FileName));
            if(Commit(next)) ResetInput();
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidDataException) { SetMessage("Import failed: "+e.Message); }
    }
    private void Export()
    {
        using var dialog=new SaveFileDialog { Filter="SPS connections (*.json)|*.json",FileName="sps-connections.json" }; if(dialog.ShowDialog(this)!=DialogResult.OK) return;
        try { SpsBoardStore.Export(dialog.FileName,document); }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or InvalidDataException) { SetMessage("Export failed: "+e.Message); }
    }
    private void SetMessage(string value) { message.Text=value; message.Height=value.Length==0?0:26; }
    public void Shutdown() { if(closed) return; closed=true; timer.Stop(); query?.Dispose(); discovery.Reset(); activation.Reset(); board.Discoveries=[]; Session.Dispose(); }
    protected override void Dispose(bool disposing) { if(disposing) { Shutdown(); timer.Dispose(); } base.Dispose(disposing); }
}
