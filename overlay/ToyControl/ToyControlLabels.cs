using System.Drawing.Imaging;
using System.Drawing.Text;
using ShockWave.ShaderLab;

namespace ShockWave.Overlay;

internal static class ToyControlLabels
{
    public static Bitmap Create(ToyControlSession session)
    {
        var model = session.DisplayModel;
        var image = new Bitmap(1240, 800, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(image); g.Clear(Color.Transparent); g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var white = Color.FromArgb(242,243,245); var faint = Color.FromArgb(181,186,193); var green = Color.FromArgb(24,185,145);
        void Text(string value, float x, float y, float size, Color? color = null, bool bold = false, float width = 240)
        {
            using var font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(color ?? white);
            using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(value, font, brush, new RectangleF(x,y,width,size*1.8f), format);
        }
        void Center(string value, RectangleF box, float size, bool bold = false)
        {
            using var font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(white); using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(value,font,brush,box,format);
        }
        if(session.ShockerPage)
        {
            Text("SHOCKERS",43,124,17,white,true); Text("Choose one shocker",43,151,12,faint);
            for(var i=0;i<model.Visible.Count();i++)
            {
                var row=model.Visible.ElementAt(i); var y=244+i*64;
                Text(row.Toy.Name,54,y,17,row.Connected ? green : faint,true,208);
                Text(row.Function.Name,54,y+24,12,faint,width:208);
            }
            if(model.PageCount>1) { Text("‹",55,645,27,faint); Text("›",272,645,27,faint); Center($"{model.Page+1} / {model.PageCount}",new(112,648,134,36),13); }
            Center("STOP ALL",ToyControlLayout.Stop,14,true);
            Center(session.PointerMode ? "Pointer" : "Touch",ToyControlLayout.InputMode,14,true);
            Center("Toys",ToyControlLayout.PageSwitch,16,true);
            Text("Shocker control",368,178,22,white,true,width:400);
            Center("Intensity",new(380,210,148,30),18,true); Center("Duration",new(540,210,148,30),18,true);
            Center($"{session.Shockers.Intensity}%",new(400,600,108,36),24,true);
            Center($"{session.Shockers.DurationMs/1000d:0.0}s",new(560,600,108,36),24,true);
            Center("SHOCK",ToyControlLayout.ShockButton(33),19,true); Center("ARM",ToyControlLayout.ShockButton(32),19,true);
            Center("2 · confirm",new(800,385,156,30),16); Center("1 · arm",new(1010,385,156,30),16);
            Center("Vibrate",ToyControlLayout.ShockButton(34),22,true); Center("Sound",ToyControlLayout.ShockButton(35),22,true);
            Text("Right → release → left within 3 seconds",800,600,15,faint,width:390);
            Text(session.Shockers.Armed ? $"Confirm · {Math.Ceiling(session.Shockers.Remaining(ToyOscRouter.Clock)*3):0}s remaining" : "Two steps for every shock",800,626,16,session.Shockers.Armed ? green : faint,width:390);
            Text(session.TestMode ? "TEST MODE · no device output" : "Live · owner limits apply",800,661,13,faint,width:390);
            return image;
        }
        Text("TOY FUNCTIONS",43,124,17,white,true); Text("Choose functions to draw together",43,151,12,faint);
        for (var i=0;i<model.Visible.Count();i++)
        {
            var row=model.Visible.ElementAt(i); var y=242+i*64;
            Text(row.Toy.Name,54,y,12,faint,width:208); Text(row.Function.Name,54,y+16,16,row.Function.Color,true,204);
            var mode=!row.Connected ? "Disconnected" : row.Function.LoopRunning ? "Looping" : row.Selected ? "Editing" : "Holding";
            Text($"{mode} {row.Level*100:0}% · battery {(row.Toy.Battery<0 ? "?" : row.Toy.Battery+"%")}",54,y+37,11,faint,width:239);
        }
        if(model.PageCount>1) { Text("‹",55,645,27,faint); Text("›",272,645,27,faint); Center($"{model.Page+1} / {model.PageCount}",new(112,648,134,36),13); }
        Text($"{model.Toys.Count} toys · {model.FunctionCount} functions",52,690,11,faint,width:260);
        Text("VRC toy control",368,178,20,white,true,width:245); Text($"{model.Level*100:0}%",645,175,28,model.LiveColor,true,165);
        Center("Shockers",ToyControlLayout.PageSwitch,16,true);
        Center(model.Recording || model.RecordArmed ? "Recording" : "Record",ToyControlLayout.Record,14,true);
        Center("Play loop",ToyControlLayout.Play,14,true);
        Center(session.PointerMode ? "Pointer" : "Touch",ToyControlLayout.InputMode,14,true);
        Text(model.PatternStatus,841,178,13,faint,width:342); Text($"{model.SelectedCount} selected",841,197,11,faint,width:342);
        if(model.SelectedCount>0)
        {
            Text("100%",350,222,13,faint, width:52); Text("75",368,312,12,faint); Text("50",368,400,12,faint); Text("25",368,488,12,faint); Text("0%",365,565,13,faint);
            for(var tick=0;tick<5;tick++) Text(model.DisplayPattern is { } clip ? $"{clip.Duration*tick/4:0.0}s" : tick==4 ? "NOW" : $"−{8-tick*2}s",399+tick*186,592,12,faint,width:70);
        }
        else Center("Select a function to draw",new(405,382,765,48),20,true);
        Center("Clear curve",ToyControlLayout.Clear,14,true); Center("STOP ALL",ToyControlLayout.Stop,14,true);
        var status=session.Vrc.IsLive ? "VRC CONNECTED" : "WAITING FOR VRC";
        Text(status,960,633,14,session.Vrc.IsLive ? green : faint,true,width:213);
        Text(session.TestMode ? "Test mode · no toy output" : "Live · Toy panel limits apply",960,654,11,faint,width:213);
        return image;
    }
}
