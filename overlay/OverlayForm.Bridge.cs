using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShockWave.Overlay;

internal sealed partial class OverlayForm
{
    private static string NodePath()
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "node.exe");
        if (File.Exists(bundled)) return bundled;
        var installed = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe");
        return File.Exists(installed) ? installed : "node.exe";
    }

    private void StartBridge()
    {
        if (bridge is { HasExited: false }) return;
        var script = Path.Combine(bridgeDirectory, "bridge.mjs");
        if (!File.Exists(script))
        {
            AppendLog("error", "The packaged bridge files are missing.");
            SetBridgeStatus("Error", ErrorColor);
            return;
        }
        var start = new ProcessStartInfo(NodePath())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = bridgeDirectory,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        start.ArgumentList.Add(script);
        start.ArgumentList.Add(BridgeConfig.ConfigPath);
        start.Environment["OPENSHOCK_TOKEN"] = config.IsPiShock ? "" : apiToken;
        start.Environment["PISHOCK_API_KEY"] = config.IsPiShock ? piShockApiKey : "";
        start.Environment["SHOCKWAVE_EXTERNAL_OSC"] = "1";
        var process = new Process { StartInfo = start, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) =>
        {
            if (args.Data is not null) SafeUi(() => { if (bridge == process) HandleBridgeLine("bridge", args.Data); });
        };
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is not null) SafeUi(() =>
            {
                if (bridge == process) HandleBridgeLine("error", args.Data);
                else if (lastStartedBridge == process) AppendLog("error", args.Data);
            });
        };
        process.Exited += (_, _) => SafeUi(() => BridgeExited(process));
        try
        {
            stopRequested = false;
            apiHealth = null;
            nameRequests.Clear(); failedNames.Clear(); deferredNames.Clear();
            foreach (var card in shockerCards) card.SetApiState(null);
            bridge = process;
            lastStartedBridge = process;
            SetBridgeStatus("Starting", Warning);
            RefreshToyControlStatus();
            process.Start();
            UpdateRemoteGate();
            RefreshApiSetupStatus(connecting: true);
            process.StandardInput.AutoFlush = true;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            SetBridgeStatus("Running", Accent);
            AppendLog("overlay", "PiCtrl bridge started");
            foreach (var card in shockerCards) SyncName(card);
        }
        catch (Exception error)
        {
            bridge = null;
            process.Dispose();
            SetBridgeStatus("Error", ErrorColor);
            RefreshToyControlStatus();
            AppendLog("error", "Could not start bridge: " + error.Message);
        }
    }

    private void StopBridge()
    {
        if (bridge is not { HasExited: false } process) return;
        stopRequested = true;
        foreach (var card in shockerCards) card.SetRuntime(false, config.DryRun);
        SetBridgeStatus("Stopping", Faint);
        apiHealth = null;
        ApplyHubHealth();
        bridgeButton.Enabled = false;
        try { process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { BridgeExited(process); }
        catch (Exception error)
        {
            bridgeButton.Enabled = true;
            AppendLog("error", "Could not stop bridge: " + error.Message);
        }
    }

    private void BridgeExited(Process process)
    {
        if (closing || bridge != process) return;
        var expected = stopRequested;
        bridge = null;
        apiHealth = null;
        nameRequests.Clear(); deferredNames.Clear();
        stopRequested = false;
        bridgeButton.Enabled = true;
        SetBridgeStatus(expected ? "Stopped" : "Error", expected ? Faint : ErrorColor);
        RefreshApiSetupStatus();
        RefreshToyControlStatus();
        AppendLog(expected ? "overlay" : "error", expected ? "Bridge stopped" : "Bridge exited unexpectedly");
        process.Dispose();
        if (restartPending)
        {
            restartPending = false;
            BeginInvoke(StartBridge);
        }
    }

    private void HandleBridgeLine(string source, string line)
    {
        line = Regex.Replace(line, @"^\[\d{1,2}:\d{2}:\d{2}[^\]]*\]\s*", "");
        if (HandlePiShockDevices(line)) return;
        if (line.StartsWith("@activity ", StringComparison.Ordinal))
        {
            if (!closing) shockerActivity.Receive(line[10..], config.ActiveShockers.Select(device => device.Key));
            return;
        }
        if (HandleShockerUpdate(line)) return;
        if (line.StartsWith("@api ", StringComparison.Ordinal))
        {
            if (closing || stopRequested || restartPending) return;
            try
            {
                var snapshot = JsonSerializer.Deserialize<ApiStatusSnapshot>(line[5..],
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (snapshot is not { Kind: "api-status" } || snapshot.Shockers is null)
                    throw new JsonException("Missing API status details");
                apiHealth = snapshot;
                ApplyApiHealth();
            }
            catch (JsonException error) { AppendLog("error", "Could not read API status: " + error.Message); }
            return;
        }
        if (apiHealth is null)
        {
        if (line.Contains("OpenShock logs connected", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("OpenShock logs reconnected", StringComparison.OrdinalIgnoreCase))
            SetApiStatus("Connected", Accent);
        else if (line.Contains("OpenShock HTTP 401", StringComparison.OrdinalIgnoreCase))
            SetApiStatus("Token rejected", ErrorColor);
        else if (line.Contains("OpenShock HTTP 403", StringComparison.OrdinalIgnoreCase))
            SetApiStatus("No access", ErrorColor);
        else if (line.Contains("OpenShock HTTP 404", StringComparison.OrdinalIgnoreCase))
            SetApiStatus("UUID not found", ErrorColor);
        else if (line.Contains("OpenShock log poll failed", StringComparison.OrdinalIgnoreCase) ||
                 line.Contains("OpenShock command failed", StringComparison.OrdinalIgnoreCase))
            SetApiStatus("Error", ErrorColor);
        else if (line.Contains("API setup: token missing", StringComparison.OrdinalIgnoreCase))
            SetApiStatus("Token needed", Warning);
        else if (line.Contains("API setup: shocker UUID missing", StringComparison.OrdinalIgnoreCase))
            SetApiStatus("UUID needed", Warning);
        else if (line.Contains("API setup: checking connection", StringComparison.OrdinalIgnoreCase))
            SetApiStatus("Connecting", Warning);
        }
        if (line.Contains("Listening for VRChat OSC", StringComparison.OrdinalIgnoreCase))
            RefreshToyControlStatus();
        else if (line.Contains("OSC listener error", StringComparison.OrdinalIgnoreCase))
            RefreshToyControlStatus();
        var category = source == "error" || line.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("error:", StringComparison.OrdinalIgnoreCase) ? "error"
            : line.Contains("OpenShock", StringComparison.OrdinalIgnoreCase) || line.Contains("PiShock", StringComparison.OrdinalIgnoreCase) ||
              line.Contains("API log", StringComparison.OrdinalIgnoreCase) ? "api"
            : line.Contains("OSC", StringComparison.OrdinalIgnoreCase) ||
              line.Contains("TEST ONLY", StringComparison.OrdinalIgnoreCase) ? "osc"
            : "bridge";
        AppendLog(category, line);
    }

    private void ApplyApiHealth()
    {
        ApplyHubHealth();
        if (apiHealth is null) return;
        foreach (var card in shockerCards)
            card.SetApiState(apiHealth.Shockers.FirstOrDefault(status => status.Key == card.Device.Key));
        if (config.IsPiShock && !config.ActiveShockers.Any(s => s.Uuid.Length > 0))
        {
            var connection = apiHealth.Connection;
            SetApiStatus(connection?.State == "Connected" ? "Connected" : connection?.State == "Error" ? connection.Label.Replace("API ", "") : ActiveApiToken.Length == 0 ? "Key needed" : "Connecting",
                connection?.State == "Connected" ? Accent : Warning);
            statusHelp.SetToolTip(apiStatus, connection?.Detail ?? "Enter PiShock credentials, save Settings, and start the bridge.");
            return;
        }
        var statuses = config.ActiveShockers.Where(device => device.Uuid.Length > 0)
            .Select(device => apiHealth.Shockers.FirstOrDefault(status => status.Key == device.Key)
                ?? new ShockerApiStatus { Key = device.Key }).ToList();
        var details = config.ActiveShockers.Select(device =>
            device.Name + ": " + (apiHealth.Shockers.FirstOrDefault(status => status.Key == device.Key)?.Detail
                ?? (device.Uuid.Length > 0 ? "Checking connection" : "Enter UUID under Options")));
        statusHelp.SetToolTip(apiStatus, string.Join("\n\n", details) + "\n\nClick to show errors on the Log page.");
        if (ActiveApiToken.Length == 0) { SetApiStatus(config.IsPiShock ? "Key needed" : "Token needed", Warning); return; }
        if (statuses.Count == 0) { SetApiStatus(config.IsPiShock ? "Add shocker" : "UUID needed", Warning); return; }
        var connected = statuses.Count(status => status.State == "Connected");
        var errors = statuses.Where(status => status.State is "Error" or "RateLimited").ToList();
        if (apiHealth.RetryAt > DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
            SetApiStatus("Waiting to retry", Warning);
        else if (errors.Count > 0 && connected > 0)
            SetApiStatus($"{connected}/{statuses.Count} connected", Warning);
        else if (errors.Count > 0)
            SetApiStatus(errors.Select(status => status.Label).Distinct().Count() == 1
                ? errors[0].Label.Replace("API ", "", StringComparison.Ordinal) : "Error", ErrorColor);
        else if (connected < statuses.Count)
            SetApiStatus("Connecting", Warning);
        else
            SetApiStatus(statuses.Count == 1 ? "Connected" : $"Connected ({statuses.Count})", Accent);
    }

    private void SetBridgeStatus(string value, Color color)
    {
        bridgeStatus.Text = "●  " + value;
        bridgeStatus.ForeColor = color;
        bridgeButton.Text = value is "Running" or "Starting" ? "Stop bridge" : "Start bridge";
        foreach (var card in shockerCards) card.SetRuntime(value == "Running", config.DryRun);
    }

    private void RefreshApiSetupStatus(bool connecting = false)
    {
        ApplyHubHealth();
        if (ActiveApiToken.Length == 0) SetApiStatus(config.IsPiShock ? "Key needed" : "Token needed", Warning);
        else if (!config.IsPiShock && !config.ActiveShockers.Any(s => s.Uuid.Length > 0)) SetApiStatus("UUID needed", Warning);
        else if (bridge is not { HasExited: false }) SetApiStatus("Bridge stopped", Faint);
        else if (connecting) SetApiStatus("Connecting", Warning);
        else ApplyApiHealth();
    }

    private void SetApiStatus(string value, Color color)
    {
        apiStatus.Text = "●  " + value;
        apiStatus.ForeColor = color;
    }

    private void SetOscStatus(string value, Color color)
    {
        oscStatus.Text = "●  " + value;
        oscStatus.ForeColor = color;
    }
}
