using System.Reflection;
using System.Runtime.InteropServices;
using ShockWave.Overlay;
using ShockWave.Overlay.Sps;

internal static class WheelSettingsChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int passed;
    private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, Private)!.GetValue(target)!;
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); passed++; Console.WriteLine("PASS " + message); }
    private static void Call(Control control, string method, params object?[] args) =>
        control.GetType().GetMethod(method, Private)!.Invoke(control, args);
    private static void Wheel(Control control, int delta)
    {
        var point = control.PointToScreen(new Point(4, 4));
        SendMessage(control.Handle, 0x020A, (nint)(delta << 16),
            (nint)unchecked((ushort)point.X | ((int)(ushort)point.Y << 16)));
    }
    private static IEnumerable<Control> Children(Control control)
    { foreach (Control child in control.Controls) { yield return child; foreach (var next in Children(child)) yield return next; } }

    public static void Run()
    {
        using (var page = new Panel { Size = new(320, 160), AutoScroll = true, AutoScrollMinSize = new(0, 2000) })
        {
            using var number = new SettingNumberInput { ShowSpinner = false, BorderStyle = BorderStyle.None,
                Minimum = 1, Maximum = 65535, Value = 9001, Location = new(12, 12) };
            using var choice = new SettingChoiceInput { DropDownStyle = ComboBoxStyle.DropDownList, Location = new(12, 50) };
            choice.Items.AddRange(["First", "Second", "Third"]); choice.SelectedIndex = 1;
            page.Controls.AddRange([number, choice]); _ = page.Handle; _ = number.Handle; _ = choice.Handle; page.PerformLayout();
            var changes = 0; number.ValueChanged += (_, _) => changes++; choice.SelectedIndexChanged += (_, _) => changes++;
            var handled = new HandledMouseEventArgs(MouseButtons.None, 0, 4, 4, -120);
            Call(number, "OnMouseWheel", handled);
            Check(handled.Handled && number.Value == 9001 && changes == 0, "handled wheel leaves the listen port unchanged");
            foreach (var control in new Control[] { number, choice }.Concat(number.Controls.Cast<Control>()))
            {
                page.AutoScrollPosition = new(0, 300);
                var before = page.AutoScrollPosition.Y;
                Wheel(control, -120);
                Check(number.Value == 9001 && choice.SelectedIndex == 1 && changes == 0,
                    "native wheel cannot edit " + control.GetType().Name);
                Check(page.AutoScrollPosition.Y < before, "wheel over " + control.GetType().Name + " scrolls the page");
            }
            for (var count = 0; count < 20; count++) { Wheel(number, 15); Wheel(choice, -15); }
            Check(number.Value == 9001 && choice.SelectedIndex == 1 && changes == 0,
                "small repeated wheel deltas cannot accumulate setting edits");
            number.UpButton(); Check(number.Value == 9002, "number increment API remains available without visible arrows");
            Call(number, "OnTextBoxKeyDown", null, new KeyEventArgs(Keys.Down));
            Check(number.Value == 9001, "number arrow key still edits explicitly");
            number.Text = "9010"; Call(number, "ValidateEditText");
            Check(number.Value == 9010, "typed numbers still validate");
            SendMessage(choice.Handle, 0x0100, (nint)Keys.Down, 0);
            Check(choice.SelectedIndex == 2, "dropdown arrow key still changes the selection");
        }
        using (var detached = new SettingNumberInput { Minimum = 0, Maximum = 100, Value = 37 })
        {
            Wheel(detached, -120); Wheel(detached, 120);
            Check(detached.Value == 37, "wheel outside a scrollable page cannot edit numbers");
        }

        // Never show the real form: its Shown handler would start services.
        var saves = 0;
        var config = new BridgeConfig { Shockers = [new() { Key = "fixture", Name = "Fixture" }] };
        using (var form = new OverlayForm(config, initialSaveConfig: _ => saves++,
            initialSpsStore: new SpsStore(Path.Combine(AppContext.BaseDirectory, "wheel-fixture-sps.json"))))
        {
            var numbers = Children(form).OfType<NumericUpDown>().ToArray();
            var choices = Children(form).OfType<ComboBox>().ToArray();
            Check(numbers.Length >= 5 && numbers.All(c => c is SettingNumberInput) &&
                choices.Length > 0 && choices.All(c => c is SettingChoiceInput),
                "shared settings, cooldown and Bluetooth controls use scroll-safe inputs");
            var before = numbers.Select(c => c.Value).ToArray(); var beforeChoices = choices.Select(c => c.SelectedIndex).ToArray();
            var beforeSaves = saves;
            foreach (var control in numbers.Cast<Control>().Concat(choices)) { Wheel(control, -120); Wheel(control, 120); }
            Check(numbers.Select(c => c.Value).SequenceEqual(before) && choices.Select(c => c.SelectedIndex).SequenceEqual(beforeChoices) && saves == beforeSaves,
                "scrolling the actual controls changes no values, source selection or saved configuration");
            Check(Get<NumericUpDown>(form, "listenPortInput").Value == 9001 && Get<NumericUpDown>(form, "sendPortInput").Value == 9000,
                "OSC input 9001 and output 9000 remain unchanged");
        }
        var hole = SpsBoardModel.Hole("/avatar/parameters/OGB/Orf/Mouth/PenOthersNewRoot");
        using (var options = new SpsWireOptions(new() { HoleId = hole.Id, Target = new("fixture", "motor") }, hole, "Fixture"))
        {
            var before = options.Value;
            foreach (var control in Children(options).Where(c => c is NumericUpDown or ComboBox))
            { Wheel(control, -120); Wheel(control, 120); }
            var after = options.Value;
            Check(after.Gain == before.Gain && after.DeadZone == before.DeadZone && after.SmoothingMs == before.SmoothingMs &&
                after.TimeoutMs == before.TimeoutMs && after.InputMin == before.InputMin && after.InputMax == before.InputMax &&
                after.ExactInputs.SequenceEqual(before.ExactInputs), "SPS connection options cannot change under the wheel");
        }
        Console.WriteLine($"{passed} wheel/settings checks passed; no service startup or user settings writes.");
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
