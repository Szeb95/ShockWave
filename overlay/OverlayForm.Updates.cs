using System.Diagnostics;
using ShockWave.Overlay.Updates;

namespace ShockWave.Overlay;

internal sealed partial class OverlayForm
{
    private readonly Button updateNav = NavButton("↓", "Update");
    private readonly LinkLabel updateAvailable = new() { Text = "Update available", AutoSize = true, Visible = false,
        LinkColor = Accent, ActiveLinkColor = Color.White, VisitedLinkColor = Accent, Font = new("Segoe UI", 10, FontStyle.Bold), BackColor = Color.Transparent, TabStop = true };
    private readonly AppUpdater updater;
    private readonly UpdatePage updatePage;
    private readonly System.Windows.Forms.Timer updateTimer = new() { Interval = 60 * 60 * 1000 };
    private bool updateRestartRequested;
    internal Action<ProcessStartInfo> StartUpdateHelper { get; set; } = start => { Process.Start(start); };

    private void ConfigureUpdates()
    {
        updatePage.CheckRequested += async () => await CheckUpdatesAsync();
        updatePage.InstallRequested += async () =>
        {
            if (await updater.DownloadAsync() && !closing && !IsDisposed)
            {
                updateRestartRequested = true;
                Close();
            }
        };
        updatePage.AutomaticChanged += async () =>
        {
            if (updater.Automatic && updater.Available is not null && !closing) await updater.DownloadAsync();
        };
        updatePage.ReleaseRequested += () =>
        {
            try { Process.Start(new ProcessStartInfo(updater.Available?.Page.AbsoluteUri ?? AppUpdater.Repository + "/releases/latest") { UseShellExecute = true }); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            { MessageBox.Show(this, "The browser could not be opened. Visit github.com/Szeb95/ShockWave/releases.", "ShockWave update"); }
        };
        updateAvailable.LinkClicked += (_, _) => ShowPage(updatePage, updateNav);
        updater.Changed += RefreshUpdateBadge;
        updateTimer.Tick += async (_, _) => await CheckUpdatesAsync();
        if (updater.BackgroundChecks)
        {
            Shown += async (_, _) => { updateTimer.Start(); await CheckUpdatesAsync(); };
        }
        FormClosed += (_, _) =>
        {
            updateTimer.Stop();
            if (updater.Prepared is null || (!updater.Automatic && !updateRestartRequested)) return;
            try { StartUpdateHelper(UpdateInstaller.CreateStart(updater.Prepared, updater.InstallDirectory, updater.CurrentVersion, updateRestartRequested)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            { MessageBox.Show("The updater could not start. Your current version is unchanged.\n" + e.Message, "ShockWave update", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
    }

    private async Task CheckUpdatesAsync()
    {
        if (closing || IsDisposed) return;
        await updater.CheckAsync();
        if (!closing && !IsDisposed && updater.Automatic && updater.Available is not null) await updater.DownloadAsync();
    }

    private void RefreshUpdateBadge()
    {
        if (!IsDisposed) updateAvailable.Visible = updater.Available is not null;
    }
}
