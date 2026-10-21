using System.Runtime.InteropServices;

namespace ShockWave.Overlay;

// Wheel gestures scroll the surrounding page; editing requires a click or key.
internal sealed class SettingNumberInput : NumericUpDown
{
    private bool showSpinner = true, arranging;
    public bool ShowSpinner
    {
        get => showSpinner;
        set { if (showSpinner == value) return; showSpinner = value; PerformLayout(); Invalidate(); }
    }
    protected override void OnLayout(LayoutEventArgs args)
    {
        if (arranging) return;
        base.OnLayout(args);
        ArrangeInput();
    }
    protected override void OnFontChanged(EventArgs args) { base.OnFontChanged(args); ArrangeInput(); }
    protected override void OnHandleCreated(EventArgs args) { base.OnHandleCreated(args); ArrangeInput(); }
    protected override void OnTextBoxResize(object? source, EventArgs args)
    {
        // UpDownBase otherwise restores the spinner's reserved width when the edit resizes.
        if (arranging) return;
        base.OnTextBoxResize(source, args);
        ArrangeInput();
    }
    private void ArrangeInput()
    {
        if (arranging) return;
        arranging = true;
        try
        {
            foreach (Control child in Controls)
            {
                if (child is TextBox edit)
                {
                    if (!showSpinner)
                        edit.SetBounds(0, Math.Max(0, (ClientSize.Height - edit.PreferredHeight) / 2), ClientSize.Width, edit.PreferredHeight);
                }
                else
                {
                    child.Visible = showSpinner;
                    // WM_PRINT/child repainting can still include a hidden spin
                    // control. Keep it outside the edit's visible client area.
                    if (!showSpinner) child.SetBounds(ClientSize.Width, 0, 0, ClientSize.Height);
                }
            }
        }
        finally { arranging = false; }
    }
    protected override void OnMouseWheel(MouseEventArgs e) => SettingScroll.Route(this, e);
    protected override void OnPaint(PaintEventArgs args)
    {
        // UpDownBase is opaque but skips filling the margins around a borderless
        // edit. Clear them so old text/arrow pixels cannot survive a repaint.
        if (!showSpinner) args.Graphics.Clear(BackColor);
        base.OnPaint(args);
    }
}

internal sealed class SettingChoiceInput : ComboBox
{
    protected override void OnMouseWheel(MouseEventArgs e) => SettingScroll.Route(this, e);
}

internal static class SettingScroll
{
    private const int MouseWheel = 0x020A;

    public static void Route(Control source, MouseEventArgs e)
    {
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
        if (source.IsDisposed || e.Delta == 0) return;
        for (var parent = source.Parent; parent is not null; parent = parent.Parent)
        {
            if (parent is not ScrollableControl { AutoScroll: true } page || !page.IsHandleCreated ||
                (!page.VerticalScroll.Visible && !page.HorizontalScroll.Visible)) continue;
            var point = source.PointToScreen(e.Location);
            var position = unchecked((ushort)point.X | ((int)(ushort)point.Y << 16));
            SendMessage(page.Handle, MouseWheel, (nint)(e.Delta << 16), (nint)position);
            return;
        }
    }

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint window, int message, nint wParam, nint lParam);
}
