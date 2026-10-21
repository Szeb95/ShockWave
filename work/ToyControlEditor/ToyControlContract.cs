using System;
using System.Collections.Generic;
using System.Linq;

namespace ShockWave.ToyControl.Shared
{
    // Shared by the Windows test host and Unity Editor installer. The avatar
    // receives normalized numeric parameters; no runtime C# is uploaded.
    public static class ToyControlContract
    {
        public const string Prefix = "SzebsToy_", ReceivePrefix = "SzebsToy_Rx_";
        public const int Version = 5, Rows = 7, BackgroundSlots = 16, LoopSamples = 32, CurveSamples = 64, NameChars = 18;
        public const int DefaultReceivePort = 9001, DefaultSendPort = 9000;
        public const float MaximumCycles = 300;
        public static readonly string[] BoolInputs = {
            "Visible", "Follow", "Recall", "InBeat", "InTouch", "InRecord", "InPlay", "InHold", "InStop", "InClear",
            "InPrev", "InNext", "InOwn", "InRemote", "InBoth", "InTestTouch", "InFlipX", "InFlipY",
            "InRow0", "InRow1", "InRow2", "InRow3", "InRow4", "InRow5", "InRow6", "InInputMode", "InPointerLHit", "InPointerLPress", "InPointerRHit", "InPointerRPress", "InPage", "InPanelTouch", "InShockerReady"
        };
        public static readonly string[] FloatInputs = { "InX", "InY", "InTestX", "InTestY", "InPointerLX", "InPointerLY", "InPointerRX", "InPointerRY", "InPanelX", "InPanelY" };
        public static readonly string[] GlobalFields = { "Online", "Level", "Touch", "TouchX", "TouchY", "Route", "Record", "Mode", "Phase", "Seconds", "Page", "Pages", "Count", "Selected", "LoopCount", "Overflow", "LiveRG", "LiveB", "CurveStart", "Pointer", "ShockerPage", "ShockIntensity", "ShockDuration", "ShockArmed", "ShockRemaining", "ShockReady" };
        public static string Row(int row, string field) { return "R" + row + "_" + field; }
        public static string Slot(int slot, string field) { return "S" + slot.ToString("00") + "_" + field; }
        public static string Sample(string prefix, int pair) { return prefix + pair.ToString("00"); }
        public static string[] FeedbackFields()
        {
            var fields = new List<string>(GlobalFields);
            for (var i=0;i<CurveSamples/2;i++) fields.Add(Sample("Curve",i));
            for (var row=0;row<Rows;row++)
            {
                foreach(var field in new[] { "State", "RG", "B", "Battery" }) fields.Add(Row(row,field));
                for(var pair=0;pair<NameChars/2;pair++) { fields.Add(Row(row,Sample("Toy",pair))); fields.Add(Row(row,Sample("Function",pair))); }
            }
            for(var slot=0;slot<BackgroundSlots;slot++)
            {
                foreach(var field in new[] { "State", "RG", "B", "Phase", "Cycles" }) fields.Add(Slot(slot,field));
                for(var pair=0;pair<LoopSamples/2;pair++) fields.Add(Slot(slot,Sample("C",pair)));
            }
            return fields.ToArray();
        }
        public static float PackBytes(int low, int high) { return (Math.Max(0,Math.Min(255,low)) + Math.Max(0,Math.Min(255,high))*256)/65535f; }
        public static int Byte(float level) { return (int)Math.Round(Math.Max(0,Math.Min(1,level))*255); }
        public static float PackLevels(float a,float b) { return PackBytes(Byte(a),Byte(b)); }
        public static float PackGlyphs(char a,char b)
        {
            int Code(char c) { return c >= 32 && c <= 126 ? c-32 : '?'-32; }
            return (Code(a)+Code(b)*128)/16383f;
        }
        public static float[] Name(string text)
        {
            text=(text ?? "").PadRight(NameChars).Substring(0,NameChars);
            return Enumerable.Range(0,NameChars/2).Select(i=>PackGlyphs(text[i*2],text[i*2+1])).ToArray();
        }
    }
}
