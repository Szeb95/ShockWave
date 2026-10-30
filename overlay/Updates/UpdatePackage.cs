using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShockWave.Overlay.Updates;

internal sealed record UpdateFile(string Path, string Sha256);
internal sealed record UpdateManifest(int Format, string Version, string Platform, List<UpdateFile> Files);
internal sealed record UpdatePayload(string Directory, UpdateManifest Manifest);

internal static class UpdatePackage
{
    internal const string ManifestName = "update-manifest.json";
    private static readonly HashSet<string> RootFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "ShockWave.exe", "node.exe", "README.txt", "README.md", "LICENSE", "THIRD-PARTY.md", "THIRD-PARTY.txt",
        "REMOTE.md", "TOY-CONTROL.md", "SPS.md", "PISHOCK.md", "UPDATES.md", "STEAMVR.md", "INTIFACE-PHONE.md",
        "app.vrmanifest", "openvr_api.dll", "openvr-version.json", "build-info.json", "manifest.sha256", "payload-manifest.json",
        "D3DCompiler_47_cor3.dll", "PenImc_cor3.dll", "PresentationNative_cor3.dll", "vcruntime140_cor3.dll", "wpfgfx_cor3.dll"
    };

    internal static string SafePath(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Length > 220 || relative.Contains('\\') || relative.StartsWith('/') || relative.Contains(':'))
            throw new UpdateException("The update contains an invalid file path.");
        var parts = relative.Split('/');
        foreach (var part in parts)
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(\..*)?$", RegexOptions.IgnoreCase))
                throw new UpdateException("The update contains an invalid file name.");
        var absoluteRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var destination = Path.GetFullPath(Path.Combine(absoluteRoot, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!destination.StartsWith(absoluteRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UpdateException("An update file escaped its package directory.");
        return destination;
    }

    internal static void NoLinks(string path)
    {
        for (var item = Path.GetFullPath(path); !string.IsNullOrEmpty(item); item = Path.GetDirectoryName(item))
            if ((File.Exists(item) || Directory.Exists(item)) &&
                ((File.GetAttributes(item) & FileAttributes.Directory) != 0 ? new DirectoryInfo(item).LinkTarget : new FileInfo(item).LinkTarget) is not null)
                throw new UpdateException("Updates cannot use a linked file or folder. Use a normal program folder.");
    }

    internal static bool AllowedFile(string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals("config.json", StringComparison.OrdinalIgnoreCase) || name.Equals("bridge-config.json", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("update-settings.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".bin", StringComparison.OrdinalIgnoreCase) ||
            name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dpapi", StringComparison.OrdinalIgnoreCase)) return false;
        return RootFiles.Contains(path) || path.StartsWith("bindings/", StringComparison.Ordinal) && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWith("bridge/", StringComparison.Ordinal) || path.StartsWith("ToyControl/", StringComparison.Ordinal) ||
            path.StartsWith("native/buttplug/", StringComparison.Ordinal) || path.StartsWith("docs/", StringComparison.Ordinal) ||
            path.StartsWith("licenses/", StringComparison.Ordinal) || path.StartsWith("UnityEditor/", StringComparison.Ordinal) || path.StartsWith("Avatar-tools/", StringComparison.Ordinal);
    }

    internal static UpdateManifest ReadManifest(string directory)
    {
        var path = Path.Combine(directory, ManifestName);
        NoLinks(path);
        if (!File.Exists(path) || new FileInfo(path).Length > 3 * 1024 * 1024) throw new UpdateException("This copy needs a complete Windows release package before it can update in place. Use the GitHub link.");
        var result = JsonSerializer.Deserialize<UpdateManifest>(File.ReadAllText(path), AppUpdater.Json)
            ?? throw new UpdateException("The update file list is missing.");
        if (result.Format != 1 || result.Platform != "win-x64" || !System.Version.TryParse(result.Version, out _) || result.Files is null || result.Files.Count is < 3 or > 10000)
            throw new UpdateException("The update package format is not supported.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in result.Files)
        {
            if (file is null) throw new UpdateException("The update file list is invalid.");
            _ = SafePath(directory, file.Path);
            if (!AllowedFile(file.Path) || !Regex.IsMatch(file.Sha256 ?? "", "^[a-fA-F0-9]{64}$") || !names.Add(file.Path))
                throw new UpdateException("The update file list contains an invalid or duplicate entry.");
        }
        foreach (var required in new[] { "ShockWave.exe", "node.exe", "bridge/bridge.mjs", "bridge/remote-worker.mjs", "native/buttplug/intiface-engine.exe", "LICENSE" })
            if (!names.Contains(required)) throw new UpdateException("The update package is incomplete.");
        return result;
    }

    internal static UpdateManifest ReadInstalledManifest(string directory, Version expected)
    {
        var manifest = ReadManifest(directory);
        if (AppUpdater.Normalize(Version.Parse(manifest.Version)) != AppUpdater.Normalize(expected))
            throw new UpdateException("The installed package version does not match this app. Extract a complete release into a new folder.");
        return manifest;
    }

    internal static UpdatePayload Extract(string archivePath, string destination, Version expected, CancellationToken cancellation = default)
    {
        NoLinks(destination);
        if (Directory.Exists(destination)) throw new UpdateException("The update staging folder is already in use.");
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is < 3 or > 12000) throw new UpdateException("The update archive has an unsupported number of files.");
        var entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            var isDirectory = entry.FullName.EndsWith('/');
            var path = isDirectory ? entry.FullName[..^1] : entry.FullName;
            var target = SafePath(destination, path);
            if (!entries.Add(path) || ((entry.ExternalAttributes >> 16) & 0xf000) == 0xa000 || (entry.ExternalAttributes & 0x400) != 0)
                throw new UpdateException("The update contains duplicate or linked files.");
            expanded = checked(expanded + entry.Length);
            if (entry.Length > 536870912 || expanded > 2L * 1024 * 1024 * 1024) throw new UpdateException("The expanded update is too large.");
            if (isDirectory) Directory.CreateDirectory(target);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                entry.ExtractToFile(target, false);
            }
        }
        var manifest = ReadManifest(destination);
        if (AppUpdater.Normalize(Version.Parse(manifest.Version)) != AppUpdater.Normalize(expected)) throw new UpdateException("The downloaded package has the wrong version.");
        VerifyPayload(destination, manifest, cancellation);
        return new(destination, manifest);
    }

    internal static void VerifyPayload(string directory, UpdateManifest manifest, CancellationToken cancellation = default)
    {
        var expected = manifest.Files.Select(f => f.Path).Append(ManifestName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.AllDirectories))
        {
            NoLinks(path);
            if (File.Exists(path) && !expected.Contains(Path.GetRelativePath(directory, path).Replace('\\', '/')))
                throw new UpdateException("The update contains a file outside its verified list.");
        }
        foreach (var file in manifest.Files)
        {
            cancellation.ThrowIfCancellationRequested();
            var path = SafePath(directory, file.Path);
            NoLinks(path);
            if (!File.Exists(path) || !Hash(path).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new UpdateException("An update file failed verification.");
        }
        var version = FileVersionInfo.GetVersionInfo(Path.Combine(directory, "ShockWave.exe")).FileVersion;
        if (!Version.TryParse(version, out var exeVersion) || AppUpdater.Normalize(exeVersion) != AppUpdater.Normalize(Version.Parse(manifest.Version)))
            throw new UpdateException("The new app does not match the release version.");
    }

    internal static void CheckDestination(string target, UpdateManifest incoming, Version current)
    {
        NoLinks(target);
        var installed = ReadInstalledManifest(target, current);
        if (AppUpdater.Normalize(Version.Parse(incoming.Version)) <= AppUpdater.Normalize(current)) throw new UpdateException("Updates cannot install an older or equal version.");
        var owned = installed.Files.Select(f => f.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in incoming.Files)
        {
            var path = SafePath(target, file.Path);
            NoLinks(path);
            if (Directory.Exists(path) || (File.Exists(path) && !owned.Contains(file.Path)))
                throw new UpdateException("An update would replace a user-added file. Install the new release in a separate folder.");
        }
    }

    internal static string Hash(string path) { using var stream = File.OpenRead(path); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }
}
