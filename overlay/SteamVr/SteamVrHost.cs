#nullable enable
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Valve.VR;

namespace ShockWave.Overlay.SteamVr;

// Native presentation and passive controller actions. Commands use the production panel model.
internal sealed class SteamVrHost : IDisposable
{
    private readonly PanelModel model;
    private readonly PanelInput input;
    private readonly WristMoveSession wristMove;
    private readonly PanelHoldSession panelGrab=new();
    private readonly WristMenuGesture wristMenu=new();
    private bool wristPresentationPaused;
    private bool wristGripAvailable;
    public SteamVrHost(PanelModel model,PanelInput input,WristPlacementStore? store=null)
    {
        this.model=model;this.input=input;wristMove=new(store ?? new WristPlacementStore(null));
        input.MoveFinished+=FinishWristMove;
        input.GrabFinished+=FinishPanelGrab;
    }
    private CVRSystem? system;
    private CVROverlay? overlays;
    private CVRInput? vrInput;
    private ulong panel,dashboard,thumbnail,actionSet;
    private readonly ulong[] poseActions=new ulong[2],clickActions=new ulong[2],hapticActions=new ulong[2];
    private readonly ulong[] gripActions=new ulong[2],handActions=new ulong[2];
    private readonly PressGate[] gates=[new(),new()];
    private readonly PressGate[] gripGates=[new(),new()];
    private readonly PoseSmoother[] handMotion=[PoseSmoother.HandPanel(),PoseSmoother.HandPanel()];
    private readonly bool[] handSourceKnown=new bool[2],handSourceAction=new bool[2];
    private readonly bool[] wasDown=new bool[2];
    private readonly PoseSmoother[] pointerMotion=[PoseSmoother.Pointer(),PoseSmoother.Pointer()];
    private readonly PoseSmoother panelMotion=new(.025f,.030f);
    private double frameTime;
    private readonly TrackedDevicePose_t[] poses=new TrackedDevicePose_t[OpenVR.k_unMaxTrackedDeviceCount];
    private bool initialized,placed,lastDashboard,hadTracking,actionsReady;
    private string activeSource="";
    private readonly HashSet<string> releasedMouseSources=[];
    public bool Running=>system is not null;
    public string Status {get;private set;}="Desktop preview · open SteamVR when ready";
    public WristFit CurrentFit=>wristMove.Settings.For(model.WristHand);
    public Mount SavedWrist=>wristMove.Settings.LastWrist;
    public float HandTilt {get=>CurrentFit.Tilt;set=>EditFit(CurrentFit.WithTilt(value));}
    public float HandHeight {get=>CurrentFit.Height;set=>EditFit(CurrentFit with {Height=value});}
    public float HandForward {get=>CurrentFit.Forward;set=>EditFit(CurrentFit with {Forward=value});}
    public float HandSide {get=>CurrentFit.Side;set=>EditFit(CurrentFit with {Side=value});}
    public void ResetHandFit()=>EditFit(WristFit.Default);
    private void EditFit(WristFit fit)
    {
        if(!fit.Valid) {model.PlacementNotice("Invalid wrist placement ignored");return;}
        ResetInput();model.StopAll();var saved=wristMove.Set(model.WristHand,fit);needsPlacement=true;panelMotion.Reset();
        model.PlacementNotice(saved ? "Wrist placement saved" : "Placement kept for this session · could not save");
    }
    private void FinishWristMove(string source,bool commit)
    {
        panelMotion.Reset();
        if(commit && model.Compact && model.MovingWrist && wristMove.Finish(source,out var saved))
            model.PlacementNotice(saved ? "Wrist placement saved" : "Placement kept for this session · could not save");
        else {wristMove.Cancel();panelMotion.Reset();model.PlacementNotice("Move cancelled · previous placement restored");}
        model.SetWristFitMode(false);wristMenu.Open();PublishWristMenu();
    }
    private void PublishWristMenu()=>model.SetWristMenu(wristMenu.Amount,wristMenu.HandleVisible,wristMenu.Active,wristMenu.Contact,wristMenu.AnchorVisible);
    private bool KeepClosedWristPlayback=>model.Compact && model.WristMenuEnabled && !model.WristFitMode && (!model.WristInputReady || wristPresentationPaused);
    internal void PausePresentationInput()
    {
        var keep=KeepClosedWristPlayback;ResetInput();
        if(keep) {model.CancelWristMenuInput();wristMenu.Reset();PublishWristMenu();wristPresentationPaused=true;}
        else {model.StopAll();wristPresentationPaused=false;}
    }
    internal void ResumePresentationInput()
    {
        var keep=wristPresentationPaused && model.Compact && model.WristMenuEnabled && !model.WristFitMode;
        ResetInput();
        if(keep) {model.CancelWristMenuInput();wristMenu.Reset();PublishWristMenu();}else model.StopAll();
        wristPresentationPaused=false;
    }
    internal bool UpdateWristMenu(WristMenuFrame frame,HmdMatrix34_t hand,bool valid,bool down,double now,bool gripAvailable=true)
    {
        var started=wristMenu.Update(frame,hand,valid,down,model.AcceptsInput && model.WristHandleVisible && !input.ExternalInputBlocked,now,gripAvailable);
        if(started)
        {
            ResetPointerInput();model.CancelWristMenuInput();
            wristMenu.Reanchor(frame with {Size=model.Layout().Size,Width=model.WidthMeters},hand);
        }
        PublishWristMenu();return started;
    }
    public void OpenWristMenu()
    {
        if(!Running) {Status="Open SteamVR first, then open the wrist menu";return;}
        if(!model.Compact) {Status="Choose Wrist in settings before opening the wrist menu";return;}
        ResetPointerInput();model.CancelWristMenuInput();model.SetWristFitMode(false);wristMenu.Open();PublishWristMenu();
    }
    public void AdjustWristPosition()
    {
        OpenWristMenu();if(!Running || !model.Compact) return;
        model.SetWristFitMode(true);model.PlacementNotice("Point at Move and hold trigger · release to save wrist position");
    }
    private void FinishPanelGrab(string source,bool commit)
    {
        if(commit && !model.Compact && model.GrabbingPanel) {if(!panelGrab.Release(source)) return;}
        else if(!panelGrab.Cancel(source)) return;
        if(!model.Compact && overlays is not null && panelPoseReady)
        {
            if(Math.Abs(model.WidthMeters-panelGrab.Width)>.000001f) model.SetWidth(panelGrab.Width);
            ApplySurfaceWidth();SetPanelPose(panelGrab.WorldPose);
        }
        model.PlacementNotice(panelGrab.Active ? "Panel held · the other hand can use the pointer" : commit ? "Panel placed" : "Grab cancelled · previous position restored");
    }
    private GpuOverlayTexture? texture;
    private PointerBeamOverlay? pointer;
    private OverlayVisibility? panelVisibility;
    private bool wantsPanelVisible;
    private float appliedWidth=-1;
    private uint mountedDevice=uint.MaxValue;
    private bool needsPlacement=true;
    private HmdMatrix34_t panelWorldPose;
    private HmdMatrix34_t panelBasePose;
    private Size presentedSize=PanelSizing.MaximumSurface;
    private bool panelPoseReady;
    private Mount contextMount=Mount.LeftHand;
    private bool contextWrist=true;
    public void PlacementChanged()
    {
        var changed=contextMount!=model.Mount || contextWrist!=model.Compact;
        needsPlacement |= changed;contextMount=model.Mount;contextWrist=model.Compact;
        if(changed) {ResetInput();wristMenu.Reset();model.SetWristFitMode(false);}else {ResetPointerInput();wristMenu.Cancel();}
        PublishWristMenu();
    }

