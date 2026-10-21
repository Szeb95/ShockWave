using System.Runtime.InteropServices;

namespace ShockWave.Overlay;

internal sealed partial class OverlayForm
{
    private WindowTitleBar? windowTitleBar;
    private bool arrangingWindowChrome;
    private int ResizeMargin => Math.Max(6, (int)Math.Round(6 * DeviceDpi / 96d));
    private int SidebarBoundary
    {
        get
        {
            var shell = Controls.OfType<TableLayoutPanel>().FirstOrDefault();
            return shell is null ? Padding.Left + 212 : shell.Left + shell.GetColumnWidths()[0];
        }
    }

    private void ConfigureWindowFrame()
    {
        FormBorderStyle = FormBorderStyle.None;
        Padding = new Padding(ResizeMargin);
        DoubleBuffered = true;
        SetStyle(ControlStyles.ResizeRedraw, true);
    }

    protected override void OnHandleCreated(EventArgs args)
    {
        base.OnHandleCreated(args);
        // Windows 11's compositor border is separate from the classic frame.
        // Unsupported older Windows versions simply reject this optional attribute.
        var noBorder = unchecked((int)0xfffffffe);
        DwmSetWindowAttribute(Handle, 34, ref noBorder, sizeof(int));
    }

    private void BuildWindowTitleBar()
    {
        var shell = Controls.OfType<TableLayoutPanel>().Single();
        windowTitleBar = new WindowTitleBar(this, () => SidebarBoundary - (windowTitleBar?.Left ?? Padding.Left))
        {
            Dock = DockStyle.None,
            Height = (int)Math.Round(32 * DeviceDpi / 96d),
            BackColor = Background,
            AccessibleName = "Window title bar",
        };
        Controls.Add(windowTitleBar);
        shell.Layout += (_, _) => { Invalidate(); windowTitleBar.Invalidate(); };
        SynchronizeWindowChrome();
    }

    private void SynchronizeWindowChrome()
    {
        if (windowTitleBar is null || arrangingWindowChrome || Disposing || IsDisposed || windowTitleBar.IsDisposed ||
            WindowState == FormWindowState.Minimized) return;
        arrangingWindowChrome = true;
        SuspendLayout();
        try
        {
            var inset = WindowState == FormWindowState.Maximized ? 0 : ResizeMargin;
            var titleHeight = (int)Math.Round(32 * DeviceDpi / 96d);
            // Reserve the caption separately from the fill-docked pages. Native
            // snap/restore can change size, state and z-order in different orders.
            Padding = new Padding(inset, inset + titleHeight, inset, inset);
            windowTitleBar.SetBounds(inset, inset, Math.Max(0, ClientSize.Width - inset * 2), titleHeight);
            windowTitleBar.BringToFront();
            windowTitleBar.RefreshWindowState();
        }
        finally { ResumeLayout(false); arrangingWindowChrome = false; }
    }

