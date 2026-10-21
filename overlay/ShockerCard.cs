namespace ShockWave.Overlay;

internal sealed class ShockerCard : RoundedPanel
{
    public const int CompactHeight = 182;
    public const int OptionsHeight = 598;
    private const string NameSyncHelp = "Synced with OpenShock while the bridge is connected. Edit the name, then click elsewhere to save it to OpenShock. Renaming needs Shockers Edit permission. Offline edits wait for a live connection.";
    private static readonly Color PanelColor = ColorTranslator.FromHtml("#2b2d31");
    private static readonly Color InputColor = ColorTranslator.FromHtml("#383a40");
    private static readonly Color TextColor = ColorTranslator.FromHtml("#f2f3f5");
    private static readonly Color Muted = ColorTranslator.FromHtml("#b5bac1");
    private static readonly Color Accent = ColorTranslator.FromHtml("#10a37f");
    private readonly ToolTip help;
    private readonly TextBox nameInput = TextInput();
    private readonly TextBox modelInput = TextInput();
    private readonly Label state = TextLabel("", 8);
    private readonly CheckBox options = new ThemeToggle() { Text = "Options", TextBeforeSwitch = true, BackColor = PanelColor, ForeColor = Muted, Font = new Font("Segoe UI", 8) };
    private readonly RangeSlider intensity = new() { Minimum = 0, Maximum = 100, AccessibleName = "Intensity range" };
    private readonly RangeSlider duration = new() { Minimum = 300, Maximum = 15000, Step = 100, AccessibleName = "Duration range" };
    private readonly Label intensityLabel = TextLabel("Intensity", 8, true);
    private readonly Label durationLabel = TextLabel("Duration", 8, true);
    private readonly Label intensityValue = TextLabel("", 8);
    private readonly Label durationValue = TextLabel("", 8);
    private readonly Button[] actions = [ActionButton("Shock", true), ActionButton("Vibrate", true), ActionButton("Sound", true)];
    private readonly Panel advanced = new() { BackColor = PanelColor };
    private readonly TextBox uuidInput = TextInput();
    private readonly CheckBox paused = new ThemeToggle() { Text = "Pause in this overlay", ThinSwitch = true, BackColor = PanelColor, ForeColor = TextColor, Font = new Font("Segoe UI", 9) };
    private readonly NumericUpDown cooldownInput = Number(0, 60, 1);
    private readonly RangeSlider otherIntensity = new() { Minimum = 0, Maximum = 100, AccessibleName = "Intensity limits for others" };
    private readonly RangeSlider otherDuration = new() { Minimum = 300, Maximum = 15000, Step = 100, AccessibleName = "Duration limits for others" };
    private readonly Label otherIntensityValue = TextLabel("", 8);
    private readonly Label otherDurationValue = TextLabel("", 8);
    private readonly Button applyShares = ActionButton("Apply to OpenShock");
    private readonly Label shareMessage = TextLabel("OpenShock shares: not applied", 8);
    private readonly System.Windows.Forms.Timer saveTimer = new() { Interval = 180 };
    private readonly System.Windows.Forms.Timer autoApplyTimer = new() { Interval = 650 };
    private bool pendingManual;
    private bool pendingOthers;
    private bool closing;
    private string? queuedShareSignature;
    private string? shareRequest;
    private string? shareSignature;
    private readonly Label message = TextLabel("", 8);
    private bool filling;
    private bool running;
    private bool testMode;
    private ShockerApiStatus? apiState;
    private string? hubState;
    public ShockerConfig Device { get; private set; }
    public bool Expanded { get => options.Checked; set => options.Checked = value; }
    public bool NameIsEditing => nameInput.Focused;
    public event Action<ShockerCard, ShockerConfig>? SettingsChanged;
    public event Action<ShockerCard, string>? ActionRequested;
    public event Action<ShockerCard>? RemoveRequested;
    public event Action<ShockerCard, string>? ApplySharesRequested;
    public event Action<ShockerCard>? NameEditingFinished;
    public event Action<ShockerCard>? NameRetryRequested;
    public event EventHandler? ExpandedChanged;

