using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

namespace ShockWave.Overlay.Updates;

internal sealed record UpdateRequest(string Target, string CurrentVersion, string Version, string ManifestHash, int ParentId, long ParentStarted, bool Restart);

internal static class UpdateInstaller
{
    private static string LockName(string target) => "Local\\ShockWave.Update." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(target).TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())));
    internal static bool IsInstalling(string target)
    {
        if (!Mutex.TryOpenExisting(LockName(target), out var mutex)) return false;
        using (mutex)
        {
            try { if (!mutex.WaitOne(0)) return true; }
            catch (AbandonedMutexException) { }
            mutex.ReleaseMutex();
            return false;
        }
    }
    // This is a copy of the already running single-file app, not an executable supplied by the download.
    internal static ProcessStartInfo CreateStart(PreparedAppUpdate update, string target, Version current, bool restart)
    {
        var stage = Path.GetFullPath(update.StageDirectory);
        UpdatePackage.NoLinks(stage);
        if (!Guid.TryParseExact(Path.GetFileName(stage), "N", out _) || update.PayloadDirectory != Path.Combine(stage, "payload"))
            throw new UpdateException("The update staging location is invalid.");
        using var parent = Process.GetCurrentProcess();
        var request = new UpdateRequest(Path.GetFullPath(target), AppUpdater.Display(current), AppUpdater.Display(update.Release.Version),
            UpdatePackage.Hash(Path.Combine(update.PayloadDirectory, UpdatePackage.ManifestName)), parent.Id, parent.StartTime.ToUniversalTime().Ticks, restart);
        var helper = Path.Combine(stage, "ShockWave.Update.exe");
        File.Copy(Environment.ProcessPath ?? throw new UpdateException("The app could not locate its updater."), helper, true);
        var requestPath = Path.Combine(stage, "request.json");
        File.WriteAllText(requestPath, JsonSerializer.Serialize(request, AppUpdater.Json));
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = stage };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(requestPath);
        // Updaters and their newly launched app never inherit provider/session credentials.
        foreach (var key in start.Environment.Keys.Where(k => k.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) || k.Contains("API_KEY", StringComparison.OrdinalIgnoreCase) || k.StartsWith("SHOCKWAVE_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        return start;
    }

    internal static int Run(string requestPath)
    {
        try
        {
            var fullRequest = Path.GetFullPath(requestPath);
            var stage = Path.GetDirectoryName(fullRequest)!;
            if (Path.GetFileName(fullRequest) != "request.json" || !Guid.TryParseExact(Path.GetFileName(stage), "N", out _) ||
                !Path.GetFullPath(Environment.ProcessPath!).Equals(Path.Combine(stage, "ShockWave.Update.exe"), StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("The updater was started from an invalid location.");
            UpdatePackage.NoLinks(fullRequest);
            var request = JsonSerializer.Deserialize<UpdateRequest>(File.ReadAllText(fullRequest), AppUpdater.Json)
                ?? throw new UpdateException("The update request is missing.");
            if (request.ParentId == Environment.ProcessId) throw new UpdateException("The update parent is invalid.");
            try
            {
                using var parent = Process.GetProcessById(request.ParentId);
                if (parent.StartTime.ToUniversalTime().Ticks != request.ParentStarted || !parent.WaitForExit(60000))
                    throw new UpdateException("ShockWave is still running. Close it normally and try again.");
            }
            catch (ArgumentException) { /* The exact parent already exited. */ }
            Apply(stage, request);
            if (request.Restart)
                Process.Start(new ProcessStartInfo(Path.Combine(request.Target, "ShockWave.exe")) { UseShellExecute = false, WorkingDirectory = request.Target });
            return 0;
        }
        catch (Exception e)
        {
            MessageBox.Show("The update could not finish. " + e.Message + "\n\nUse the GitHub release page to download a complete copy if needed.",
                "ShockWave update", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 1;
        }
    }

    internal static void Apply(string stage, UpdateRequest request, Action<int>? afterCopy = null)
    {
        using var installation = new Mutex(false, LockName(request.Target));
        try { if (!installation.WaitOne(0)) throw new UpdateException("Another update is already installing."); }
        catch (AbandonedMutexException) { }
        try { ApplyFiles(stage, request, afterCopy); }
        finally { installation.ReleaseMutex(); }
    }

    private static void ApplyFiles(string stage, UpdateRequest request, Action<int>? afterCopy)
    {
        var target = Path.GetFullPath(request.Target).TrimEnd(Path.DirectorySeparatorChar);
        var fullStage = Path.GetFullPath(stage).TrimEnd(Path.DirectorySeparatorChar);
        UpdatePackage.NoLinks(fullStage); UpdatePackage.NoLinks(target);
        if (fullStage.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            target.StartsWith(fullStage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || fullStage.Equals(target, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("The app folder and update staging folder must be separate.");
        var payload = Path.Combine(fullStage, "payload");
        var current = AppUpdater.Normalize(Version.Parse(request.CurrentVersion));
        var next = AppUpdater.Normalize(Version.Parse(request.Version));
        var manifestPath = Path.Combine(payload, UpdatePackage.ManifestName);
        if (!UpdatePackage.Hash(manifestPath).Equals(request.ManifestHash, StringComparison.OrdinalIgnoreCase)) throw new UpdateException("The staged update file list changed.");
        var manifest = UpdatePackage.ReadManifest(payload);
        if (AppUpdater.Normalize(Version.Parse(manifest.Version)) != next) throw new UpdateException("The staged version changed.");
        UpdatePackage.VerifyPayload(payload, manifest);
        UpdatePackage.CheckDestination(target, manifest, current);
        var backup = Path.Combine(fullStage, "backup");
        if (Directory.Exists(backup)) throw new UpdateException("This update has already been attempted. Download it again.");
        var files = manifest.Files.Select(f => f.Path).Append(UpdatePackage.ManifestName).ToArray();
        // Preflight every replacement before the first write; no other app or worker is killed.
        foreach (var relative in files)
        {
            var destination = UpdatePackage.SafePath(target, relative);
            UpdatePackage.NoLinks(destination);
            if (File.Exists(destination))
            {
                using var file = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
        }
        Directory.CreateDirectory(backup);
        var changed = new List<(string Path, bool Existed)>();
        try
        {
            for (var index = 0; index < files.Length; index++)
            {
                var relative = files[index];
                var destination = UpdatePackage.SafePath(target, relative);
                UpdatePackage.NoLinks(destination);
                var existed = File.Exists(destination);
                if (existed)
                {
                    var saved = UpdatePackage.SafePath(backup, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
                    File.Copy(destination, saved, false);
                }
                changed.Add((relative, existed));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(UpdatePackage.SafePath(payload, relative), destination, true);
                afterCopy?.Invoke(index);
            }
            File.WriteAllText(Path.Combine(fullStage, "result.json"), "{\"success\":true}");
        }
        catch (Exception failure)
        {
            var rollbackFailures = new List<Exception>();
            foreach (var item in changed.AsEnumerable().Reverse())
            {
                try
                {
                    var destination = UpdatePackage.SafePath(target, item.Path);
                    UpdatePackage.NoLinks(destination);
                    if (item.Existed) File.Copy(UpdatePackage.SafePath(backup, item.Path), destination, true);
                    else File.Delete(destination);
                }
                catch (Exception rollback) { rollbackFailures.Add(rollback); }
            }
            if (rollbackFailures.Count > 0) throw new UpdateException("Installation failed and some files could not be restored. Your previous files are retained in " + backup);
            throw new UpdateException("Installation failed; the previous program files were restored. " + failure.Message);
        }
        // Obsolete files are intentionally retained: user-added files and older package content are never deleted.
    }
}
