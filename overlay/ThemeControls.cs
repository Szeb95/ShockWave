using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ShockWave.Overlay;

internal static class ThemeDrawing
{
    public static readonly Color Accent = ColorTranslator.FromHtml("#10a37f");
    public static readonly Color Highlight = ColorTranslator.FromHtml("#18b991");
    public const float CornerRadius = 8;
    public static void PaintSurface(Control control, Graphics graphics)
    {
        graphics.Clear(control.Parent?.BackColor ?? control.BackColor);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Rounded(new(0, 0, control.Width, control.Height), CornerRadius * control.DeviceDpi / 96f);
        using var fill = new SolidBrush(control.BackColor);
        graphics.FillPath(fill, path);
    }
    public static GraphicsPath Rounded(RectangleF bounds, float radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
        if (diameter <= 0) return path;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }
}

internal class RoundedPanel : Panel
{
    public RoundedPanel()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnSizeChanged(EventArgs args)
    {
        base.OnSizeChanged(args);
        Invalidate(true);
    }
    protected override void OnPaintBackground(PaintEventArgs args)
    {
        ThemeDrawing.PaintSurface(this, args.Graphics);
    }
    protected override void OnControlAdded(ControlEventArgs args)
    {
        base.OnControlAdded(args);
        if (args.Control is { } child) child.Paint += PaintChildCorners;
    }
    protected override void OnControlRemoved(ControlEventArgs args)
    {
        if (args.Control is { } child) child.Paint -= PaintChildCorners;
        base.OnControlRemoved(args);
    }
    private void PaintChildCorners(object? sender, PaintEventArgs args)
    {
        if (sender is Control child) PaintOutsideCorners(args.Graphics, new(-child.Left, -child.Top, Width, Height));
    }
    private void PaintOutsideCorners(Graphics graphics, RectangleF bounds)
    {
        // Preserve smooth edge pixels instead of cutting them off with a binary
        // HWND region. Full-width child panels use the same corner mask.
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var outside = new GraphicsPath(FillMode.Alternate);
        outside.AddRectangle(bounds);
        using var inside = ThemeDrawing.Rounded(bounds, ThemeDrawing.CornerRadius * DeviceDpi / 96f);
        outside.AddPath(inside, false);
        using var background = new SolidBrush(Parent?.BackColor ?? BackColor);
        graphics.FillPath(background, outside);
    }
    protected override void WndProc(ref Message message)
    {
        base.WndProc(ref message);
        if (message.Msg == 0x0317 && message.WParam != IntPtr.Zero && Width > 0 && Height > 0)
        {
            // Apply the same smooth corner mask after child HWNDs are printed.
            using var g = Graphics.FromHdc(message.WParam);
            PaintOutsideCorners(g, ClientRectangle);
        }
    }
}

