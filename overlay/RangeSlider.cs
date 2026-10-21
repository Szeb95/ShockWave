using System.Drawing.Drawing2D;

namespace ShockWave.Overlay;

internal sealed class RangeSlider : Control
{
    private int lower;
    private int upper = 100;
    private bool dragging;
    private bool upperSelected = true;
    public int Minimum { get; set; }
    public int Maximum { get; set; } = 100;
    public int Step { get; set; } = 1;
    public int Lower => lower;
    public int Upper => upper;
    public bool IsDragging => dragging;
    public event EventHandler? ValueChanged;
    public event EventHandler? ValueCommitted;

    public RangeSlider()
    {
        Height = 32;
        TabStop = true;
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.Slider;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.AllPaintingInWmPaint | ControlStyles.Selectable | ControlStyles.ResizeRedraw, true);
    }

    public void SetRange(int minimum, int maximum)
    {
        lower = Math.Clamp(minimum, Minimum, Maximum);
        upper = Math.Clamp(maximum, lower, Maximum);
        AccessibleDescription = $"Range {lower} to {upper}. Up or Down selects a handle; Left or Right changes it.";
        Invalidate();
    }

    private float Position(int value) => 8 + (Width - 16) * (value - Minimum) / (float)Math.Max(1, Maximum - Minimum);

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var y = Height / 2f;
        using var track = new Pen(ColorTranslator.FromHtml("#80848e"), 6) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var active = new Pen(ColorTranslator.FromHtml("#10a37f"), 6) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        args.Graphics.DrawLine(track, 8, y, Math.Max(8, Width - 8), y);
        args.Graphics.DrawLine(active, Position(lower), y, Position(upper), y);
        using var thumb = new SolidBrush(ColorTranslator.FromHtml("#f2f3f5"));
        args.Graphics.FillEllipse(thumb, Position(lower) - 7, y - 7, 14, 14);
        args.Graphics.FillEllipse(thumb, Position(upper) - 7, y - 7, 14, 14);
        if (Focused)
        {
            using var focus = new Pen(ColorTranslator.FromHtml("#18b991"), 1);
            var x = Position(upperSelected ? upper : lower);
            args.Graphics.DrawEllipse(focus, x - 9, y - 9, 18, 18);
        }
    }

    protected override void OnMouseDown(MouseEventArgs args)
    {
        base.OnMouseDown(args);
        if (args.Button != MouseButtons.Left) return;
        Focus();
        var lowDistance = Math.Abs(args.X - Position(lower));
        var highDistance = Math.Abs(args.X - Position(upper));
        upperSelected = highDistance < lowDistance || (highDistance == lowDistance && args.X > Position(lower));
        dragging = true;
        Capture = true;
        MoveHandle(args.X);
    }

    protected override void OnMouseMove(MouseEventArgs args)
    {
        base.OnMouseMove(args);
        if (dragging) MoveHandle(args.X);
    }

    protected override void OnMouseUp(MouseEventArgs args)
    {
        base.OnMouseUp(args);
        if (!dragging || args.Button != MouseButtons.Left) return;
        MoveHandle(args.X);
        dragging = false;
        Capture = false;
        ValueCommitted?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnMouseCaptureChanged(EventArgs args)
    {
        base.OnMouseCaptureChanged(args);
        if (Capture || !dragging) return;
        dragging = false;
        ValueCommitted?.Invoke(this, EventArgs.Empty);
    }

    private void MoveHandle(int x)
    {
        var value = Minimum + Math.Clamp((x - 8) / (double)Math.Max(1, Width - 16), 0, 1) * (Maximum - Minimum);
        SetHandle((int)Math.Round(value / Step) * Step);
    }

    private void SetHandle(int value)
    {
        value = Math.Clamp(value, Minimum, Maximum);
        if (upperSelected) upper = Math.Max(lower, value);
        else lower = Math.Min(upper, value);
        AccessibleDescription = $"Range {lower} to {upper}. Up or Down selects a handle; Left or Right changes it.";
        Invalidate();
        ValueChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Left or Keys.Right or Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs args)
    {
        base.OnKeyDown(args);
        if (args.KeyCode is Keys.Up or Keys.Down)
        {
            upperSelected = args.KeyCode == Keys.Up;
            Invalidate();
        }
        else if (args.KeyCode is Keys.Left or Keys.Right or Keys.Home or Keys.End)
        {
            var value = upperSelected ? upper : lower;
            SetHandle(args.KeyCode == Keys.Home ? Minimum : args.KeyCode == Keys.End ? Maximum
                : value + (args.KeyCode == Keys.Right ? Step : -Step));
            ValueCommitted?.Invoke(this, EventArgs.Empty);
        }
        else return;
        args.Handled = true;
    }
}
