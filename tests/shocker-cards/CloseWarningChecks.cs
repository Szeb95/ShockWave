using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using ShockWave.Overlay;
using ShockWave.Overlay.Sps;
using ShockWave.Overlay.Toys;

internal static class CloseWarningChecks
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;
    private static int passed;
    private static void Check(bool value, string label)
    { if (!value) throw new Exception(label); passed++; Console.WriteLine("PASS " + label); }
    private static void Pump(Func<bool> condition)
    {
        var end = DateTime.UtcNow.AddSeconds(5);
        while (!condition()) { if (DateTime.UtcNow >= end) throw new Exception("Close check timed out"); Application.DoEvents(); Thread.Sleep(5); }
        Application.DoEvents();
    }
    private static T Get<T>(object target, string field) => (T)target.GetType().GetField(field, Private)!.GetValue(target)!;
    private static string Event(string phase, string id = "command-1", int remaining = 1500, string key = "fixture") =>
        JsonSerializer.Serialize(new { kind = "shocker-activity", key, id, phase, remainingMs = remaining });
    private static void Bridge(OverlayForm form, string json) => typeof(OverlayForm).GetMethod("HandleBridgeLine", Private)!.Invoke(form, ["bridge", "@activity " + json]);
    public static void Run()
    {
        Tracker(); Dialog(); FormClose(); ToyClose(); OfflineToyClose(); ConnectedIdleToyClose();
        Console.WriteLine($"Closing warning: {passed} checks passed (fake devices only)");
    }
    private static void Dialog()
    {
        using var dialog = CloseOutputWarning.CreateDialog(new(true, 2));
        _ = dialog.Handle; dialog.PerformLayout();
        var message = dialog.Controls.OfType<Label>().Single();
        var buttons = dialog.Controls.OfType<Button>().ToArray();
        Check(dialog.AcceptButton == dialog.CancelButton && ((Button)dialog.AcceptButton!).DialogResult == DialogResult.Cancel,
            "warning defaults Enter and Escape to Cancel");
        var measured = TextRenderer.MeasureText(message.Text, message.Font, new Size(message.Width, int.MaxValue), TextFormatFlags.WordBreak);
        Check(measured.Height <= message.Height && buttons.All(button => button.Top >= message.Bottom && dialog.ClientRectangle.Contains(button.Bounds)),
            "warning explanation and both buttons fit without overlap");
        Check(buttons.Any(button => button.Text == "Close app" && button.DialogResult == DialogResult.OK), "warning offers explicit Close app action");
        using var host = new Panel { Size = dialog.ClientSize, BackColor = dialog.BackColor, ForeColor = dialog.ForeColor, Font = dialog.Font };
        foreach (var control in dialog.Controls.Cast<Control>().ToArray()) { dialog.Controls.Remove(control); host.Controls.Add(control); }
        _ = host.Handle; host.PerformLayout();
        using var image = new Bitmap(host.Width, host.Height);
        host.DrawToBitmap(image, host.ClientRectangle); image.Save(Path.Combine(AppContext.BaseDirectory, "close-warning.png"));
    }
    private static void Tracker()
    {
        double now = 10;
        var activity = new ShockerOutputActivity(() => now);
        void Put(string phase, string id = "command-1", int remaining = 1500, string key = "fixture") => activity.Receive(Event(phase, id, remaining, key), ["fixture", "second"]);
        Check(activity.ActiveCount == 0, "connected/configured shockers alone do not warn");
        Put("pending", remaining: 9500); Check(activity.ActiveCount == 1, "in-flight live request warns before an HTTP response");
        Put("accepted"); now += 1; Check(activity.ActiveCount == 1, "accepted command remains active through its bounded duration");
        now += .6; Check(activity.ActiveCount == 0, "completed shocker duration removes the warning");
        Put("pending"); Put("rejected"); Check(activity.ActiveCount == 0, "definite rejected request does not warn");
        Put("accepted", "older", 3000); Put("pending", "newer"); Put("rejected", "newer");
        Check(activity.ActiveCount == 1, "rejected request cannot erase an earlier accepted command");
        Put("feedback", "external", 1200, "second"); Check(activity.ActiveCount == 2, "fresh other-controller feedback tracks each configured shocker");
        Put("stop", "stop", 0); Check(activity.ActiveCount == 1, "Stop clears only the target's active duration");
        Put("pending", "pending"); Put("stop", "stop2", 0); Check(activity.ActiveCount == 2, "Stop feedback does not erase a request still in flight");
        Put("uncertain", "pending", 2000); now += 2.1; Check(activity.ActiveCount == 0, "uncertain network result expires without retry or permanent warning");
        Put("pending", remaining: 9500); now += 9.6; Check(activity.ActiveCount == 0, "bridge loss cannot leave a permanent pending warning");
        activity.Receive("{", ["fixture"]); activity.Receive("[]", ["fixture"]);
        Put("accepted", key: "unknown"); Put("accepted", remaining: -1); Put("accepted", remaining: 73536); Put("invented");
        Check(activity.ActiveCount == 0, "malformed, unconfigured and out-of-bound activity is ignored");
    }
    private static OverlayForm Form(LocalToysPage? local = null, bool dryRun = false) => new(new BridgeConfig
        { DryRun = dryRun, Shockers = [new() { Key = "fixture", Name = "Fixture shocker" }] },
        initialSaveConfig: _ => { }, initialLocalToys: local,
        initialSpsStore: new SpsStore(Path.Combine(AppContext.BaseDirectory, "close-fixture-sps.json")));
    private static void FormClose()
    {
        using (var idle = Form())
        {
            var prompts = 0; idle.ConfirmActiveClose = _ => { prompts++; return false; };
            _ = idle.Handle; idle.Close(); Pump(() => idle.IsDisposed);
            Check(prompts == 0, "inactive actual Form.Close exits without a warning");
        }
        using var form = Form(); _ = form.Handle;
        var count = 0; CloseOutputState? shown = null;
        form.ConfirmActiveClose = state => { count++; shown = state; return false; };
        Bridge(form, Event("accepted")); form.Close();
        Check(!form.IsDisposed && count == 1 && shown is { ToysActive: false, ShockersActive: 1 }, "active shocker close displays a cancelable warning");
        Check(!Get<bool>(form, "closing") && !Get<bool>(form, "localToysClosing") && !Get<bool>(form, "localToysClosed"), "Cancel happens before any shutdown begins");
        Check(!Get<bool>(Get<SpsPage>(form, "spsPage"), "closed"), "Cancel keeps SPS monitoring available");
        Check(!Get<bool>(Get<List<ShockerCard>>(form, "shockerCards")[0], "closing"), "Cancel keeps card save handlers available");
        form.Close(); Check(count == 2 && !form.IsDisposed, "a later close attempt warns again after Cancel");
        form.ConfirmActiveClose = state => { count++; return true; };
        form.Close(); Pump(() => form.IsDisposed);
        Check(count == 3, "confirmed async cleanup closes without a second warning");
        Check(Get<bool>(form, "localToysClosed"), "confirmed close completes existing Bluetooth cleanup");
    }
    private static void ToyClose()
    {
        var fake = new FakeAdapter();
        using var local = new LocalToysPage(_ => fake);
        typeof(LocalToysPage).GetField("sessionControlStore", Private)!.SetValue(local,
            new LocalToyControlStore(Path.Combine(AppContext.BaseDirectory, "close-fixture-preferences.json")));
        using var form = Form(local); _ = form.Handle;
        local.StartRememberedConnections(); Pump(() => local.Controller is not null && !local.Busy);
        var controller = local.Controller!;
        controller.Request("fixture", "v1", 60); Pump(() => controller.State("fixture", "v1").Active);
        Check(!controller.HasPotentialOutput, "Test mode preview is excluded from potential physical output");
        controller.Stop(); local.SetTestMode(false);
        Check(!controller.HasPotentialOutput, "connected and ready toy at zero has no potential output");
        controller.Request("fixture", "depth", 60); Pump(() => fake.Writes.Any(write => write.Channel == "depth" && write.Value > 0));
        Check(!controller.HasPotentialOutput, "saved depth/position setting alone is not a running motor");
        controller.Request("fixture", "v1", 60); Pump(() => fake.Writes.Any(write => write.Channel == "v1" && write.Value > 0));
        Check(controller.HasPotentialOutput, "actual positive native submission warns");
        var prompts = 0;
        form.ConfirmActiveClose = state => { prompts++; Check(state.ToysActive, "toy warning identifies local output"); return false; };
        form.Close();
        Check(!form.IsDisposed && prompts == 1 && controller.State("fixture", "v1") is { Armed: true, RequestedPercent: 60 }, "Cancel preserves live toy input and arming");
        Check(!fake.Disposed && !Get<bool>(Get<SpsPage>(form, "spsPage"), "closed"), "Cancel preserves adapter and SPS lifecycle");
        fake.Simulated = true; Check(!controller.HasPotentialOutput, "simulated motor output is excluded from the warning"); fake.Simulated = false;
        fake.BlockZero = true; controller.Request("fixture", "v1", 0); Pump(() => fake.ZeroEntered);
        Check(controller.HasPotentialOutput, "zero submission still in flight warns even when requested input is zero");
        fake.ZeroRelease.Set(); Pump(() => !controller.HasPotentialOutput);
        Check(!controller.HasPotentialOutput, "successful zero clears potential-output state");
        fake.BlockZero = false; fake.BlockPositive = true;
        controller.Request("fixture", "v1", 60); Pump(() => fake.PositiveEntered);
        Check(controller.HasPotentialOutput && controller.State("fixture", "v1").SentPercent == 0, "positive submission in flight warns before a native return");
        controller.Request("fixture", "v1", 0); fake.PositiveRelease.Set(); Pump(() => !controller.HasPotentialOutput);
        Check(!controller.HasPotentialOutput, "late positive completion is followed by zero before potential-output state clears");
        fake.BlockPositive = false; fake.FailZero = true;
        controller.Request("fixture", "v1", 60); Pump(() => controller.State("fixture", "v1").SentPercent > 0);
        controller.Request("fixture", "v1", 0); Pump(() => controller.State("fixture", "v1").Detail.StartsWith("Stop could not"));
        Check(controller.HasPotentialOutput, "failed zero retains the physical-stop-unconfirmed warning");
        Bridge(form, Event("accepted"));
        form.ConfirmActiveClose = state => { prompts++; Check(state is { ToysActive: true, ShockersActive: 1 }, "one close warning combines toy and shocker activity"); return true; };
        form.Close(); Pump(() => form.IsDisposed);
        Check(prompts == 2 && fake.Disposed && !controller.State("fixture", "v1").Armed, "confirmed toy close disarms and disposes exactly once");
    }
    private static void OfflineToyClose()
    {
        foreach (var (state, present, failZero) in new[] {
            (ToyConnectionState.Disconnected, true, false),
            (ToyConnectionState.Unavailable, true, true),
            (ToyConnectionState.Disconnected, false, true) })
        {
            var fake = new FakeAdapter();
            using var local = new LocalToysPage(_ => fake);
            typeof(LocalToysPage).GetField("sessionControlStore", Private)!.SetValue(local,
                new LocalToyControlStore(Path.Combine(AppContext.BaseDirectory, "close-fixture-preferences.json")));
            using var form = Form(local); _ = form.Handle;
            local.StartRememberedConnections(); Pump(() => local.Controller is not null && !local.Busy);
            local.SetTestMode(false); var controller = local.Controller!;
            controller.Request("fixture", "v1", 60); Pump(() => controller.State("fixture", "v1").SentPercent > 0);
            if (failZero)
            {
                fake.FailZero = true; controller.Request("fixture", "v1", 0);
                Pump(() => controller.State("fixture", "v1").Detail.StartsWith("Stop could not"));
                Check(controller.HasPotentialOutput, "connected stop failure still warns before loss " + present);
            }
            fake.Lose(state, present);
            Check(!controller.HasPotentialOutput, "offline or removed toy history does not warn " + state + "/" + present);
            var prompts = 0; form.ConfirmActiveClose = _ => { prompts++; return false; };
            form.Close(); Pump(() => form.IsDisposed);
            Check(prompts == 0 && fake.Disposed, "actual close with no connected toy finishes without a warning " + state + "/" + present);
        }
    }
    private static void ConnectedIdleToyClose()
    {
        var fake = new FakeAdapter();
        using var local = new LocalToysPage(_ => fake);
        using var form = Form(local); _ = form.Handle;
        local.StartRememberedConnections(); Pump(() => local.Controller is not null && !local.Busy);
        var prompts = 0; CloseOutputState? shown = null;
        form.ConfirmActiveClose = state => { prompts++; shown = state; return false; };
        form.Close();
        Check(!form.IsDisposed && prompts == 1 && shown is { ToysActive: true } && local.Controller!.TestMode,
            "connected physical toy at zero warns in Test mode");
        Check(!fake.Disposed && fake.Writes.IsEmpty && !Get<bool>(form, "localToysClosing"), "Cancel preserves idle connection before cleanup");
        local.SetTestMode(false); form.Close();
        Check(!form.IsDisposed && prompts == 2 && !local.Controller!.HasPotentialOutput, "connected physical toy at zero warns in live mode");
        fake.Lose(ToyConnectionState.Disconnected, true); form.Close(); Pump(() => form.IsDisposed);
        Check(prompts == 2 && fake.Disposed, "disconnecting the idle toy removes its closing warning");
        var simulation = new FakeAdapter { Simulated = true };
        using var simulatedLocal = new LocalToysPage(_ => simulation);
        using var simulatedForm = Form(simulatedLocal); _ = simulatedForm.Handle;
        simulatedLocal.StartRememberedConnections(); Pump(() => simulatedLocal.Controller is not null && !simulatedLocal.Busy);
        var simulatedPrompts = 0; simulatedForm.ConfirmActiveClose = _ => { simulatedPrompts++; return false; };
        simulatedForm.Close(); Pump(() => simulatedForm.IsDisposed);
        Check(simulatedPrompts == 0, "connected simulated toy does not cause a closing warning");
    }
    private sealed class FakeAdapter : IRememberedToyAdapter, ILocalToyOutput
    {
        public event Action? Changed;
        public event Action<string>? OutputFailed { add { } remove { } }
        public bool Disposed, BlockZero, FailZero, BlockPositive, Simulated;
        private ToyConnectionState connectionState = ToyConnectionState.Connected;
        private bool present = true;
        public void Lose(ToyConnectionState state, bool remainsInInventory)
        { connectionState = state; present = remainsInInventory; Changed?.Invoke(); }
        public volatile bool ZeroEntered, PositiveEntered;
        public ManualResetEventSlim ZeroRelease { get; } = new(false);
        public ManualResetEventSlim PositiveRelease { get; } = new(false);
        public ConcurrentQueue<(string Channel, int Value)> Writes { get; } = new();
        public ToyAdapterSnapshot Snapshot => new(ToyAdapterAvailability.Ready, false, "Fixture adapter", present ? [new(
            "fixture", "fake", "Fixture toy", "Fixture", connectionState,
            [new("v1", ToyCapabilityType.Vibration, "Vibration", 0, 20, 1, true, null),
             new("depth", ToyCapabilityType.Depth, "Depth", 0, 100, 1, true, null)], true, 60, DateTimeOffset.UtcNow, 10, Simulated)] : []);
        public void SendLevel(string key, string channel, int value)
        {
            if (value > 0 && channel == "v1" && BlockPositive)
            { PositiveEntered = true; if (!PositiveRelease.Wait(4000)) throw new TimeoutException(); }
            if (value == 0 && channel == "v1")
            {
                if (BlockZero) { ZeroEntered = true; if (!ZeroRelease.Wait(4000)) throw new TimeoutException(); }
                if (FailZero) throw new IOException("Fixture zero failure");
            }
            Writes.Enqueue((channel, value));
        }
        public Task InitializeAsync(CancellationToken c = default) => Task.CompletedTask;
        public Task EnableAutomaticConnectionsAsync(CancellationToken c = default, Func<bool>? canScan = null) => Task.CompletedTask;
        public Task StartScanAsync(CancellationToken c = default) => Task.CompletedTask;
        public Task StopScanAsync(CancellationToken c = default) => Task.CompletedTask;
        public Task ConnectAsync(string key, CancellationToken c = default) => Task.CompletedTask;
        public Task DisconnectAsync(string key, CancellationToken c = default) => Task.CompletedTask;
        public Task ReadBatteryAsync(string key, CancellationToken c = default) => Task.CompletedTask;
        public Task SetAutoConnectAsync(string key, bool enabled, CancellationToken c = default) => Task.CompletedTask;
        public Task ForgetAsync(string key, CancellationToken c = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() { Disposed = true; ZeroRelease.Set(); PositiveRelease.Set(); return ValueTask.CompletedTask; }
    }
}