    public bool Start()
    {
        if(Running) return true;
        try
        {
            if(!Process.GetProcessesByName("vrserver").Any()) {Status="Start SteamVR, then use the VR icon";return false;}
            if(!OpenVR.IsRuntimeInstalled()) {Status="SteamVR runtime was not found";return false;}
            var error=EVRInitError.None;
            system=OpenVR.Init(ref error,EVRApplicationType.VRApplication_Overlay);
            initialized=system is not null;
            if(error!=EVRInitError.None || system is null) throw new InvalidOperationException("SteamVR: "+error);
            overlays=OpenVR.Overlay ?? throw new InvalidOperationException("SteamVR overlay interface unavailable");
            var key="szebs.shockwave.steamvr."+Environment.ProcessId;
            Check(overlays.CreateOverlay(key+".panel","ShockWave VR Panel · "+PanelBuildInfo.Version,ref panel));
            Check(overlays.CreateDashboardOverlay(key+".dashboard","ShockWave VR Panel · "+PanelBuildInfo.Version,ref dashboard,ref thumbnail));
            panelVisibility=new(visible=>Check(visible ? overlays.ShowOverlay(panel) : overlays.HideOverlay(panel)));
            foreach(var handle in new[]{panel,dashboard})
            {
                Check(overlays.SetOverlayInputMethod(handle,VROverlayInputMethod.Mouse));
                Check(overlays.SetOverlayTextureColorSpace(handle,EColorSpace.Gamma));
            }
            // Use passive named actions outside the dashboard. Requesting global
            // overlay interaction can interfere with the scene application's input.
            var adapterIndex=-1;system.GetDXGIOutputInfo(ref adapterIndex);
            if(adapterIndex<0) throw new InvalidOperationException("SteamVR did not report its graphics adapter");
            texture=new GpuOverlayTexture(adapterIndex,fixedSize:PanelSizing.MaximumSurface);
            pointer=new PointerBeamOverlay(overlays,adapterIndex,key+".pointer");
            Check(overlays.SetOverlayWidthInMeters(dashboard,1.1f));
            SetupActions();UploadThumbnail();
            model.EnableWristMenu();wristMenu.Reset();model.SetWristFitMode(false);wristPresentationPaused=false;
            placed=false;hadTracking=false;needsPlacement=true;ResetInput();model.StopAll();
            lastDashboard=overlays.IsDashboardVisible();
            Status=actionsReady ? "SteamVR connected · point and pull the trigger" : "SteamVR connected · dashboard/native pointer input";
            return true;
        }
        catch(Exception error) {var message=error.Message;Dispose();Status="VR unavailable · "+message;return false;}
    }
    private void SetupActions()
    {
        vrInput=OpenVR.Input;
        if(vrInput is null) throw new InvalidOperationException("SteamVR input interface is unavailable");
        var registration=new OpenVrAppRegistration(OpenVR.Applications);
        var files=VrInputFiles.Prepare(AppContext.BaseDirectory,VrInputFiles.DestinationFor(registration.ActionManifest));
        VrInputRegistration.Register(files,registration,(uint)Environment.ProcessId);
        CheckInput("action manifest",vrInput.SetActionManifestPath(files.Actions));
        CheckInput("action set",vrInput.GetActionSetHandle("/actions/panel",ref actionSet));
        for(var hand=0;hand<2;hand++)
        {
            var side=hand==0 ? "left" : "right";
            CheckInput(side+" pointer",vrInput.GetActionHandle("/actions/panel/in/pose_"+side,ref poseActions[hand]));
            CheckInput(side+" trigger",vrInput.GetActionHandle("/actions/panel/in/click_"+side,ref clickActions[hand]));
            CheckInput(side+" grip",vrInput.GetActionHandle("/actions/panel/in/grip_"+side,ref gripActions[hand]));
            CheckInput(side+" hand pose",vrInput.GetActionHandle("/actions/panel/in/hand_"+side,ref handActions[hand]));
            vrInput.GetActionHandle("/actions/panel/out/haptic_"+side,ref hapticActions[hand]);
        }
        actionsReady=true;
    }
    private static void CheckInput(string operation,EVRInputError error)
    {if(error!=EVRInputError.None) throw new InvalidOperationException("SteamVR input "+operation+": "+error);}
    public void Tick()
    {
        if(wristMove.Active && !model.MovingWrist) {input.Cancel();wristMove.Cancel();}
        if(panelGrab.Active && !model.GrabbingPanel) input.Cancel();
        if(system is null || overlays is null) return;
        try
        {
            frameTime=Stopwatch.GetTimestamp()/(double)Stopwatch.Frequency;
            var evt=new VREvent_t();var eventSize=(uint)Marshal.SizeOf<VREvent_t>();
            while(system.PollNextEvent(ref evt,eventSize))
            {
                if((EVREventType)evt.eventType==EVREventType.VREvent_Quit)
                {system.AcknowledgeQuit_Exiting();Dispose();Status="SteamVR closed · open again to reconnect";return;}
            }
            system.GetDeviceToAbsoluteTrackingPose(ETrackingUniverseOrigin.TrackingUniverseStanding,0,poses);
            var tracking=Tracked(0);
            var inDashboard=overlays.IsDashboardVisible();
            if(inDashboard!=lastDashboard) {PausePresentationInput();lastDashboard=inDashboard;}
            if(!tracking)
            {
                if(hadTracking || KeepClosedWristPlayback) PausePresentationInput();
                panelMotion.Reset();panelPoseReady=false;hadTracking=false;wantsPanelVisible=false;panelVisibility!.Set(false);pointer?.Hide();Status="Tracking unavailable · panel paused";return;
            }
            if(!hadTracking) {ResumePresentationInput();needsPlacement=true;}
            hadTracking=true;
            var visible=PlacePanel();
            if(visible) wristPresentationPaused=false;
            var actionInput=false;
            wantsPanelVisible=visible && !inDashboard;
            panelVisibility!.Set(wantsPanelVisible && texture?.Ready==true);
            if(inDashboard) {pointer?.Hide();DiscardEvents(panel);PollMouse(dashboard,"dashboard");}
            else
            {
                DiscardEvents(dashboard);
                if(visible && actionsReady && ReadActions()) {actionInput=true;DiscardEvents(panel);}
                else if(visible) {pointer?.Hide();DiscardEvents(panel);UpdateWristProximityWithoutInput();Status="Controller bindings unavailable · use Open wrist menu in Settings";}
                else PausePresentationInput();
            }
            if(visible && (inDashboard || actionInput)) Status="SteamVR connected · "+(inDashboard ? "dashboard · release trigger, then click" : model.Compact && !model.WristInputReady ? wristGripAvailable ? "wrist menu · bring the other hand near, then grip and pull up" : "controller grip unavailable · use Open wrist menu in Settings" : (model.Mount==Mount.Floating ? "floating panel" : model.Mount==Mount.LeftHand ? "left hand" : "right hand")+" · controller actions");
        }
        catch(Exception error) {var message=error.Message;Dispose();Status="VR disconnected · "+message;}
    }
    private bool Tracked(int device)=>device>=0 && device<poses.Length && poses[device].bDeviceIsConnected && poses[device].bPoseIsValid;
    private bool PlacePanel()
    {
        if(overlays is null || system is null) return false;
        ApplySurfaceWidth();
        if(model.Mount==Mount.Floating)
        {
            panelMotion.Reset();
            if(!placed || needsPlacement)
            {
                // Head-yaw alignment keeps the board upright and within reach.
                var transform=VrPresentation.Floating(poses[0].mDeviceToAbsoluteTracking);
                SetPanelPose(transform);placed=true;needsPlacement=false;
            }
            return true;
        }
        var hand=system.GetTrackedDeviceIndexForControllerRole(model.Mount==Mount.LeftHand ? ETrackedControllerRole.LeftHand : ETrackedControllerRole.RightHand);
        if(hand==OpenVR.k_unTrackedDeviceIndexInvalid || !Tracked((int)hand))
        {panelMotion.Reset();panelPoseReady=false;Status="Waiting for the mounted controller · use Floating or the dashboard";return false;}
        if(needsPlacement || mountedDevice!=hand)
        {
            panelMotion.Reset();mountedDevice=hand;needsPlacement=false;
        }
        if(wristMove.Active) return true;
        var hadHistory=panelMotion.Initialized;
        var target=VrPoseMath.Compose(poses[hand].mDeviceToAbsoluteTracking,CurrentFit.Matrix());
        if(!panelMotion.TryUpdate(target,frameTime,out var smoothed)) {PausePresentationInput();return false;}
        if(hadHistory && panelMotion.Restarted) PausePresentationInput();
        SetPanelPose(smoothed);
        return true;
    }
    private void SetPanelPose(HmdMatrix34_t pose)
    {
        panelBasePose=pose;
        var surfacePose=PanelSizing.Pose(pose,model.WidthMeters,model.Compact,presentedSize);
        Check(overlays!.SetOverlayTransformAbsolute(panel,ETrackingUniverseOrigin.TrackingUniverseStanding,ref surfacePose));
        panelWorldPose=surfacePose;panelPoseReady=true;
    }
    private float SurfaceWidth=>PanelSizing.WidthMeters(model.WidthMeters,model.Compact,presentedSize);
    private void ApplySurfaceWidth()
    {
        var width=SurfaceWidth;
        if(appliedWidth==width) return;
        Check(overlays!.SetOverlayWidthInMeters(panel,width));appliedWidth=width;
    }
    private bool ReadActions()
    {
        if(vrInput is null || overlays is null) return false;
        if(model.TransparencyPercent==100) {ResetInput();model.StopAll();return true;}
        var sets=new[]{new VRActiveActionSet_t {ulActionSet=actionSet}};
        if(vrInput.UpdateActionState(sets,(uint)Marshal.SizeOf<VRActiveActionSet_t>())!=EVRInputError.None) {ResetInput();return false;}
        var any=ReadGripActions();
        if(model.Compact && !model.WristInputReady)
        {
            activeSource="";model.Cursor=null;foreach(var gate in gates) gate.Reset();Array.Clear(wasDown);pointer?.Hide();return true;
        }
        if(input.IsPointerOwnedBy("desktop"))
        {
            activeSource="";foreach(var gate in gates) gate.Reset();Array.Clear(wasDown);pointer?.Hide();return true;
        }
        PointerAim? leftAim=null,rightAim=null;
        for(var hand=0;hand<2;hand++)
        {
            var source="vr"+hand;
            if(input.IsGrabbing(source)) {pointerMotion[hand].Reset();gates[hand].Reset();wasDown[hand]=false;continue;}
            var pose=new InputPoseActionData_t();var click=new InputDigitalActionData_t();
            var poseOk=vrInput.GetPoseActionDataForNextFrame(poseActions[hand],ETrackingUniverseOrigin.TrackingUniverseStanding,ref pose,(uint)Marshal.SizeOf<InputPoseActionData_t>(),0)==EVRInputError.None;
            var clickOk=vrInput.GetDigitalActionData(clickActions[hand],ref click,(uint)Marshal.SizeOf<InputDigitalActionData_t>(),0)==EVRInputError.None;
            var valid=poseOk && clickOk && pose.bActive && click.bActive && pose.pose.bPoseIsValid && pose.pose.bDeviceIsConnected;
            any|=valid;
            var mounted=model.Mount==Mount.LeftHand && hand==0 || model.Mount==Mount.RightHand && hand==1;
            var matrix=default(HmdMatrix34_t);
            if(valid && !mounted) valid=pointerMotion[hand].TryUpdate(pose.pose.mDeviceToAbsoluteTracking,frameTime,out matrix);
            if(!valid || mounted)
            {
                pointerMotion[hand].Reset();
                gates[hand].Reset();wasDown[hand]=false;
                if(activeSource==source) {input.CancelPointer();model.StopAll();activeSource="";}
                continue;
            }
            if(pointerMotion[hand].Restarted)
            {
                gates[hand].Reset();wasDown[hand]=false;
                if(activeSource==source) {input.CancelPointer();model.StopAll();activeSource="";}
            }
            if(input.IsMoving(source) && wristMove.Active)
            {
                if(!TryWristPose(out var mountedPose) || !wristMove.Update(source,model.Mount,mountedPose,matrix))
                {input.Cancel();model.StopAll();activeSource="";gates[hand].Reset();wasDown[hand]=false;continue;}
                SetPanelPose(wristMove.WorldPose);
            }
            var ray=new VROverlayIntersectionParams_t {eOrigin=ETrackingUniverseOrigin.TrackingUniverseStanding,vSource=new HmdVector3_t {v0=matrix.m3,v1=matrix.m7,v2=matrix.m11},vDirection=new HmdVector3_t {v0=-matrix.m2,v1=-matrix.m6,v2=-matrix.m10}};
            var hit=new VROverlayIntersectionResults_t();
            var intersects=overlays.ComputeOverlayIntersection(panel,ref ray,ref hit) && hit.fDistance is >0 and <3;
            var layout=model.Layout();var size=layout.Size;
            var point=PanelSizing.Remap(new(hit.vUVs.v0*presentedSize.Width,(1-hit.vUVs.v1)*presentedSize.Height),presentedSize,size);
            intersects &= layout.ContainsSurface(point);
            if(intersects)
            {
                var aim=new PointerAim(hand,point,new(matrix.m3,matrix.m7,matrix.m11),new(hit.vPoint.v0,hit.vPoint.v1,hit.vPoint.v2));
                if(!input.IsGrabbing(source)) {if(hand==0) leftAim=aim;else rightAim=aim;}
                if(activeSource.Length==0 || activeSource==source) input.Move(source,point);
            }
            else if(activeSource==source && (input.IsDrawing(source) || input.IsSliding(source) || input.IsResizing(source)) && panelPoseReady &&
                PanelRayProjection.TryProject(matrix,panelWorldPose,SurfaceWidth,presentedSize,out var extended))
            {
                // Outside the board, keep recording/clamp the graph value until
                // release. An unprojectable aim simply holds the last value.
                input.Move(source,PanelSizing.Remap(extended,presentedSize,size));
            }
            if(gates[hand].Update(valid,click.bState) && intersects && (activeSource.Length==0 || layout.At(point)=="stop"))
            {
                activeSource=source;input.Down(source,point);
                if(input.IsMoving(source))
                {
                    if(!panelPoseReady || !TryWristPose(out var mountedPose) || !wristMove.Begin(source,model.Mount,mountedPose,matrix,panelBasePose)) {input.Cancel();activeSource="";}
                    else panelMotion.Reset();
                }
                if(hapticActions[hand]!=0) vrInput.TriggerHapticVibrationAction(hapticActions[hand],0,.018f,110,.18f,0);
            }
            if(wasDown[hand] && !click.bState && activeSource==source && !input.IsGrabbing(source))
            {input.Up(source,intersects ? point : new(-100,-100));activeSource="";}
            if(click.bState && activeSource==source && !intersects && !input.IsDrawing(source) && !input.IsMoving(source) && !input.IsSliding(source) && !input.IsResizing(source)) {input.CancelPointer();activeSource="";}
            wasDown[hand]=click.bState;
        }
        ApplyHeldPose();
        var chosen=PointerBeam.Choose(leftAim,rightAim,activeSource);
        if(model.Owner=="desktop") {pointer?.Hide();return any;}
        if(!input.IsResizing(activeSource)) model.Cursor=chosen?.Point;
        var head=poses[0].mDeviceToAbsoluteTracking;
        if(chosen is { } target && wantsPanelVisible && texture?.Ready==true &&
            PointerBeam.TryPose(target.Origin,target.Hit,new(head.m3,head.m7,head.m11),out var beam)) pointer?.Update(beam);
        else pointer?.Hide();
        return any;
    }
    private bool ReadGripActions()
    {
        if(vrInput is null || system is null) return false;
        model.GripContacts.Clear();var any=false;
        var samples=new (bool Valid,bool Down,HmdMatrix34_t Pose)[2];var gripReady=new bool[2];
        for(var hand=0;hand<2;hand++)
        {
            var source="vr"+hand;var grip=new InputDigitalActionData_t();
            var available=gripActions[hand]!=0 && vrInput.GetDigitalActionData(gripActions[hand],ref grip,(uint)Marshal.SizeOf<InputDigitalActionData_t>(),0)==EVRInputError.None && grip.bActive;
            gripReady[hand]=available;
            var raw=default(HmdMatrix34_t);var fromAction=false;
            var tracked=TryHandPose(hand,out raw,out fromAction);
            // Visibility follows tracking even if a grip binding is unavailable.
            // Floating captures retain their original binding requirement.
            var valid=tracked && (available || model.Compact && model.WristMenuEnabled && !model.WristFitMode);
            var sourceChanged=handSourceKnown[hand] && handSourceAction[hand]!=fromAction;
            if(valid) {handSourceKnown[hand]=true;handSourceAction[hand]=fromAction;}
            var matrix=default(HmdMatrix34_t);
            if(valid && !sourceChanged) valid=handMotion[hand].TryUpdate(raw,frameTime,out matrix);
            if(!valid || sourceChanged || handMotion[hand].Restarted)
            {
                if(model.Compact && wristMenu.Active && hand==(model.WristHand==Mount.LeftHand ? 1 : 0)) {wristMenu.Cancel();PublishWristMenu();}
                gripGates[hand].Reset();
                if(!valid || sourceChanged) handMotion[hand].Reset();
                if(input.IsGrabbing(source)) {input.Cancel();model.StopAll(false);activeSource="";foreach(var gate in gripGates) gate.Reset();}
                if(!valid || sourceChanged) continue;
            }
            any |= !model.Compact;
            samples[hand]=(true,available && grip.bState,matrix);
            if(panelGrab.Contains(source)) panelGrab.SetHand(source,matrix);
        }
        if(model.Compact && model.WristMenuEnabled && !model.WristFitMode)
        {
            var hand=model.WristHand==Mount.LeftHand ? 1 : 0;var sample=samples[hand];
            wristGripAvailable=gripReady[hand];
            var frame=new WristMenuFrame(panelBasePose,model.Layout().Size,model.WidthMeters);
            var started=UpdateWristMenu(frame,sample.Pose,sample.Valid && panelPoseReady,sample.Down,frameTime,wristGripAvailable);
            if(started)
            {
                // Cancel only unfinished interaction. Never Stop/Suspend here:
                // recorded waves, held values and submitted shocks continue.
                if(hapticActions[hand]!=0) vrInput!.TriggerHapticVibrationAction(hapticActions[hand],0,.018f,110,.18f,0);
            }
            return true;
        }
        ApplyHeldPose();
        for(var hand=0;hand<2;hand++)
        {
            var source="vr"+hand;var sample=samples[hand];
            if(sample.Valid && input.IsGrabbing(source) && !sample.Down) {input.GripUp(source);gates[hand].Reset();wasDown[hand]=false;}
        }
        for(var hand=0;hand<2;hand++)
        {
            var source="vr"+hand;var sample=samples[hand];if(!sample.Valid) continue;
            string? handle=null;
            if(!model.Compact && panelPoseReady && PanelContact.TryHandle(sample.Pose,panelWorldPose,SurfaceWidth,presentedSize,model.Layout(),out handle)) model.GripContacts.Add(handle!);
            var fresh=gripGates[hand].Update(true,sample.Down);
            if(fresh && handle is not null && !model.Compact && panelPoseReady && !input.IsGrabbing(source))
            {
                var wasPointer=activeSource==source;
                if(wasPointer)
                {
                    input.CancelPointer(source);activeSource="";gates[hand].Reset();wasDown[hand]=false;ApplyHeldPose();
                    if(!PanelContact.TryHandle(sample.Pose,panelWorldPose,SurfaceWidth,presentedSize,model.Layout(),out handle)) continue;
                }
                if(handle is null) continue;
                if(input.GripDown(source,handle) && panelGrab.Begin(source,handle,sample.Pose,panelBasePose,model.WidthMeters,model.Layout()))
                {
                    if(wasPointer) {activeSource="";gates[hand].Reset();wasDown[hand]=false;}
                    if(panelGrab.Count==2) model.PlacementNotice("Both grips · pull apart or push together to resize");
                    if(hapticActions[hand]!=0) vrInput.TriggerHapticVibrationAction(hapticActions[hand],0,.018f,110,.18f,0);
                }
                else if(input.IsGrabbing(source) && !panelGrab.Contains(source)) input.CancelGrip();
            }
        }
        return any;
    }
    private void ApplyHeldPose()
    {
        if(!panelGrab.Active || model.Compact || !panelPoseReady) return;
        if(!panelGrab.Update(model.Layout(),model.WidthMeters)) {input.Cancel();model.StopAll(false);activeSource="";return;}
        if(Math.Abs(panelGrab.Width-model.WidthMeters)>.000001f) model.SetWidth(panelGrab.Width);
        ApplySurfaceWidth();SetPanelPose(panelGrab.WorldPose);
    }
    private bool TryHandPose(int hand,out HmdMatrix34_t pose,out bool fromAction)
    {
        pose=default;fromAction=false;
        if(system is null) return false;
        var data=new InputPoseActionData_t();
        if(vrInput is not null && handActions[hand]!=0 && vrInput.GetPoseActionDataForNextFrame(handActions[hand],ETrackingUniverseOrigin.TrackingUniverseStanding,ref data,(uint)Marshal.SizeOf<InputPoseActionData_t>(),0)==EVRInputError.None && data.bActive && data.pose.bPoseIsValid && data.pose.bDeviceIsConnected)
        {pose=data.pose.mDeviceToAbsoluteTracking;fromAction=true;return true;}
        var device=system.GetTrackedDeviceIndexForControllerRole(hand==0 ? ETrackedControllerRole.LeftHand : ETrackedControllerRole.RightHand);
        if(device==OpenVR.k_unTrackedDeviceIndexInvalid || !Tracked((int)device)) return false;
        pose=poses[device].mDeviceToAbsoluteTracking;return true;
    }
    private void UpdateWristProximityWithoutInput()
    {
        if(!model.Compact || !model.WristMenuEnabled || model.WristFitMode || !panelPoseReady) return;
        var hand=model.WristHand==Mount.LeftHand ? 1 : 0;wristGripAvailable=false;
        var valid=TryHandPose(hand,out var raw,out _);
        var pose=default(HmdMatrix34_t);
        if(valid) valid=handMotion[hand].TryUpdate(raw,frameTime,out pose);
        UpdateWristMenu(new(panelBasePose,model.Layout().Size,model.WidthMeters),pose,valid,false,frameTime,gripAvailable:false);
    }
    private bool TryWristPose(out HmdMatrix34_t pose)
    {
        pose=default;
        if(model.Mount==Mount.Floating || mountedDevice==uint.MaxValue || !Tracked((int)mountedDevice)) return false;
        pose=poses[mountedDevice].mDeviceToAbsoluteTracking;return true;
    }
    private void PollMouse(ulong handle,string prefix)
    {
        if(overlays is null) return;
        var evt=new VREvent_t();var size=(uint)Marshal.SizeOf<VREvent_t>();
        while(overlays.PollNextOverlayEvent(handle,ref evt,size))
        {
            var type=(EVREventType)evt.eventType;
            var source=prefix+evt.data.mouse.cursorIndex;
            var l=model.Layout();
            // OpenVR mouse space has its origin at the lower left.
            var point=PanelSizing.Remap(new(evt.data.mouse.x,presentedSize.Height-evt.data.mouse.y),presentedSize,l.Size);
            if(type==EVREventType.VREvent_MouseMove) input.Move(source,point);
            else if(type==EVREventType.VREvent_MouseButtonDown && evt.data.mouse.button==1)
            {
                if(releasedMouseSources.Remove(source) || l.At(point)=="stop") input.Down(source,point);
            }
            else if(type==EVREventType.VREvent_MouseButtonUp && evt.data.mouse.button==1) {input.Up(source,point);releasedMouseSources.Add(source);}
            else if(type is EVREventType.VREvent_FocusLeave or EVREventType.VREvent_OverlayHidden) PausePresentationInput();
        }
    }
    private void DiscardEvents(ulong handle)
    {
        var evt=new VREvent_t();for(var i=0;i<256 && overlays!.PollNextOverlayEvent(handle,ref evt,(uint)Marshal.SizeOf<VREvent_t>());i++) { }
    }
    public void Upload(Bitmap frame)
    {
        if(overlays is null) return;
        try
        {
            texture!.Upload(frame);
            var descriptor=texture.Descriptor;
            var surfaceSize=texture.SurfaceSize;
            var scale=new HmdVector2_t {v0=surfaceSize.Width,v1=surfaceSize.Height};
            foreach(var h in new[]{panel,dashboard})
            {
                Check(overlays.SetOverlayMouseScale(h,ref scale));
                Check(overlays.SetOverlayTexture(h,ref descriptor));
            }
            texture.PresentationCommitted();
            presentedSize=surfaceSize;ApplySurfaceWidth();
            if(panelPoseReady) SetPanelPose(panelBasePose);
            panelVisibility!.Set(wantsPanelVisible);
        }
        catch(Exception error) {var message=error.Message;Dispose();Status="VR display stopped · "+message;}
    }
    private void UploadThumbnail()
    {
        var data=new byte[128*128*4];
        for(var i=0;i<data.Length;i+=4) {data[i]=45;data[i+1]=160;data[i+2]=110;data[i+3]=255;}
        var pin=GCHandle.Alloc(data,GCHandleType.Pinned);
        try {Check(overlays!.SetOverlayRaw(thumbnail,pin.AddrOfPinnedObject(),128,128,4));}finally {pin.Free();}
    }
    public void Recall() {needsPlacement=true;placed=false;ResetInput();model.StopAll();}
    private void ResetPointerInput() {input.CancelPointer();wristMove.Cancel();activeSource="";releasedMouseSources.Clear();foreach(var gate in gates) gate.Reset();foreach(var motion in pointerMotion) motion.Reset();Array.Clear(wasDown);try {pointer?.Hide();} catch { }}
    private void ResetInput() {ResetPointerInput();input.CancelGrip();wristMenu.Cancel();PublishWristMenu();if(panelGrab.Source is { } source) FinishPanelGrab(source,false);foreach(var gate in gripGates) gate.Reset();foreach(var motion in handMotion) motion.Reset();model.GripContacts.Clear();Array.Clear(handSourceKnown);}
    private static void Check(EVROverlayError error) {if(error!=EVROverlayError.None) throw new InvalidOperationException("SteamVR overlay: "+error);}
    public void Dispose()
    {
        ResetInput();model.StopAll();
        panelMotion.Reset();
        pointer?.Dispose();pointer=null;
        if(overlays is not null)
        {
            foreach(var handle in new[]{panel,dashboard}) if(handle!=0) {try {overlays.ClearOverlayTexture(handle);overlays.DestroyOverlay(handle);}catch { }}
        }
        texture?.Dispose();texture=null;panelVisibility=null;wantsPanelVisible=false;panelPoseReady=false;presentedSize=PanelSizing.MaximumSurface;appliedWidth=-1;mountedDevice=uint.MaxValue;
        if(initialized) {try {OpenVR.Shutdown();}catch { }}
        system=null;overlays=null;vrInput=null;panel=dashboard=thumbnail=0;initialized=false;actionsReady=false;
        Status="VR closed · desktop preview available";
    }
}
