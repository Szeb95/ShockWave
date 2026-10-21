using System.Drawing.Drawing2D;

namespace ShockWave.Overlay;

internal sealed class PercentSlider : Control
{
    private int value;
    public event Action? ValueChanged;
    public int Value
    {
        get => value;
        set
        {
            var next = Math.Clamp(value, 0, 100);
            if (this.value == next) return;
            this.value = next; AccessibleName = "Level " + next + " percent"; Invalidate(); ValueChanged?.Invoke();
        }
    }
    public PercentSlider()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
        Size = new Size(180, 32); TabStop = true; Cursor = Cursors.Hand;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var center = Height / 2f; var x = 10 + (Width - 20) * value / 100f;
        using var track = new Pen(Color.FromArgb(80, 84, 92), 6) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        using var active = new Pen(Enabled ? Color.FromArgb(16, 163, 127) : Color.Gray, 6) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        e.Graphics.DrawLine(track, 10, center, Width - 10, center);
        e.Graphics.DrawLine(active, 10, center, x, center);
        using var dot = new SolidBrush(Enabled ? Color.White : Color.Gray);
        e.Graphics.FillEllipse(dot, x - 7, center - 7, 14, 14);
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, ClientRectangle);
    }
    private void At(int x) => Value = (int)Math.Round(Math.Clamp((x - 10d) / Math.Max(1, Width - 20), 0, 1) * 100);
    protected override void OnMouseDown(MouseEventArgs e) { base.OnMouseDown(e); if (e.Button == MouseButtons.Left) { Focus(); Capture = true; At(e.X); } }
    protected override void OnMouseMove(MouseEventArgs e) { base.OnMouseMove(e); if (Capture && e.Button == MouseButtons.Left) At(e.X); }
    protected override void OnMouseUp(MouseEventArgs e) { base.OnMouseUp(e); if (e.Button == MouseButtons.Left) Capture = false; }
    protected override bool IsInputKey(Keys keyData) => keyData is Keys.Left or Keys.Right or Keys.Home or Keys.End || base.IsInputKey(keyData);
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Left) Value--;
        else if (e.KeyCode == Keys.Right) Value++;
        else if (e.KeyCode == Keys.Home) Value = 0;
        else if (e.KeyCode == Keys.End) Value = 100;
        else return;
        e.Handled = true;
    }
}
