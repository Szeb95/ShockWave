#nullable enable
using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Valve.VR;

namespace ShockWave.Overlay.SteamVr;

internal static class VrHiddenCheck
{
    // Diagnostic only: a temporary, never-shown overlay. It does not register an
    // application, request input, open a dashboard or change the running scene.
    public static int Run(string output)
    {
        Directory.CreateDirectory(output);var initialized=false;ulong overlay=0;
        var submitted=0;var hidden=true;object report;var exitCode=1;
        GpuOverlayTexture? texture=null;
        PointerBeamOverlay? pointer=null;
        try
        {
            if(!Process.GetProcessesByName("vrserver").Any()) throw new InvalidOperationException("SteamVR is not already running");
            var error=EVRInitError.None;var system=OpenVR.Init(ref error,EVRApplicationType.VRApplication_Background);
            if(error!=EVRInitError.None || system is null) throw new InvalidOperationException("SteamVR: "+error);
            initialized=true;var overlays=OpenVR.Overlay;
            var sceneBefore=OpenVR.Compositor.GetCurrentSceneFocusProcess();
            var dashboardBefore=overlays.IsDashboardVisible();var pauseBefore=system.ShouldApplicationPause();
            var adapter=-1;system.GetDXGIOutputInfo(ref adapter);
            if(adapter<0) throw new InvalidOperationException("No runtime graphics adapter");
            var create=overlays.CreateOverlay("szebs.shockwave.hiddencheck."+Environment.ProcessId,"ShockWave hidden texture check",ref overlay);
            if(create!=EVROverlayError.None) throw new InvalidOperationException(create.ToString());
            texture=new GpuOverlayTexture(adapter,fixedSize:PanelSizing.MaximumSurface);
            pointer=new PointerBeamOverlay(overlays,adapter,"szebs.shockwave.hiddenbeam."+Environment.ProcessId);
            if(!PointerBeam.TryPose(Vector3.Zero,new(1,0,0),new(.5f,0,1),out var beamPose)) throw new InvalidOperationException("Beam geometry unavailable");
            pointer.Update(beamPose,false);
            uint beamTextureWidth=0,beamTextureHeight=0;float beamWidth=0,texelAspect=0;
            if(overlays.GetOverlayTextureSize(pointer.Handle,ref beamTextureWidth,ref beamTextureHeight)!=EVROverlayError.None ||
                overlays.GetOverlayWidthInMeters(pointer.Handle,ref beamWidth)!=EVROverlayError.None ||
                overlays.GetOverlayTexelAspect(pointer.Handle,ref texelAspect)!=EVROverlayError.None) throw new InvalidOperationException("Beam texture metadata unavailable");
            var beamGeometryMatches=Math.Abs(beamWidth-beamPose.Length)<.001 && beamTextureWidth==PointerBeam.TextureWidth && beamTextureHeight==PointerBeam.TextureHeight && Math.Abs(texelAspect-1)<.001;
            var pointerInput=VROverlayInputMethod.Mouse;
            if(overlays.GetOverlayInputMethod(pointer.Handle,ref pointerInput)!=EVROverlayError.None) throw new InvalidOperationException("Cannot read pointer input mode");
            var pointerInputDisabled=pointerInput==VROverlayInputMethod.None;
            using var session=new ToyControlSession(()=>null,()=>null);var model=new PanelModel(session);var motion=PoseSmoother.HandPanel();var motionUpdates=0;var poseReadbackMatches=true;
            var adjustment=new WristMoveSession(new WristPlacementStore(null));
            var mountedStart=VrPoseMath.Matrix(new(0,1.3f,-.5f),Quaternion.CreateFromAxisAngle(Vector3.UnitX,MathF.PI/2));
            var localAim=new Vector3(.2f,.1f,-.2f);var aimStart=VrPoseMath.Compose(mountedStart,VrPoseMath.Matrix(localAim,Quaternion.Identity));
            var aimMoved=VrPoseMath.Compose(mountedStart,VrPoseMath.Matrix(localAim+new Vector3(.035f,.02f,-.01f),Quaternion.Identity));
            if(!adjustment.Begin("vr1",Mount.LeftHand,mountedStart,aimStart) || !adjustment.Update("vr1",Mount.LeftHand,mountedStart,aimMoved)) throw new InvalidOperationException("Wrist placement move fixture failed");
            var translatedFit=adjustment.Settings.Left;
            var wristMoveOffsetVerified=translatedFit.Side>.034f && translatedFit.Height>.094f && translatedFit.Forward<.021f;
            var turnedAim=VrPoseMath.Compose(mountedStart,VrPoseMath.Matrix(localAim+new Vector3(.035f,.02f,-.01f),Quaternion.CreateFromYawPitchRoll(.18f,.07f,.1f)));
            if(!adjustment.Update("vr1",Mount.LeftHand,mountedStart,turnedAim) || !adjustment.Finish("vr1",out var fitSaved) || !fitSaved) throw new InvalidOperationException("Pointer rotation fixture failed");
            var adjustedFit=adjustment.Settings.Left;
            var pointerRotationVerified=adjustedFit.Orientation is not null && VrPoseMath.Angle(adjustedFit.Rotation,WristFit.Default.Rotation)>.05f;
            var fadedSubmissions=0;var fadedAlphaVerified=true;var resizedSubmissions=0;var surfaceSizeVerified=true;
            var textureResourceStable=true;IntPtr firstTexture=IntPtr.Zero;
            var floatingGrab=new PanelGrabSession();var floatingGrabUpdates=0;
            for(var i=0;i<60;i++)
            {
                if(i==15) model.SetTransparency(50);
                if(i==45) model.SetTransparency(0);
                if(i==30) model.SetCompact(false);
                if(i==20) model.SwitchPage();
                if(i>=5 && i<30) {model.SetContentSize(480+(i-5)*16,400+(i-5)*8);resizedSubmissions++;}
                if(i>=35) {model.SetContentSize(1000+(i-35)*8,660+(i-35)*4);resizedSubmissions++;}
                model.Tick(i/30d);using var frame=PanelView.Render(model,PanelBuildInfo.Version);
                if(model.TransparencyPercent==50)
                {
                    var stop=model.Layout().Hits.Single(h=>h.Id=="stop").Rect;
                    fadedAlphaVerified &= frame.GetPixel((int)(stop.X+stop.Width/2),(int)(stop.Y+stop.Height/2)).A==128;fadedSubmissions++;
                }
                texture.Upload(frame);var descriptor=texture.Descriptor;
                if(i==0) firstTexture=descriptor.handle;else textureResourceStable &= descriptor.handle==firstTexture;
                var submittedError=overlays.SetOverlayTexture(overlay,ref descriptor);
                if(submittedError!=EVROverlayError.None) throw new InvalidOperationException("Texture submit: "+submittedError);
                texture.PresentationCommitted();submitted++;
                var surfaceSize=texture.SurfaceSize;var surfaceWidth=PanelSizing.WidthMeters(model.WidthMeters,model.Compact,surfaceSize);
                if(overlays.SetOverlayWidthInMeters(overlay,surfaceWidth)!=EVROverlayError.None) throw new InvalidOperationException("Resized width rejected");
                uint submittedWidth=0,submittedHeight=0;float readWidth=0;
                surfaceSizeVerified &= overlays.GetOverlayTextureSize(overlay,ref submittedWidth,ref submittedHeight)==EVROverlayError.None && submittedWidth==surfaceSize.Width && submittedHeight==surfaceSize.Height &&
                    overlays.GetOverlayWidthInMeters(overlay,ref readWidth)==EVROverlayError.None && Math.Abs(readWidth-surfaceWidth)<.00001f;
                var hand=VrPoseMath.Matrix(new(.001f*MathF.Sin(i),1.3f,-.5f),Quaternion.CreateFromAxisAngle(Vector3.UnitX,MathF.PI/2));
                var target=VrPoseMath.Compose(hand,adjustedFit.Matrix());
                if(!motion.TryUpdate(target,i/90d,out var smoothed)) throw new InvalidOperationException("Smoothed mounted pose unavailable");
                var basePose=smoothed;
                if(!model.Compact)
                {
                    var grabAim=VrPoseMath.Matrix(new(.1f+(i-30)*.001f,1.3f,-.2f),Quaternion.CreateFromYawPitchRoll((i-30)*.003f,.05f,0));
                    if(i==30 ? !floatingGrab.Begin("vr0",grabAim,smoothed) : !floatingGrab.Update("vr0",grabAim)) throw new InvalidOperationException("Floating grab fixture failed");
                    basePose=floatingGrab.WorldPose;floatingGrabUpdates++;
                }
                var surfacePose=PanelSizing.Pose(basePose,model.WidthMeters,model.Compact,surfaceSize);
                if(overlays.SetOverlayTransformAbsolute(overlay,ETrackingUniverseOrigin.TrackingUniverseStanding,ref surfacePose)!=EVROverlayError.None) throw new InvalidOperationException("Smoothed panel transform rejected");
                var origin=ETrackingUniverseOrigin.TrackingUniverseStanding;var readback=new HmdMatrix34_t();
                if(overlays.GetOverlayTransformAbsolute(overlay,ref origin,ref readback)!=EVROverlayError.None) throw new InvalidOperationException("Smoothed transform readback failed");
                poseReadbackMatches &= Math.Abs(readback.m3-surfacePose.m3)<.00001 && Math.Abs(readback.m7-surfacePose.m7)<.00001 && Math.Abs(readback.m11-surfacePose.m11)<.00001 &&
                    VrPoseMath.TryRead(readback,out _,out var readRotation) && VrPoseMath.TryRead(surfacePose,out _,out var expectedRotation) && VrPoseMath.Angle(readRotation,expectedRotation)<.001;motionUpdates++;
                pointer.Update(beamPose,false);
                hidden &= !overlays.IsOverlayVisible(overlay) && !overlays.IsOverlayVisible(pointer.Handle);
                Thread.Sleep(12);
            }
            var stretchLayout=model.Layout();var stretchImage=texture.SurfaceSize;var seed=VrPoseMath.Matrix(new(0,1.2f,-.7f),Quaternion.Identity);
            Vector3 StretchRail(string id)
            {
                var r=stretchLayout.Hits.Single(h=>h.Id==id).Rect;var density=.44f/1160;
                return new Vector3(seed.m3,seed.m7,seed.m11)+new Vector3((r.X+r.Width/2-580)*density,(stretchLayout.Size.Height-380-r.Y-r.Height/2)*density,0);
            }
            var stretchLeft=StretchRail("grab-left");var stretchRight=StretchRail("grab-right");var stretchMiddle=(stretchLeft+stretchRight)/2;
            var stretch=new PanelHoldSession();var twoHandScaleVerified=stretch.Begin("vr0","grab-left",VrPoseMath.Matrix(stretchLeft,Quaternion.Identity),seed,.44f,stretchLayout) && stretch.Begin("vr1","grab-right",VrPoseMath.Matrix(stretchRight,Quaternion.Identity),seed,.44f,stretchLayout);
            var twoHandResizeUpdates=0;var minimumStretch=.44f;var maximumStretch=.44f;
            for(var step=0;step<15;step++)
            {
                var ratio=1+.2f*MathF.Sin(step*MathF.PI/7);var pan=new Vector3(step*.001f,0,0);
                twoHandScaleVerified &= stretch.SetHand("vr0",VrPoseMath.Matrix(stretchMiddle+(stretchLeft-stretchMiddle)*ratio+pan,Quaternion.Identity)) && stretch.SetHand("vr1",VrPoseMath.Matrix(stretchMiddle+(stretchRight-stretchMiddle)*ratio+pan,Quaternion.Identity)) && stretch.Update(stretchLayout,.44f);
                minimumStretch=Math.Min(minimumStretch,stretch.Width);maximumStretch=Math.Max(maximumStretch,stretch.Width);
                var stretchWidth=PanelSizing.WidthMeters(stretch.Width,false,stretchImage);var stretchPose=PanelSizing.Pose(stretch.WorldPose,stretch.Width,false,stretchImage);
                if(overlays.SetOverlayWidthInMeters(overlay,stretchWidth)!=EVROverlayError.None || overlays.SetOverlayTransformAbsolute(overlay,ETrackingUniverseOrigin.TrackingUniverseStanding,ref stretchPose)!=EVROverlayError.None) throw new InvalidOperationException("Two-hand resize submission failed");
                var readOrigin=ETrackingUniverseOrigin.TrackingUniverseStanding;var readPose=new HmdMatrix34_t();float readScale=0;
                twoHandScaleVerified &= overlays.GetOverlayWidthInMeters(overlay,ref readScale)==EVROverlayError.None && Math.Abs(readScale-stretchWidth)<.00001f && overlays.GetOverlayTransformAbsolute(overlay,ref readOrigin,ref readPose)==EVROverlayError.None && Math.Abs(readPose.m3-stretchPose.m3)<.00001f && Math.Abs(readPose.m7-stretchPose.m7)<.00001f && Math.Abs(readPose.m11-stretchPose.m11)<.00001f;
                hidden &= !overlays.IsOverlayVisible(overlay);twoHandResizeUpdates++;
            }
            twoHandScaleVerified &= minimumStretch<.4f && maximumStretch>.48f;
            const float projectionWidth=.26f;var image=texture.SurfaceSize;
            if(overlays.SetOverlayWidthInMeters(overlay,projectionWidth)!=EVROverlayError.None) throw new InvalidOperationException("Projection width rejected");
            var scale=new HmdVector2_t {v0=image.Width,v1=image.Height};overlays.SetOverlayMouseScale(overlay,ref scale);
            var panelOrigin=ETrackingUniverseOrigin.TrackingUniverseStanding;var panelPose=new HmdMatrix34_t();
            if(overlays.GetOverlayTransformAbsolute(overlay,ref panelOrigin,ref panelPose)!=EVROverlayError.None) throw new InvalidOperationException("Projection pose unavailable");
            var center=new Vector3(panelPose.m3,panelPose.m7,panelPose.m11);var right=new Vector3(panelPose.m0,panelPose.m4,panelPose.m8);var up=new Vector3(panelPose.m1,panelPose.m5,panelPose.m9);var normal=new Vector3(panelPose.m2,panelPose.m6,panelPose.m10);
            var expected=new PointF(image.Width*.37f,image.Height*.62f);
            var worldHit=center+right*(expected.X/image.Width-.5f)*projectionWidth+up*(.5f-expected.Y/image.Height)*(projectionWidth*image.Height/image.Width);
            var aim=panelPose;var rayOrigin=worldHit+normal*.6f;aim.m3=rayOrigin.X;aim.m7=rayOrigin.Y;aim.m11=rayOrigin.Z;
            var projected=PanelRayProjection.TryProject(aim,panelPose,projectionWidth,image,out var projectedPoint) && Math.Abs(projectedPoint.X-expected.X)<.03f && Math.Abs(projectedPoint.Y-expected.Y)<.03f;
            var nativeRay=new VROverlayIntersectionParams_t {eOrigin=ETrackingUniverseOrigin.TrackingUniverseStanding,vSource=new HmdVector3_t {v0=rayOrigin.X,v1=rayOrigin.Y,v2=rayOrigin.Z},vDirection=new HmdVector3_t {v0=-normal.X,v1=-normal.Y,v2=-normal.Z}};
            var nativeHit=new VROverlayIntersectionResults_t();var nativeIntersectionAvailable=overlays.ComputeOverlayIntersection(overlay,ref nativeRay,ref nativeHit);
            bool? nativeProjectionMatches=nativeIntersectionAvailable ? Math.Abs(nativeHit.vUVs.v0*image.Width-projectedPoint.X)<.1f && Math.Abs((1-nativeHit.vUVs.v1)*image.Height-projectedPoint.Y)<.1f : null;
            var contactLayout=model.Layout();var rail=contactLayout.Hits.Single(h=>h.Id=="grab-left").Rect;
            var contactPixel=new PointF(rail.X+rail.Width/2,rail.Y+rail.Height/2+image.Height-contactLayout.Size.Height);
            var contactWorld=center+right*(contactPixel.X/image.Width-.5f)*projectionWidth+up*(.5f-contactPixel.Y/image.Height)*(projectionWidth*image.Height/image.Width);
            var contactPose=VrPoseMath.Matrix(contactWorld,Quaternion.CreateFromYawPitchRoll(MathF.PI,.4f,0));
            var directGripContactVerified=PanelContact.TryHandle(contactPose,panelPose,projectionWidth,image,contactLayout,out var contacted) && contacted=="grab-left";
            var distantGrip=VrPoseMath.Matrix(contactWorld+normal*.2f,Quaternion.Identity);
            var distantGripRejected=!PanelContact.TryHandle(distantGrip,panelPose,projectionWidth,image,contactLayout,out _);
            var focusPreserved=sceneBefore==OpenVR.Compositor.GetCurrentSceneFocusProcess();
            var dashboardPreserved=dashboardBefore==overlays.IsDashboardVisible();
            var pauseAfter=system.ShouldApplicationPause();
            exitCode=hidden && focusPreserved && dashboardPreserved && pauseBefore==pauseAfter && beamGeometryMatches && pointerInputDisabled && poseReadbackMatches && wristMoveOffsetVerified && pointerRotationVerified && fadedAlphaVerified && fadedSubmissions==30 && resizedSubmissions==50 && floatingGrabUpdates==30 && surfaceSizeVerified && textureResourceStable && projected && nativeProjectionMatches!=false && directGripContactVerified && distantGripRejected && twoHandResizeUpdates==15 && twoHandScaleVerified ? 0 : 1;
            report=new {version=PanelBuildInfo.Version,passed=exitCode==0,textureSubmissions=submitted,hiddenThroughout=hidden,sceneFocusPreserved=focusPreserved,dashboardPreserved,pauseBefore,pauseAfter,
                beamWidthMeters=beamWidth,beamTextureWidth,beamTextureHeight,texelAspect,beamGeometryMatches,pointerInputDisabled,
                smoothedPoseUpdates=motionUpdates,poseReadbackMatches,wristMoveOffsetVerified,pointerRotationVerified,fadedSubmissions,fadedAlphaVerified,
                resizedSubmissions,surfaceSizeVerified,textureResourceStable,floatingGrabUpdates,
                extendedProjectionMatchesKnownPoint=projected,nativeIntersectionAvailable,nativeProjectionMatches,directGripContactVerified,distantGripRejected,twoHandResizeUpdates,twoHandScaleVerified,minimumStretch,maximumStretch,
                runtime=system.GetRuntimeVersion(),note="Hidden panel/beam overlays only; no headset visual result, input action, haptic, provider or settings operation."};
        }
        catch(Exception error) {report=new {version=PanelBuildInfo.Version,passed=false,textureSubmissions=submitted,error=error.ToString()};}
        finally
        {
            if(initialized && overlay!=0) {try {OpenVR.Overlay.ClearOverlayTexture(overlay);OpenVR.Overlay.DestroyOverlay(overlay);}catch { }}
            pointer?.Dispose();texture?.Dispose();if(initialized) OpenVR.Shutdown();
        }
        File.WriteAllText(Path.Combine(output,"hidden-vr-check.json"),JsonSerializer.Serialize(report,new JsonSerializerOptions {WriteIndented=true}));
        return exitCode;
    }
}