    public ShockerCard(ShockerConfig device, ToolTip help)
    {
        Device = device;
        this.help = help;
        BackColor = PanelColor;
        Width = 243;
        Height = CompactHeight;
        Margin = Padding.Empty;
        nameInput.BorderStyle = modelInput.BorderStyle = BorderStyle.None;
        nameInput.BackColor = modelInput.BackColor = PanelColor;
        nameInput.Font = new Font("Segoe UI", 10, FontStyle.Bold);
        modelInput.Font = new Font("Segoe UI", 8);
        modelInput.ForeColor = Muted;
        nameInput.PlaceholderText = "Toy name";
        nameInput.MaxLength = 64;
        modelInput.PlaceholderText = "Toy model";
        nameInput.Leave += (_, _) => { SaveName(); NameEditingFinished?.Invoke(this); };
        modelInput.Leave += (_, _) => SaveName();
        Controls.AddRange([nameInput, modelInput, state, options, durationLabel, durationValue,
            duration, intensityLabel, intensityValue, intensity, advanced]);
        for (var index = 0; index < actions.Length; index++)
        {
            var button = actions[index];
            button.Click += (_, _) => ActionRequested?.Invoke(this, button.Text);
            Controls.Add(button);
        }
        options.CheckedChanged += (_, _) =>
        {
            Height = Expanded ? OptionsHeight : CompactHeight;
            advanced.Visible = Expanded;
            ExpandedChanged?.Invoke(this, EventArgs.Empty);
        };
        saveTimer.Tick += (_, _) => FlushPendingChanges();
        autoApplyTimer.Tick += (_, _) => ApplyQueuedShares();
        duration.ValueChanged += (_, _) => { UpdateValues(); QueueSave(false); };
        intensity.ValueChanged += (_, _) => { UpdateValues(); QueueSave(false); };
        otherDuration.ValueChanged += (_, _) => { UpdateValues(); QueueSave(true); };
        otherIntensity.ValueChanged += (_, _) => { UpdateValues(); QueueSave(true); };
        foreach (var slider in new[] { duration, intensity, otherDuration, otherIntensity })
            slider.ValueCommitted += (_, _) =>
            {
                FlushPendingChanges();
                if ((slider == otherDuration || slider == otherIntensity) && queuedShareSignature is not null)
                    RestartAutoApplyTimer();
            };
        durationValue.Cursor = intensityValue.Cursor = Cursors.Hand;
        durationValue.Click += (_, _) => duration.Focus();
        intensityValue.Click += (_, _) => intensity.Focus();
        help.SetToolTip(options, "Show this shocker's UUID, pause, cooldown, and limits for others.");
        help.SetToolTip(duration, "Your button duration range, saved automatically. Buttons use the upper value. Arrow keys make precise adjustments.");
        help.SetToolTip(intensity, "Your button intensity range, saved automatically. Limits for other controllers are separate under Options. Moving sliders never activates a device.");
        advanced.Width = Width;
        BuildOptions();
        Resize += (_, _) => Arrange();
        ApplyDevice(device, true);
        Arrange();
    }

    private static Label TextLabel(string text, int size = 9, bool bold = false) => new()
    {
        Text = text, ForeColor = Muted, BackColor = Color.Transparent,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
        AutoEllipsis = true,
    };

    private static TextBox TextInput() => new()
    {
        BackColor = InputColor, ForeColor = TextColor, BorderStyle = BorderStyle.None,
        Font = new Font("Segoe UI", 9),
    };

