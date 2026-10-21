using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using ShockWave.VrDemo.Shared;

namespace ShockWave.ShaderLab;

internal static class VrcDemoTests
{
    public static void Run(string directory,Action<bool,string> check)
    {
        var fields=VrPanelContract.FeedbackFields();
        check(fields.Length==518 && fields.Distinct().Count()==fields.Length,"Shared VR demo defines a bounded unique numeric feedback contract");
        var model=new PanelModel();
        using var fakeVrc=new UdpClient(new IPEndPoint(IPAddress.Loopback,0));
        using var input=new UdpClient();
        using var link=new VrcDemoLink(model,0,((IPEndPoint)fakeVrc.Client.LocalEndPoint!).Port);
        double now=0;
        void Send(params (string Name,object Value)[] items)
        {
            var before=link.Received;
            var messages=items.Select(i=>OscCodec.Encode(i.Name.StartsWith('/') ? i.Name : "/avatar/parameters/"+VrPanelContract.Prefix+i.Name,i.Value));
            var packet=VrcDemoLink.Bundle(messages); input.Send(packet,packet.Length,new IPEndPoint(IPAddress.Loopback,link.ReceivePort));
            var limit=DateTime.UtcNow.AddSeconds(2);
            while(link.Received==before && DateTime.UtcNow<limit) Thread.Sleep(5);
            link.Tick(now);
        }
        link.Tick(now);
        List<OscMessage> feedback=[];
        while(fakeVrc.Available>0) { IPEndPoint? from=null; feedback.AddRange(OscCodec.Decode(fakeVrc.Receive(ref from))); }
        check(feedback.Count==fields.Length+1 && feedback.All(m=>m.Address.StartsWith("/avatar/parameters/"+VrPanelContract.ReceivePrefix) || m.Address.EndsWith("SzebsVR_RxBeat")),"Actual UDP sends only avatar feedback and heartbeat in bounded immediate bundles");
        check(feedback.Where(m=>m.Arguments[0] is float).All(m=>(float)m.Arguments[0]>=0 && (float)m.Arguments[0]<=1),"All VR shader feedback floats fit avatar local parameter range");
        Send(("InDemo",true)); check(model.Toys.Count==0 && !link.IsLive,"Hidden or heartbeat-free VR input cannot activate a demo session");
        now=0.1; Send(("Visible",true),("InBeat",true),("InDemo",false));
        now=0.2; Send(("InDemo",true));
        check(link.IsLive && model.FunctionCount==10,"Visible avatar heartbeat and Demo populate the same eight toys / ten functions through OSC");
        Send(("InRow1",true)); Send(("InRow1",true));
        check(model.SelectedCount==1 && model.Functions.ElementAt(1).Selected,"A rising row contact selects once; repeated true packets never toggle it twice");
        now=0.3; Send(("InRecord",true),("InTouch",true),("InX",0.3f),("InY",0.2f));
        check(model.Recording && Math.Abs(model.TouchPosition.X-0.3f)<0.001 && Math.Abs(model.Level-0.2f)<0.001,"OSC Record and XY touch start program-owned capture after the input batch");
        now=0.7; Send(("InY",0.8f),("InBeat",false));
        now=1.3; Send(("InTouch",false),("InY",0f));
        var recorded=model.Functions.ElementAt(1).Function;
        check(recorded.LoopRunning && Math.Abs(recorded.Pattern!.Duration-1)<0.001 && recorded.Pattern.At(0.8)>0.79,"Touch release loops the captured pattern without recording the contact's reset-to-zero value");
        Send(("InRow1",false)); now=1.4; Send(("InRow1",true));
        check(model.SelectedCount==0 && recorded.LoopRunning,"OSC deselection preserves the independent recorded loop");
        var exported=new VrFeedback().Frame(model,true);
        check(exported["Mode"]==0 && exported[VrPanelContract.Slot(0,"Cycles")]>0 && exported["LoopCount"]>0,"Deselected loop clip, phase and level are exported for the avatar shader");
        var rejected=link.Rejected; Send(("InX",float.NaN));
        check(link.Rejected==rejected+1 && recorded.LoopRunning,"Invalid coordinate packets are rejected without mutating a running loop");
        Send(("/avatar/parameters/SzebsVR_Rx_Level",0.99f));
        check(recorded.LoopRunning && model.SelectedCount==0,"Returned feedback echo is filtered before the input queue and cannot become control input");
        now=3; link.Tick(now);
        check(!link.IsLive && model.LoopCount==0 && model.Functions.All(f=>f.Level==0) && model.SelectedCount==0,"Avatar heartbeat loss invalidates capture, selection and preview activity without replay");
        now=3.1; Send(("InBeat",true),("InRow1",false));
        Send(("InRow1",true)); now=3.2; Send(("InTouch",true),("InY",0.7f));
        check(model.Touching && model.SelectedCount==1,"Fresh heartbeat and new input can start a new simulated source session");
        now=5.5; link.Tick(now); now=5.6; Send(("InBeat",false));
        check(link.IsLive && model.Functions.All(f=>f.Level==0) && !model.Touching,"Heartbeat recovery cannot replay a touch that remained held through the interruption");
        Send(("InTouch",false)); Send(("InRow1",false)); Send(("InRow1",true)); Send(("InTouch",true),("InY",0.6f));
        check(model.Touching && Math.Abs(model.Level-0.6f)<0.001,"A release and fresh touch intentionally restart simulated input after interruption");
        Send(("/avatar/change","avtr-synthetic-demo"));
        check(!link.IsLive && !model.Touching && model.Functions.All(f=>f.Level==0) && model.SelectedCount==0,"Avatar change invalidates old input and activity");
        check(PanelLayout.Hit(new(880,752))==28,"Desktop VR OSC toggle has its own button hit region");

        var preview=PatternTests.Preview(); preview.ToggleVisible(1);
        var frame=new VrFeedback().Frame(preview,true);
        check(frame.Count==fields.Length && frame.Values.All(f=>float.IsFinite(f) && f>=0 && f<=1),"Rendered Unity fixture shares the exact complete bounded feedback frame");
        var glyph=VrPanelContract.Name("Nora ✨")[0]; var packed=(int)Math.Round(glyph*16383);
        check(packed%128=='N'-32 && packed/128=='o'-32,"Numeric glyph pairs preserve arbitrary ASCII names without a toy model database");
        var levels=VrPanelContract.PackLevels(0.2f,0.8f); var bytes=(int)Math.Round(levels*65535);
        check(bytes%256==51 && bytes/256==204,"Packed waveform pairs retain bounded eight-bit preview levels");
        File.WriteAllText(Path.Combine(directory,"vr-feedback.json"),JsonSerializer.Serialize(new { version=VrPanelContract.Version,values=frame.Select(f=>new {name=f.Key,value=f.Value}).ToArray() },new JsonSerializerOptions {WriteIndented=true}));
    }
}
