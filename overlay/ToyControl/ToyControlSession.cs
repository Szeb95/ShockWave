using System.Drawing;
using ShockWave.ShaderLab;
using ShockWave.Overlay.Toys;
using ShockWave.ToyControl.Shared;

namespace ShockWave.Overlay;

// All methods run on the owning UI thread. Hardware leases expire independently
// in LocalToyController if this loop, the OSC peer or the UI stops progressing.
internal sealed class ToyControlSession : IDisposable
{
    private sealed record Binding(ToyInputLease Lease, bool Vrc, IToyInputController Controller);
    private readonly Func<ToyAdapterSnapshot?> inventory;
    private readonly Func<LocalToyController?> getController;
    private readonly Dictionary<(string Toy, string Function), Binding> bindings = [];
    private readonly Guid source = Guid.NewGuid();
    private LocalToyController? controller;
    private string inventorySignature = "";
    private bool drawingVrc, disposed, blockDesktop;
    private int remoteResetRevision;
    private readonly TimeSpan leaseLife = TimeSpan.FromSeconds(2);
    public PanelModel Model { get; } = new() { AdaptivePaging = true };
    private RemoteToyController? remote;
    public RemoteToyController? Remote
    {
        get=>remote;
        set
        {
            if(ReferenceEquals(remote,value)) return;
            foreach(var key in bindings.Keys.Where(k=>k.Toy.StartsWith("remote/",StringComparison.Ordinal)).ToArray()) End(key,true);
            if(remote is not null) remote.InventoryChanged-=RemoteInventoryChanged;
            remote=value;
            if(!disposed && remote is not null) remote.InventoryChanged+=RemoteInventoryChanged;
            RemoteInventoryChanged();
        }
    }
    private void RemoteInventoryChanged() { if(!disposed) Sync(); }
    public ShockerControlSession Shockers { get; } = new();
    public bool ShockerPage { get; private set; }
    public PanelModel DisplayModel => ShockerPage ? Shockers.Model : Model;
    public event Action? PageChanged;
    public event Action<string>? Log;
    private bool lastTestMode;
    public void SetPage(bool shockers)
    {
        if(ShockerPage==shockers) return;
        Model.Release(false); Shockers.Cancel(); Vrc.BlockTouch(); ShockerPage=shockers;
        Vrc.ResetShockerView(); InputModeRevision++; PageChanged?.Invoke();
    }
    private IToyInputController? ControllerFor(string toy) => toy.StartsWith("remote/", StringComparison.Ordinal) ? Remote : controller;
    public VrcToyInput Vrc { get; }
    private bool clearRequested;
    public int ClearRevision { get; private set; }
    public bool ClearRequested { get => clearRequested; set { clearRequested=value; if(value) ClearRevision++; } }
    public string Notice { get; private set; } = "Select connected functions to draw";
    public bool TestMode => controller?.TestMode ?? Remote?.TestMode ?? true;
    public bool TouchFromVrc => Model.Touching && drawingVrc;
    public void Slider(PointF point,int hit)
    {
        var rect=ToyControlLayout.Slider(hit-30);
        Shockers.SetSlider(hit,1-(point.Y-rect.Top)/rect.Height);
    }
    public bool PointerMode { get; private set; }
    public int InputModeRevision { get; private set; }
    public ToyControlSession(Func<ToyAdapterSnapshot?> inventory, Func<LocalToyController?> getController)
    {
        this.inventory = inventory; this.getController = getController; Vrc = new(this);
    }
    public void Tick(double now)
    {
        if (disposed) return;
        Sync(); Model.AdvanceTo(now); Shockers.Tick(now);
        if(lastTestMode!=TestMode) { lastTestMode=TestMode; Shockers.Cancel(); Vrc.BlockTouch(); }
        Vrc.Tick(now);
        foreach (var (key, binding) in bindings.ToArray())
        {
            var row = Find(key);
            if (row is null || !row.Connected || binding.Vrc && !Vrc.IsLive)
            { End(key, reset: true); continue; }
            if (!binding.Controller.UpdateInput(binding.Lease, Percent(row.Function.Level), leaseLife))
            { bindings.Remove(key); ResetFromOwner(key); if (!key.Toy.StartsWith("remote/", StringComparison.Ordinal)) Notice = "Control reset · use fresh input"; }
        }
    }
    private static int Percent(float level) => (int)Math.Round(Math.Clamp(level, 0, 1) * 100);
    private FunctionBinding? Find((string Toy, string Function) key) => Model.Functions.FirstOrDefault(f => f.Toy.Id == key.Toy && f.Function.Id == key.Function);
    private void Sync()
    {
        var nextController = getController();
        if (!ReferenceEquals(controller, nextController))
        {
            foreach (var key in bindings.Keys.ToArray()) End(key, true);
            controller = nextController; inventorySignature = ""; Model.Release(false); Model.StopAll(); Model.ReplaceInventory([]); Vrc.BlockTouch();
        }
        var snapshot = inventory();
        var localDevices = snapshot?.Devices ?? [];
        var remoteDevices = Remote?.Snapshot.Devices ?? [];
        var devices = localDevices.Concat(remoteDevices).Take(PanelModel.MaximumToys).ToArray();
        var updates = devices.Select(d => new ToyUpdate(d.DeviceKey, Name(d.DisplayName),
            d.ConnectionState == ToyConnectionState.Connected && d.CapabilitiesKnown && d.Capabilities.Any(c => c.NativeMaximum is > 0),
            d.BatteryPercent ?? -1, null,
            d.Capabilities.Count == 0 ? [new FunctionUpdate("unknown", "Functions unavailable")] :
                d.Capabilities.Take(PanelModel.MaximumFunctionsPerToy).Select(c => new FunctionUpdate(c.ChannelId, Name(c.DisplayName))).ToArray())).ToArray();
        var signature = System.Text.Json.JsonSerializer.Serialize(updates);
        if (signature != inventorySignature)
        {
            try { Model.ReplaceInventory(updates); inventorySignature = signature; }
            catch (FormatException) { Notice = "Too many functions for this controller"; foreach (var key in bindings.Keys.ToArray()) End(key, true); Model.ReplaceInventory([]); }
        }
        foreach (var (key, binding) in bindings.ToArray())
        {
            var row = Find(key);
            if (row?.Connected != true) { End(key, true); continue; }
            var state = State(key);
            if (state is null || state.InputSource != binding.Lease.Source || state.InputGeneration != binding.Lease.Generation)
            { bindings.Remove(key); ResetFromOwner(key); }
        }
        // Manual edits are also shown for functions the graph has never claimed.
        foreach (var row in Model.Functions.ToArray())
        {
            var key = (row.Toy.Id, row.Function.Id);
            if (!bindings.ContainsKey(key) && State(key) is { } state && Math.Abs(row.Function.Level - state.RequestedPercent / 100f) > .00001)
                ResetFromOwner(key,preserveSelection:true);
        }
        Model.RefreshIdleDisplay();
    }
    private static string Name(string value) => new string(value.Where(c => !char.IsControl(c)).Take(96).ToArray()) is { Length: > 0 } clean ? clean : "Toy";
    private ToyChannelState? State((string Toy, string Function) key)
    {
        try { return ControllerFor(key.Toy)?.State(key.Toy, key.Function); }
        catch (InvalidOperationException) { return null; }
    }
    private void ResetFromOwner((string Toy, string Function) key,bool preserveSelection=false)
    {
        var row = Find(key); if (row is null) return;
        var remote = key.Toy.StartsWith("remote/", StringComparison.Ordinal);
        var level = row.Connected ? (State(key)?.RequestedPercent ?? 0) / 100f : 0;
        // Explicit replacement changes this target's generation, cancels its
        // unfinished recording/loop, and cannot affect sibling recordings.
        Model.Upsert(new(row.Toy.Id, row.Toy.Name, row.Toy.Connected, row.Toy.Battery, null,
            row.Toy.Functions.Select(f => new FunctionUpdate(f.Id, f.Name, f.Id == key.Function ? level : null)).ToArray()));
        if (remote)
        {
            remoteResetRevision++;
            // Selection is the user's editing choice, not an output lease.
            // Keep it while cancelling the old loop/capture. A held press must
            // release before fresh input can acquire another claim.
            if (Model.Touching && row.Selected)
            {
                blockDesktop = !drawingVrc; Model.Release(false); Vrc.BlockTouch();
            }
            Notice = "Remote toy stopped · draw again or press Play";
        }
        else if (!preserveSelection && Find(key) is { } updated) updated.Function.Selected = false;
    }
    private bool Ready(bool vrc)
    {
        Sync();
        if (!Model.Functions.Any(f => f.Connected) || vrc && !Vrc.IsLive)
        { Notice = Model.Route == 1 ? "Connect to your friend's app on the Connect page" : "Waiting for toys / VRC connection"; return false; }
        return true;
    }
    private void ClaimSelected(bool vrc, bool loopsOnly=false)
    {
        foreach (var row in Model.Functions.Where(f => f.Selected && f.Connected && (!loopsOnly || f.Function.LoopRunning)).ToArray())
        {
            var key = (Toy: row.Toy.Id, Function: row.Function.Id);
            try
            {
                var target = ControllerFor(key.Toy) ?? throw new InvalidOperationException();
                bindings[key] = new(target.BeginInput(key.Toy, key.Function, source, Percent(row.Function.Level), leaseLife), vrc, target);
            }
            catch (InvalidOperationException) { bindings.Remove(key); ResetFromOwner(key); Notice = "Toy unavailable · wait for connection or Stop to finish"; }
        }
    }
    public void Draw(PointF logical, bool vrc, bool freshPress=false)
    {
        if(ShockerPage) return;
        if(!vrc && freshPress) blockDesktop=false;
        if(!vrc && blockDesktop) return;
        var resetRevision = remoteResetRevision;
        if (!Ready(vrc) || resetRevision != remoteResetRevision || !vrc && blockDesktop) return;
        if(!vrc) Vrc.BlockTouch();
        if (Model.Touching && drawingVrc != vrc) Model.Release(false);
        var fresh = !Model.Touching; drawingVrc = vrc;
        if (fresh && Model.RecordArmed) ClearRequested = true;
        Model.DrawAt(logical);
        if (fresh) ClaimSelected(vrc);
        Notice = "Each owner's Sensitivity and Max % apply";
    }
    public void Release(bool commit = true) { if(!drawingVrc) blockDesktop=false; Model.Release(commit); }
    public void Action(string name, bool vrc)
    {
        Sync();
        if(vrc && !Vrc.IsLive) return;
        if(vrc && ShockerPage && !Vrc.ShockerReady && name is not ("InPage" or "InInputMode" or "InStop")) return;
        if(name=="InPage") { SetPage(!ShockerPage); return; }
        if(ShockerPage && name.StartsWith("InRow") && int.TryParse(name[5..],out var shockRow)) { Shockers.Select(shockRow); return; }
        if(ShockerPage && name is "InPrev" or "InNext") { Shockers.Cancel(); Shockers.Model.ChangePage(name=="InPrev" ? -1 : 1); return; }
        if(ShockerPage && name is "InRecord" or "InPlay" or "InHold" or "InClear") return;
        if(!vrc && name is "InRecord" or "InPlay" or "InHold") { Vrc.BlockTouch(); if(TouchFromVrc) Model.Release(false); }
        if (name.StartsWith("InRow") && int.TryParse(name[5..], out var row)) { if (!Model.Touching) Model.ToggleVisible(row); return; }
        switch (name)
        {
            case "InInputMode":
                Shockers.Cancel();
                if (Model.Touching) { blockDesktop = !drawingVrc; Model.Release(false); }
                PointerMode = !PointerMode; InputModeRevision++; Vrc.BlockTouch();
                Notice = PointerMode ? "Pointer · aim either hand like a pistol; Thumbs Up clicks / draws" : "Touch · use your index finger";
                break;
            case "InRecord": if (Ready(vrc)) { drawingVrc = vrc; Model.ToggleRecord(); } break;
            case "InPlay": if (Ready(vrc) && !Model.Touching) { Model.PlaySelected(); ClaimSelected(vrc,true); } break;
            case "InHold": if (Ready(vrc)) { Model.HoldSelected(); ClaimSelected(vrc); Vrc.BlockTouch(); } break;
            case "InStop": StopAll(); break;
            case "InClear": ClearRequested = true; break;
            case "InPrev": if (!Model.Touching) Model.ChangePage(-1); break;
            case "InNext": if (!Model.Touching) Model.ChangePage(1); break;
            // Older avatars may still emit these. Inventory is always unified.
            case "InOwn": case "InRemote": case "InBoth": break;
        }
    }
    public void StopAll()
    {
        if (bindings.Keys.Any(k=>k.Toy.StartsWith("remote/",StringComparison.Ordinal))) Log?.Invoke("Stop all ended the graph's remote toy inputs.");
        Shockers.Cancel();
        Model.Release(false); Model.StopAll(); controller?.Stop(); Remote?.Stop(); bindings.Clear(); Vrc.BlockTouch(); ClearRequested = true;
        foreach (var row in Model.Functions) row.Function.Selected = false;
        Notice = "Stopped · use fresh input";
    }
    private void End((string Toy, string Function) key, bool reset)
    {
        if (bindings.Remove(key, out var binding)) binding.Controller.EndInput(binding.Lease);
        if (reset) ResetFromOwner(key);
    }
    public void VrcLost()
    {
        if (bindings.Values.Any(b=>b.Vrc)) Log?.Invoke("VRC panel link interrupted or hidden; avatar-owned toy inputs ended, fresh input required.");
        Shockers.Cancel();
        if (drawingVrc) Model.Release(false);
        foreach (var key in bindings.Where(b => b.Value.Vrc).Select(b => b.Key).ToArray()) End(key, true);
        ClearRequested = true;
    }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        if(remote is not null) remote.InventoryChanged-=RemoteInventoryChanged;
        foreach (var key in bindings.Keys.ToArray()) End(key, false);
    }
    public void Suspend()
    {
        if (bindings.Count > 0) Log?.Invoke("Toy control paused; graph input ended, fresh input required.");
        Shockers.Cancel();
        Model.Release(false); Vrc.Reset();
        foreach(var key in bindings.Keys.ToArray()) End(key,true);
        ClearRequested=true;
    }
}

