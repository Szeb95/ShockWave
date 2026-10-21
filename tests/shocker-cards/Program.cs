using System.Reflection;
using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.Json;
using System.Collections;
using ShockWave.Overlay;

internal static class Program
{
    private static readonly BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Environment.SetEnvironmentVariable("OPENSHOCK_TOKEN", null);
            Environment.SetEnvironmentVariable("PISHOCK_API_KEY", null);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            ApplicationConfiguration.Initialize();
            if (args.Contains("--pishock-only")) { PiShockChecks.Run(); return 0; }
            if (args.Contains("--snap-only")) { WindowTransitionChecks.Run(); return 0; }
            if (args.Contains("--window-only")) { WindowRenderingChecks.Run(); return 0; }
            if (args.Contains("--close-only")) { CloseWarningChecks.Run(); return 0; }
            if (args.Contains("--wheel-only")) { WheelSettingsChecks.Run(); return 0; }
            var path = Path.Combine(AppContext.BaseDirectory, $"fixture-settings-{Environment.ProcessId}.json");
            if (args.Contains("--toys-only")) { ToyPanelChecks.Run(); return 0; }
            if (args.Contains("--hub-only"))
            {
                CheckHubPanel(new BridgeConfig { Shockers = [new() { Key = "fixture", Uuid = "11111111-1111-1111-1111-111111111111" }] }, path);
                return 0;
            }
            PiShockChecks.Run();
            WheelSettingsChecks.Run();
            WindowRenderingChecks.Run();
            WindowTransitionChecks.Run();
            CloseWarningChecks.Run();
            var legacy = new BridgeConfig { DryRun = false, Shockers = [new() { Key = "fixture", Name = "My shocker",
                Uuid = "11111111-1111-1111-1111-111111111111", MinIntensity = 10, MaxIntensity = 35,
                MinDurationMs = 500, MaxDurationMs = 2400, AvatarGroup = "FixtureRoute",
                IntensityAddress = "/avatar/parameters/FixtureIntensity", ActionAddress = "/avatar/parameters/FixtureAction" }] };
            var old = JsonNode.Parse(legacy.ToJson())!;
            foreach (var name in new[] { "manualMinIntensity", "manualMaxIntensity", "manualMinDurationMs", "manualMaxDurationMs" })
                old["shockers"]![0]!.AsObject().Remove(name);
            var migrated = BridgeConfig.Parse(old.ToJsonString());
            Check(migrated.Shockers[0].ManualMaxIntensity == 35 && migrated.Shockers[0].ManualMaxDurationMs == 2400,
                "old card selections migrate without returning to defaults");
            using (var form = new OverlayForm(migrated, initialSaveConfig: config => config.Save(path)))
            {
                var card = Get<List<ShockerCard>>(form, "shockerCards")[0];
                Change(card, "intensity", 10, 64);
                Change(card, "duration", 500, 3100);
                Change(card, "otherIntensity", 0, 29);
                Change(card, "otherDuration", 300, 1100);
                var saved = BridgeConfig.Parse(File.ReadAllText(path)).Shockers[0];
                Check(saved.ManualMaxIntensity == 65 && saved.ManualMaxDurationMs == 3200, "top slider changes save through the actual form handler");
                Check(saved.MaxIntensity == 30 && saved.MaxDurationMs == 1200, "other-controller sliders save independently");
                var cooldown = Get<NumericUpDown>(card, "cooldownInput");
                cooldown.Text = 1.2m.ToString(); Call(cooldown, "ValidateEditText");
                Call(card, "SaveOptions");
                saved = BridgeConfig.Parse(File.ReadAllText(path)).Shockers[0];
                Check(saved.ManualMaxIntensity == 65 && saved.ManualMaxDurationMs == 3200 && saved.CooldownMs == 1200 &&
                    saved.AvatarGroup == "FixtureRoute" && saved.IntensityAddress == legacy.Shockers[0].IntensityAddress &&
                    saved.ActionAddress == legacy.Shockers[0].ActionAddress,
                    "plain cooldown saves typed decimals while retaining button ranges and existing avatar routing");
                Get<TextBox>(form, "tokenInput").Text = "fixture-fake-token";
                Call(form, "SaveSettings");
                Check(ReferenceEquals(card, Get<List<ShockerCard>>(form, "shockerCards")[0]),
                    "settings refresh preserves the card and its share-update state");
                saved = BridgeConfig.Parse(File.ReadAllText(path)).Shockers[0];
                Check(saved.ManualMaxIntensity == 65 && saved.MaxIntensity == 30, "Save settings retains both ranges");
                card = Get<List<ShockerCard>>(form, "shockerCards")[0];
                var slider = Get<RangeSlider>(card, "intensity");
                Call(slider, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, slider.Width - 9, 15, 0));
                Call(form, "FlushShockerChanges");
                saved = BridgeConfig.Parse(File.ReadAllText(path)).Shockers[0];
                Check(saved.ManualMaxIntensity > 95 && saved.MaxIntensity == 30, "closing flush saves a drag before mouse release");
                // No form is shown: Shown must never start the bridge or remembered Bluetooth connections.
            }
            var restored = BridgeConfig.Parse(File.ReadAllText(path));
            using (var form = new OverlayForm(restored, initialSaveConfig: config => config.Save(path)))
            {
                var card = Get<List<ShockerCard>>(form, "shockerCards")[0];
                Check(Get<RangeSlider>(card, "intensity").Upper == restored.Shockers[0].ManualMaxIntensity &&
                    Get<RangeSlider>(card, "duration").Upper == 3200, "new app instance restores top slider positions");
                Check(Get<RangeSlider>(card, "otherIntensity").Upper == 30 && Get<RangeSlider>(card, "otherDuration").Upper == 1200,
                    "new app instance restores independent other-controller limits");
            }
            using var help = new ToolTip();
            foreach (var width in new[] { 243, 154 })
            {
                using var card = new ShockerCard(restored.Shockers[0], help) { Width = width, Expanded = true };
                card.SetRuntime(true, false);
                var button = Get<Button>(card, "applyShares");
                string? request = null;
                card.ApplySharesRequested += (_, value) => request = value;
                Call(button, "OnClick", EventArgs.Empty);
                Check(request is not null && !button.Enabled, "Apply blocks duplicate clicks " + width);
                card.SetShareResult(request!, true, "Applied to 2 existing shares. Reapply after adding shares.");
                Check(button.Enabled && Get<Label>(card, "shareMessage").Text.StartsWith("Applied"), "Apply acknowledgement is shown " + width);
                var changed = card.Device.Clone(); changed.MaxIntensity = 20; card.ApplyDevice(changed);
                Check(Get<Label>(card, "shareMessage").Text.StartsWith("Limits changed"), "changed limits invalidate previous Apply " + width);
                card.SetRuntime(false, false);
                var actions = Get<Button[]>(card, "actions");
                Check(actions.All(action => !action.Enabled && action.ForeColor.R > 230), "unavailable action buttons retain white text " + width);
                using var bitmap = new Bitmap(width, card.Height);
                card.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
                bitmap.Save(Path.Combine(AppContext.BaseDirectory, $"card-{width}.png"));
            }
            CheckAutomaticApply(restored.Shockers[0], help);
            CheckReconnectButtons(restored, help);
            CheckNameSync(restored, path);
            CheckHubPanel(restored, path);
            Console.WriteLine("Native card persistence and layout checks passed; no account, bridge, BLE or device commands used.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static T Get<T>(object value, string name) => (T)value.GetType().GetField(name, Private)!.GetValue(value)!;
    private static void Call(object value, string name, params object[] args) => value.GetType().GetMethod(name, Private)!.Invoke(value, args);
    private static void Change(ShockerCard card, string name, int low, int high)
    {
        var slider = Get<RangeSlider>(card, name); slider.SetRange(low, high);
        Call(slider, "OnKeyDown", new KeyEventArgs(Keys.Up));
        Call(slider, "OnKeyDown", new KeyEventArgs(Keys.Right));
    }
    private static void Pump(int milliseconds)
    {
        var watch = Stopwatch.StartNew();
        while (watch.ElapsedMilliseconds < milliseconds) { Application.DoEvents(); Thread.Sleep(5); }
        Application.DoEvents();
    }
    private static void CheckReconnectButtons(BridgeConfig saved, ToolTip help)
    {
        // Fresh deserialization for each app instance; restored button values must survive reconnection.
        for (var instance = 0; instance < 2; instance++)
        {
            using var card = new ShockerCard(BridgeConfig.Parse(saved.ToJson()).Shockers[0], help);
            var clicks = new List<string>();
            card.ActionRequested += (_, type) => clicks.Add(type);
            var buttons = Get<Button[]>(card, "actions");
            for (var cycle = 0; cycle < 3; cycle++)
            {
                card.SetRuntime(false, false);
                card.SetApiState(null);
                Check(buttons.All(button => !button.Enabled), $"buttons stop before reconnect {instance}/{cycle}");
                card.SetRuntime(true, false);
                card.SetApiState(new ShockerApiStatus { Key = card.Device.Key, State = "Connected", Detail = "Fixture connected" });
                card.Width = cycle % 2 == 0 ? 154 : 243;
                card.Expanded = cycle % 2 == 0;
                Check(buttons.All(button => button.Enabled), $"buttons enable after restart/reconnect and layout {instance}/{cycle}");
                foreach (var button in buttons) Call(button, "OnClick", EventArgs.Empty);
                Check(clicks.TakeLast(3).SequenceEqual(new[] { "Shock", "Vibrate", "Sound" }),
                    $"all button handlers survive reconnect {instance}/{cycle}");
            }
        }
    }
    private static void CheckNameSync(BridgeConfig saved, string path)
    {
        using var form = new OverlayForm(saved.Clone(), initialSaveConfig: settings => settings.Save(path));
        var card = Get<List<ShockerCard>>(form, "shockerCards")[0];
        var before = card.Device.Clone();
        void IncomingName(string name, string? target = null) => Call(form, "HandleBridgeLine", "bridge", "@name " + JsonSerializer.Serialize(new
        { kind = "shocker-name", key = card.Device.Key, uuid = target ?? card.Device.Uuid, name }));
        void RenameResult(string id, string name, bool success) => Call(form, "HandleBridgeLine", "bridge", "@rename " + JsonSerializer.Serialize(new
        { kind = "shocker-rename", key = card.Device.Key, uuid = card.Device.Uuid, name, requestId = id, success, detail = success ? "Name saved to OpenShock." : "Fixture permission denied." }));
        void Request(string id, string name)
        {
            var type = typeof(OverlayForm).GetNestedType("NameOperation", BindingFlags.NonPublic)!;
            Get<IDictionary>(form, "nameRequests")[card.Device.Key] = Activator.CreateInstance(type, id, card.Device.Uuid, name)!;
        }
        IncomingName("Name from app");
        var restored = BridgeConfig.Parse(File.ReadAllText(path));
        Check(card.Device.Name == "Name from app" && restored.Shockers[0].Name == card.Device.Name && !card.Device.NameSyncPending,
            "incoming OpenShock name updates the panel and disk without outgoing intent");
        Check(card.Device.ManualMaxIntensity == before.ManualMaxIntensity && card.Device.MaxIntensity == before.MaxIntensity &&
            card.Device.ManualMaxDurationMs == before.ManualMaxDurationMs && card.Device.Uuid == before.Uuid,
            "incoming name leaves UUID and both slider sets intact");
        IncomingName("Wrong target", "22222222-2222-2222-2222-222222222222");
        IncomingName("Invalid\nname");
        Check(card.Device.Name == "Name from app", "stale UUID and invalid incoming names are ignored");
        var deferred = Get<Dictionary<string, (string Uuid, string Name)>>(form, "deferredNames");
        deferred[card.Device.Key] = (card.Device.Uuid, "Deferred app name");
        Call(form, "ApplyDeferredName", card);
        Check(card.Device.Name == "Deferred app name", "a deferred app rename applies after leaving an unchanged name field");

        var slider = Get<RangeSlider>(card, "intensity");
        Call(slider, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, slider.Width - 30, 15, 0));
        Get<TextBox>(card, "nameInput").Text = "Edited in panel";
        Call(card, "SaveName");
        Check(card.Device.Name == "Edited in panel" && card.Device.NameSyncPending,
            "local rename survives a pending slider flush and records sync intent");
        restored = BridgeConfig.Parse(File.ReadAllText(path));
        Check(restored.Shockers[0].NameSyncPending && restored.Shockers[0].Name == "Edited in panel",
            "offline rename intent and text survive deserialization");
        IncomingName("Old cloud name");
        deferred[card.Device.Key] = (card.Device.Uuid, "Deferred conflicting name");
        Call(form, "ApplyDeferredName", card);
        Check(card.Device.Name == "Edited in panel", "remote snapshots cannot overwrite a pending local rename");

        const string request = "33333333-3333-3333-3333-333333333333";
        Request(request, card.Device.Name);
        RenameResult("44444444-4444-4444-4444-444444444444", card.Device.Name, true);
        Check(card.Device.NameSyncPending && Get<IDictionary>(form, "nameRequests").Count == 1, "stale rename acknowledgement cannot clear pending intent");
        RenameResult(request, card.Device.Name, true);
        Check(!card.Device.NameSyncPending && !BridgeConfig.Parse(File.ReadAllText(path)).Shockers[0].NameSyncPending,
            "matching successful rename clears the saved pending flag");

        Get<TextBox>(card, "nameInput").Text = "First edit"; Call(card, "SaveName"); Request(request, "First edit");
        Get<TextBox>(card, "nameInput").Text = "Latest edit"; Call(card, "SaveName");
        RenameResult(request, "First edit", true);
        Check(card.Device.Name == "Latest edit" && card.Device.NameSyncPending, "older successful rename cannot replace a newer local edit");
        Request(request, "Latest edit"); RenameResult(request, "Latest edit", false);
        Check(card.Device.NameSyncPending && card.Expanded && Get<Label>(card, "message").Text.Contains("permission denied"),
            "rename failure stays pending and shows the error with retry instructions");

        Get<TextBox>(card, "uuidInput").Text = "22222222-2222-2222-2222-222222222222"; Call(card, "SaveOptions");
        Check(!card.Device.NameSyncPending, "assigning a different UUID discards the old target's rename intent: " + Get<Label>(card, "message").Text);
        var latestName = card.Device.Name;
        typeof(OverlayForm).GetField("stopRequested", Private)!.SetValue(form, true);
        IncomingName("Late name while stopping");
        typeof(OverlayForm).GetField("stopRequested", Private)!.SetValue(form, false);
        typeof(OverlayForm).GetField("restartPending", Private)!.SetValue(form, true);
        IncomingName("Late name while restarting");
        typeof(OverlayForm).GetField("restartPending", Private)!.SetValue(form, false);
        Check(card.Device.Name == latestName, "name frames from a stopping or restarting bridge cannot change the panel");
        var invalid = card.Device.Clone(); invalid.Name = new string('x', 65); invalid.NameSyncPending = true;
        Check(invalid.Validate() is not null, "outgoing names enforce OpenShock's 64-character limit");

        using var failure = new OverlayForm(saved.Clone(), initialSaveConfig: _ => throw new IOException("Fixture disk failure"));
        var failureCard = Get<List<ShockerCard>>(failure, "shockerCards")[0];
        var originalName = failureCard.Device.Name;
        Call(failure, "HandleBridgeLine", "bridge", "@name " + JsonSerializer.Serialize(new
        { kind = "shocker-name", key = failureCard.Device.Key, uuid = failureCard.Device.Uuid, name = "Unsaveable name" }));
        Check(failureCard.Device.Name == originalName && Get<Label>(failureCard, "message").Text.Contains("disk failure"),
            "incoming name persistence failure restores the previous panel name");
    }
    private static void CheckHubPanel(BridgeConfig saved, string path)
    {
        using (var help = new ToolTip())
        using (var card = new ShockerCard(saved.Shockers[0].Clone(), help))
        {
            var name = Get<TextBox>(card, "nameInput"); var neutral = ColorTranslator.FromHtml("#f2f3f5");
            string NameTip() => help.GetToolTip(name) ?? "";
            card.SetRuntime(true, false); card.SetApiState(new ShockerApiStatus { State = "Connected" }); card.SetHubState("Online");
            Check(name.ForeColor == neutral && NameTip().Contains("Shocker power: unknown") && NameTip().Contains("Hub online."),
                "an online hub cannot turn the shocker name green or claim that its power is known");
            Check(NameTip().Contains("Renaming needs Shockers Edit permission"), "power status explanation preserves name-sync guidance");
            card.SetHubState("Offline");
            Check(name.ForeColor == neutral && NameTip().Contains("Hub offline."), "offline hub retains a neutral name and explains the hub state");
            card.SetHubState("Unknown");
            Check(name.ForeColor == neutral && NameTip().Contains("Hub status unknown."), "unknown hub does not imply shocker power");
            card.SetHubState("Online"); card.SetRuntime(false, false);
            Check(name.ForeColor == neutral && NameTip().Contains("Bridge stopped."), "stopped bridge cannot leave a stale online name tooltip");
            card.SetRuntime(true, true);
            Check(name.ForeColor == neutral && NameTip().Contains("Shocker power: unknown"), "Test mode does not imply shocker power");
        }
        using var form = new OverlayForm(saved.Clone(), initialSaveConfig: settings => settings.Save(path));
        typeof(OverlayForm).GetField("apiToken", Private)!.SetValue(form, "fixture-token");
        var hubLabel = Get<Label>(form, "hubStatus");
        var apiLabel = Get<Label>(form, "apiStatus");
        var bridgeLabel = Get<Label>(form, "bridgeStatus");
        var oscLabel = Get<Label>(form, "oscStatus");
        var key = saved.Shockers[0].Key;
        void Status(string state, int total = 1, int online = 0) => Call(form, "HandleBridgeLine", "bridge", "@api " + JsonSerializer.Serialize(new
        {
            kind = "api-status", retryAt = 0,
            hub = new { state, total, online, detail = "Fixture live hub status.", shockers = new[] { new { key, state } } },
            shockers = new[] { new { key, state = "Connected", statusCode = 0, detail = "Fixture verified." } },
        }));
        Status("Online", 1, 1);
        Check(hubLabel.Text.EndsWith("Online") && apiLabel.Text.EndsWith("Connected"), "hub and API status display independently");
        Status("Offline");
        Check(hubLabel.Text.EndsWith("Offline") && hubLabel.ForeColor.R > hubLabel.ForeColor.G && apiLabel.Text.EndsWith("Connected"),
            "an offline hub is red without claiming the API is broken");
        Status("Mixed", 2, 1);
        Check(hubLabel.Text.EndsWith("1/2 online") && (Get<ToolTip>(form, "statusHelp").GetToolTip(hubLabel) ?? "").Contains(saved.Shockers[0].Name),
            "multiple hubs show an online count with per-shocker hover details");
        Status("Unknown");
        Check(hubLabel.Text.EndsWith("Unknown"), "lost live status cannot show stale online");
        typeof(OverlayForm).GetField("stopRequested", Private)!.SetValue(form, true);
        Status("Online", 1, 1); Call(form, "RefreshApiSetupStatus", false);
        Check(hubLabel.Text.EndsWith("Stopped"), "late hub frames cannot restore online during stop");
        typeof(OverlayForm).GetField("stopRequested", Private)!.SetValue(form, false);
        Status("Online", 1, 1);
        Call(form, "SetBridgeStatus", "Running", ColorTranslator.FromHtml("#10a37f"));
        Call(form, "SetOscStatus", "Listening", ColorTranslator.FromHtml("#10a37f"));
        foreach (var width in new[] { 1040, 790 })
        {
            form.Size = new Size(width, 760);
            form.CreateControl(); form.PerformLayout(); Call(form, "ArrangeActivity");
            var row = (TableLayoutPanel)hubLabel.Parent!.Parent!;
            // Render detached controls; a hidden parent form defers child docking on resize.
            var parent = (TableLayoutPanel)row.Parent!;
            var rowSize = row.Size;
            parent.Controls.Remove(row); row.Dock = DockStyle.None; row.Size = rowSize; row.Visible = true;
            row.PerformLayout();
            using var bitmap = new Bitmap(row.Width, row.Height);
            row.DrawToBitmap(bitmap, row.ClientRectangle);
            bitmap.Save(Path.Combine(AppContext.BaseDirectory, $"hub-row-{width}.png"));
            var panels = new[] { bridgeLabel, apiLabel, hubLabel, oscLabel }.Select(label => label.Parent!).ToArray();
            Check(panels.Max(panel => panel.Width) - panels.Min(panel => panel.Width) <= 1 &&
                panels[0].Left == 0 && panels[^1].Right == row.ClientSize.Width &&
                panels.Zip(panels.Skip(1)).All(pair => pair.Second.Left - pair.First.Right == 12) && panels.All(panel => panel.Height == 91),
                "four equal panels keep the existing row footprint and gaps " + width + ": " + row.ClientSize.Width + " " + string.Join(", ", panels.Select(panel => panel.Bounds.ToString())));
            foreach (var panel in panels)
            {
                var header = panel.Controls.OfType<TableLayoutPanel>().Single();
                var heading = header.Controls.OfType<Label>().Single();
                var icon = header.Controls.OfType<HelpIcon>().Single();
                Check(heading.Right <= icon.Left && icon.Right <= header.ClientSize.Width &&
                    TextRenderer.MeasureText(heading.Text, heading.Font).Width <= heading.Width,
                    "status heading and question mark fit " + width + " " + panel.AccessibleName + ": " + heading.Text + " " + heading.Bounds + " icon " + icon.Bounds + " header " + header.ClientSize + " measured " + TextRenderer.MeasureText(heading.Text, heading.Font).Width);
            }
            row.Dock = DockStyle.Fill; parent.Controls.Add(row, 0, 1);
        }
    }

    private static void CheckAutomaticApply(ShockerConfig fixture, ToolTip help)
    {
        using (var card = new ShockerCard(fixture.Clone(), help) { Expanded = true })
        {
            card.SettingsChanged += (_, next) => card.ApplyDevice(next);
            var requests = new List<(string Id, int Intensity, int Duration)>();
            card.ApplySharesRequested += (_, id) => requests.Add((id, card.Device.MaxIntensity, card.Device.MaxDurationMs));
            card.SetRuntime(true, false);
            Change(card, "otherIntensity", 0, 39);
            Pump(250);
            Change(card, "otherDuration", 300, 1900);
            Pump(450);
            Check(requests.Count == 0, "quick bottom adjustments wait for one debounce");
            Pump(300);
            Check(requests.Count == 1 && requests[0].Intensity == 40 && requests[0].Duration == 2000,
                "bottom maximums auto-apply together with the latest saved values");
            card.SetShareResult(requests[0].Id, true, "Applied to fixture shares.");
            Change(card, "intensity", 0, 69);
            Change(card, "duration", 300, 3900);
            Pump(750);
            Check(requests.Count == 1, "top sliders never request OpenShock share writes");
            var lower = Get<RangeSlider>(card, "otherIntensity");
            Call(lower, "OnKeyDown", new KeyEventArgs(Keys.Down));
            Call(lower, "OnKeyDown", new KeyEventArgs(Keys.Right));
            Pump(750);
            Check(requests.Count == 1 && card.Device.MinIntensity == 1,
                "bottom minimum saves locally without an unnecessary share write");

            var slider = Get<RangeSlider>(card, "otherDuration");
            card.CreateControl(); slider.CreateControl();
            var upperX = 8 + (slider.Width - 16) * (slider.Upper - slider.Minimum) / (slider.Maximum - slider.Minimum);
            Call(slider, "OnMouseDown", new MouseEventArgs(MouseButtons.Left, 1, upperX, 15, 0));
            Call(slider, "OnMouseMove", new MouseEventArgs(MouseButtons.Left, 0, 150, 15, 0));
            Pump(950);
            Check(slider.IsDragging && requests.Count == 1 && card.Device.MaxDurationMs == slider.Upper,
                "a held drag saves locally but never auto-applies mid-drag");
            Call(slider, "OnMouseUp", new MouseEventArgs(MouseButtons.Left, 1, 150, 15, 0));
            Pump(750);
            Check(requests.Count == 2 && !slider.IsDragging, "releasing the bottom drag triggers auto-apply after the pause");

            Change(card, "otherIntensity", 1, 49);
            Change(card, "otherIntensity", 1, 59);
            Pump(750);
            Check(requests.Count == 2, "new edits cannot overlap an in-flight share update");
            card.SetShareResult(requests[1].Id, true, "Previous fixture update completed.");
            Pump(750);
            Check(requests.Count == 3 && requests[2].Intensity == 60 && requests[2].Id != requests[1].Id,
                "one fresh request applies the latest edits after the old update finishes");
            card.SetShareResult(requests[1].Id, true, "Stale acknowledgement.");
            Check(!Get<Button>(card, "applyShares").Enabled, "stale acknowledgement cannot unlock the current request");
            card.SetShareResult(requests[2].Id, false, "Not applied: fixture permission error.");
            Pump(750);
            Check(requests.Count == 3 && Get<Label>(card, "shareMessage").Text.Contains("permission error") &&
                Get<Button>(card, "applyShares").Enabled, "failed auto-apply is shown with manual retry and no automatic retry");

            Change(card, "otherIntensity", 1, 54);
            card.SetRuntime(false, false);
            card.SetRuntime(true, false);
            Pump(750);
            Check(requests.Count == 3, "stopping and restarting cancels a queued update without replay");
            Change(card, "otherIntensity", 1, 44);
            card.PrepareToClose();
            Change(card, "otherIntensity", 1, 34);
            Pump(750);
            Check(requests.Count == 3 && card.Device.MaxIntensity == 35, "closing keeps local saves but cancels automatic API writes");
        }
        foreach (var mode in new[] { "stopped", "test", "missing UUID" })
        {
            var device = fixture.Clone();
            if (mode == "missing UUID") device.Uuid = "";
            using var card = new ShockerCard(device, help);
            var requests = 0;
            card.SettingsChanged += (_, next) => card.ApplyDevice(next);
            card.ApplySharesRequested += (_, _) => requests++;
            card.SetRuntime(mode != "stopped", mode == "test");
            Change(card, "otherIntensity", 0, 44);
            Pump(750);
            Check(requests == 0 && card.Device.MaxIntensity == 45 && Get<Label>(card, "shareMessage").Text.StartsWith("Saved locally"),
                "blocked auto-apply saves locally and reports why: " + mode);
            var configured = card.Device.Clone(); configured.Uuid = fixture.Uuid;
            card.ApplyDevice(configured); card.SetRuntime(true, false);
            Pump(750);
            Check(requests == 0, "becoming ready does not replay a blocked edit: " + mode);
        }
        using (var card = new ShockerCard(fixture.Clone(), help))
        {
            var requests = 0;
            card.ApplySharesRequested += (_, _) => requests++;
            card.SettingsChanged += (_, _) => card.ShowError("Fixture save failed.");
            card.SetRuntime(true, false);
            Change(card, "otherIntensity", 0, 49);
            Pump(750);
            Check(requests == 0 && card.Device.MaxIntensity == fixture.MaxIntensity,
                "a failed local save cannot trigger an OpenShock write");
        }
        var disposedRequests = 0;
        var disposed = new ShockerCard(fixture.Clone(), help);
        disposed.SettingsChanged += (_, next) => disposed.ApplyDevice(next);
        disposed.ApplySharesRequested += (_, _) => disposedRequests++;
        disposed.SetRuntime(true, false);
        Change(disposed, "otherIntensity", 0, 49);
        disposed.Dispose();
        Pump(750);
        Check(disposedRequests == 0, "disposing a card cancels its pending timer");
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
}