    private static NumericUpDown Number(decimal minimum, decimal maximum, int decimals = 0) => new SettingNumberInput()
    {
        ShowSpinner = false, Minimum = minimum, Maximum = maximum, DecimalPlaces = decimals,
        Increment = decimals > 0 ? .1m : 1, BackColor = InputColor, ForeColor = TextColor,
        BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 9),
    };

    private static Button ActionButton(string text, bool keepCardColors = false)
    {
        Button button = keepCardColors ? new ShockerActionButton() : new Button();
        button.Text = text;
        button.BackColor = InputColor;
        button.ForeColor = TextColor;
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.Font = new Font("Segoe UI Semibold", 8, FontStyle.Bold);
        button.Cursor = Cursors.Hand;
        return button;
    }

    private sealed class ShockerActionButton : Button
    {
        private bool hover, pressed;
        public ShockerActionButton() => SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        protected override void OnMouseEnter(EventArgs args) { hover = true; base.OnMouseEnter(args); Invalidate(); }
        protected override void OnMouseLeave(EventArgs args) { hover = pressed = false; base.OnMouseLeave(args); Invalidate(); }
        protected override void OnMouseDown(MouseEventArgs args) { pressed = args.Button == MouseButtons.Left; base.OnMouseDown(args); Invalidate(); }
        protected override void OnMouseUp(MouseEventArgs args) { pressed = false; base.OnMouseUp(args); Invalidate(); }
        protected override void OnPaint(PaintEventArgs args)
        {
            // Paint the full surface in every state, including after movement/resizing.
            args.Graphics.Clear(Parent?.BackColor ?? BackColor);
            args.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var path = ThemeDrawing.Rounded(new(0, 0, Width, Height), 4 * DeviceDpi / 96f);
            using var background = new SolidBrush(Enabled && hover
                ? ColorTranslator.FromHtml(pressed ? "#454850" : "#40434a") : BackColor);
            args.Graphics.FillPath(background, path);
            TextRenderer.DrawText(args.Graphics, Text, Font, ClientRectangle, ForeColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            if (Focused && ShowFocusCues) ControlPaint.DrawFocusRectangle(args.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3));
        }
    }

    private void BuildOptions()
    {
        advanced.Controls.Add(paused);
        paused.SetBounds(10, 8, Width - 20, 24);
        paused.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        paused.CheckedChanged += (_, _) =>
        {
            if (filling) return;
            FlushPendingChanges();
            var next = Device.Clone(); next.Paused = paused.Checked;
            SettingsChanged?.Invoke(this, next);
        };
        help.SetToolTip(paused, "Block new manual and avatar commands from this overlay for this shocker. Commands already sent may finish. OpenShock account settings are unchanged.");
        AddOption(Device.IsPiShock ? "PiShock hub/shocker[/share]" : "Shocker UUID", uuidInput, 38, 50);
        var copy = ActionButton("Copy");
        copy.FlatAppearance.BorderSize = 0;
        copy.SetBounds(Width - 60, 58, 50, 25);
        copy.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        copy.Click += (_, _) =>
        {
            try { if (!string.IsNullOrWhiteSpace(uuidInput.Text)) Clipboard.SetText(uuidInput.Text.Trim()); }
            catch (System.Runtime.InteropServices.ExternalException) { ShowError("Clipboard is busy; try again."); }
        };
        advanced.Controls.Add(copy);
        AddOption("Cooldown (seconds)", cooldownInput, 94);
        var limitsHeading = TextLabel("Limits for others", 8, true);
        limitsHeading.SetBounds(10, 146, Width - 20, 18);
        limitsHeading.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        advanced.Controls.Add(limitsHeading);
        AddLimitSlider("Duration max", otherDuration, otherDurationValue, 168);
        AddLimitSlider("Intensity max", otherIntensity, otherIntensityValue, 224);
        help.SetToolTip(limitsHeading, "Limits for other controllers using this app. Changing a maximum also applies to existing OpenShock user shares and public links after dragging; new shares need another Apply. Your own card buttons and self-test use the top ranges.");
        const string limitHelp = "The right handle sets the maximum for others. After release, changes save locally and apply to existing OpenShock shares after a short pause, while the live bridge is running. The left handle sets the minimum for app-routed commands only. Values are clipped, not scaled. No device is activated. Use Apply to OpenShock to retry a failed update or include new shares.";
        help.SetToolTip(otherDuration, limitHelp);
        help.SetToolTip(otherIntensity, limitHelp);
        if (Device.IsPiShock)
        {
            const string localHelp = "Limits for avatar/other-controller commands passing through this app. They do not change PiShock account shares. Your buttons use the top ranges. Manage account sharing limits on the PiShock website.";
            help.SetToolTip(limitsHeading, localHelp); help.SetToolTip(otherDuration, localHelp); help.SetToolTip(otherIntensity, localHelp);
            help.SetToolTip(paused, "Block new commands from this app. Commands already accepted may finish. PiShock account pause settings remain separate.");
        }
        var save = ActionButton("Save options");
        save.Tag = "save-options";
        save.BackColor = Accent;
        save.FlatAppearance.BorderSize = 0;
        save.SetBounds(10, 284, 110, 28);
        save.Click += (_, _) => SaveOptions();
        var remove = ActionButton("Remove");
        remove.FlatAppearance.BorderSize = 0;
        remove.SetBounds(126, 284, 90, 28);
        remove.Click += (_, _) => RemoveRequested?.Invoke(this);
        help.SetToolTip(remove, "Remove this panel and its avatar assignment from the overlay. The shocker stays in your OpenShock account.");
        advanced.Controls.AddRange([save, remove, message]);
        message.SetBounds(10, 314, Width - 20, 18);
        message.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        applyShares.FlatAppearance.BorderSize = 0;
        applyShares.SetBounds(10, 336, Width - 20, 28);
        applyShares.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        applyShares.Click += (_, _) =>
        {
            FlushPendingChanges();
            BeginShareApply();
        };
        help.SetToolTip(applyShares, "Apply these maxima to existing OpenShock user shares and public links, keeping tighter caps, permissions and pause unchanged. Public links also use this card's cooldown (0 means none; otherwise at least 0.3 seconds). Requires a running live bridge, an owner token, Shockers Edit and Publicshares Edit. It never sends a device command. Reapply after adding shares; unused share codes and owner credentials are not covered.");
        shareMessage.SetBounds(10, 370, Width - 20, 42);
        shareMessage.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        advanced.Controls.AddRange([applyShares, shareMessage]);
        if (Device.IsPiShock)
        {
            applyShares.Visible = false;
            shareMessage.SetBounds(10, 340, Width - 20, 62);
            shareMessage.Text = "Limits save locally. Manage PiShock shares on its website.";
            help.SetToolTip(remove, "Remove this card and its avatar assignment from the app. Your PiShock device and account shares remain.");
        }
        advanced.Visible = false;
    }

    private void AddOption(string title, Control input, int y, int reservedRight = 0)
    {
        var label = TextLabel(title, 8, true);
        label.SetBounds(10, y, 210, 18);
        label.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        input.SetBounds(10, y + 20, Width - 20 - reservedRight - (reservedRight > 0 ? 6 : 0), 25);
        input.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        advanced.Controls.AddRange([label, input]);
    }

    private void AddLimitSlider(string title, RangeSlider slider, Label value, int y)
    {
        var label = TextLabel(title, 8, true);
        value.TextAlign = ContentAlignment.TopRight;
        advanced.Controls.AddRange([label, slider, value]);
        void Layout()
        {
            label.Text = advanced.Width < 200 ? title.StartsWith("Duration") ? "Max time" : "Max %" : title;
            var width = Math.Max(1, advanced.Width - 20);
            var valueWidth = Math.Min(74, width / 2);
            label.SetBounds(10, y, width - valueWidth, 18);
            value.SetBounds(10 + width - valueWidth, y, valueWidth, 18);
            slider.SetBounds(10, y + 18, width, 32);
        }
        advanced.Resize += (_, _) => Layout();
        Layout();
    }

    private void Arrange()
    {
        var leftWidth = Math.Max(1, Width - 98);
        nameInput.SetBounds(10, 9, leftWidth, 21);
        modelInput.SetBounds(10, 31, leftWidth, 18);
        state.SetBounds(10, 46, Math.Max(1, Width - 90), 16);
        options.SetBounds(10, 63, Math.Max(1, leftWidth + 8), 20);
        options.Text = Width < 200 ? "More" : "Options";
        for (var index = 0; index < actions.Length; index++) actions[index].SetBounds(Width - 78, 7 + index * 26, 68, 24);
        var half = Math.Max(1, (Width - 20) / 2);
        durationLabel.SetBounds(10, 84, half, 16);
        durationValue.SetBounds(10 + half, 84, Width - 20 - half, 16);
        intensityLabel.SetBounds(10, 134, half, 16);
        intensityValue.SetBounds(10 + half, 134, Width - 20 - half, 16);
        durationValue.TextAlign = intensityValue.TextAlign = ContentAlignment.TopRight;
        duration.SetBounds(10, 100, Math.Max(1, Width - 20), 32);
        intensity.SetBounds(10, 150, Math.Max(1, Width - 20), 30);
        advanced.SetBounds(0, CompactHeight, Width, OptionsHeight - CompactHeight);
        var remove = advanced.Controls.OfType<Button>().Single(b => b.Text == "Remove");
        var save = advanced.Controls.OfType<Button>().Single(b => Equals(b.Tag, "save-options"));
        save.Text = Width < 200 ? "Save" : "Save options";
        var actionWidth = Math.Max(1, (Width - 26) / 2);
        save.SetBounds(10, 284, actionWidth, 28);
        remove.SetBounds(16 + actionWidth, 284, actionWidth, 28);
        SetRuntime(running, testMode);
    }

    public void ApplyDevice(ShockerConfig device, bool fillDraft = false)
    {
        var keepNameDraft = nameInput.Focused && !fillDraft;
        var keepModelDraft = modelInput.Focused && !fillDraft;
        filling = true;
        Device = device;
        if (queuedShareSignature is not null && queuedShareSignature != LimitSignature())
            ClearQueuedShares();
        if (shareSignature is not null && shareSignature != LimitSignature())
        {
            shareMessage.Text = "Limits changed: apply to OpenShock again.";
            shareMessage.ForeColor = ColorTranslator.FromHtml("#ffb74d");
            shareSignature = null;
        }
        if (!keepNameDraft) nameInput.Text = device.Name;
        if (!keepModelDraft) modelInput.Text = device.Model;
        paused.Checked = device.Paused;
        duration.Maximum = Math.Max(15000, device.ManualMaxDurationMs);
        duration.SetRange(device.ManualMinDurationMs, device.ManualMaxDurationMs);
        intensity.SetRange(device.ManualMinIntensity, device.ManualMaxIntensity);
        otherDuration.Maximum = Math.Max(15000, device.MaxDurationMs);
        otherDuration.SetRange(device.MinDurationMs, device.MaxDurationMs);
        otherIntensity.SetRange(device.MinIntensity, device.MaxIntensity);
        if (fillDraft)
        {
            uuidInput.Text = device.Uuid;
            cooldownInput.Value = device.CooldownMs / 1000m;
        }
        filling = false;
        UpdateValues();
        SetRuntime(running, testMode);
    }

    private void UpdateValues()
    {
        durationValue.Text = $"{duration.Lower / 1000.0:0.#}–{duration.Upper / 1000.0:0.#}s";
        intensityValue.Text = $"{intensity.Lower}–{intensity.Upper}%";
        otherDurationValue.Text = $"{otherDuration.Lower / 1000.0:0.#}–{otherDuration.Upper / 1000.0:0.#}s";
        otherIntensityValue.Text = $"{otherIntensity.Lower}–{otherIntensity.Upper}%";
        foreach (var button in actions) help.SetToolTip(button,
            $"Send {button.Text} to {Device.Name} using {intensity.Upper}% for {duration.Upper / 1000.0:0.#} seconds. Test mode logs it without activating a device. Start the bridge first.");
    }

    private void QueueSave(bool others)
    {
        if (filling) return;
        if (others) pendingOthers = true; else pendingManual = true;
        saveTimer.Stop(); saveTimer.Start();
        if (others && queuedShareSignature is not null) RestartAutoApplyTimer();
    }

    public void FlushPendingChanges()
    {
        saveTimer.Stop();
        if (filling || (!pendingManual && !pendingOthers)) return;
        var previousSignature = LimitSignature();
        var othersChanged = pendingOthers;
        var next = Device.Clone();
        if (pendingManual)
        {
            next.ManualMinIntensity = intensity.Lower; next.ManualMaxIntensity = intensity.Upper;
            next.ManualMinDurationMs = duration.Lower; next.ManualMaxDurationMs = duration.Upper;
        }
        if (pendingOthers)
        {
            next.MinIntensity = otherIntensity.Lower; next.MaxIntensity = otherIntensity.Upper;
            next.MinDurationMs = otherDuration.Lower; next.MaxDurationMs = otherDuration.Upper;
        }
        pendingManual = pendingOthers = false;
        SettingsChanged?.Invoke(this, next);
        // The form confirms successful persistence by updating Device. Failed saves must not write shares.
        if (!Device.IsPiShock && othersChanged && previousSignature != LimitSignature() &&
            Device.MaxIntensity == next.MaxIntensity && Device.MaxDurationMs == next.MaxDurationMs)
            QueueShareApply();
    }

    private bool CanApplyShares => !Device.IsPiShock && running && !testMode && Device.Uuid.Length > 0 && !closing && !IsDisposed;

    private void SetShareMessage(string text, bool warning = false)
    {
        shareMessage.Text = text;
        shareMessage.ForeColor = warning ? ColorTranslator.FromHtml("#ffb74d") : Muted;
        help.SetToolTip(shareMessage, text);
    }

    private void RestartAutoApplyTimer()
    {
        autoApplyTimer.Stop();
        autoApplyTimer.Start();
    }

    private void ClearQueuedShares()
    {
        autoApplyTimer.Stop();
        queuedShareSignature = null;
    }

    private void QueueShareApply()
    {
        ClearQueuedShares();
        if (closing) return;
        if (!CanApplyShares)
        {
            SetShareMessage(testMode ? "Saved locally; test mode blocks OpenShock Apply."
                : Device.Uuid.Length == 0 ? "Saved locally; add a UUID, then Apply."
                : "Saved locally; start the live bridge, then Apply.", true);
            return;
        }
        queuedShareSignature = LimitSignature();
        SetShareMessage("Saved; applying to OpenShock shortly…");
        RestartAutoApplyTimer();
    }

    private void ApplyQueuedShares()
    {
        autoApplyTimer.Stop();
        if (queuedShareSignature is null) return;
        if (!CanApplyShares || queuedShareSignature != LimitSignature())
        { ClearQueuedShares(); return; }
        if (otherDuration.IsDragging || otherIntensity.IsDragging || pendingOthers)
        { RestartAutoApplyTimer(); return; }
        // A later slider edit is a new desired update, never a retry of the in-flight request.
        // Its debounce restarts when that request completes; only the latest saved values are used.
        if (shareRequest is not null) return;
        BeginShareApply();
    }

    private void BeginShareApply()
    {
        if (!CanApplyShares || shareRequest is not null) return;
        ClearQueuedShares();
        shareRequest = Guid.NewGuid().ToString("D"); shareSignature = LimitSignature();
        SetShareMessage("Applying to existing OpenShock shares…");
        SetRuntime(running, testMode);
        ApplySharesRequested?.Invoke(this, shareRequest);
    }

    public void PrepareToClose()
    {
        closing = true;
        ClearQueuedShares();
    }

    private void SaveName()
    {
        if (filling || (nameInput.Text == Device.Name && modelInput.Text.Trim() == Device.Model)) return;
        var draftName = nameInput.Text.Trim();
        var draftModel = modelInput.Text.Trim();
        FlushPendingChanges();
        var next = Device.Clone(); next.Name = draftName; next.Model = draftModel;
        if (!Device.IsPiShock && next.Name != Device.Name && next.Uuid.Length > 0) next.NameSyncPending = true;
        SettingsChanged?.Invoke(this, next);
    }

    private void SaveOptions()
    {
        var draftName = nameInput.Text.Trim();
        var draftModel = modelInput.Text.Trim();
        FlushPendingChanges();
        var next = Device.Clone();
        next.Name = draftName; next.Model = draftModel; next.Uuid = uuidInput.Text.Trim();
        next.NameSyncPending = Device.IsPiShock || next.Uuid != Device.Uuid ? false
            : next.NameSyncPending || (next.Uuid.Length > 0 && next.Name != Device.Name);
        next.CooldownMs = (int)Math.Round(cooldownInput.Value * 1000m);
        SettingsChanged?.Invoke(this, next);
        if (Device.NameSyncPending) NameRetryRequested?.Invoke(this);
    }

    public void ShowError(string text)
    {
        message.Text = text; message.ForeColor = ColorTranslator.FromHtml("#ed6a6a");
        help.SetToolTip(message, text);
        Expanded = true;
    }

    public void ShowSaved()
    {
        message.Text = Device.NameSyncPending ? "Saved locally; name sync pending." : "Saved";
        message.ForeColor = Device.NameSyncPending ? ColorTranslator.FromHtml("#ffb74d") : Accent;
    }

    public void ShowNameSync(string text, bool success)
    {
        message.Text = text;
        message.ForeColor = success ? Accent : ColorTranslator.FromHtml("#ffb74d");
        help.SetToolTip(message, text);
    }

    public void SetRuntime(bool isRunning, bool dryRun)
    {
        running = isRunning; testMode = dryRun;
        UpdateNameStatus();
        state.Text = Device.Paused ? "Paused" : "";
        help.SetToolTip(state, (Device.Uuid.Length == 0 ? "Choose the shocker under Options." : (Device.IsPiShock ? "PiShock target: " : "UUID: ") + Device.Uuid) +
            "\n" + (Device.Paused ? "Paused in this overlay." : dryRun ? "Test mode: commands are logged only." : isRunning ? "Bridge running." : "Start the bridge to use this shocker."));
        state.ForeColor = Device.Paused ? ColorTranslator.FromHtml("#ffb74d") : Muted;
        if (isRunning && Device.Uuid.Length > 0 && apiState is not null)
        {
            state.Text = Device.Paused ? "Paused" : Width < 200 ? apiState.ShortLabel : apiState.Label;
            state.ForeColor = !Device.Paused && apiState.State == "Connected" ? Accent : ColorTranslator.FromHtml("#ffb74d");
            help.SetToolTip(state, (Device.IsPiShock ? "PiShock target: " : "UUID: ") + Device.Uuid + "\n" + apiState.Detail +
                (Device.Paused ? "\nPaused in this overlay." : ""));
        }
        foreach (var button in actions)
        {
            button.Enabled = isRunning && !Device.Paused && (dryRun || Device.Uuid.Length > 0) && Device.ManualMaxIntensity > 0;
            button.FlatAppearance.BorderSize = 0;
        }
        if (!CanApplyShares && queuedShareSignature is not null)
        {
            ClearQueuedShares();
            SetShareMessage("Auto-apply canceled; use Apply with a live bridge.", true);
        }
        if (!CanApplyShares && shareRequest is not null)
        { shareRequest = null; shareSignature = null; SetShareMessage("Apply interrupted: start live bridge and try again.", true); }
        applyShares.Enabled = CanApplyShares && shareRequest is null;
    }

    private string LimitSignature() => $"{Device.Uuid}|{Device.MaxIntensity}|{Device.MaxDurationMs}|{Device.CooldownMs}";

    public void SetShareResult(string request, bool success, string detail)
    {
        if (request != shareRequest) return;
        shareRequest = null;
        shareMessage.Text = shareSignature == LimitSignature() ? detail : "Limits changed: apply to OpenShock again.";
        shareMessage.ForeColor = success && shareSignature == LimitSignature() ? Accent : ColorTranslator.FromHtml("#ffb74d");
        help.SetToolTip(shareMessage, shareMessage.Text);
        SetRuntime(running, testMode);
        if (queuedShareSignature is not null)
        {
            SetShareMessage(success ? "New limits saved; applying shortly…"
                : "Previous apply failed; applying your latest edits…", !success);
            RestartAutoApplyTimer();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            closing = true;
            saveTimer.Dispose();
            autoApplyTimer.Dispose();
            queuedShareSignature = null;
        }
        base.Dispose(disposing);
    }

    public void SetApiState(ShockerApiStatus? value)
    {
        apiState = value;
        SetRuntime(running, testMode);
    }

    public void SetHubState(string? value)
    {
        hubState = value;
        UpdateNameStatus();
    }

    private void UpdateNameStatus()
    {
        // OpenShock reports hub connectivity, not the RF shocker's power state.
        // Keep the name neutral rather than treating hub Online as toy Online.
        nameInput.ForeColor = TextColor;
        var connection = !running ? "Bridge stopped."
            : Device.Uuid.Length == 0 ? "Enter the shocker UUID under Options."
            : apiState?.State != "Connected" ? "Hub status unavailable."
            : hubState switch { "Online" => "Hub online.", "Offline" => "Hub offline.", "Checking" => "Checking hub connection.", _ => "Hub status unknown." };
        help.SetToolTip(nameInput, Device.IsPiShock
            ? "Shocker power: unknown.\n" + connection + "\n\nThis name and model are saved locally. Manage PiShock account names on its website."
            : "Shocker power: unknown. OpenShock does not report whether this shocker is on or off.\n" + connection + "\n\n" + NameSyncHelp);
    }

    protected override void OnPaint(PaintEventArgs args)
    {
        base.OnPaint(args);
        using var border = new Pen(InputColor);
        if (Expanded) args.Graphics.DrawLine(border, 10, CompactHeight, Width - 10, CompactHeight);
    }
}
