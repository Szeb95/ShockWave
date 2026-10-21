using System.Drawing;
using System.Drawing.Drawing2D;

namespace ShockWave.Overlay;

internal sealed class HelpIcon : Control
{
    private readonly Color normalColor;
    private readonly Color hoverColor;

    public HelpIcon(Color normal, Color hover)
    {
        normalColor = normal;
        hoverColor = hover;
        ForeColor = normal;
        Font = new Font("Segoe UI Semibold", 9);
        Size = new Size(18, 18);
        Cursor = Cursors.Help;
        TabStop = true;
        AccessibleRole = AccessibleRole.PushButton;
        SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer |
            ControlStyles.AllPaintingInWmPaint | ControlStyles.Selectable, true);
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        args.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var diameter = Math.Min(ClientSize.Width, ClientSize.Height) - 4;
        var circle = new RectangleF((ClientSize.Width - diameter) / 2f,
            (ClientSize.Height - diameter) / 2f, diameter, diameter);
        using var outline = new Pen(ForeColor, DeviceDpi / 96f);
        args.Graphics.DrawEllipse(outline, circle);
        // Center the visible glyph instead of the font's padded line box.
        using var glyph = new GraphicsPath();
        glyph.AddString("?", Font.FontFamily, (int)Font.Style,
            Font.SizeInPoints * args.Graphics.DpiY / 72f, PointF.Empty, StringFormat.GenericTypographic);
        var glyphBounds = glyph.GetBounds();
        using var alignment = new Matrix();
        alignment.Translate(
            circle.Left + (circle.Width - glyphBounds.Width) / 2f - glyphBounds.Left,
            circle.Top + (circle.Height - glyphBounds.Height) / 2f - glyphBounds.Top);
        glyph.Transform(alignment);
        using var glyphBrush = new SolidBrush(ForeColor);
        args.Graphics.FillPath(glyphBrush, glyph);
        if (Focused) ControlPaint.DrawFocusRectangle(args.Graphics, ClientRectangle, ForeColor, BackColor);
    }

    protected override void OnMouseEnter(EventArgs args)
    {
        base.OnMouseEnter(args);
        ForeColor = hoverColor;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs args)
    {
        base.OnMouseLeave(args);
        ForeColor = normalColor;
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs args)
    {
        if (args.KeyCode is Keys.Enter or Keys.Space)
        {
            OnClick(EventArgs.Empty);
            args.Handled = true;
        }
        base.OnKeyDown(args);
    }
}
