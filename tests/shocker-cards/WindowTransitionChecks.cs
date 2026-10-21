using System.Reflection;
using System.Runtime.InteropServices;
using ShockWave.Overlay;
using ShockWave.Overlay.Sps;

internal static class WindowTransitionChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int passed;
    private static T Field<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Check(bool pass, string message)
    { if (!pass) throw new Exception(message); passed++; Console.WriteLine("PASS " + message); }
    private static IntPtr Pack(Point point) => (IntPtr)unchecked((ushort)point.X | ((int)(ushort)point.Y << 16));
    private static OverlayForm Create() => new(new BridgeConfig { DryRun = true, Shockers = [
        new() { Key = "transition-fixture", Name = "Fixture", Uuid = "11111111-1111-1111-1111-111111111111" }] },
        initialSaveConfig: _ => { }, initialSpsStore: new SpsStore(Path.Combine(AppContext.BaseDirectory, "transition-fixture-sps.json")));

    public static void Run()
    {
        var foreground = GetForegroundWindow();
        using var form = Create();
        var events = (System.ComponentModel.EventHandlerList)typeof(System.ComponentModel.Component).GetProperty("Events", Private)!.GetValue(form)!;
        var shown = typeof(Form).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(field => field.Name.Contains("shown", StringComparison.OrdinalIgnoreCase)).GetValue(null)!;
        if (events[shown] is { } handlers) events.RemoveHandler(shown, handlers);
        Check(events[shown] is null, "snap fixture has no service startup callback");
        form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new(-22000, -22000);
        _ = form.Handle;
        SetWindowLongPtr(form.Handle, -20, (IntPtr)(GetWindowLongPtr(form.Handle, -20).ToInt64() | 0x08000000));
        form.Show(); Application.DoEvents();
        var title = Field<Panel>(form, "windowTitleBar");
        var shell = form.Controls.OfType<TableLayoutPanel>().Single();
        // Use real HWND size/state/move messages entirely offscreen. No shell-wide
        // shortcut, cursor movement, work-area change or foreground activation.
        var normalStyle = GetWindowLongPtr(form.Handle, -16).ToInt64() & ~0x01000000L;
        foreach (var state in new[] {
            ("left snap", new Size(960, 1040), false), ("drag from left", new Size(1040, 760), false),
            ("right snap", new Size(960, 1040), false), ("drag from right", new Size(1040, 760), false),
            ("maximized", new Size(1920, 1040), true), ("restore from maximized", new Size(1040, 760), false),
            ("short native height", new Size(820, 520), false), ("grow from short height", new Size(1040, 920), false),
            ("narrow native half", new Size(720, 1040), false), ("restore from narrow half", new Size(1040, 760), false) })
        {
            if (state.Item1.StartsWith("drag") || state.Item1.StartsWith("restore")) shell.BringToFront();
            // A caption collapsed during an intermediate layout must recover on
            // the next native restore; it cannot rely on its previous height.
            if (state.Item1 == "drag from left") title.Height = 0;
            SendMessage(form.Handle, 0x0231, IntPtr.Zero, IntPtr.Zero);
            SetWindowLongPtr(form.Handle, -16, (IntPtr)(normalStyle | (state.Item3 ? 0x01000000L : 0)));
            SetWindowPos(form.Handle, IntPtr.Zero, -22000, -22000, state.Item2.Width, state.Item2.Height, 0x0034);
            GetClientRect(form.Handle, out var resized);
            SendMessage(form.Handle, 0x0005, (IntPtr)(state.Item3 ? 2 : 0), Pack(new(resized.Right, resized.Bottom)));
            SendMessage(form.Handle, 0x0232, IntPtr.Zero, IntPtr.Zero);
            GetClientRect(form.Handle, out var native);
            Check(title.Visible && title.Top >= 0 && title.Height >= 30 && title.Right <= native.Right && shell.Top == title.Bottom &&
                shell.Bottom <= native.Bottom && !shell.Bounds.IntersectsWith(title.Bounds),
                "title and content remain separate after " + state.Item1);
            Check(form.WindowState == (state.Item3 ? FormWindowState.Maximized : FormWindowState.Normal) &&
                title.Left == (state.Item3 ? 0 : form.Padding.Left), "native window state and caption inset agree after " + state.Item1);
            var caption = Pack(form.PointToScreen(new(title.Left + 250, title.Top + title.Height / 2)));
            Check(SendMessage(form.Handle, 0x0084, IntPtr.Zero, caption).ToInt32() == 2,
                "native drag area survives " + state.Item1);
            Check(title.Controls.OfType<Button>().All(button => button.Visible && button.Top == 0 && button.Bottom <= title.Height &&
                button.Right <= title.Width && HasGlyph(button)), "all three window buttons remain visible after " + state.Item1);
            Check(!GetUpdateRect(title.Handle, IntPtr.Zero, false), "title repaint is complete after " + state.Item1);
        }
        var card = Field<List<ShockerCard>>(form, "shockerCards").Single(); card.Expanded = true;
        var page = Field<Panel>(form, "toyPanelPage"); page.ScrollControlIntoView(card);
        var number = Field<NumericUpDown>(card, "cooldownInput");
        for (var cycle = 0; cycle < 12; cycle++)
        {
            card.Width = cycle % 2 == 0 ? 243 : 154;
            number.Value = cycle % 2 == 0 ? 1.2m : 12.3m;
            number.Invalidate(true); number.Update(); Application.DoEvents();
            using var image = new Bitmap(number.Width, number.Height);
            number.DrawToBitmap(image, number.ClientRectangle);
            if (cycle == 0) image.Save(Path.Combine(AppContext.BaseDirectory, "cooldown-first-repaint.png"));
            // The rightmost area contains no text/caret: hidden arrows, stale
            // parent graphics or native spin glyphs must never paint there.
            Check(Enumerable.Range(Math.Max(0, image.Width - 12), Math.Min(12, image.Width)).All(x =>
                Enumerable.Range(0, image.Height).All(y => image.GetPixel(x, y).ToArgb() == number.BackColor.ToArgb())),
                "cooldown has no arrow fragments after repaint " + cycle);
            if (cycle == 11) image.Save(Path.Combine(AppContext.BaseDirectory, "cooldown-clean.png"));
        }
        Check(Field<System.Diagnostics.Process?>(form, "bridge") is null && Field<LocalToysPage>(form, "localToysPage").Controller is null &&
            GetForegroundWindow() == foreground && form.Left < -10000, "transition fixture leaves focus, services and hardware untouched");
        form.Hide();
        Console.WriteLine($"{passed} snap/cooldown rendering checks passed; offscreen native transitions, no physical shell snap or hardware.");
    }
    private static bool HasGlyph(Button button)
    {
        if (button.Width <= 0 || button.Height <= 0) return false;
        using var image = new Bitmap(button.Width, button.Height); button.DrawToBitmap(image, button.ClientRectangle);
        return Enumerable.Range(0, image.Width).Any(x => Enumerable.Range(0, image.Height).Any(y =>
            { var pixel = image.GetPixel(x, y); return pixel.R > 200 && pixel.G > 200 && pixel.B > 200; }));
    }
    [StructLayout(LayoutKind.Sequential)] private struct RectangleNative { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out RectangleNative rectangle);
    [DllImport("user32.dll")] private static extern bool GetUpdateRect(IntPtr window, IntPtr rectangle, bool erase);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
}
