using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ShockWave.Overlay;
using ShockWave.Overlay.Sps;
using ShockWave.Overlay.Updates;

internal static class Program
{
    private static int assertions;
    private static string root = "";
    private static string exe = "";
    private const BindingFlags Hidden = BindingFlags.Instance | BindingFlags.NonPublic;
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        root = Path.Combine(AppContext.BaseDirectory, "fixtures", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        // Never launch the production form or any hardware. This is only a versioned PE for package fixtures.
        exe = typeof(OverlayForm).Assembly.Location;
        try
        {
            if (args.Length == 3 && args[0] == "--inspect-package")
            {
                var inspected = UpdatePackage.Extract(args[1], Path.Combine(root, "published-package"), Version.Parse(args[2]));
                Console.WriteLine($"Verified published package: {inspected.Manifest.Files.Count} files; version {inspected.Manifest.Version}.");
                return 0;
            }
            if (args.Length == 2 && args[0] == "--published-helper") { PublishedHelper(args[1]); Console.WriteLine($"PASS: {assertions} published helper checks."); return 0; }
            ReleaseValidation();
            PackageValidation();
            Installation();
            NetworkAndUi();
            Console.WriteLine($"PASS: {assertions} update checks. Fixtures: {root}");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }
    private static void Check(bool value, string message) { assertions++; if (!value) throw new Exception(message); }
    private static void Reject(Action action, string message)
    {
        try { action(); } catch (Exception e) when (e is IOException or ArgumentException or JsonException or InvalidOperationException or KeyNotFoundException) { Check(true, message); return; }
        throw new Exception("Was not rejected: " + message);
    }
    private static string DirectoryFor(string name) { var path = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(path); return path; }
    private static Version Current => new(0, 7, 7);
    private static Version Next => new(0, 7, 8);
    private static byte[] Release(byte[]? zip = null)
    {
        zip ??= [1, 2, 3];
        return JsonSerializer.SerializeToUtf8Bytes(new { draft = false, prerelease = false, tag_name = "v0.7.8", html_url = AppUpdater.Repository + "/releases/tag/v0.7.8",
            assets = new[] { new { name = "ShockWave-0.7.8-win-x64.zip", state = "uploaded", size = zip.Length,
                digest = "sha256:" + Convert.ToHexString(SHA256.HashData(zip)), browser_download_url = AppUpdater.Repository + "/releases/download/v0.7.8/ShockWave-0.7.8-win-x64.zip" } } });
    }
    private static void ReleaseValidation()
    {
        var release = AppUpdater.ParseRelease(Release(), Current)!;
        Check(release.Version == AppUpdater.Normalize(Next), "new stable numeric release is offered");
        Check(AppUpdater.ParseRelease(Release(), Next) is null && AppUpdater.ParseRelease(Release(), new(1, 0, 0)) is null, "equal and older versions are not offered");
        foreach (var property in new[] { "draft", "prerelease" })
        {
            var node = JsonNode.Parse(Release())!; node[property] = true;
            Check(AppUpdater.ParseRelease(Encoding.UTF8.GetBytes(node.ToJsonString()), Current) is null, "exclude " + property);
        }
        foreach (var invalid in new[] { "v0.7.8-beta", "latest", "v0.7.8;start" })
        {
            var node = JsonNode.Parse(Release())!; node["tag_name"] = invalid;
            Reject(() => AppUpdater.ParseRelease(Encoding.UTF8.GetBytes(node.ToJsonString()), Current), "invalid version");
        }
        foreach (var url in new[] { "http://github.com/Szeb95/ShockWave/releases/download/v0.7.8/ShockWave-0.7.8-win-x64.zip", "https://github.com/SomeoneElse/Repo/releases/download/v0.7.8/ShockWave-0.7.8-win-x64.zip", "https://github.com.evil.invalid/Szeb95/ShockWave/releases/download/v0.7.8/ShockWave-0.7.8-win-x64.zip" })
        {
            var node = JsonNode.Parse(Release())!; node["assets"]![0]!["browser_download_url"] = url;
            Reject(() => AppUpdater.ParseRelease(Encoding.UTF8.GetBytes(node.ToJsonString()), Current), "untrusted asset URL");
        }
        foreach (var digest in new[] { "", "sha1:" + new string('a', 40), "sha256:short" })
        {
            var node = JsonNode.Parse(Release())!; node["assets"]![0]!["digest"] = digest;
            Reject(() => AppUpdater.ParseRelease(Encoding.UTF8.GetBytes(node.ToJsonString()), Current), "missing or invalid asset digest");
        }
        Check(!AppUpdater.TrustedTransport(new("https://github.com@evil.invalid/a")) && !AppUpdater.TrustedTransport(new("https://github.com:444/a")), "credentials and nonstandard ports cannot evade transport trust");
        Check(AppUpdater.Normalize(new(0, 7, 0)) == AppUpdater.Normalize(new(0, 7, 0, 0)), "three and four part versions compare consistently");
    }
    private static UpdateManifest MakePackage(string directory, string version)
    {
        foreach (var path in new[] { "ShockWave.exe", "node.exe", "bridge/bridge.mjs", "bridge/remote-worker.mjs", "native/buttplug/intiface-engine.exe", "LICENSE" })
        {
            var target = Path.Combine(directory, path); Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (path == "ShockWave.exe") File.Copy(exe, target); else File.WriteAllText(target, "fixture-" + version + "-" + path);
        }
        var manifest = new UpdateManifest(1, version, "win-x64", Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
            .Where(p => Path.GetFileName(p) != UpdatePackage.ManifestName).Select(p => new UpdateFile(Path.GetRelativePath(directory, p).Replace('\\', '/'), UpdatePackage.Hash(p))).ToList());
        File.WriteAllText(Path.Combine(directory, UpdatePackage.ManifestName), JsonSerializer.Serialize(manifest, AppUpdater.Json));
        return manifest;
    }
    private static string Zip(string directory)
    {
        var path = Path.Combine(root, Guid.NewGuid().ToString("N") + ".zip"); ZipFile.CreateFromDirectory(directory, path); return path;
    }
    private static void PackageValidation()
    {
        var source = DirectoryFor("payload"); MakePackage(source, "0.7.8");
        var destination = Path.Combine(root, "extracted"); var zip = Zip(source);
        var payload = UpdatePackage.Extract(zip, destination, Next);
        Check(File.Exists(Path.Combine(destination, "ShockWave.exe")) && payload.Manifest.Files.Count == 6, "complete package extracts and hashes verify");
        Reject(() => UpdatePackage.Extract(zip, Path.Combine(root, "wrong-version"), new(0, 8, 0)), "package version must match release");
        File.AppendAllText(Path.Combine(destination, "node.exe"), "tampered");
        Reject(() => UpdatePackage.VerifyPayload(destination, payload.Manifest), "staged file tampering is rejected");
        foreach (var path in new[] { "../escape", "/absolute", "C:/escape", "a\\b", "NUL.txt", "a/../b", "a/file:stream", "bridge/file. ", "bridge//file" })
            Reject(() => UpdatePackage.SafePath(root, path), "dangerous archive path " + path);
        foreach (var path in new[] { "bridge/config.json", "bridge/bridge-config.json", "docs/key.bin", "other/file.txt", "native/lovense/LovenseBLE_Lib.dll" })
            Check(!UpdatePackage.AllowedFile(path), "private/unsupported package file excluded " + path);
        var traversal = Path.Combine(root, "traversal.zip");
        using (var archive = ZipFile.Open(traversal, ZipArchiveMode.Create))
            foreach (var path in new[] { "../escape.txt", "a", "b" }) using (var writer = new StreamWriter(archive.CreateEntry(path).Open())) writer.Write("test");
        Reject(() => UpdatePackage.Extract(traversal, Path.Combine(root, "badzip"), Next), "ZIP traversal is rejected before writing outside staging");
        Check(!File.Exists(Path.Combine(root, "escape.txt")), "traversal did not escape staging");
        var duplicate = Path.Combine(root, "duplicate.zip");
        using (var archive = ZipFile.Open(duplicate, ZipArchiveMode.Create))
            foreach (var path in new[] { "a.txt", "A.txt", "b.txt" }) using (var writer = new StreamWriter(archive.CreateEntry(path).Open())) writer.Write("test");
        Reject(() => UpdatePackage.Extract(duplicate, Path.Combine(root, "duplicate"), Next), "case-insensitive duplicate entries rejected");
        var linked = Path.Combine(root, "linked.zip");
        using (var archive = ZipFile.Open(linked, ZipArchiveMode.Create))
        {
            var link = archive.CreateEntry("link"); link.ExternalAttributes = unchecked((int)0xa1ff0000);
            using (var writer = new StreamWriter(link.Open())) writer.Write("../outside");
            archive.CreateEntry("a"); archive.CreateEntry("b");
        }
        Reject(() => UpdatePackage.Extract(linked, Path.Combine(root, "linked"), Next), "archive symlinks are rejected");
    }
    private static UpdateRequest Request(string stage, string target) => new(target, "0.7.7", "0.7.8", UpdatePackage.Hash(Path.Combine(stage, "payload", UpdatePackage.ManifestName)), -1, 0, false);
    private static void Installation()
    {
        var stage = DirectoryFor("install-stage"); var source = Path.Combine(stage, "payload"); Directory.CreateDirectory(source); var incoming = MakePackage(source, "0.7.8");
        var target = DirectoryFor("installed"); MakePackage(target, "0.7.7");
        File.WriteAllText(Path.Combine(target, "personal-notes.txt"), "keep");
        var settings = DirectoryFor("user-data"); File.WriteAllText(Path.Combine(settings, "api-token.bin"), "fixture-encrypted-token");
        var installLocked = false;
        UpdateInstaller.Apply(stage, Request(stage, target), _ =>
        {
            // A different thread models a second app process; the updater's mutex is reentrant on its own thread.
            installLocked = Task.Run(() => UpdateInstaller.IsInstalling(target)).GetAwaiter().GetResult();
        });
        Check(installLocked && !UpdateInstaller.IsInstalling(target), "installation blocks another app launch until the transaction ends");
        Check(File.ReadAllText(Path.Combine(target, "node.exe")).Contains("0.7.8"), "transaction installs incoming program files");
        Check(UpdatePackage.ReadInstalledManifest(target, Next).Version == "0.7.8", "ownership manifest is committed after program files");
        Check(File.ReadAllText(Path.Combine(target, "personal-notes.txt")) == "keep" && File.ReadAllText(Path.Combine(settings, "api-token.bin")) == "fixture-encrypted-token", "user-added files and external settings are preserved");
        Check(File.Exists(Path.Combine(stage, "backup", "node.exe")), "previous program files retained for recovery");
        var rollbackStage = DirectoryFor("rollback"); var rollbackPayload = Path.Combine(rollbackStage, "payload"); Directory.CreateDirectory(rollbackPayload); MakePackage(rollbackPayload, "0.7.8");
        var rollbackTarget = DirectoryFor("old-app"); MakePackage(rollbackTarget, "0.7.7");
        var before = Directory.GetFiles(rollbackTarget, "*", SearchOption.AllDirectories).ToDictionary(p => Path.GetRelativePath(rollbackTarget, p), UpdatePackage.Hash);
        Reject(() => UpdateInstaller.Apply(rollbackStage, Request(rollbackStage, rollbackTarget), i => { if (i == 3) throw new IOException("injected copy failure"); }), "copy failure triggers rollback");
        Check(before.All(item => UpdatePackage.Hash(Path.Combine(rollbackTarget, item.Key)) == item.Value), "rollback restores every original file including manifest");
        var collisionTarget = DirectoryFor("user-collision"); MakePackage(collisionTarget, "0.7.7");
        File.WriteAllText(Path.Combine(collisionTarget, "README.txt"), "user added");
        var collisionIncoming = incoming with { Files = [.. incoming.Files, new UpdateFile("README.txt", new string('a', 64))] };
        Reject(() => UpdatePackage.CheckDestination(collisionTarget, collisionIncoming, Current), "incoming file cannot overwrite a user-added collision");
        Reject(() => UpdatePackage.CheckDestination(collisionTarget, incoming with { Version = "0.5.0" }, Current), "helper refuses a downgrade");
        var lockStage = DirectoryFor("lock-stage"); var lockPayload = Path.Combine(lockStage, "payload"); Directory.CreateDirectory(lockPayload); MakePackage(lockPayload, "0.7.8");
        var lockTarget = DirectoryFor("locked-app"); MakePackage(lockTarget, "0.7.7");
        using (var held = new FileStream(Path.Combine(lockTarget, "node.exe"), FileMode.Open, FileAccess.Read, FileShare.Read))
            Reject(() => UpdateInstaller.Apply(lockStage, Request(lockStage, lockTarget)), "in-use files fail preflight without killing any process");
        Check(!Directory.Exists(Path.Combine(lockStage, "backup")), "in-use preflight fails before the first program write");
    }
    private static void NetworkAndUi()
    {
        var source = DirectoryFor("network-payload"); MakePackage(source, "0.7.8"); var zip = File.ReadAllBytes(Zip(source));
        var target = DirectoryFor("network-target"); MakePackage(target, "0.7.7");
        var handler = new FakeHttp(Release(zip), zip);
        var preference = Path.Combine(root, "update-settings.json");
        using var updater = new AppUpdater(new HttpClient(handler), Current, target, Path.Combine(root, "cache"), preference);
        Pump(updater.CheckAsync());
        Check(updater.Available is not null && !updater.Busy, "actual async check offers fixture release");
        updater.SetAutomatic(true); Check(updater.Automatic && File.Exists(preference), "automatic update preference saves independently");
        Pump(updater.DownloadAsync());
        Check(updater.Prepared is not null && File.ReadAllText(Path.Combine(target, "node.exe")).Contains("0.7.7"), "automatic download stages without changing running copy");
        using var readBack = new AppUpdater(new HttpClient(new FakeHttp(Release(zip), zip)), Current, target, Path.Combine(root, "cache2"), preference);
        Check(readBack.Automatic, "automatic update restores on next session");
        using var page = new UpdatePage(updater) { Size = new(720, 590) };
        page.CreateControl(); page.PerformLayout();
        var installs = 0; page.InstallRequested += () => installs++;
        Field<Button>(page, "update").PerformClick(); Check(installs == 1, "Update button invokes install action");
        var links = 0; page.ReleaseRequested += () => links++;
        typeof(LinkLabel).GetMethod("OnLinkClicked", Hidden)!.Invoke(Field<LinkLabel>(page, "release"), [new LinkLabelLinkClickedEventArgs(new LinkLabel.Link())]);
        Check(links == 1, "release link invokes the GitHub action");
        using (var preview = new Bitmap(720, 590)) { page.DrawToBitmap(preview, new(0, 0, 720, 590)); preview.Save(Path.Combine(root, "update-page.png")); }
        // The production form is never shown; its Shown handlers cannot start OSC, Bluetooth, bridge or background checks.
        using var form = new OverlayForm(new BridgeConfig { DryRun = true }, initialSaveConfig: _ => { },
            initialSpsStore: new SpsStore(Path.Combine(root, "sps.json")), initialUpdater: readBack);
        _ = form.Handle; form.PerformLayout();
        var shell = form.Controls.OfType<TableLayoutPanel>().Single(); form.Controls.Remove(shell);
        using var host = new Panel { Size = new(1040, 720) }; host.Controls.Add(shell);
        _ = host.Handle; host.PerformLayout(); shell.PerformLayout();
        var updateNav = Field<Button>(form, "updateNav"); var settingsNav = Field<Button>(form, "settingsNav");
        Check(updateNav.Top >= settingsNav.Bottom, "Update navigation is below Settings");
        updateNav.PerformClick();
        Check(Field<UpdatePage>(form, "updatePage").Visible, "Update navigation opens the new page");
        Field<Button>(form, "toyPanelNav").PerformClick();
        Pump(readBack.CheckAsync());
        Check(Field<LinkLabel>(form, "updateAvailable").Visible, "new release makes Toy panel update link visible");
        typeof(LinkLabel).GetMethod("OnLinkClicked", Hidden)!.Invoke(Field<LinkLabel>(form, "updateAvailable"), [new LinkLabelLinkClickedEventArgs(new LinkLabel.Link())]);
        Check(Field<UpdatePage>(form, "updatePage").Visible, "Toy panel update link opens Update page");
        host.Size = new(790, 550); host.PerformLayout(); shell.PerformLayout();
        Check(updateNav.Bounds.Bottom <= updateNav.Parent!.ClientSize.Height - 58, "Update fits minimum window without overlapping footer");
        Check(!Field<UpdatePage>(form, "updatePage").Controls.OfType<ScrollableControl>().Single().HorizontalScroll.Visible, "minimum Update layout needs no horizontal scrolling");
        using (var preview = new Bitmap(host.Width, host.Height)) { host.DrawToBitmap(preview, host.ClientRectangle); preview.Save(Path.Combine(root, "update-minimum.png")); }
        Field<Button>(form, "toyPanelNav").PerformClick();
        using (var preview = new Bitmap(host.Width, host.Height)) { host.DrawToBitmap(preview, host.ClientRectangle); preview.Save(Path.Combine(root, "toy-panel-update.png")); }
        host.Controls.Remove(shell); form.Controls.Add(shell);
        Pump(readBack.DownloadAsync());
        Check(readBack.Prepared is not null, "automatic mode prepares an update without closing the form");
        var helperStarts = 0; UpdateRequest? automaticRequest = null;
        form.StartUpdateHelper = start => { helperStarts++; automaticRequest = JsonSerializer.Deserialize<UpdateRequest>(File.ReadAllText(start.ArgumentList[1]), AppUpdater.Json); };
        form.Close(); Application.DoEvents();
        Check(form.IsDisposed && helperStarts == 1 && automaticRequest is { Restart: false }, "normal close launches one automatic installer without reopening the app");
        using var missing = new AppUpdater(new HttpClient(new FakeHttp([], []) { Status = HttpStatusCode.NotFound }), Current, target, Path.Combine(root, "missing"));
        Pump(missing.CheckAsync()); Check(missing.Available is null && missing.Status.Contains("No public release"), "unpublished repository is reported honestly");
        using var corrupt = new AppUpdater(new HttpClient(new FakeHttp(Release(zip), [1, 2, 3])), Current, target, Path.Combine(root, "corrupt"));
        Pump(corrupt.CheckAsync()); Pump(corrupt.DownloadAsync()); Check(corrupt.Prepared is null && File.ReadAllText(Path.Combine(target, "node.exe")).Contains("0.7.7"), "interrupted/wrong-size download leaves current installation intact");
        var badHashBytes = (byte[])zip.Clone(); badHashBytes[32] ^= 1;
        using var badHash = new AppUpdater(new HttpClient(new FakeHttp(Release(zip), badHashBytes)), Current, target, Path.Combine(root, "bad-hash"));
        Pump(badHash.CheckAsync()); Pump(badHash.DownloadAsync());
        Check(badHash.Prepared is null && badHash.Status.Contains("could not be verified"), "same-size download with incorrect SHA-256 is rejected before extraction");
        Check(!Directory.EnumerateDirectories(Path.Combine(root, "bad-hash")).Any(), "failed download's owned temporary directory is removed");
        using var throttled = new AppUpdater(new HttpClient(new FakeHttp([], []) { Status = HttpStatusCode.Forbidden }), Current, target, Path.Combine(root, "limited"));
        Pump(throttled.CheckAsync()); Check(throttled.Available is null && throttled.Status.Contains("limiting update checks"), "GitHub throttling reports delay without offering an update");
        using var badRedirect = new AppUpdater(new HttpClient(new FakeHttp([], []) { Redirect = new("https://evil.invalid/update") }), Current, target, Path.Combine(root, "redirect"));
        Pump(badRedirect.CheckAsync()); Check(badRedirect.Available is null && badRedirect.Status.Contains("not trusted"), "untrusted redirects rejected before following");
        updater.SetAutomatic(false);
        using var cancelForm = new OverlayForm(new BridgeConfig { DryRun = true }, initialSaveConfig: _ => { },
            initialSpsStore: new SpsStore(Path.Combine(root, "cancel-sps.json")), initialUpdater: updater);
        _ = cancelForm.Handle;
        Field<ShockerOutputActivity>(cancelForm, "shockerActivity").Receive("{\"kind\":\"shocker-activity\",\"key\":\"fixture\",\"id\":\"test\",\"phase\":\"accepted\",\"remainingMs\":15000}", ["fixture"]);
        var prompts = 0; var manualHelpers = 0;
        cancelForm.ConfirmActiveClose = _ => { prompts++; return false; };
        cancelForm.StartUpdateHelper = _ => manualHelpers++;
        var manualButton = Field<Button>(Field<UpdatePage>(cancelForm, "updatePage"), "update");
        typeof(Button).GetMethod("OnClick", Hidden)!.Invoke(manualButton, [EventArgs.Empty]);
        Application.DoEvents();
        Check(prompts == 1 && !cancelForm.IsDisposed && manualHelpers == 0 && !Field<bool>(cancelForm, "updateRestartRequested"), "Cancel in existing active-output warning cancels manual install before cleanup");
        UpdateRequest? manualRequest = null;
        cancelForm.ConfirmActiveClose = _ => true;
        cancelForm.StartUpdateHelper = start => { manualHelpers++; manualRequest = JsonSerializer.Deserialize<UpdateRequest>(File.ReadAllText(start.ArgumentList[1]), AppUpdater.Json); };
        typeof(Button).GetMethod("OnClick", Hidden)!.Invoke(manualButton, [EventArgs.Empty]); Application.DoEvents();
        Check(cancelForm.IsDisposed && manualHelpers == 1 && manualRequest is { Restart: true }, "confirmed manual Update closes normally and requests one restart");
    }
    private static void PublishedHelper(string published)
    {
        exe = Path.GetFullPath(published);
        var stage = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
        var payload = Path.Combine(stage, "payload"); Directory.CreateDirectory(payload); MakePackage(payload, "0.7.8");
        var target = DirectoryFor("published-helper-target"); MakePackage(target, "0.7.7");
        File.WriteAllText(Path.Combine(target, "user-note.txt"), "keep");
        var request = Request(stage, target) with { ParentId = int.MaxValue };
        var requestPath = Path.Combine(stage, "request.json"); File.WriteAllText(requestPath, JsonSerializer.Serialize(request, AppUpdater.Json));
        var helper = Path.Combine(stage, "ShockWave.Update.exe"); File.Copy(exe, helper);
        var start = new ProcessStartInfo(helper) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = stage };
        start.ArgumentList.Add("--apply-update"); start.ArgumentList.Add(requestPath);
        using var process = Process.Start(start)!;
        Check(process.WaitForExit(25000), "published single-file helper finishes without starting the normal app");
        Check(process.ExitCode == 0 && UpdatePackage.ReadInstalledManifest(target, Next).Version == "0.7.8", "published helper applies verified fake install");
        Check(File.ReadAllText(Path.Combine(target, "user-note.txt")) == "keep", "published helper preserves user files");
    }
    private static T Field<T>(object value, string name) => (T)value.GetType().GetField(name, Hidden)!.GetValue(value)!;
    private static void Pump(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted && watch.Elapsed < TimeSpan.FromSeconds(30)) { Application.DoEvents(); Thread.Sleep(5); }
        if (!task.IsCompleted) throw new Exception("Fixture timed out.");
        task.GetAwaiter().GetResult(); Application.DoEvents();
    }
    private sealed class FakeHttp(byte[] release, byte[] zip) : HttpMessageHandler
    {
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public Uri? Redirect { get; init; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            var response = new HttpResponseMessage(Redirect is null ? Status : HttpStatusCode.Redirect) { Content = new ByteArrayContent(request.RequestUri!.Host == "api.github.com" ? release : zip) };
            if (Redirect is not null) response.Headers.Location = Redirect;
            return Task.FromResult(response);
        }
    }
}
