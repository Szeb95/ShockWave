using System.Reflection;
using ShockWave.Overlay;
using ShockWave.Overlay.Toys;

internal static class ToyPanelChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static T Get<T>(object target, string name) => (T)target.GetType().GetField(name, Private)!.GetValue(target)!;
    private static void Click(Control control) => typeof(Control).GetMethod("OnClick", Private)!.Invoke(control, [EventArgs.Empty]);
    private static void Check(bool condition, string message)
    { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); }
    private static void Pump(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(6);
        while (!condition()) { if (DateTime.UtcNow > deadline) throw new Exception("Toy panel check timed out."); Application.DoEvents(); Thread.Sleep(5); }
        Application.DoEvents();
    }

    public static void Run()
    {
        foreach (var width in new[] { 1040, 790 })
        {
            var local = new LocalToysPage();
            var config = new BridgeConfig { Shockers = [new() { Name = "My shocker" }, new() { Name = "Toy 2" }] };
            using var form = new OverlayForm(config, initialSaveConfig: _ => { }, initialLocalToys: local)
            { Size = new Size(width, width == 790 ? 590 : 760) };
            // Render the detached shell; never show the main form (Shown would start real services).
            var shell = form.Controls.OfType<TableLayoutPanel>().Single(); form.Controls.Remove(shell);
            using var host = new Panel { Size = form.ClientSize }; host.Controls.Add(shell);
            _ = host.Handle; host.PerformLayout(); shell.PerformLayout(); _ = local.Handle;
            local.SourceInput.SelectedIndex = 1;
            Pump(() => !local.Busy && local.Snapshot?.Devices.Count == 4 &&
                local.Snapshot.Devices.All(device => device.ConnectionState == ToyConnectionState.Connected) && local.Cards.Count == 8);
            var toyPage = Get<Panel>(form, "toyPanelPage");
            var layout = Get<TableLayoutPanel>(form, "activityLayout");
            layout.PerformLayout(); local.ArrangeCards();
            var upper = (TableLayoutPanel)Get<Label>(form, "bridgeStatus").Parent!.Parent!;
            var lower = (TableLayoutPanel)Get<Label>(form, "bluetoothStatus").Parent!.Parent!;
            Check(upper.Left == lower.Left && upper.Width == lower.Width && upper.Height == lower.Height &&
                lower.Top - upper.Top == 103 && lower.Controls.Count == 4, "two aligned rows of four status cards " + width);
            foreach (var panel in lower.Controls.OfType<Panel>())
            {
                var header = panel.Controls.OfType<TableLayoutPanel>().Single();
                var heading = header.Controls.OfType<Label>().Single(); var icon = header.Controls.OfType<HelpIcon>().Single();
                Check(heading.Right <= icon.Left && TextRenderer.MeasureText(heading.Text, heading.Font).Width <= heading.Width,
                    "second-row heading fits: " + panel.AccessibleName + " " + width);
            }
            Check(Get<Label>(form, "curveStatus").Text.EndsWith("Inactive") && Get<Label>(form, "resetBluetoothStatus").Text.EndsWith("Reset") &&
                Get<Label>(form, "bluetoothStatus").Text.EndsWith("Simulated"), "honest curve/Bluetooth status and reset action " + width);
            Check(!Descendants(shell).OfType<Button>().Any(button => button.Text.Contains("◉   Local toys")) &&
                Get<Panel>(form, "settingsPage").Contains(local.UpdateRateInput) && !toyPage.Contains(local.UpdateRateInput),
                "old tab removed and update rate belongs to Settings " + width);
            var shocker = Get<List<ShockerCard>>(form, "shockerCards")[0]; var card = local.Cards[0];
            Check(card.Width == shocker.Width && card.Height == shocker.Height && local.Cards[1].Left - card.Right == 12 &&
                local.Cards[3].Top - card.Bottom == 12, "toy card dimensions and gaps match shockers " + width);
            var options = Get<CheckBox>(card, "options");
            Check(options.Location == Get<CheckBox>(shocker, "options").Location && Get<Label>(card, "title").Location == new Point(10, 9) &&
                Get<PercentSlider>(card, "input").Location == Get<RangeSlider>(shocker, "duration").Location &&
                Get<PercentSlider>(card, "sensitivity").Location == Get<RangeSlider>(shocker, "intensity").Location &&
                Get<PercentSlider>(card, "maximum").Parent == Get<Panel>(card, "advanced") &&
                Get<PercentSlider>(card, "maximum").Right == card.Width - 10 &&
                Get<PercentSlider>(card, "maximum").Width == Get<PercentSlider>(card, "input").Width &&
                !card.Controls.OfType<Button>().Any(button => button.Text == "Stop") &&
                Get<TextBox>(shocker, "nameInput").Font.Equals(Get<Label>(card, "title").Font) &&
                Get<TextBox>(shocker, "modelInput").Location == new Point(10, 31),
                "matching name font, unchanged model position, Max slider in Options, and no compact Stop " + width);
            var scan = Get<Label>(form, "scanStatus"); Click(scan.Parent!);
            Pump(() => !local.Busy && local.IsScanning && scan.Text.EndsWith("Scanning"));
            Click(scan); Pump(() => !local.Busy && !local.IsScanning && scan.Text.EndsWith("Inactive"));
            Check(!local.IsScanning && local.CanToggleScan, "scan panel starts and stops scanning " + width);
            var reset = Get<Label>(form, "resetBluetoothStatus");
            var activeCard = local.Cards.First(candidate => candidate.ChannelId.Length > 0 && Get<PercentSlider>(candidate, "input").Enabled);
            var key = activeCard.DeviceKey; var channel = activeCard.ChannelId;
            Get<PercentSlider>(activeCard, "sensitivity").Value = 50;
            Get<PercentSlider>(activeCard, "maximum").Value = 40;
            Get<PercentSlider>(activeCard, "input").Value = 60;
            Pump(() => Get<Label>(activeCard, "state").Text.Contains("Preview 30%"));
            Click(reset.Parent!);
            if (local.Busy) Click(reset);
            Pump(() => !local.Busy && local.Snapshot?.Devices.Count == 4 && local.Cards.Count == 8 &&
                local.Snapshot.Devices.All(device => device.ConnectionState == ToyConnectionState.Connected));
            var restored = local.Cards.Single(candidate => candidate.DeviceKey == key && candidate.ChannelId == channel);
            Check(!ReferenceEquals(activeCard, restored) && Get<PercentSlider>(restored, "input").Value == 0 &&
                Get<PercentSlider>(restored, "sensitivity").Value == 50 && Get<PercentSlider>(restored, "maximum").Value == 40 &&
                reset.Text.EndsWith("Reset"), "eighth panel resets connections idle with saved limits " + width);
            card = local.Cards[0];
            using (var image = new Bitmap(host.Width, host.Height))
            { host.DrawToBitmap(image, host.ClientRectangle); image.Save(Path.Combine(AppContext.BaseDirectory, $"combined-toy-panel-{width}.png")); }
            toyPage.ScrollControlIntoView(card);
            card.Expanded = true; toyPage.ScrollControlIntoView(card);
            Check(local.GridHeight > ToyFunctionRow.CompactHeight * 3 && card.Height == ToyFunctionRow.OptionsHeight && toyPage.VerticalScroll.Visible,
                "expanded toy options remain accessible " + width);
            var originalDock = local.Dock; var originalParent = local.Parent!;
            originalParent.Controls.Remove(local); local.Dock = DockStyle.None; local.Size = new Size(lower.Width, local.GridHeight); local.Visible = true;
            local.ArrangeCards();
            using (var image = new Bitmap(local.Width, local.Height))
            { local.DrawToBitmap(image, local.ClientRectangle); image.Save(Path.Combine(AppContext.BaseDirectory, $"toy-cards-{width}.png")); }
            local.Dock = originalDock; originalParent.Controls.Add(local); ((TableLayoutPanel)originalParent).SetCellPosition(local, new(0, 6));
            Click(Get<Button>(form, "settingsNav"));
            Get<Panel>(form, "settingsPage").Controls.OfType<FlowLayoutPanel>().Single().ScrollControlIntoView(local.UpdateRateInput.Parent!);
            using (var image = new Bitmap(host.Width, host.Height))
            { host.DrawToBitmap(image, host.ClientRectangle); image.Save(Path.Combine(AppContext.BaseDirectory, $"toy-settings-{width}.png")); }
            Check(Get<System.Diagnostics.Process?>(form, "bridge") is null, "no bridge or physical toys used by preview " + width);
            var shutdown = local.ShutdownAsync(); Pump(() => shutdown.IsCompleted); shutdown.GetAwaiter().GetResult();
            host.Controls.Remove(shell); form.Controls.Add(shell);
        }
    }
    private static IEnumerable<Control> Descendants(Control control)
    { foreach (Control child in control.Controls) { yield return child; foreach (var descendant in Descendants(child)) yield return descendant; } }
}
