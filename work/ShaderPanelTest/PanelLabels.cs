using System.Drawing.Imaging;
using System.Drawing.Text;

namespace ShockWave.ShaderLab;

internal static class PanelLabels
{
    // Only glyphs are rasterized on the CPU. Panel, buttons, grid and history
    // are drawn by HLSL, which composites this transparent text texture.
    public static Bitmap Create(PanelModel model, string oscStatus, string shaderStatus, string backend, int packets, int rejected)
    {
        var bitmap = new Bitmap(PanelLayout.Width, PanelLayout.Height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.Clear(Color.Transparent); g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        var white = Color.FromArgb(242, 243, 245);
        var subdued = Color.FromArgb(181, 186, 193);
        var accent = Color.FromArgb(16, 163, 127);
        var highlight = Color.FromArgb(24, 185, 145);
        void Text(string value, float x, float y, float size, Color? color = null, FontStyle style = FontStyle.Regular, float width = 1000)
        {
            using var font = new Font("Segoe UI", size, style, GraphicsUnit.Pixel);
            using var brush = new SolidBrush(color ?? white);
            using var format = new StringFormat { Trimming = StringTrimming.EllipsisCharacter, FormatFlags = StringFormatFlags.NoWrap };
            g.DrawString(value, font, brush, new RectangleF(x, y, width, size * 1.8f), format);
        }
        Text("SHOCKWAVE", 30, 24, 28, accent, FontStyle.Bold);
        Text("CURVE PANEL", 225, 28, 24, white, FontStyle.Bold);
        Text("Windows shader test · mouse touch · no toy output", 32, 67, 14, subdued);
        Text(oscStatus, 891, 32, 15, highlight, FontStyle.Bold, 298);
        Text($"{packets} packets · {rejected} rejected", 891, 55, 12, subdued, width: 298);
        Text("TOY FUNCTIONS", 43, 124, 17, white, FontStyle.Bold, 255);
        Text("Choose functions to draw together", 43, 151, 12, subdued, width: 255);
        var visible = model.Visible.ToArray();
        if (visible.Length == 0)
        {
            Text("Waiting for toy names…", 53, 236, 18, white, width: 240);
            Text("Click Demo OSC to try the list.", 53, 271, 13, subdued, width: 239);
            Text("Or send your own OSC inventory.", 53, 297, 13, subdued, width: 239);
        }
        for (var i = 0; i < visible.Length; i++)
        {
            var binding = visible[i]; var y = 178 + i * 72;
            Text(binding.Toy.Name, 54, y, 12, subdued, width: 208);
            Text(binding.Function.Name, 54, y + 16, 16, binding.Connected ? binding.Function.Color : subdued, FontStyle.Bold, 204);
            var status = !binding.Connected ? "Disconnected" : binding.Function.LoopRunning ? "Looping" : binding.Selected ? "Editing" : "Holding";
            var battery = binding.Toy.Battery < 0 ? "battery ?" : $"battery {binding.Toy.Battery}%";
            Text($"{status} {binding.Level * 100:0}% · {battery}", 54, y + 37, 11, binding.Selected ? binding.Function.Color : subdued, width: 239);
        }
        Text("‹", 55, 645, 27, subdued);
        Text("›", 272, 645, 27, subdued);
        Text($"{model.Page + 1} / {model.PageCount}", 155, 658, 13, subdued, width: 94);
        Text($"{model.Toys.Count} toys · {model.FunctionCount} functions", 52, 690, 11, subdued, width: 250);
        Text(model.Recording ? "RECORDING" : model.DisplayPattern is not null ? "LOOP PREVIEW" : "LIVE CURVE", 368, 128, 20, white, FontStyle.Bold, 260);
        Text($"{model.Level * 100:0}%", 645, 125, 28, model.LiveColor, FontStyle.Bold, 170);
        Text("OWN", 863, 139, 13, model.Route == 0 ? highlight : subdued, FontStyle.Bold, 70);
        Text("REMOTE", 969, 139, 13, model.Route == 1 ? highlight : subdued, FontStyle.Bold, 85);
        Text("BOTH", 1095, 139, 13, model.Route == 2 ? highlight : subdued, FontStyle.Bold, 70);
        Text(model.Recording || model.RecordArmed ? "Recording" : "Record",389,188,14,model.Recording || model.RecordArmed ? Color.FromArgb(255,170,180) : white,FontStyle.Bold,108);
        Text("Play loop",555,188,14,width:105);
        Text("Hold selected",701,188,14,width:115);
        Text(model.PatternStatus,841,178,13,model.Recording ? Color.FromArgb(255,170,180) : subdued,width:340);
        Text($"{model.SelectedCount} selected · {(model.DisplayPattern is null ? "hold left click to draw" : model.SelectedCount>1 ? "first selected loop shown" : "recording in memory")}",841,197,11,subdued,width:340);
        Text("100%", 350, 222, 13, subdued, width: 52);
        Text("75", 368, 312, 12, subdued, width: 30);
        Text("50", 368, 400, 12, subdued, width: 30);
        Text("25", 368, 488, 12, subdued, width: 30);
        Text("0%", 365, 565, 13, subdued, width: 37);
        for (var i = 0; i < 5; i++) Text(model.DisplayPattern is { } clip ? $"{clip.Duration*i/4.0:0.0}s" : i == 4 ? "NOW" : $"−{8 - i * 2}s", 399 + i * 186, 592, 12, subdued, width: 70);
        Text("Clear curve", 393, 643, 14, width: 108);
        Text("Zero selected", 547, 643, 14, width: 145);
        Text("STOP ALL", 749, 643, 14, white, FontStyle.Bold, 110);
        Text("Demo OSC", 1024, 643, 14, width: 145);
        Text("Record → draw → release to loop. With Record off, release holds the last level.", 27, 719, 13, subdued, width: 925);
        Text(shaderStatus + " · " + backend + " · " + model.VrStatus, 27, 744, 13, shaderStatus.StartsWith("Shader error") ? Color.FromArgb(237, 106, 106) : highlight, width: 775);
        Text(model.VrStatus=="VR OSC: off" ? "Start VR OSC" : "Stop VR OSC",834,746,13,width:115);
        Text("Open HLSL", 986, 746, 13, width: 95);
        Text("Reload", 1128, 746, 13, width: 75);
        Text("PREVIEW ONLY · destination buttons are visual · no hardware or remote connection", 27, 777, 11, subdued, width: 1180);
        return bitmap;
    }
}
