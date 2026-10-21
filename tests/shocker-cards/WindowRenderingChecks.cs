using System.Reflection;
using System.Runtime.InteropServices;
using ShockWave.Overlay;
using ShockWave.Overlay.Sps;

internal static class WindowRenderingChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int passed;
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); passed++; Console.WriteLine("PASS " + message); }
    private static int Pack(Point point) => unchecked((ushort)point.X | ((int)(ushort)point.Y << 16));
    private static void Mouse(Control target, int message, Point point, int keys = 0) =>
        SendMessage(target.Handle, message, (IntPtr)keys, (IntPtr)Pack(point));
    private static void Pump()
    {
        // Let deferred hover/theme painting run before checking the idle image.
        for (var i = 0; i < 30; i++) { Application.DoEvents(); Thread.Sleep(10); }
    }
    private static void ShowOffscreen(Form form)
    {
        form.StartPosition = FormStartPosition.Manual;
        form.Location = new(-20000, -20000);
        form.ShowInTaskbar = false;
        _ = form.Handle;
        SetWindowLongPtr(form.Handle, -20, (IntPtr)(GetWindowLongPtr(form.Handle, -20).ToInt64() | 0x08000000));
        form.Show(); Application.DoEvents();
    }
    private static ThemeScrollbar Bar(Control parent, bool vertical) => parent.Controls.OfType<ThemeScrollbar>()
        .Single(bar => bar.AccessibleName == (vertical ? "Vertical scroll" : "Horizontal scroll"));
    private static bool Green(ThemeScrollbar bar)
    {
        using var image = new Bitmap(bar.Width, bar.Height);
        bar.DrawToBitmap(image, bar.ClientRectangle);
        var green = 0;
        for (var y = 0; y < image.Height; y++)
            for (var x = 0; x < image.Width; x++)
            {
                var color = image.GetPixel(x, y);
                if (color.G > 100 && color.R < 40 && color.B > 70 && color.B < color.G) green++;
            }
        return green > 20;
    }

    public static void Run()
    {
        var foreground = GetForegroundWindow();
        using (var form = new Form { Size = new(420, 320), BackColor = Color.FromArgb(30, 31, 34) })
        using (var page = new ThemedScrollPanel { Location = new(20, 20), Size = new(320, 240),
            BackColor = form.BackColor, AutoScroll = true, AutoScrollMinSize = new(0, 2200) })
        {
            form.Controls.Add(page);
            ShowOffscreen(form);
            var bar = Bar(form, true);
            Check(bar.Visible && Green(bar), "green scrollbar exists and paints before any hover or wheel input");
            Check(page.Region is not null && !page.Region.IsVisible(page.ClientSize.Width + 2, 20) &&
                bar.Left == page.Left + page.ClientSize.Width && bar.Right == page.Right,
                "native scrollbar is clipped out and the managed scrollbar occupies its space");
            Mouse(bar, 0x0200, new(bar.Width / 2, 10)); Pump();
            SendMessage(bar.Handle, 0x02A3, IntPtr.Zero, IntPtr.Zero); Pump();
            Check(Green(bar), "green thumb survives hover, mouse leave and deferred painting");
            var screen = page.PointToScreen(new(page.ClientSize.Width + 2, 20));
            SendMessage(page.Handle, 0x00A0, (IntPtr)7, (IntPtr)Pack(screen));
            SendMessage(page.Handle, 0x0085, (IntPtr)1, IntPtr.Zero); Pump();
            Check(Green(bar) && !page.Region!.IsVisible(page.ClientSize.Width + 2, 20),
                "native non-client repaint cannot replace the visible green scrollbar");
            SendMessage(bar.Handle, 0x020A, (IntPtr)(-120 << 16), IntPtr.Zero);
            Check(page.AutoScrollPosition.Y < 0 && Green(bar), "wheel over the managed bar scrolls and retains green");
            page.AutoScrollPosition = Point.Empty; page.PerformLayout();
            Mouse(bar, 0x0201, new(bar.Width / 2, 10), 1);
            Mouse(bar, 0x0200, new(bar.Width / 2, bar.Height - 1), 1);
            Mouse(bar, 0x0202, new(bar.Width / 2, bar.Height - 1));
            Check(-page.AutoScrollPosition.Y == page.VerticalScroll.Maximum - page.VerticalScroll.LargeChange + 1,
                "thumb dragging reaches the real bottom of the content");
            SendMessage(bar.Handle, 0x0100, (IntPtr)Keys.Home, IntPtr.Zero);
            Check(page.AutoScrollPosition.Y == 0, "Home on the scrollbar returns to the top");
            Mouse(bar, 0x0201, new(bar.Width / 2, bar.Height - 1), 1);
            Mouse(bar, 0x0202, new(bar.Width / 2, bar.Height - 1));
            Check(page.AutoScrollPosition.Y < 0, "clicking the dark track pages the content");
            foreach (var width in new[] { 220, 340, 250, 320 })
            {
                page.Width = width; page.PerformLayout(); Pump();
                Check(bar.Visible && bar.Right == page.Right && Green(bar), "scrollbar survives resize without hover " + width);
            }
            page.Hide(); Check(!bar.Visible, "hiding a page hides its scrollbar");
            page.Show(); Check(bar.Visible && Green(bar), "returning to a page restores its green scrollbar immediately");
            page.AutoScrollMinSize = new(1400, 2200); page.PerformLayout();
            var across = Bar(form, false);
            Check(across.Visible && Green(across), "horizontal scrollbar also uses the managed green rendering");
            SendMessage(across.Handle, 0x0100, (IntPtr)Keys.End, IntPtr.Zero);
            Check(page.AutoScrollPosition.X < 0, "horizontal scrollbar keyboard input still scrolls");
            page.Dispose();
            Check(bar.IsDisposed && across.IsDisposed, "page disposal releases its sibling scrollbar controls");
        }
        Check(GetForegroundWindow() == foreground, "scrollbar fixture does not take focus from the user's app");

        using (var form = new OverlayForm(new BridgeConfig { DryRun = true, Shockers = [] }, initialSaveConfig: _ => { },
            initialSpsStore: new SpsStore(Path.Combine(AppContext.BaseDirectory, "rendering-fixture-sps.json"))))
        {
            // Disable startup only on this owned fixture, never on the real app.
            var events = (System.ComponentModel.EventHandlerList)typeof(System.ComponentModel.Component)
                .GetProperty("Events", Private)!.GetValue(form)!;
            var key = typeof(Form).GetFields(BindingFlags.Static | BindingFlags.NonPublic)
                .Single(field => field.Name.Contains("shown", StringComparison.OrdinalIgnoreCase)).GetValue(null)!;
            if (events[key] is { } handlers) events.RemoveHandler(key, handlers);
            Check(events[key] is null, "window fixture has no bridge, OSC or Bluetooth startup callback");
            ShowOffscreen(form);
            var memory = Marshal.AllocHGlobal(Marshal.SizeOf<Position>());
            try
            {
                Marshal.StructureToPtr(new Position { Width = 900, Height = 650, Flags = 0x0014 }, memory, false);
                SendMessage(form.Handle, 0x0046, IntPtr.Zero, memory);
                Check((Marshal.PtrToStructure<Position>(memory).Flags & 0x0114) == 0x0114,
                    "live sizing discards saved client pixels while preserving activation/order flags");
            }
            finally { Marshal.FreeHGlobal(memory); }
            var shell = form.Controls.OfType<TableLayoutPanel>().Single();
            var title = form.Controls.OfType<Panel>().Single(panel => panel.AccessibleName == "Window title bar");
            SendMessage(form.Handle, 0x0231, IntPtr.Zero, IntPtr.Zero);
            foreach (var width in new[] { 790, 1040, 860, 1180, 790, 1040 })
            {
                SetWindowPos(form.Handle, IntPtr.Zero, form.Left, form.Top, width, 660, 0x0014);
                Check(!GetUpdateRect(form.Handle, IntPtr.Zero, false) && !GetUpdateRect(title.Handle, IntPtr.Zero, false),
                    "top edge and title bar are repainted before sizing returns " + width);
                using var image = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(image, form.ClientRectangle);
                var boundary = shell.Left + shell.GetColumnWidths()[0];
                Check(new[] { 1, title.Top + 1, title.Bottom - 2, shell.Top + 10 }
                    .All(y => image.GetPixel(boundary - 1, y).ToArgb() == Color.FromArgb(17, 18, 20).ToArgb() &&
                        image.GetPixel(boundary + 1, y).ToArgb() == form.BackColor.ToArgb()),
                    "sidebar boundary stays a straight line through the top inset and title bar " + width);
            }
            SendMessage(form.Handle, 0x0232, IntPtr.Zero, IntPtr.Zero);
            var settings = (Panel)typeof(OverlayForm).GetField("settingsPage", Private)!.GetValue(form)!;
            var settingsNav = (Button)typeof(OverlayForm).GetField("settingsNav", Private)!.GetValue(form)!;
            typeof(Control).GetMethod("OnClick", Private)!.Invoke(settingsNav, [EventArgs.Empty]);
            var settingsFlow = settings.Controls.OfType<FlowLayoutPanel>().Single();
            var settingsBar = Bar(settings, true);
            Check(settings.Visible && settingsBar.Visible && Green(settingsBar),
                "actual Settings page has its green scrollbar before any input");
            Mouse(settingsBar, 0x0200, new(settingsBar.Width / 2, 10));
            SendMessage(settingsBar.Handle, 0x02A3, IntPtr.Zero, IntPtr.Zero); Pump();
            var listen = (NumericUpDown)typeof(OverlayForm).GetField("listenPortInput", Private)!.GetValue(form)!;
            var before = listen.Value;
            SendMessage(settingsBar.Handle, 0x020A, (IntPtr)(-120 << 16), IntPtr.Zero);
            Check(settingsFlow.AutoScrollPosition.Y < 0 && Green(settingsBar) && listen.Value == before,
                "Settings hover/leave/wheel keep green and leave saved number values unchanged");
            var toysNav = (Button)typeof(OverlayForm).GetField("toyPanelNav", Private)!.GetValue(form)!;
            typeof(Control).GetMethod("OnClick", Private)!.Invoke(toysNav, [EventArgs.Empty]);
            Check(!settingsBar.Visible, "leaving Settings removes its scrollbar from the other page");
            var process = typeof(OverlayForm).GetField("bridge", Private)!.GetValue(form);
            Check(process is null && GetForegroundWindow() == foreground,
                "native sizing verification starts no bridge and leaves the user's focused app unchanged");
            form.Hide();
        }
        Console.WriteLine($"{passed} scrollbar/window rendering checks passed; owned offscreen fixtures, no hardware or services.");
    }

    [StructLayout(LayoutKind.Sequential)] private struct Position
    { public IntPtr Window, InsertAfter; public int X, Y, Width, Height; public uint Flags; }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetUpdateRect(IntPtr window, IntPtr rectangle, bool erase);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
