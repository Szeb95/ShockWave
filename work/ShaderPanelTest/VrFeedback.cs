using System.Drawing;
using ShockWave.VrDemo.Shared;

namespace ShockWave.ShaderLab;

internal sealed class VrFeedback
{
    private readonly float[] history=new float[VrPanelContract.CurveSamples];
    private int valid;
    private double lastSample;
    public void Clear() { Array.Clear(history); valid=0; }
    public Dictionary<string,float> Frame(PanelModel model,bool online)
    {
        var steps=Math.Min(4,(int)((model.Now-lastSample)*8));
        for(var i=0;i<steps;i++) { Array.Copy(history,1,history,0,history.Length-1); history[^1]=model.Level; valid=Math.Min(valid+1,history.Length); }
        lastSample+=steps/8.0; if(model.Now-lastSample>0.5) lastSample=model.Now;
        var result=VrPanelContract.FeedbackFields().ToDictionary(n=>n,_=>0f);
        if(model.AdaptivePaging)
        {
            result["Selected"]=model.SelectedCount/256f;
            // Always clear the optional seventh slot when a paged list replaces
            // a seven-row single page; old avatar panels ignore these additions.
            foreach(var field in new[] {"State","RG","B","Battery"}) result[VrPanelContract.Row(6,field)]=0;
            for(var pair=0;pair<VrPanelContract.NameChars/2;pair++)
            { result[VrPanelContract.Row(6,VrPanelContract.Sample("Toy",pair))]=0; result[VrPanelContract.Row(6,VrPanelContract.Sample("Function",pair))]=0; }
        }
        var display=model.DisplayPattern; var window=display?.Duration ?? 8; var playhead=display is null ? 1 : model.DisplayPhase;
        result["Online"]=online ? 1 : 0; result["Level"]=model.Level;
        result["Touch"]=model.Touching ? 1 : 0; result["TouchX"]=model.TouchPosition.X; result["TouchY"]=model.TouchPosition.Y;
        result["Route"]=model.Route/2f; result["Record"]=model.Recording ? 1 : model.RecordArmed ? 0.5f : 0;
        result["Mode"]=display is null ? 0 : 1; result["Phase"]=(float)model.DisplayPhase; result["Seconds"]=(float)window/60;
        result["Page"]=model.Page/42f; result["Pages"]=model.PageCount/43f; result["Count"]=model.FunctionCount/256f; result["LoopCount"]=model.LoopCount/256f;
        result["LiveRG"]=VrPanelContract.PackBytes(model.LiveColor.R,model.LiveColor.G); result["LiveB"]=model.LiveColor.B/255f;
        result["CurveStart"]=display is null ? (history.Length-valid)/(float)history.Length : 0;
        for(var pair=0;pair<VrPanelContract.CurveSamples/2;pair++)
        {
            float Value(int index) => display?.At(index/(double)(VrPanelContract.CurveSamples-1)*display.Duration) ?? history[index];
            result[VrPanelContract.Sample("Curve",pair)]=VrPanelContract.PackLevels(Value(pair*2),Value(pair*2+1));
        }
        var rows=model.Visible.ToArray();
        for(var row=0;row<rows.Length;row++)
        {
            var f=rows[row]; SetColor(VrPanelContract.Row(row,""),f.Function.Color);
            result[VrPanelContract.Row(row,"State")]=State(f);
            result[VrPanelContract.Row(row,"Battery")]=(f.Toy.Battery+1)/101f;
            var toy=VrPanelContract.Name(f.Toy.Name); var function=VrPanelContract.Name(f.Function.Name);
            for(var pair=0;pair<toy.Length;pair++) { result[VrPanelContract.Row(row,VrPanelContract.Sample("Toy",pair))]=toy[pair]; result[VrPanelContract.Row(row,VrPanelContract.Sample("Function",pair))]=function[pair]; }
        }
        var background=model.Functions.Where(f=>f.Connected && !f.Selected && (f.Level>0 || f.Function.LoopRunning)).ToArray();
        result["Overflow"]=Math.Max(0,background.Length-VrPanelContract.BackgroundSlots)/256f;
        for(var slot=0;slot<Math.Min(background.Length,VrPanelContract.BackgroundSlots);slot++)
        {
            var f=background[slot]; SetColor(VrPanelContract.Slot(slot,""),f.Function.Color); result[VrPanelContract.Slot(slot,"State")]=State(f);
            if(!f.Function.LoopRunning || f.Function.Pattern is not { } clip) continue;
            var cycles=(model.Now-playhead*window-f.Function.LoopStarted)/clip.Duration;
            result[VrPanelContract.Slot(slot,"Phase")]=(float)(cycles-Math.Floor(cycles));
            result[VrPanelContract.Slot(slot,"Cycles")]=(float)(window/clip.Duration)/VrPanelContract.MaximumCycles;
            for(var pair=0;pair<VrPanelContract.LoopSamples/2;pair++) result[VrPanelContract.Slot(slot,VrPanelContract.Sample("C",pair))]=VrPanelContract.PackLevels(
                clip.At(pair*2.0/(VrPanelContract.LoopSamples-1)*clip.Duration),clip.At((pair*2.0+1)/(VrPanelContract.LoopSamples-1)*clip.Duration));
        }
        return result;
        void SetColor(string prefix,Color color) { result[prefix+"RG"]=VrPanelContract.PackBytes(color.R,color.G); result[prefix+"B"]=color.B/255f; }
    }
    private static float State(FunctionBinding f) => VrPanelContract.PackBytes(VrPanelContract.Byte(f.Level),(f.Connected ? 1 : 0)|(f.Selected ? 2 : 0)|(f.Function.LoopRunning ? 4 : 0));
}