internal sealed class RoundedLabel : Label
{
    public RoundedLabel() => SetStyle(ControlStyles.AllPaintingInWmPaint |
        ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    protected override void OnPaintBackground(PaintEventArgs args) => ThemeDrawing.PaintSurface(this, args.Graphics);
}

internal sealed class RoundedButton : Button
{
    private bool hover, pressed;
    public Color DisabledForeColor { get; init; } = SystemColors.GrayText;
    public RoundedButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        UseVisualStyleBackColor = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnMouseEnter(EventArgs args) { hover = true; base.OnMouseEnter(args); Invalidate(); }
    protected override void OnMouseLeave(EventArgs args) { hover = pressed = false; base.OnMouseLeave(args); Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs args) { pressed = args.Button == MouseButtons.Left; base.OnMouseDown(args); Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs args) { pressed = false; base.OnMouseUp(args); Invalidate(); }
    protected override void OnMouseCaptureChanged(EventArgs args) { base.OnMouseCaptureChanged(args); if (!Capture) { pressed = false; Invalidate(); } }
    protected override void OnKeyDown(KeyEventArgs args) { if (args.KeyCode == Keys.Space) { pressed = true; Invalidate(); } base.OnKeyDown(args); }
    protected override void OnKeyUp(KeyEventArgs args) { pressed = false; Invalidate(); base.OnKeyUp(args); }
    protected override void OnPaint(PaintEventArgs args)
    {
        args.Graphics.Clear(Parent?.BackColor ?? BackColor);
        args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var factor = Enabled && pressed ? .9 : Enabled && hover ? 1.12 : 1;
        var color = Color.FromArgb((int)Math.Min(255, BackColor.R * factor),
            (int)Math.Min(255, BackColor.G * factor), (int)Math.Min(255, BackColor.B * factor));
        using var path = ThemeDrawing.Rounded(new(0, 0, Width, Height), ThemeDrawing.CornerRadius * DeviceDpi / 96f);
        using var background = new SolidBrush(color);
        args.Graphics.FillPath(background, path);
        TextRenderer.DrawText(args.Graphics, Text, Font, ClientRectangle, Enabled ? ForeColor : DisabledForeColor,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(args.Graphics, Rectangle.Inflate(ClientRectangle, -4, -4));
    }
}

internal sealed class NavigationButton : Button
{
    public string Icon { get; init; } = "";
    private readonly Font iconFont = new("Segoe UI Symbol", 11);
    private bool hover, pressed;
    public NavigationButton() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
        ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    protected override void OnMouseEnter(EventArgs args) { hover = true; base.OnMouseEnter(args); Invalidate(); }
    protected override void OnMouseLeave(EventArgs args) { hover = pressed = false; base.OnMouseLeave(args); Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs args) { pressed = args.Button == MouseButtons.Left; base.OnMouseDown(args); Invalidate(); }
    protected override void OnMouseUp(MouseEventArgs args) { pressed = false; base.OnMouseUp(args); Invalidate(); }
    protected override void OnPaint(PaintEventArgs args)
    {
        var factor = Enabled && pressed ? .9 : Enabled && hover ? 1.12 : 1;
        args.Graphics.Clear(Color.FromArgb((int)Math.Min(255, BackColor.R * factor),
            (int)Math.Min(255, BackColor.G * factor), (int)Math.Min(255, BackColor.B * factor)));
        var scale = DeviceDpi / 96f;
        var iconBounds = new Rectangle((int)(12 * scale), 0, (int)(20 * scale), Height);
        var labelBounds = new Rectangle((int)(40 * scale), 0, Math.Max(0, Width - (int)(48 * scale)), Height);
        var color = Enabled ? ForeColor : SystemColors.GrayText;
        TextRenderer.DrawText(args.Graphics, Icon, iconFont, iconBounds, color,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
        TextRenderer.DrawText(args.Graphics, Text, Font, labelBounds, color,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(args.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3));
    }
    protected override void Dispose(bool disposing) { if (disposing) iconFont.Dispose(); base.Dispose(disposing); }
}

// Retain CheckBox.Checked, keyboard input, accessibility and existing handlers.
internal class ThemeToggle : CheckBox
{
    public bool TextBeforeSwitch { get; set; }
    public bool ThinSwitch { get; set; }
    public ThemeToggle()
    {
        AutoSize = false;
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        SetStyle(ControlStyles.SupportsTransparentBackColor, false);
    }
    protected override void OnPaintBackground(PaintEventArgs args) { }
    protected override void OnCheckedChanged(EventArgs args) { base.OnCheckedChanged(args); Invalidate(); }
    protected override void OnPaint(PaintEventArgs args)
    {
        var g = args.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var scale = Math.Min(DeviceDpi / 96f, Height / 20f);
        var measuredText = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.NoPadding).Width;
        if (TextBeforeSwitch)
            scale = Math.Min(scale, Math.Max(.65f * scale, (Width - measuredText - 5 * scale) / 38f));
        var trackWidth = 38 * scale;
        var gap = 5 * scale;
        var availableText = Math.Max(0, Width - trackWidth - gap);
        var textWidth = Math.Min(availableText, measuredText);
        var x = TextBeforeSwitch ? textWidth + gap : 0;
        var y = Height / 2f;
        var thin = TextBeforeSwitch || ThinSwitch;
        var trackThickness = thin ? 6 : 14;
        var knobDiameter = thin ? 14 : 20;
        using var track = new Pen(Checked && Enabled ? thin ? ThemeDrawing.Accent : ColorTranslator.FromHtml("#246456")
            : ColorTranslator.FromHtml("#54585f"), trackThickness * scale)
            { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(track, x + 7 * scale, y, x + 31 * scale, y);
        using var knob = new SolidBrush(Checked && Enabled ? ThemeDrawing.Highlight : ColorTranslator.FromHtml("#dcdde0"));
        g.FillEllipse(knob, x + ((Checked ? 18 : 0) + (20 - knobDiameter) / 2f) * scale,
            y - knobDiameter * scale / 2, knobDiameter * scale, knobDiameter * scale);
        var textBounds = TextBeforeSwitch ? new Rectangle(0, 0, (int)availableText, Height)
            : new Rectangle((int)(trackWidth + gap), 0, Math.Max(0, Width - (int)(trackWidth + gap)), Height);
        TextRenderer.DrawText(g, Text, Font, textBounds, Enabled ? ForeColor : ColorTranslator.FromHtml("#80848e"),
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -1, -1));
    }
}

internal sealed class ThemedScrollPanel : Panel
{
    private readonly ManagedScrollbarHost scrollbars;
    public ThemedScrollPanel() { DoubleBuffered = true; scrollbars = new(this); }
    protected override void WndProc(ref Message message)
    { base.WndProc(ref message); scrollbars?.AfterMessage(message); }
}
internal sealed class ThemedFlowLayoutPanel : FlowLayoutPanel
{
    private readonly ManagedScrollbarHost scrollbars;
    public ThemedFlowLayoutPanel() { DoubleBuffered = true; scrollbars = new(this); }
    protected override void WndProc(ref Message message)
    { base.WndProc(ref message); scrollbars?.AfterMessage(message); }
}

// Keep WinForms' layout, ranges, wheel routing and ScrollControlIntoView. Clip
// its native non-client bars out of the HWND and put managed sibling controls
// in that space. Windows hover/animation painting cannot reach the visible bars.
internal sealed class ManagedScrollbarHost
{
    private readonly ScrollableControl owner;
    private readonly ThemeScrollbar vertical;
    private readonly ThemeScrollbar horizontal;
    private Rectangle? clip;
    private bool synchronizing;