    protected override void OnLayout(LayoutEventArgs args)
    {
        SynchronizeWindowChrome();
        base.OnLayout(args);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            // Retain native move/size, taskbar, system-menu and keyboard behavior.
            // WM_NCCALCSIZE removes their standard visible frame.
            parameters.Style |= 0x00040000 | 0x00080000 | 0x00020000 | 0x00010000;
            return parameters;
        }
    }

    protected override void OnResize(EventArgs args)
    {
        SynchronizeWindowChrome();
        base.OnResize(args);
        Invalidate(true);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs args)
    {
        base.OnDpiChanged(args);
        SynchronizeWindowChrome();
    }

    protected override void OnPaintBackground(PaintEventArgs args)
    {
        base.OnPaintBackground(args);
        using var sidebar = new SolidBrush(Sidebar);
        args.Graphics.FillRectangle(sidebar, 0, 0, SidebarBoundary, Height);
    }

    protected override void WndProc(ref Message message)
    {
        const int WmNcCalcSize = 0x0083, WmNcHitTest = 0x0084, WmGetMinMaxInfo = 0x0024;
        if (message.Msg == 0x0046 && message.LParam != IntPtr.Zero) // WM_WINDOWPOSCHANGING
        {
            var position = Marshal.PtrToStructure<WindowPosition>(message.LParam);
            if ((position.Flags & 0x0001) == 0 && (position.Width != Width || position.Height != Height))
            {
                // WVR_REDRAW alone does not stop SetWindowPos from restoring
                // saved pixels at the top/left edge during live native sizing.
                position.Flags |= 0x0100; // SWP_NOCOPYBITS
                Marshal.StructureToPtr(position, message.LParam, false);
            }
        }
        if (message.Msg == WmNcCalcSize)
        {
            // Do not preserve/copy old client pixels across a resize. The full
            // client remains the window rectangle and is repainted in both axes.
            message.Result = message.WParam == IntPtr.Zero ? IntPtr.Zero : (IntPtr)0x0300;
            return;
        }
        if (message.Msg == 0x0085) { message.Result = IntPtr.Zero; return; } // WM_NCPAINT
        if (message.Msg == 0x0086) message.LParam = (IntPtr)(-1); // WM_NCACTIVATE without classic frame painting
        if (message.Msg == WmNcHitTest)
        {
            // Signed coordinates also cover monitors to the left/above the primary.
            var packed = message.LParam.ToInt64();
            var point = PointToClient(new Point(unchecked((short)packed), unchecked((short)(packed >> 16))));
            if (ClientRectangle.Contains(point))
            {
                if (WindowState == FormWindowState.Normal)
                {
                    var left = point.X < ResizeMargin;
                    var right = point.X >= ClientSize.Width - ResizeMargin;
                    var top = point.Y < ResizeMargin;
                    var bottom = point.Y >= ClientSize.Height - ResizeMargin;
                    var hit = top ? left ? 13 : right ? 14 : 12
                        : bottom ? left ? 16 : right ? 17 : 15
                        : left ? 10 : right ? 11 : 0;
                    if (hit != 0) { message.Result = (IntPtr)hit; return; }
                }
                if (windowTitleBar?.Bounds.Contains(point) == true)
                {
                    message.Result = (IntPtr)2; // HTCAPTION: native drag, double-click and system menu.
                    return;
                }
            }
        }
        base.WndProc(ref message);
        if (message.Msg is 0x0047 or 0x0232 && windowTitleBar is not null && IsHandleCreated && Visible && !Disposing && !IsDisposed)
        {
            SynchronizeWindowChrome();
            PerformLayout();
            // Paint after the completed size/layout change, including the inset
            // and title bar. Do not leave old edge pixels until the next input.
            RedrawWindow(Handle, IntPtr.Zero, IntPtr.Zero, 0x0001 | 0x0004 | 0x0080 | 0x0100);
        }
        if (message.Msg == WmGetMinMaxInfo && message.LParam != IntPtr.Zero)
        {
            // Borderless maximization must respect this monitor's taskbar/work area.
            var screen = Screen.FromHandle(message.HWnd);
            var limits = Marshal.PtrToStructure<WindowSizeLimits>(message.LParam);
            limits.MaximumPosition = new Point(screen.WorkingArea.Left - screen.Bounds.Left,
                screen.WorkingArea.Top - screen.Bounds.Top);
            limits.MaximumSize = screen.WorkingArea.Size;
            Marshal.StructureToPtr(limits, message.LParam, false);
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr window, IntPtr rectangle, IntPtr region, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPosition
    {
        public IntPtr Window, InsertAfter;
        public int X, Y, Width, Height;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowSizeLimits
    {
        public Point Reserved;
        public Size MaximumSize;
        public Point MaximumPosition;
        public Size MinimumTrackSize;
        public Size MaximumTrackSize;
    }

    private sealed class WindowTitleBar : Panel
    {
        private readonly OverlayForm owner;
        private readonly Func<int> sidebarWidth;
        private readonly WindowCaptionButton minimize;
        private readonly WindowCaptionButton maximize;
        private readonly WindowCaptionButton close;

        public WindowTitleBar(OverlayForm owner, Func<int> sidebarWidth)
        {
            this.owner = owner;
            this.sidebarWidth = sidebarWidth;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.Opaque |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            minimize = new WindowCaptionButton(0, () => false) { AccessibleName = "Minimize" };
            maximize = new WindowCaptionButton(1, () => owner.WindowState == FormWindowState.Maximized);
            close = new WindowCaptionButton(2, () => false) { AccessibleName = "Close" };
            minimize.Click += (_, _) => owner.WindowState = FormWindowState.Minimized;
            maximize.Click += (_, _) => owner.WindowState = owner.WindowState == FormWindowState.Maximized
                ? FormWindowState.Normal : FormWindowState.Maximized;
            close.Click += (_, _) => owner.Close(); // Keep the existing active-output warning and cleanup.
            Controls.AddRange([minimize, maximize, close]);
            owner.statusHelp.SetToolTip(minimize, "Minimize");
            owner.statusHelp.SetToolTip(close, "Close");
            RefreshWindowState();
        }

        public void RefreshWindowState()
        {
            var label = owner.WindowState == FormWindowState.Maximized ? "Restore" : "Maximize";
            if (maximize.AccessibleName == label) return;
            maximize.AccessibleName = label;
            owner.statusHelp.SetToolTip(maximize, maximize.AccessibleName);
            maximize.Invalidate();
        }

        protected override void OnLayout(LayoutEventArgs args)
        {
            base.OnLayout(args);
            var width = (int)Math.Round(46 * DeviceDpi / 96d);
            close.SetBounds(Math.Max(0, Width - width), 0, width, Height);
            maximize.SetBounds(Math.Max(0, close.Left - width), 0, width, Height);
            minimize.SetBounds(Math.Max(0, maximize.Left - width), 0, width, Height);
        }

        protected override void OnPaintBackground(PaintEventArgs args) { }
        protected override void OnPaint(PaintEventArgs args)
        {
            args.Graphics.Clear(BackColor);
            base.OnPaint(args);
            using var sidebar = new SolidBrush(Sidebar);
            args.Graphics.FillRectangle(sidebar, 0, 0, sidebarWidth(), Height);
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x0084)
            {
                // Let the parent handle blank title-bar space as a native caption.
                // The three child buttons retain ordinary button hit testing.
                message.Result = (IntPtr)(-1); // HTTRANSPARENT
                return;
            }
            base.WndProc(ref message);
        }
    }

    private sealed class WindowCaptionButton : Button
    {
        private readonly int glyph;
        private readonly Func<bool> restored;

        public WindowCaptionButton(int glyph, Func<bool> restored)
        {
            this.glyph = glyph;
            this.restored = restored;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            FlatAppearance.MouseOverBackColor = glyph == 2 ? Hex("#c42b1c") : PanelHover;
            FlatAppearance.MouseDownBackColor = glyph == 2 ? Hex("#a92318") : InputColor;
            BackColor = Background;
            ForeColor = TextColor;
            UseVisualStyleBackColor = false;
            TabStop = false;
            AccessibleRole = AccessibleRole.PushButton;
        }

        protected override void OnPaint(PaintEventArgs args)
        {
            base.OnPaint(args);
            var scale = DeviceDpi / 96f;
            var side = 10 * scale;
            var x = (Width - side) / 2;
            var y = (Height - side) / 2;
            using var pen = new Pen(TextColor, Math.Max(1, scale));
            if (glyph == 0) args.Graphics.DrawLine(pen, x, y + side / 2, x + side, y + side / 2);
            else if (glyph == 2)
            {
                args.Graphics.DrawLine(pen, x, y, x + side, y + side);
                args.Graphics.DrawLine(pen, x + side, y, x, y + side);
            }
            else if (restored())
            {
                var offset = 2 * scale;
                args.Graphics.DrawLines(pen, new PointF[] { new(x + offset, y + offset), new(x + offset, y),
                    new(x + side, y), new(x + side, y + side - offset), new(x + side - offset, y + side - offset) });
                args.Graphics.DrawRectangle(pen, x, y + offset, side - offset, side - offset);
            }
            else args.Graphics.DrawRectangle(pen, x, y, side, side);
        }
    }
}
