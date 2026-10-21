using System.Diagnostics;
using System.Text.Json;

namespace ShockWave.Overlay;

internal sealed record CloseOutputState(bool ToysActive, int ShockersActive)
{
    public bool HasActivity => ToysActive || ShockersActive > 0;
    public string Message => (ToysActive && ShockersActive > 0 ? "A toy is still connected and a shocker may still be active." :
        ToysActive ? "A toy is still connected." : "A shocker may still be active.") +
        "\n\nClosing disconnects local toys and tries to stop their output. Shocker commands already sent may continue until their timer ends.";
}

// Session-only deadlines survive bridge exit/restart: stopping the process cannot cancel an already sent radio command.
internal sealed class ShockerOutputActivity(Func<double>? clock = null)
{
    private sealed class DeviceActivity
    {
        public double ActiveUntil;
        public readonly Dictionary<string, double> Pending = [];
    }
    private readonly Dictionary<string, DeviceActivity> devices = [];
    private readonly Func<double> now = clock ?? (() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
    public int ActiveCount
    {
        get
        {
            Prune();
            return devices.Count;
        }
    }
    private void Prune()
    {
        var time = now();
        foreach (var (key, device) in devices.ToArray())
        {
            foreach (var id in device.Pending.Where(pair => pair.Value <= time).Select(pair => pair.Key).ToArray()) device.Pending.Remove(id);
            if (device.ActiveUntil <= time && device.Pending.Count == 0) devices.Remove(key);
        }
    }
    public void Receive(string json, IEnumerable<string> configuredKeys)
    {
        if (json.Length > 2048) return;
        try
        {
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 4 });
            var root = document.RootElement;
            var kind = root.GetProperty("kind").GetString();
            var key = root.GetProperty("key").GetString();
            var id = root.GetProperty("id").GetString();
            var phase = root.GetProperty("phase").GetString();
            var remaining = root.GetProperty("remainingMs").GetInt32();
            if (kind != "shocker-activity" || string.IsNullOrEmpty(key) || key.Length > 128 ||
                string.IsNullOrEmpty(id) || id.Length > 64 || !configuredKeys.Contains(key) ||
                remaining is < 0 or > 73535 || phase is not ("pending" or "accepted" or "rejected" or "uncertain" or "feedback" or "stop")) return;
            Prune();
            if (!devices.TryGetValue(key, out var device))
            {
                if (devices.Count >= 256) return;
                devices[key] = device = new();
            }
            if (phase == "pending")
            {
                if (device.Pending.Count < 256) device.Pending[id] = now() + remaining / 1000d;
                else device.ActiveUntil = Math.Max(device.ActiveUntil, now() + remaining / 1000d);
            }
            else if (phase == "stop") device.ActiveUntil = 0;
            else
            {
                device.Pending.Remove(id);
                if (phase != "rejected") device.ActiveUntil = Math.Max(device.ActiveUntil, now() + remaining / 1000d);
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or OverflowException) { }
    }
}

internal static class CloseOutputWarning
{
    public static bool Confirm(CloseOutputState state)
    {
        using var dialog = CreateDialog(state);
        return dialog.ShowDialog(Form.ActiveForm) == DialogResult.OK;
    }
    internal static Form CreateDialog(CloseOutputState state)
    {
        var dialog = new Form
        {
            Text = "Close while devices are active?", ClientSize = new Size(500, 218),
            FormBorderStyle = FormBorderStyle.FixedDialog, StartPosition = FormStartPosition.CenterParent,
            MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false,
            BackColor = Color.FromArgb(43, 45, 49), ForeColor = Color.FromArgb(242, 243, 245), Font = new Font("Segoe UI", 10),
            AutoScaleMode = AutoScaleMode.Dpi,
        };
        var message = new Label { Text = state.Message, Location = new(22, 22), Size = new(456, 122) };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new(254, 160), Size = new(106, 36) };
        var close = new Button { Text = "Close app", DialogResult = DialogResult.OK, Location = new(372, 160), Size = new(106, 36) };
        foreach (var button in new[] { cancel, close })
        {
            button.FlatStyle = FlatStyle.Flat; button.FlatAppearance.BorderSize = 0; button.ForeColor = dialog.ForeColor;
            button.BackColor = button == cancel ? Color.FromArgb(16, 163, 127) : Color.FromArgb(112, 62, 65);
        }
        dialog.Controls.AddRange([message, cancel, close]); dialog.AcceptButton = cancel; dialog.CancelButton = cancel;
        dialog.Shown += (_, _) => cancel.Focus();
        return dialog;
    }
}