    public ManagedScrollbarHost(ScrollableControl owner)
    {
        this.owner = owner;
        vertical = new(true, value => Move(true, value), Wheel);
        horizontal = new(false, value => Move(false, value), Wheel);
        owner.ParentChanged += (_, _) => Synchronize();
        owner.HandleCreated += (_, _) => { clip = null; Synchronize(); };
        owner.Layout += (_, _) => Synchronize();
        owner.SizeChanged += (_, _) => Synchronize();
        owner.VisibleChanged += (_, _) => Synchronize();
        owner.BackColorChanged += (_, _) => Synchronize();
        owner.EnabledChanged += (_, _) => Synchronize();
        owner.Scroll += (_, _) => Synchronize();
        owner.Disposed += (_, _) => { vertical.Dispose(); horizontal.Dispose(); };
    }
    public void AfterMessage(Message message)
    {
        if (message.Msg is 0x000F or 0x0085 or 0x0047 or 0x0114 or 0x0115 or 0x020A or 0x0317)
            Synchronize();
        if (message.Msg == 0x0317 && message.WParam != IntPtr.Zero)
        {
            // WM_PRINT ignores window regions. Replace native bars in exports
            // with the same managed renderer used by the visible siblings.
            using var g = Graphics.FromHdc(message.WParam);
            using var background = new SolidBrush(owner.BackColor);
            g.FillRectangle(background, owner.ClientSize.Width, 0,
                Math.Max(0, owner.Width - owner.ClientSize.Width), owner.Height);
            g.FillRectangle(background, 0, owner.ClientSize.Height,
                owner.Width, Math.Max(0, owner.Height - owner.ClientSize.Height));
            foreach (var bar in new[] { vertical, horizontal })
            {
                if (!bar.Visible || bar.Width <= 0 || bar.Height <= 0) continue;
                using var bitmap = new Bitmap(bar.Width, bar.Height);
                bar.DrawToBitmap(bitmap, bar.ClientRectangle);
                g.DrawImageUnscaled(bitmap, bar.Left - owner.Left, bar.Top - owner.Top);
            }
        }
    }
    public void Synchronize()
    {
        if (synchronizing || owner.IsDisposed || vertical.IsDisposed) return;
        synchronizing = true;
        try
        {
            foreach (var bar in new[] { vertical, horizontal })
            {
                if (bar.Parent != owner.Parent) bar.Parent = owner.Parent;
                bar.BackColor = owner.BackColor;
                bar.Enabled = owner.Enabled;
            }
            if (!owner.IsHandleCreated) return;
            var client = owner.ClientRectangle;
            if (clip != client)
            {
                clip = client;
                var previous = owner.Region;
                owner.Region = new Region(client);
                previous?.Dispose();
            }
            vertical.Bounds = new(owner.Left + client.Width, owner.Top,
                Math.Max(0, owner.Width - client.Width), client.Height);
            horizontal.Bounds = new(owner.Left, owner.Top + client.Height,
                client.Width, Math.Max(0, owner.Height - client.Height));
            vertical.UpdateRange(owner.VerticalScroll);
            horizontal.UpdateRange(owner.HorizontalScroll);
            vertical.Visible = owner.Visible && owner.VerticalScroll.Visible;
            horizontal.Visible = owner.Visible && owner.HorizontalScroll.Visible;
            // Each bar lives outside the owner's clipped client rectangle.
            // Bringing the page forward cannot cover or repaint its scrollbar.
            if (vertical.Visible) vertical.BringToFront();
            if (horizontal.Visible) horizontal.BringToFront();
        }
        finally { synchronizing = false; }
    }

