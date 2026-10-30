using System.Numerics;
using ShockWave.Overlay.Toys;
using Valve.VR;

namespace ShockWave.Overlay.SteamVr;

// Explicit diagnostic only. Poses, outputs and devices are entirely synthetic.
internal static class WristMenuChecks
{
    public static void Run(Action<bool,string> check,string output)
    {
        var size=PanelSizing.Reference(true);var identity=VrPoseMath.Matrix(Vector3.Zero,Quaternion.Identity);
        var frame=new WristMenuFrame(identity,size,.24f);var anchor=WristMenuFrame.Anchor(size);var top=WristMenuFrame.Top(size);
        var closed=frame.HandAt(anchor);var open=frame.HandAt(top);var gesture=new WristMenuGesture();var time=100d;
        gesture.Update(frame,frame.HandAt(anchor,.17f),true,false,true,time);
        check(gesture.HandleVisible && gesture.AnchorVisible && !gesture.Contact,"closed wrist icon reveals at eighteen centimetres without enabling a distant grab");
        gesture.Update(frame,frame.HandAt(anchor,.21f),true,false,true,time+=.016);
        check(gesture.HandleVisible,"wrist proximity hysteresis keeps the handle visible across the near boundary");
        gesture.Update(frame,frame.HandAt(anchor,.25f),true,false,true,time+=.016);
        check(!gesture.HandleVisible,"closed wrist icon hides beyond twenty-four centimetres");
        var hover=new WristMenuGesture();var hovering=frame.HandAt(anchor,.10f);
        hover.Update(frame,hovering,true,false,true,time+=.016);
        check(hover.Contact && hover.Update(frame,hovering,true,true,true,time+=.016),"fresh grip ten centimetres above the icon opens without controller collision");
        check(hover.Amount==0 && hover.Target==0,"hovering grip retains the picked opening amount without a depth jump");
        hover.Update(frame,frame.HandAt(new(top.X+2000,top.Y)),true,true,true,time+=.016);
        check(hover.Active && hover.HandleVisible && hover.AnchorVisible,"an owned pull keeps both the fixed origin and moving bar visible outside proximity");
        hover.Reset();hover.Update(frame,frame.HandAt(anchor,.13f),true,false,true,time+=.016);
        check(hover.AnchorVisible && !hover.Contact && !hover.Update(frame,frame.HandAt(anchor,.13f),true,true,true,time+=.016),"visibility range remains separate from the twelve-centimetre depth grab limit");
        gesture.Reset();gesture.Update(frame,closed,true,true,true,time+=.016);
        check(!gesture.Active,"arriving with grip already held cannot open the wrist menu");
        gesture.Update(frame,closed,true,false,true,time+=.016);
        check(gesture.Update(frame,closed,true,true,true,time+=.016) && gesture.Active,"touch followed by fresh grip begins wrist opening");
        var half=new PointF((anchor.X+top.X)/2,(anchor.Y+top.Y)/2);
        for(var i=0;i<10;i++) gesture.Update(frame,frame.HandAt(half),true,true,true,time+=.016);
        check(gesture.Amount is >.25f and <.75f,"pull distance smoothly controls the wrist reveal amount");
        gesture.Update(frame,frame.HandAt(half),true,false,true,time+=.016);
        for(var i=0;i<24;i++) gesture.Update(frame,open,true,false,true,time+=.016);
        check(gesture.Amount==1 && !gesture.Active,"release past the opening threshold settles fully open");
        gesture.Update(frame,open,true,true,true,time+=.016);
        for(var i=0;i<5;i++) gesture.Update(frame,frame.HandAt(half),true,true,true,time+=.016);
        gesture.Update(frame,default,false,true,true,time+=.016);
        check(gesture.Amount==1 && !gesture.Active,"lost free-hand tracking restores the previous open state instead of leaving a partial menu");
        gesture.Reset();gesture.Update(frame,closed,true,false,true,time+=.016);gesture.Update(frame,closed,true,true,true,time+=.016);
        gesture.Update(frame,default,false,true,true,time+=.016);
        check(gesture.Amount==0 && !gesture.Active,"lost tracking during first opening restores the closed state");
        var moved=new WristMenuFrame(VrPoseMath.Matrix(new(.4f,.7f,-.3f),Quaternion.CreateFromYawPitchRoll(.6f,-.8f,.4f)),size,.24f);
        moved.Sample(moved.HandAt(top),1,out var movedProjection,out var movedContact,out _,out var movedLength);
        frame.Sample(open,1,out var projection,out _,out _,out var length);
        check(Math.Abs(movedProjection-projection)<.00001 && Math.Abs(movedLength-length)<.00001 && movedContact<.00001,
            "wrist-relative contact and opening survive simultaneous arm translation and rotation");
        var small=new WristMenuFrame(identity,new(718,468),.24f);var large=new WristMenuFrame(identity,new(1258,828),.24f);
        VrPoseMath.TryRead(small.HandAt(WristMenuFrame.Anchor(small.Size)),out var smallPoint,out _);
        VrPoseMath.TryRead(large.HandAt(WristMenuFrame.Anchor(large.Size)),out var largePoint,out _);
        check(Vector3.Distance(smallPoint,largePoint)<.00001,"closed wrist anchor stays fixed when the saved menu size changes");

        var fake=new PanelIntegrationChecks.FakeAdapter();var controller=new LocalToyController(fake,new LocalToyControlStore(),automaticControls:true);
        try
        {
            using var session=new ToyControlSession(()=>fake.Snapshot,()=>controller);
            var now=ToyOscRouter.Clock;session.Tick(now);session.SelectPanelFunction("fixture","f0");session.Action("InRecord",false);
            session.DrawPanel(.25f,true);session.Tick(now+=.3);session.DrawPanel(.75f,false);session.Tick(now+=.3);session.Release(true);
            var row=session.Model.Functions.First();var pattern=row.Function.Pattern;var epoch=row.Function.LoopStarted;
            check(session.Model.LoopCount==1 && pattern is not null,"wrist visibility fixture starts an existing production wave");
            var panel=new PanelModel(session);var external=false;var input=new PanelInput(panel,()=>external);
            panel.ContextInvalidated+=()=>input.CancelPointer();
            using var host=new SteamVrHost(panel,input,new WristPlacementStore(null));var fit=host.CurrentFit;
            panel.Tick(now);panel.EnableWristMenu();
            bool Step(PointF point,bool down,bool valid=true,float depth=0,bool gripAvailable=true)
            {
                session.Tick(now+=.016);panel.Tick(now);
                var current=new WristMenuFrame(identity,panel.Layout().Size,panel.WidthMeters);
                var started=host.UpdateWristMenu(current,current.HandAt(point,depth),valid,down,now,gripAvailable);panel.MarkPresented();return started;
            }
            PointF A()=>WristMenuFrame.Anchor(panel.Layout().Size);
            PointF T()=>WristMenuFrame.Top(panel.Layout().Size);
            void Settle(PointF p,bool down,int count=24) {for(var i=0;i<count;i++) Step(p,down);}
            void Capture(string name) {using var image=PanelView.Render(panel.CaptureDisplay(),"wrist gesture preview");image.Save(Path.Combine(output,name+".png"));}
            using(var image=PanelView.Render(panel.CaptureDisplay(),"hidden"))
            {
                var ink=false;for(var y=0;y<image.Height;y+=4) for(var x=0;x<image.Width;x+=4) ink|=image.GetPixel(x,y).A!=0;
                check(!ink,"closed distant wrist renders a completely transparent surface");
            }
            Capture("wrist-menu-hidden");
            Step(A(),false,true,.10f,gripAvailable:false);
            check(panel.WristAnchorVisible && panel.WristHandleVisible && !panel.WristMenuDragging,"tracked hand reveals the closed wrist icon even without an active grip binding");
            Capture("wrist-menu-no-grip-binding");
            check(!Step(A(),true,true,.10f) && !panel.WristMenuDragging,"binding recovery with grip held cannot start an unseen capture");
            Step(A(),false,true,.10f);check(panel.WristHandleVisible && panel.WristAnchorVisible && !panel.WristInputReady,"near wrist shows only the fixed opening icon");
            Capture("wrist-menu-near");
            check(Step(A(),true,true,.10f),"native host starts the fresh hovering grip through the production wrist gesture path");
            Capture("wrist-menu-grab-start");
            Step(T(),true,true,.10f,gripAvailable:false);
            check(!panel.WristMenuDragging && panel.WristMenuAmount==0 && panel.WristAnchorVisible,"losing a grip binding cancels the unfinished opening but retains tracking visibility");
            check(session.Model.LoopCount==1 && row.Function.LoopStarted==epoch,"grip binding loss leaves the active wave and original playback epoch intact");
            check(!Step(A(),true,true,.10f),"held grip after a binding interruption requires release before recapture");
            Step(A(),false,true,.10f);check(Step(A(),true,true,.10f),"a new released grip recaptures after binding recovery");
            var middle=new PointF((A().X+T().X)/2,(A().Y+T().Y)/2);Settle(middle,true,10);
            check(panel.WristMenuAmount is >0 and <1 && !panel.WristInputReady,"partly unfolded wrist has no live curve or button targets");
            Capture("wrist-menu-partial");
            using(var closedImage=new Bitmap(Path.Combine(output,"wrist-menu-near.png")))
            using(var partialImage=PanelView.Render(panel.CaptureDisplay(),"fixed icon fixture"))
            {
                var stable=true;var a=A();
                for(var y=-15;y<=15;y++) for(var x=-15;x<=15;x++) stable&=closedImage.GetPixel((int)a.X+x,(int)a.Y+y)==partialImage.GetPixel((int)a.X+x,(int)a.Y+y);
                check(stable,"the actual Move glyph and icon interior remain fixed while the separate bar is pulled");
            }
            Settle(T(),true);Step(T(),false);Settle(T(),false);
            check(panel.WristInputReady && panel.Layout().Hits.Any(h=>h.Id=="wrist-handle") && panel.Layout().Hits.All(h=>h.Id!="move"),"fully open wrist enables controls and places its grip handle above the menu");
            Capture("wrist-menu-open");
            Step(new(A().X+2000,A().Y+2000),false);
            check(panel.WristInputReady && panel.WristMenuAmount==1 && !panel.WristHandleVisible && !panel.WristAnchorVisible,"expanded wrist keeps its controls open while both handle and origin hide from a distant hand");
            Capture("wrist-menu-open-distant");
            Step(T(),false,true,.17f);
            check(panel.WristHandleVisible && !panel.WristHandleContact,"expanded top bar returns when the free hand approaches without requiring contact");
            Capture("wrist-menu-open-idle");
            Step(T(),false);
            check(session.Model.LoopCount==1 && ReferenceEquals(row.Function.Pattern,pattern) && row.Function.LoopStarted==epoch,"opening leaves the wave and its original playback phase intact");

            session.Shockers.Inventory=()=>[new("shock-fixture","Fixture shocker","Mock",true,40,15000,false)];
            var sends=0;session.Shockers.Send=(_,_,_,_,_)=>{sends++;return true;};
            session.SetPage(true);session.Tick(now+=.02);panel.Tick(now);session.Shockers.SelectPanelTarget("shock-fixture");session.Shockers.SetSlider(30,.8f);session.Shockers.SetSlider(31,1);
            session.Shockers.Press(4,32,now);session.Shockers.Release(4);session.Shockers.Press(4,33,now+.001);session.Shockers.Release(4);
            session.Shockers.Press(4,32,now+.002);session.Shockers.Release(4);Step(T(),false);
            check(session.Shockers.Armed && sends==1 && panel.ShockerFeedback.Length>0,"fixture has a submitted shock plus a separate pending confirmation");
            check(Step(T(),true,true,.10f),"top wrist bar captures a fresh closing grip above its plane");
            check(!session.Shockers.Armed && sends==1 && panel.ShockerFeedback.Length>0,"closing cancels only pending confirmation and preserves the submitted shock receipt");
            Settle(A(),true);Step(A(),false);Settle(A(),false);
            check(!panel.WristInputReady && panel.WristMenuAmount==0 && session.Model.LoopCount==1 && row.Function.LoopStarted==epoch && ReferenceEquals(row.Function.Pattern,pattern),
                "closing to zero hides the wrist while the same production wave keeps running");
            check(sends==1 && panel.ShockerFeedback.Length>0,"closed wrist neither cancels, resends nor extends an already submitted shock");
            var low=row.Level;var high=row.Level;
            for(var i=0;i<48;i++) {Step(A(),false);low=Math.Min(low,row.Level);high=Math.Max(high,row.Level);}
            check(row.Function.LoopRunning && high-low>.1,"wave values continue advancing while the wrist is hidden");
            var stop=panel.Layout().Hits.Single(h=>h.Id=="stop").Rect;var stopPoint=new PointF(stop.Left+stop.Width/2,stop.Top+stop.Height/2);
            input.Down("vr1",stopPoint);input.Up("vr1",stopPoint);
            check(session.Model.LoopCount==1 && sends==1,"invisible wrist buttons cannot receive trigger clicks");
            Step(new(A().X+2000,A().Y+2000),false);check(!panel.WristHandleVisible,"closed wrist handle disappears when the free controller leaves");
            Capture("wrist-menu-closed-playing");
            host.PausePresentationInput();session.Tick(now+=.1);panel.Tick(now);
            check(session.Model.LoopCount==1 && row.Function.LoopStarted==epoch && sends==1 && panel.ShockerFeedback.Length>0,"closed wrist tracking or focus loss preserves active waves and submitted shock");
            host.ResumePresentationInput();session.Tick(now+=.1);panel.Tick(now);
            check(session.Model.LoopCount==1 && sends==1 && panel.WristMenuAmount==0,"tracking recovery keeps the wrist closed without restarting output");
            Step(A(),false);Step(A(),true);Settle(T(),true);Step(T(),false);Settle(T(),false);
            check(panel.WristInputReady && session.Model.LoopCount==1 && sends==1 && row.Function.LoopStarted==epoch,"reopening restores current controls without restarting waves or replaying shock");
            Capture("wrist-menu-shocker-open");
            check(host.CurrentFit==fit,"opening and closing never rewrite saved wrist placement");
            external=true;Step(T(),false);var blocked=Step(T(),true);
            check(!blocked && !panel.WristMenuDragging,"desktop input ownership prevents a competing wrist grip");external=false;Step(T(),false);
            panel.SetWristFitMode(true);
            check(panel.WristInputReady && panel.Layout().Hits.Any(h=>h.Id=="move"),"separate wrist adjustment restores the existing pointer Move control");panel.SetWristFitMode(false);
            var snapshot=panel.CaptureDisplay();panel.SetWristMenu(0,false,false,false);
            check(snapshot.WristMenuAmount==1 && snapshot.WristHandleVisible && snapshot.WristAnchorVisible,"background raster snapshot freezes the opening amount and separate fixed icon visibility");
            panel.SetWristMenu(.3f,true,false,false);var late=panel.CaptureFrameStamp();panel.SetWristMenu(0,true,false,false);
            check(!panel.CanDisplay(late),"a late partial-reveal image cannot reopen an already closed wrist");
            using(var gpu=new GpuOverlayTexture(software:true,fixedSize:PanelSizing.MaximumSurface))
            {
                nint handle=0;var stable=true;
                foreach(var amount in new[]{0f,.15f,.5f,1f,.5f,0f})
                {
                    panel.SetWristMenu(amount,true,amount is >0 and <1,false);using var image=PanelView.Render(panel.CaptureDisplay(),"reveal GPU fixture");gpu.Upload(image);
                    if(handle==0) handle=gpu.Descriptor.handle;else stable&=handle==gpu.Descriptor.handle;gpu.PresentationCommitted();
                }
                check(stable && gpu.SurfaceSize==PanelSizing.MaximumSurface,"wrist unfolding reuses the persistent GPU surface without resize allocation");
            }
        }
        finally {controller.DisposeAsync().AsTask().GetAwaiter().GetResult();}
    }
}
