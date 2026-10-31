using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShockWave.Overlay.Updates;

internal sealed record AppRelease(Version Version, Uri Page, Uri Download, long Bytes, string Sha256);

internal sealed class AppUpdater : IDisposable
{
    internal const string Repository = "https://github.com/Szeb95/ShockWave";
    internal const string LatestApi = "https://api.github.com/repos/Szeb95/ShockWave/releases/latest";
    internal static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true, WriteIndented = true };
    internal static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SzebsToys");
    private readonly HttpClient http;
    private readonly string? settingsPath;
    private readonly string stagingRoot;
    private readonly CancellationTokenSource lifetime = new();
    private bool disposed;
    public Version CurrentVersion { get; }
    public string InstallDirectory { get; }
    public AppRelease? Available { get; private set; }
    public PreparedAppUpdate? Prepared { get; private set; }
    public bool Busy { get; private set; }
    public bool Automatic { get; private set; }
    public bool BackgroundChecks { get; }
    public string Status { get; private set; } = "Check for a published update.";
    public event Action? Changed;

    public AppUpdater(HttpClient http, Version version, string installDirectory, string stagingRoot, string? settingsPath = null, bool backgroundChecks = false)
    {
        this.http = http;
        CurrentVersion = Normalize(version);
        InstallDirectory = Path.GetFullPath(installDirectory);
        this.stagingRoot = Path.GetFullPath(stagingRoot);
        this.settingsPath = settingsPath;
        BackgroundChecks = backgroundChecks;
        if (settingsPath is not null && File.Exists(settingsPath))
        {
            try { Automatic = JsonSerializer.Deserialize<UpdatePreferences>(File.ReadAllText(settingsPath), Json)?.AutomaticUpdate == true; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { Status = "Update preferences could not be read. Automatic update is off."; }
        }
    }

    public static AppUpdater Create(bool production) => new(new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(15) },
        typeof(AppUpdater).Assembly.GetName().Version ?? new Version(0, 7, 0), AppContext.BaseDirectory,
        Path.Combine(DataDirectory, "updates"), production ? Path.Combine(DataDirectory, "update-settings.json") : null, production);

    public void SetAutomatic(bool value)
    {
        if (disposed) return;
        try
        {
            if (settingsPath is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                var temporary = settingsPath + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(new UpdatePreferences(value), Json));
                File.Move(temporary, settingsPath, true);
            }
            Automatic = value;
            Status = value ? "Automatic update is on. Updates install when you close ShockWave." : "Automatic update is off.";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status = "Could not save the automatic update preference."; }
        Changed?.Invoke();
    }

    public async Task CheckAsync()
    {
        if (Busy || disposed) return;
        Busy = true; Status = "Checking for updates…"; Changed?.Invoke();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(25));
            using var response = await GetAsync(new Uri(LatestApi), timeout.Token);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                Available = null;
                Status = "No public release is available yet.";
            }
            else
            {
                if ((int)response.StatusCode is 403 or 429) throw new UpdateException("GitHub is limiting update checks. Try again later.");
                response.EnsureSuccessStatusCode();
                var bytes = await ReadBoundedAsync(response, 2 * 1024 * 1024, timeout.Token);
                Available = ParseRelease(bytes, CurrentVersion);
                Status = Available is null ? "You have the latest published version." : $"Version {Display(Available.Version)} is available.";
            }
            if (Prepared is not null && (Available is null || Prepared.Release.Version != Available.Version)) Prepared = null;
        }
        catch (OperationCanceledException) { if (!disposed) Status = "The update check timed out. Try again."; }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or FormatException or InvalidOperationException or KeyNotFoundException or ArgumentException)
        { Status = e is UpdateException ? e.Message : "Could not check for updates. Check your connection and try again."; }
        finally { Busy = false; if (!disposed) Changed?.Invoke(); }
    }

    public async Task<bool> DownloadAsync()
    {
        if (Busy || disposed || Available is null) return false;
        if (Prepared?.Release == Available) return true;
        var release = Available;
        string? stage = null;
        Busy = true; Status = "Downloading update…"; Changed?.Invoke();
        try
        {
            using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            downloadTimeout.CancelAfter(TimeSpan.FromMinutes(15));
            var cancellation = downloadTimeout.Token;
            // Packaged copies carry an ownership manifest; source builds must first install a complete package.
            _ = await Task.Run(() => UpdatePackage.ReadInstalledManifest(InstallDirectory, CurrentVersion), cancellation);
            Directory.CreateDirectory(stagingRoot);
            UpdatePackage.NoLinks(stagingRoot);
            stage = Path.Combine(stagingRoot, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            var zip = Path.Combine(stage, "package.zip");
            using (var response = await GetAsync(release.Download, cancellation))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is long length && length != release.Bytes) throw new UpdateException("The update download size does not match its release.");
                await using var incoming = await response.Content.ReadAsStreamAsync(cancellation);
                await using var output = new FileStream(zip, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true);
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var buffer = new byte[81920]; long total = 0; int count;
                while ((count = await incoming.ReadAsync(buffer, cancellation)) > 0)
                {
                    total += count;
                    if (total > release.Bytes) throw new UpdateException("The update download is larger than expected.");
                    hash.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellation);
                }
                if (total != release.Bytes || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new UpdateException("The update could not be verified. Your current version is unchanged.");
            }
            Status = "Verifying update…"; Changed?.Invoke();
            var payload = await Task.Run(() =>
            {
                var extracted = UpdatePackage.Extract(zip, Path.Combine(stage, "payload"), release.Version, cancellation);
                UpdatePackage.CheckDestination(InstallDirectory, extracted.Manifest, CurrentVersion);
                return extracted;
            }, cancellation);
            Prepared = new(release, stage, payload.Directory);
            Status = Automatic ? "Update downloaded. It will install when you close ShockWave." : "Update downloaded and ready to install.";
            return true;
        }
        catch (OperationCanceledException) { if (!disposed) Status = "Update download was cancelled. Your current version is unchanged."; }
        catch (Exception e) when (e is IOException or HttpRequestException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { Status = e is UpdateException ? e.Message : "Could not prepare the update. Your current version is unchanged."; }
        finally
        {
            if (stage is not null && Prepared?.StageDirectory != stage) DeleteIncompleteStage(stage);
            Busy = false; if (!disposed) Changed?.Invoke();
        }
        return false;
    }

    private void DeleteIncompleteStage(string stage)
    {
        try
        {
            if (Path.GetDirectoryName(stage) != stagingRoot || !Guid.TryParseExact(Path.GetFileName(stage), "N", out _)) return;
            UpdatePackage.NoLinks(stage);
            if (Directory.Exists(stage)) Directory.Delete(stage, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* A locked cache may be removed later; installed files are untouched. */ }
    }

    private async Task<HttpResponseMessage> GetAsync(Uri uri, CancellationToken cancellation)
    {
        for (var redirect = 0; redirect < 6; redirect++)
        {
            if (!TrustedTransport(uri)) throw new UpdateException("The update address is not trusted.");
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("ShockWave-Updater/0.7.0");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellation);
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            var next = response.Headers.Location;
            response.Dispose();
            if (next is null) throw new UpdateException("The update download redirected without an address.");
            uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
        }
        throw new UpdateException("The update download redirected too many times.");
    }

    internal static bool TrustedTransport(Uri uri) => uri.Scheme == "https" && uri.Port == 443 && uri.UserInfo.Length == 0 &&
        uri.Fragment.Length == 0 && uri.Host is "api.github.com" or "github.com" or "release-assets.githubusercontent.com" or "objects.githubusercontent.com";

    internal static AppRelease? ParseRelease(byte[] json, Version current)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Regex.IsMatch(tag, @"^v?\d+\.\d+\.\d+(\.\d+)?$")) throw new UpdateException("The release has an unsupported version number.");
        var version = Normalize(Version.Parse(tag.TrimStart('v')));
        if (version <= Normalize(current)) return null;
        var page = new Uri(root.GetProperty("html_url").GetString()!);
        if (!TrustedTransport(page) || page.Host != "github.com" || !page.AbsolutePath.StartsWith("/Szeb95/ShockWave/releases/tag/", StringComparison.Ordinal))
            throw new UpdateException("The release is not from the ShockWave repository.");
        var name = $"ShockWave-{Display(version)}-win-x64.zip";
        var assets = root.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name && a.GetProperty("state").GetString() == "uploaded").ToArray();
        if (assets.Length != 1) throw new UpdateException("The latest release has no compatible Windows update package. View it on GitHub.");
        var asset = assets[0];
        var digest = asset.TryGetProperty("digest", out var d) ? d.GetString() ?? "" : "";
        if (!Regex.IsMatch(digest, @"^sha256:[a-fA-F0-9]{64}$")) throw new UpdateException("GitHub has not provided a verification hash for this release yet.");
        var size = asset.GetProperty("size").GetInt64();
        var download = new Uri(asset.GetProperty("browser_download_url").GetString()!);
        if (size is <= 0 or > 536870912 || !TrustedTransport(download) || download.Host != "github.com" ||
            download.Query.Length != 0 || download.AbsolutePath != $"/Szeb95/ShockWave/releases/download/{tag}/{name}")
            throw new UpdateException("The release download does not match the ShockWave package.");
        return new(version, page, download, size, digest[7..]);
    }

    private static async Task<byte[]> ReadBoundedAsync(HttpResponseMessage response, int maximum, CancellationToken cancellation)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(cancellation);
        using var result = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, cancellation)) > 0)
        {
            if (result.Length + count > maximum) throw new UpdateException("The release information is too large.");
            result.Write(buffer, 0, count);
        }
        return result.ToArray();
    }

    internal static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
    internal static string Display(Version v) => v.Revision > 0 ? v.ToString(4) : v.ToString(3);
    public void Dispose() { if (disposed) return; disposed = true; lifetime.Cancel(); http.Dispose(); }
    private sealed record UpdatePreferences(bool AutomaticUpdate);
}

internal sealed record PreparedAppUpdate(AppRelease Release, string StageDirectory, string PayloadDirectory);
internal sealed class UpdateException(string message) : IOException(message);
