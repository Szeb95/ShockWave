using ShockWave.Overlay.Updates;

namespace ShockWave.Overlay;

internal sealed class UpdatePage : UserControl
{
    private readonly AppUpdater updater;
    private readonly Label installed = TextLabel(11);
    private readonly Label latest = TextLabel(11);
    private readonly Label status = TextLabel(10);
    private readonly Button check = Button("Check for updates", false);
    private readonly Button update = Button("Update", true);
    private readonly ThemeToggle automatic = new() { Text = "Automatic update", ForeColor = Color.FromArgb(242, 243, 245), Size = new(280, 38), Font = new("Segoe UI", 11), TextBeforeSwitch = true };
    private readonly LinkLabel release = new() { Text = "View published version on GitHub", AutoSize = true, LinkColor = Color.FromArgb(31, 201, 159), ActiveLinkColor = Color.White, VisitedLinkColor = Color.FromArgb(31, 201, 159), Font = new("Segoe UI", 10), BackColor = Color.Transparent };
    public event Action? CheckRequested;
    public event Action? InstallRequested;
    public event Action? ReleaseRequested;
    public event Action? AutomaticChanged;

    internal UpdatePage(AppUpdater updater)
    {
        this.updater = updater;
        Dock = DockStyle.Fill;
        BackColor = Color.FromArgb(30, 31, 34);
        var flow = new ThemedScrollPanel { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
        Controls.Add(flow);
        var content = new Panel { Dock = DockStyle.Top, Height = 472 };
        flow.Controls.Add(content);
        var heading = new Panel { Height = 70, Width = 720, Margin = Padding.Empty };
        var title = TextLabel(21, true); title.Text = "Update"; title.SetBounds(0, 0, 340, 36);
        var subtitle = TextLabel(9); subtitle.Text = "Keep ShockWave up to date."; subtitle.SetBounds(0, 42, 600, 24);
        heading.Controls.AddRange([title, subtitle]); content.Controls.Add(heading);
        var versionCard = new RoundedPanel { Height = 199, Width = 720, BackColor = Color.FromArgb(43, 45, 49), Margin = new(0, 0, 0, 14) };
        installed.SetBounds(22, 20, 660, 26); latest.SetBounds(22, 54, 660, 26);
        status.SetBounds(22, 94, 660, 40);
        update.SetBounds(22, 143, 125, 36); check.SetBounds(161, 143, 180, 36);
        versionCard.Top = 70;
        versionCard.Controls.AddRange([installed, latest, status, update, check]); content.Controls.Add(versionCard);
        var automaticCard = new RoundedPanel { Height = 123, Width = 720, BackColor = versionCard.BackColor, Margin = new(0, 0, 0, 18) };
        automatic.SetBounds(22, 14, 280, 38);
        var explanation = TextLabel(10);
        explanation.Text = "Download new versions automatically and install them when you close ShockWave. Your settings are kept.";
        explanation.SetBounds(22, 64, 660, 45);
        automaticCard.Top = 283;
        automaticCard.Controls.AddRange([automatic, explanation]); content.Controls.Add(automaticCard);
        var links = new Panel { Width = 720, Height = 48, Margin = Padding.Empty };
        links.Top = 424;
        release.Location = new(0, 8); links.Controls.Add(release); content.Controls.Add(links);
        void Arrange()
        {
            var width = Math.Max(250, content.ClientSize.Width - 16);
            foreach (Control item in content.Controls) item.Width = width;
            foreach (var label in new[] { installed, latest, status, explanation }) label.Width = Math.Max(1, width - 44);
            subtitle.Width = width;
        }
        content.Resize += (_, _) => Arrange();
        Arrange();
        check.Click += (_, _) => CheckRequested?.Invoke();
        update.Click += (_, _) => InstallRequested?.Invoke();
        release.LinkClicked += (_, _) => ReleaseRequested?.Invoke();
        automatic.Checked = updater.Automatic;
        automatic.CheckedChanged += (_, _) =>
        {
            if (automatic.Checked == updater.Automatic) return;
            updater.SetAutomatic(automatic.Checked); RefreshState(); AutomaticChanged?.Invoke();
        };
        updater.Changed += RefreshState;
        RefreshState();
    }

    internal void RefreshState()
    {
        if (IsDisposed) return;
        installed.Text = "Current version    " + AppUpdater.Display(updater.CurrentVersion);
        latest.Text = updater.Available is null ? "Latest version       Check for updates" : "Latest version       " + AppUpdater.Display(updater.Available.Version);
        status.Text = updater.Status;
        status.ForeColor = updater.Available is null ? Color.FromArgb(181, 186, 193) : Color.FromArgb(31, 201, 159);
        check.Enabled = !updater.Busy;
        update.Enabled = updater.Available is not null && !updater.Busy;
        automatic.Checked = updater.Automatic;
    }

    protected override void Dispose(bool disposing) { if (disposing) updater.Changed -= RefreshState; base.Dispose(disposing); }
    private static Label TextLabel(int size, bool bold = false) => new() { ForeColor = Color.FromArgb(242, 243, 245), BackColor = Color.Transparent, Font = new("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular) };
    private static Button Button(string text, bool primary) => new RoundedButton { Text = text, BackColor = primary ? Color.FromArgb(16, 163, 127) : Color.FromArgb(56, 58, 64), ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Cursor = Cursors.Hand, Font = new("Segoe UI", 10, FontStyle.Bold), UseVisualStyleBackColor = false };
}
