namespace ShockWave.ShaderLab;

internal static class PatternTests
{
    private static PanelModel NewModel()
    {
        var model=new PanelModel();
        model.Upsert(new("pattern-toy","Pattern toy",true,90,null,[new("a","First motor"),new("b","Second motor")]));
        model.ToggleVisible(0); return model;
    }
    public static void Run(Action<bool,string> check)
    {
        var model=NewModel(); var first=model.Toys[0].Functions[0];
        model.AdvanceTo(10); model.ToggleRecord(); model.AdvanceTo(12);
        check(model.RecordArmed && !model.Recording && first.Pattern is null,"Record arms without recording idle time or changing output");
        model.Draw(0.2f); model.AdvanceTo(12.4); model.Draw(0.8f); model.AdvanceTo(12.9); model.Draw(0.4f); model.AdvanceTo(13.2); model.Release();
        var clip=first.Pattern ?? throw new InvalidOperationException("No recording was created");
        check(first.LoopRunning && model.RecordArmed && !model.Recording,"Intentional release starts a loop and keeps Record enabled for the next drawing");
        check(Math.Abs(clip.Duration-1.2)<0.00001 && clip.SampleCount>=36,"Pattern duration starts at first touch and ends at release");
        check(Math.Abs(clip.At(0.2)-0.2f)<0.001 && Math.Abs(clip.At(0.6)-0.8f)<0.001 && Math.Abs(clip.At(1.1)-0.4f)<0.001,"Recorder retains stationary sections and changed levels at their elapsed times");
        model.AdvanceTo(13.8); var phaseValue=first.Level; model.AdvanceTo(15);
        check(Math.Abs(phaseValue-0.8f)<0.001 && Math.Abs(first.Level-phaseValue)<0.001,"Loop repeats the same waveform phase using elapsed time");
        model.ToggleRecord(); model.ToggleVisible(0); model.ToggleVisible(1); model.Draw(0.7f); model.Release(); model.AdvanceTo(16.2);
        check(first.LoopRunning && !first.Selected && Math.Abs(first.Level-0.8f)<0.001 && model.Toys[0].Functions[1].Level==0.7f,"Deselecting a looping motor lets another motor hold independently");
        model.ToggleRecord(); model.Draw(0.1f); model.AdvanceTo(16.7); model.Draw(0.9f); model.AdvanceTo(17.2); model.Release();
        var second=model.Toys[0].Functions[1];
        check(first.Pattern==clip && first.LoopStarted==13.2 && second.Pattern!=clip && Math.Abs(second.Pattern!.Duration-1)<0.00001,"Recording another motor preserves the first motor's clip and independent phase");
        model.AdvanceTo(17.95);
        check(first.LoopRunning && second.LoopRunning && Math.Abs(second.Level-0.9f)<0.001,"Independent motor patterns run simultaneously");
        model.HoldSelected(); var held=second.Level; model.AdvanceTo(22);
        check(!second.LoopRunning && second.Level==held && first.LoopRunning,"Hold selected freezes its current level without stopping a sibling loop");
        model.PlaySelected();
        check(second.LoopRunning && second.LoopStarted==22 && Math.Abs(second.Level-0.1f)<0.001,"Play loop explicitly restarts the stored selected clip from the beginning");
        model.ZeroSelected(); model.AdvanceTo(23);
        check(second.Level==0 && !second.LoopRunning && second.Pattern is not null && first.LoopRunning,"Zero selected cancels playback, keeps the in-memory clip and leaves other loops running");
        model.StopAll(); model.AdvanceTo(100);
        check(model.LoopCount==0 && model.Functions.All(f=>f.Level==0) && first.Pattern==clip && model.RecordArmed,"Stop clears activity without replay and preserves the explicit Record toggle");

        var group=NewModel(); group.ToggleVisible(1); group.ToggleRecord(); group.Draw(0.3f); group.AdvanceTo(0.4); group.Draw(0.6f); group.AdvanceTo(0.8); group.Release();
        var shared=group.Toys[0].Functions[0].Pattern;
        check(shared is not null && ReferenceEquals(shared,group.Toys[0].Functions[1].Pattern),"One recording is shared by exactly the functions selected at touch start");
        group.Upsert(new("pattern-toy","Renamed toy",true,88,null,[new("b","Renamed second"),new("a","Renamed first")])); group.AdvanceTo(1.3);
        check(group.Toys[0].Functions.All(f=>f.LoopRunning && f.Pattern==shared && f.LoopStarted==0.8 && Math.Abs(f.Level-0.6f)<0.001),"Metadata rename/reorder preserves stored clips and running phases by scoped alias");
        group.Upsert(new("pattern-toy","Renamed toy",true,88,null,[new("b","Renamed second",0.25f),new("a","Renamed first")]));
        check(!group.Toys[0].Functions[0].LoopRunning && group.Toys[0].Functions[0].Level==0.25f && group.Toys[0].Functions[1].LoopRunning,"Explicit incoming level replaces only that function's playback");
        group.Upsert(new("pattern-toy","Renamed toy",false,88,null)); group.Upsert(new("pattern-toy","Renamed toy",true,88,null));
        check(group.Functions.All(f=>f.Level==0 && !f.Function.LoopRunning && f.Function.Pattern is null && !f.Selected),"Disconnect/reconnect discards every function's recording and active playback");

        var cancel=NewModel(); cancel.ToggleRecord(); cancel.Draw(0.9f); cancel.AdvanceTo(0.5); cancel.Release(commitRecording:false); cancel.AdvanceTo(1);
        check(cancel.LoopCount==0 && !cancel.Recording && cancel.Toys[0].Functions[0].Pattern is null && cancel.Toys[0].Functions[0].Level==0.9f,"Lost capture/focus cancellation holds the current level without creating or starting a clip");
        check(cancel.RecordArmed,"Capture cancellation preserves Record until explicitly toggled off");
        cancel.Draw(0.5f); cancel.AdvanceTo(1.5); cancel.StopAll(); cancel.Release();
        check(cancel.LoopCount==0 && cancel.Functions.All(f=>f.Level==0),"Releasing after Stop cannot commit an interrupted recording");
        cancel.Draw(0.6f); cancel.AdvanceTo(2); cancel.ToggleRecord(); cancel.Release();
        check(cancel.LoopCount==0 && cancel.Toys[0].Functions[0].Pattern is null,"Switching Record off cancels capture without starting a loop");
        var shortTouch=NewModel(); shortTouch.ToggleRecord(); shortTouch.Draw(0.4f); shortTouch.AdvanceTo(0.05); shortTouch.Release();
        check(shortTouch.LoopCount==0 && shortTouch.Toys[0].Functions[0].Pattern is null && shortTouch.Toys[0].Functions[0].Level==0.4f,"Touches shorter than 0.2 seconds remain held values");
        var empty=new PanelModel(); empty.ToggleRecord(); empty.Draw(0.5f); empty.AdvanceTo(1); empty.Release();
        check(empty.RecordArmed && !empty.Recording && empty.LoopCount==0,"An empty selection cannot create an unbound recording");
        var lost=NewModel(); lost.ToggleRecord(); lost.Draw(0.7f); lost.AdvanceTo(0.3); lost.Remove("pattern-toy"); lost.Upsert(new("pattern-toy","Returned",true,90,null,[new("a","First motor")])); lost.AdvanceTo(0.8); lost.Release();
        check(lost.LoopCount==0 && !lost.Recording && lost.Toys[0].Functions[0].Pattern is null && lost.Toys[0].Functions[0].Level==0,"A removed/readded alias cannot inherit or commit an old recording");
        var overridden=NewModel(); overridden.ToggleRecord(); overridden.Draw(0.5f); overridden.AdvanceTo(0.3); overridden.Upsert(new("pattern-toy","Pattern toy",true,90,null,[new("a","First motor",0.2f),new("b","Second motor")])); overridden.AdvanceTo(0.8); overridden.Release();
        check(!overridden.Recording && overridden.LoopCount==0 && overridden.Toys[0].Functions[0].Level==0.2f,"An explicit external override cancels the capture generation without replay");
        var partial=NewModel(); partial.ToggleVisible(1); partial.ToggleRecord(); partial.Draw(.6f); partial.AdvanceTo(.3);
        partial.Upsert(new("pattern-toy","Pattern toy",true,90,null,[new("a","First motor",.2f),new("b","Second motor")])); partial.AdvanceTo(.6); partial.Release();
        check(partial.Toys[0].Functions[0].Level==.2f && !partial.Toys[0].Functions[0].LoopRunning && partial.Toys[0].Functions[1].LoopRunning && partial.RecordArmed,
            "One function's takeover preserves its sibling's unfinished recording and the Record toggle");
        var limit=NewModel(); limit.ToggleRecord(); limit.Draw(0.8f); limit.AdvanceTo(61);
        check(limit.LoopCount==1 && !limit.Recording && !limit.Touching && limit.Toys[0].Functions[0].Pattern!.Duration==60 && limit.Toys[0].Functions[0].Pattern!.SampleCount==1801,"Sixty-second cap finalizes a bounded clip and begins looping");
        limit.Draw(0.2f);
        check(limit.Toys[0].Functions[0].LoopRunning && limit.Toys[0].Functions[0].Level==0.8f,"A still-held mouse cannot overwrite the auto-finished capped recording");
        limit.Release(); limit.Draw(0.2f); limit.Release();
        check(!limit.Toys[0].Functions[0].LoopRunning && limit.Toys[0].Functions[0].Level==0.2f,"A fresh touch after release intentionally replaces loop playback with a held input");

        var background=NewModel(); background.ToggleRecord(); background.Draw(0.2f); background.AdvanceTo(0.5); background.Draw(0.8f); background.AdvanceTo(1); background.Release(); background.AdvanceTo(1000.75);
        check(Math.Abs(background.Toys[0].Functions[0].Level-0.8f)<0.001 && background.LoopCount==1,"A long render/background gap evaluates current loop phase without replaying a backlog");
        var restarted=NewModel();
        check(restarted.Functions.All(f=>f.Function.Pattern is null && !f.Function.LoopRunning && f.Level==0),"A fresh test app/model contains no saved recordings or activity");
        check(PanelLayout.Hit(new(400,190))==25 && PanelLayout.Hit(new(560,190))==26 && PanelLayout.Hit(new(740,190))==27,"Record, Play loop and Hold hit regions match the shader controls");
    }
    public static PanelModel Preview()
    {
        var model=new PanelModel(); model.ReplaceInventory(PanelModel.ParseInventory(OscListener.DemoJson));
        model.ToggleVisible(2); model.Draw(0.25f); model.Release(); model.ToggleVisible(2);
        model.ToggleVisible(1); model.ToggleRecord();
        for(var i=0;i<120;i++) { model.AdvanceTo(i/30.0); model.Draw(0.45f+0.32f*MathF.Sin(i/14f)); }
        model.AdvanceTo(4); model.Release(); model.AdvanceTo(5.4);
        return model;
    }
}