    private void Move(bool isVertical, int value)
    {
        if (owner.IsDisposed) return;
        var current = owner.AutoScrollPosition;
        owner.AutoScrollPosition = isVertical ? new(-current.X, value) : new(value, -current.Y);
        Synchronize();
    }
    private void Wheel(MouseEventArgs args)
    {
        if (!owner.IsDisposed && owner.IsHandleCreated)
            SendMessage(owner.Handle, 0x020A, (IntPtr)(args.Delta << 16), IntPtr.Zero);
    }
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}

internal sealed class ThemeScrollbar : Control
{
    private readonly bool vertical;
    private readonly Action<int> change;
    private readonly Action<MouseEventArgs> wheel;
    private int minimum, maximum, value, page = 1, step = 1;
    private int? dragOffset;

    public ThemeScrollbar(bool vertical, Action<int> change, Action<MouseEventArgs> wheel)
    {
        this.vertical = vertical; this.change = change; this.wheel = wheel;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque | ControlStyles.ResizeRedraw, true);
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.ScrollBar;
        AccessibleName = vertical ? "Vertical scroll" : "Horizontal scroll";
        TabStop = true;
        Visible = false;
        Margin = Padding.Empty;
    }
    public void UpdateRange(ScrollProperties range)
    {
        var next = (range.Minimum, Math.Max(range.Minimum, range.Maximum - range.LargeChange + 1),
            range.Value, Math.Max(1, range.LargeChange), Math.Max(1, range.SmallChange));
        if ((minimum, maximum, value, page, step) == next) return;
        (minimum, maximum, value, page, step) = next;
        Invalidate();
    }
    private int Length => vertical ? Height : Width;
    private int ThumbLength => Math.Min(Length, Math.Max((int)Math.Round(24 * DeviceDpi / 96d),
        (int)Math.Round(Length * (double)page / Math.Max(1, maximum - minimum + page))));
    private int ThumbStart => maximum == minimum ? 0 : (int)Math.Round(
        (Length - ThumbLength) * (double)(value - minimum) / (maximum - minimum));
    private void SetValue(int next) => change(Math.Clamp(next, minimum, maximum));
    protected override void OnPaintBackground(PaintEventArgs args) { }
    protected override void OnPaint(PaintEventArgs args)
    {
        args.Graphics.Clear(BackColor);
        if (Length <= 0) return;
        args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var thickness = Math.Min((vertical ? Width : Height) - 4, 12 * DeviceDpi / 96f);
        if (thickness <= 0) return;
        var thumb = vertical ? new RectangleF((Width - thickness) / 2, ThumbStart, thickness, ThumbLength)
            : new RectangleF(ThumbStart, (Height - thickness) / 2, ThumbLength, thickness);
        using var path = ThemeDrawing.Rounded(thumb, thickness / 2);
        using var accent = new SolidBrush(Enabled ? ThemeDrawing.Accent : Color.FromArgb(84, 88, 95));
        args.Graphics.FillPath(accent, path);
        if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(args.Graphics, Rectangle.Inflate(ClientRectangle, -1, -1));
    }
    protected override void OnMouseDown(MouseEventArgs args)
    {
        base.OnMouseDown(args);
        if (args.Button != MouseButtons.Left) return;
        Focus();
        var position = vertical ? args.Y : args.X;
        if (position >= ThumbStart && position < ThumbStart + ThumbLength)
        { dragOffset = position - ThumbStart; Capture = true; }
        else SetValue(value + (position < ThumbStart ? -page : page));
    }
    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        if (dragOffset is not { } offset || Length <= ThumbLength) return;
        var position = Math.Clamp((vertical ? args.Y : args.X) - offset, 0, Length - ThumbLength);
        SetValue(minimum + (int)Math.Round(position * (double)(maximum - minimum) / (Length - ThumbLength)));
    }
    protected override void OnMouseUp(MouseEventArgs args)
    { base.OnMouseUp(args); if (args.Button == MouseButtons.Left) { dragOffset = null; Capture = false; } }
    protected override void OnMouseCaptureChanged(EventArgs args)
    { base.OnMouseCaptureChanged(args); if (!Capture) dragOffset = null; }
    protected override void OnMouseWheel(MouseEventArgs args)
    { if (args is HandledMouseEventArgs handled) handled.Handled = true; wheel(args); }
    protected override bool IsInputKey(Keys keyData) => (keyData & Keys.KeyCode) is
        Keys.Up or Keys.Down or Keys.Left or Keys.Right or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs args)
    {
        var next = args.KeyCode switch
        {
            Keys.Home => minimum, Keys.End => maximum,
            Keys.PageUp => value - page, Keys.PageDown => value + page,
            Keys.Up or Keys.Left => value - step, Keys.Down or Keys.Right => value + step,
            _ => value,
        };
        if (IsInputKey(args.KeyCode)) { SetValue(next); args.Handled = true; }
        base.OnKeyDown(args);
    }
    protected override void OnGotFocus(EventArgs args) { base.OnGotFocus(args); Invalidate(); }
    protected override void OnLostFocus(EventArgs args) { base.OnLostFocus(args); Invalidate(); }
}