internal sealed class VrcToyInput(ToyControlSession session)
{
    private readonly Dictionary<string, bool> bools = [];
    private readonly Dictionary<string, float> floats = [];
    private readonly List<string> actions = [];
    private bool hadRowGesture;
    private string acceptedRow="";
    private double rowsReleasedAt=double.NegativeInfinity;
    private bool visible, visibilityKnown, hadBeat, previousTouch, blockedTouch, previousPanel, blockedPanel;
    private readonly bool[] previousPointerPress=new bool[2], blockedPointer=new bool[2];
    private int pointerHand=-1, pointerDrag, panelDrag;
    public PointF[] PointerPoints { get; }=new PointF[2];
    public bool[] PointerValid { get; }=new bool[2];
    private double lastBeat = double.NegativeInfinity;
    public string WaitingReason { get; private set; } = "Waiting for VRC";
    public int Interruptions { get; private set; }
    public bool IsLive { get; private set; }
    private double lastShockerBeat=double.NegativeInfinity, currentTime;
    private bool hasShockerBeat, freshShockerBeats;
    public bool ShockerReady => IsLive && session.ShockerPage && (Bool("InShockerReady") || freshShockerBeats && currentTime-lastShockerBeat<.75);
    public void ResetShockerView()
    { bools["InShockerReady"]=false; bools["InShockerBeat"]=false; hasShockerBeat=freshShockerBeats=false; lastShockerBeat=double.NegativeInfinity; }
    private bool Bool(string name) => bools.GetValueOrDefault(name);
    public void BlockTouch()
    {
        Array.Clear(PointerValid);
        acceptedRow=""; hadRowGesture=bools.Any(p=>p.Key.StartsWith("InRow",StringComparison.Ordinal) && p.Value);
        rowsReleasedAt=hadRowGesture ? double.PositiveInfinity : double.NegativeInfinity;
        session.Shockers.ResetInput();
        blockedPanel=Bool("InPanelTouch"); previousPanel=false; panelDrag=0;
        blockedTouch = Bool("InTouch") || Bool("InTestTouch"); previousTouch = false;
        for(var hand=0;hand<2;hand++) blockedPointer[hand]=previousPointerPress[hand]=Bool("InPointer"+(hand==0 ? "L" : "R")+"Press");
        pointerHand=-1;
    }
    public void Reset(string reason="Panel link reset") { if(IsLive) Interruptions++; WaitingReason=reason; IsLive = visible = visibilityKnown = hadBeat = false; ResetShockerView(); actions.Clear(); BlockTouch(); session.VrcLost(); }
    public bool Apply(OscMessage message, double now)
    {
        if (message.Address == "/avatar/change") { if(message.Arguments.Length!=1 || message.Arguments[0] is not string) return false; Reset("Avatar changed"); return true; }
        var prefix = "/avatar/parameters/" + ToyControlContract.Prefix;
        if (!message.Address.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var name = message.Address[prefix.Length..];
        if (!ToyControlContract.BoolInputs.Contains(name) && !ToyControlContract.FloatInputs.Contains(name) && name!="Size") return false;
        if (message.Arguments.Length != 1) return false;
        if (ToyControlContract.FloatInputs.Contains(name) || name=="Size")
        {
            if (message.Arguments[0] is not float value || !float.IsFinite(value) || value < 0 || value > 1) return false;
            floats[name] = value; return true;
        }
        bool current;
        if (message.Arguments[0] is bool b) current = b;
        else if (message.Arguments[0] is int i && i is 0 or 1) current = i == 1;
        else return false;
        var prior = Bool(name); bools[name] = current;
        if(name=="InShockerReady") { if(!current) {hasShockerBeat=freshShockerBeats=false;lastShockerBeat=double.NegativeInfinity;} return true; }
        if(name=="InShockerBeat")
        {
            if(session.ShockerPage && prior!=current) {freshShockerBeats=hasShockerBeat && now-lastShockerBeat<.75;hasShockerBeat=true;lastShockerBeat=now;}
            return true;
        }
        if (name == "Visible") { if (!current) Reset("Panel hidden"); visible = current; visibilityKnown = true; return true; }
        if (name == "InBeat")
        {
            // VRChat normally exports only changes. If ShockWave starts after Show,
            // Visible=true is missing. Two alternating fresh pulses establish
            // visibility: this FX pulse runs only on the shown local panel.
            // A known explicit hide wins, and a lone hidden/reset zero cannot
            // establish a link. Never turn held inputs into startup actions.
            if (!visibilityKnown && hadBeat && prior != current && now - lastBeat is >= 0 and < 2)
            { visible = visibilityKnown = true; actions.Clear(); BlockTouch(); }
            if (!hadBeat || prior != current) { hadBeat = true; lastBeat = now; }
            return true;
        }
        if (name=="InPanelTouch") { if(!current) { blockedPanel=false; session.Shockers.Release(3); } return true; }
        if (name is "InTouch" or "InTestTouch") { if (!Bool("InTouch") && !Bool("InTestTouch")) blockedTouch = false; return true; }
        if (name.StartsWith("InPointer",StringComparison.Ordinal) || name.StartsWith("InPose",StringComparison.Ordinal)) return true;
        if (name.StartsWith("InRow", StringComparison.Ordinal))
        {
            // One row per physical touch. Neighbour receivers are not separate
            // clicks: all rows must release for 200 ms before another choice.
            var other=bools.Any(p=>p.Key!=name && p.Key.StartsWith("InRow",StringComparison.Ordinal) && p.Value);
            if(!current && prior && !other) rowsReleasedAt=now;
            if(current && !prior)
            {
                if(!other && (!hadRowGesture || now-rowsReleasedAt>=.2))
                { hadRowGesture=true; acceptedRow=name; actions.Add(name); }
                else if(acceptedRow.Length>0) actions.Remove(acceptedRow); // Reject an ambiguous same-frame overlap.
            }
            return true;
        }
        if (current && !prior && name.StartsWith("In") && name is not ("InFlipX" or "InFlipY")) actions.Add(name);
        return true;
    }
    public void Tick(double now)
    {
        currentTime=now;
        var live = visible && hadBeat && now - lastBeat < 2;
        if (IsLive && !live) { Interruptions++; WaitingReason="Panel heartbeat interrupted"; session.VrcLost(); BlockTouch(); actions.Clear(); hadBeat = false; }
        IsLive = live;
        if (live)
        {
            WaitingReason="Connected";
            if(actions.Contains("InInputMode")) session.Action("InInputMode",true);
            else foreach (var action in actions.ToArray()) session.Action(action, true);
            if(session.ShockerPage && !ShockerReady)
            {
                if(session.Shockers.ArmedFromVrc) session.Shockers.Cancel();
                blockedPanel=Bool("InPanelTouch"); previousPanel=blockedPanel; panelDrag=0;
                if(session.PointerMode) {TickPointer(now,false);actions.Clear();return;}
                for(var hand=0;hand<2;hand++) { blockedPointer[hand]=previousPointerPress[hand]=Bool("InPointer"+(hand==0 ? "L" : "R")+"Press"); session.Shockers.Release(hand+1); }
                session.Shockers.Release(3); actions.Clear(); return;
            }
            if(session.PointerMode) { TickPointer(now); actions.Clear(); return; }
            if(session.ShockerPage) { TickPanel(now); actions.Clear(); return; }
            var touch = Bool("InTouch") || Bool("InTestTouch");
            if (!touch) blockedTouch = false;
            if (!blockedTouch)
            {
                if (touch)
                {
                    var test = Bool("InTestTouch"); var x = floats.GetValueOrDefault(test ? "InTestX" : "InX"); var y = floats.GetValueOrDefault(test ? "InTestY" : "InY");
                    if (Bool("InFlipX")) x = 1 - x; if (Bool("InFlipY")) y = 1 - y;
                    var graph = PanelLayout.Graph; session.Draw(new(graph.X + x * graph.Width, graph.Y + (1 - y) * graph.Height), true);
                }
                else if (previousTouch && session.TouchFromVrc) session.Release();
            }
            previousTouch = touch;
        }
        actions.Clear();
    }
    private void TickPanel(double now)
    {
        var touch=Bool("InPanelTouch");
        if(!touch) { blockedPanel=false; session.Shockers.Release(3); panelDrag=0; }
        if(touch && !blockedPanel && floats.ContainsKey("InPanelX") && floats.ContainsKey("InPanelY"))
        {
            var point=new PointF(floats["InPanelX"]*1240,100+(1-floats["InPanelY"])*608);
            var hit=ToyControlLayout.Hit(point,session);
            if(!previousPanel) { session.Shockers.Press(3,hit,now); panelDrag=hit is 30 or 31 ? hit : 0; }
            if(panelDrag!=0) session.Slider(point,panelDrag);
        }
        previousPanel=touch;
    }
    private void TickPointer(double now,bool commandsReady=true)
    {
        var held=pointerHand;
        for(var hand=0;hand<2;hand++)
        {
            var prefix="InPointer"+(hand==0 ? "L" : "R"); var press=Bool(prefix+"Press");
            if(!press) { blockedPointer[hand]=false; session.Shockers.Release(hand+1); }
            var valid=PosePoint(hand,out var point);
            PointerValid[hand]=valid;
            if(valid) PointerPoints[hand]=point;
            var hit=valid ? ToyControlLayout.Hit(point,session) : 0;
            // Keep hover and escape/navigation alive while the page acknowledgement
            // arrives. Held command presses must release before the gate opens.
            if(!commandsReady && hit is not (13 or 28 or 29))
            {pointerHand=-1;blockedPointer[hand]=press;previousPointerPress[hand]=press;session.Shockers.Release(hand+1);continue;}
            if(held==hand)
            {
                if(!valid || hit!=pointerDrag) { if(session.TouchFromVrc) session.Release(false); pointerHand=-1; blockedPointer[hand]=press; }
                else if(!press) { if(session.TouchFromVrc) session.Release(); pointerHand=-1; }
                else if(!blockedPointer[hand]) { if(pointerDrag==10) session.Draw(point,true); else session.Slider(point,pointerDrag); }
            }
            else if(held<0 && pointerHand<0 && press && !previousPointerPress[hand] && !blockedPointer[hand])
            {
                if(session.ShockerPage) session.Shockers.Press(hand+1,hit,now);
                if(hit is 10 or 30 or 31) { pointerHand=hand; pointerDrag=hit; if(hit==10) session.Draw(point,true); else session.Slider(point,hit); }
                else if(hit is >=1 and <=7) session.Action("InRow"+(hit-1),true);
                else if(ToyControlLayout.Action(hit) is { } action) session.Action(action,true);
                held=hand;
            }
            previousPointerPress[hand]=press;
            if(!session.PointerMode) return;
        }
    }
    private bool PosePoint(int hand,out PointF point)
    {
        point=default; var h=hand==0 ? "L" : "R";
        if(!Bool("InPointerReady") || !Bool("InPose"+h+"OHit") || !Bool("InPose"+h+"THit")) return false;
        var values=new float[6]; var i=0;
        foreach(var end in new[] {"O","T"}) foreach(var axis in new[] {"X","Y","Z"})
        {
            if(!floats.TryGetValue("InPose"+h+end+axis,out var v) || v<=.002f || v>=.998f) return false;
            values[i++]=(v-.5f)*PointerProjection.Span;
        }
        return PointerProjection.TryProject(new(values[0],values[1],values[2]),new(values[3],values[4],values[5]),floats.GetValueOrDefault("Size",.2f),out point);
    }
}
