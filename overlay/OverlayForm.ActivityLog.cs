namespace ShockWave.Overlay;

internal sealed partial class OverlayForm
{
    private void BuildLogsPage()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, Padding = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        logsPage.Controls.Add(layout);

        var logHeader = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var logTitle = Label("Log", 21, true);
        logTitle.SetBounds(0, 0, 220, 35);
        var logSubtitle = Label("Recent activity from this session", 9, false, Muted);
        logSubtitle.SetBounds(0, 42, 310, 25);
        var clear = StyledButton("Clear", false);
        var copy = StyledButton("Copy log", false);
        var copyRemote = StyledButton("Copy remote log", false);
        copyRemote.Width = 150; copyRemote.FlatAppearance.BorderSize = 0;
        copyRemote.Click += (_, _) =>
        {
            try { Clipboard.SetText(RemoteLogText()); }
            catch (Exception) { AppendLog("error", "Could not copy the remote log."); }
        };
        copy.Width = 100;
        copy.FlatAppearance.BorderSize = 0;
        copy.Click += (_, _) =>
        {
            try { if (recentLog.Count > 0) Clipboard.SetText(string.Join(Environment.NewLine, recentLog)); }
            catch (Exception error) { AppendLog("error", "Could not copy the log: " + error.Message); }
        };
        clear.FlatAppearance.BorderSize = 0;
        clear.Click += (_, _) => { recentLog.Clear(); logGrid.RowCount = 0; };
        logHeader.Controls.Add(logTitle);
        logHeader.Controls.Add(logSubtitle);
        logHeader.Controls.Add(clear);
        logHeader.Controls.Add(copy);
        logHeader.Controls.Add(copyRemote);
        logHeader.Resize += (_, _) =>
        {
            clear.Location = new Point(Math.Max(0, logHeader.Width - clear.Width), 4);
            copy.Location = new Point(Math.Max(0, clear.Left - copy.Width - 12), 4);
            copyRemote.Location = new Point(Math.Max(0, copy.Left - copyRemote.Width - 12), 4);
            logTitle.Width = Math.Max(1, copyRemote.Left - 12);
            logSubtitle.Width = logHeader.Width;
        };
        layout.Controls.Add(logHeader, 0, 0);

        logGrid.Dock = DockStyle.Fill;
        logGrid.Margin = Padding.Empty;
        logGrid.BackgroundColor = PanelColor;
        logGrid.BorderStyle = BorderStyle.None;
        logGrid.GridColor = InputColor;
        logGrid.RowHeadersVisible = false;
        logGrid.AllowUserToAddRows = false;
        logGrid.AllowUserToDeleteRows = false;
        logGrid.AllowUserToResizeRows = false;
        logGrid.ReadOnly = true;
        logGrid.MultiSelect = false;
        logGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        logGrid.EnableHeadersVisualStyles = false;
        logGrid.ColumnHeadersHeight = 34;
        logGrid.RowTemplate.Height = 30;
        // Do not measure every stored message on show/resize or each append:
        // this shares the UI thread with the expiring control timers.
        logGrid.VirtualMode = true;
        logGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
        logGrid.CellValueNeeded += (_, args) => args.Value = LogValue(args.RowIndex,args.ColumnIndex);
        logGrid.CellToolTipTextNeeded += (_, args) => args.ToolTipText = LogValue(args.RowIndex,2);
        logGrid.CellFormatting += (_, args) =>
        {
            if (args.CellStyle is not { } style) return;
            var error = LogValue(args.RowIndex,1) == "ERROR";
            if (args.ColumnIndex == 1) style.ForeColor = error ? Warning : Accent;
            else if (args.ColumnIndex == 2) style.ForeColor = error ? Warning : TextColor;
        };
        logGrid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = PanelHover, ForeColor = Muted, SelectionBackColor = PanelHover,
            SelectionForeColor = Muted, Font = new Font("Segoe UI Semibold", 9, FontStyle.Bold),
        };
        logGrid.DefaultCellStyle = new DataGridViewCellStyle
        {
            BackColor = PanelColor, ForeColor = TextColor, SelectionBackColor = PanelHover,
            SelectionForeColor = TextColor, Font = new Font("Segoe UI", 9),
        };
        logGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Time", Width = 92, SortMode = DataGridViewColumnSortMode.NotSortable });
        logGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Source", Width = 78, SortMode = DataGridViewColumnSortMode.NotSortable });
        logGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Event", AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill, SortMode = DataGridViewColumnSortMode.NotSortable });
        logGrid.Columns[2].DefaultCellStyle.WrapMode = DataGridViewTriState.False;
        layout.Controls.Add(logGrid, 0, 1);
    }

    private void AppendLog(string source, string message)
    {
        if (apiToken.Length > 0) message = message.Replace(apiToken, "[redacted]", StringComparison.Ordinal);
        if (piShockApiKey.Length > 0) message = message.Replace(piShockApiKey, "[redacted]", StringComparison.Ordinal);
        foreach (var device in config.Shockers.Concat(config.PiShockShockers))
            if (device.Uuid.Length > 0) message = message.Replace(device.Uuid, "[shocker]", StringComparison.OrdinalIgnoreCase);
        var time = DateTime.Now.ToString("HH:mm:ss");
        var entry = time + "  " + source.ToUpperInvariant() + "  " + message;
        recentLog.Add(entry);
        while (recentLog.Count > 500) recentLog.RemoveAt(0);
        var fileError = sessionLog?.Append(entry);
        var visible = logGrid.Visible;
        var nearBottom = visible && logGrid.RowCount == 0;
        if (visible) try
        {
            nearBottom = nearBottom || logGrid.FirstDisplayedScrollingRowIndex < 0 ||
                logGrid.FirstDisplayedScrollingRowIndex + logGrid.DisplayedRowCount(false) >= logGrid.RowCount - 2;
        }
        catch (InvalidOperationException) { nearBottom = true; }
        if (logGrid.RowCount != recentLog.Count) logGrid.RowCount = recentLog.Count;
        if (visible)
        {
            logGrid.Invalidate();
            if (nearBottom && logGrid.RowCount > 0) TryScrollLog(logGrid.RowCount - 1);
        }
        if (fileError is not null) AppendLog("error", fileError);
    }

    private string LogValue(int row, int column)
    {
        if (row < 0 || row >= recentLog.Count || column is < 0 or > 2) return "";
        var parts = recentLog[row].Split(["  "],3,StringSplitOptions.None);
        return column < parts.Length ? parts[column] : "";
    }

    private void TryScrollLog(int index)
    {
        if (!logGrid.IsHandleCreated || logGrid.Height <= logGrid.ColumnHeadersHeight) return;
        try { logGrid.FirstDisplayedScrollingRowIndex = index; }
        catch (InvalidOperationException) { }
    }

}
