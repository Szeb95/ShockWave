using System.Text.Json;

namespace ShockWave.Overlay.Toys;

internal sealed record IntifaceConnection(bool Enabled = false, string Address = "", string Scope = "", string DiscoveryHost = "");

// A separate settings file keeps provider credentials and existing migration untouched.
internal sealed class IntifaceConnectionSettings(string? path = null)
{
    private sealed record Document(int Version, IntifaceConnection Connection);
    public IntifaceConnection Value { get; private set; } = new();
    public string? Warning { get; private set; }
    public void Load()
    {
        if (path is null || !File.Exists(path)) return;
        try
        {
            if (new FileInfo(path).Length > 16384) throw new InvalidDataException();
            var data = JsonSerializer.Deserialize<Document>(File.ReadAllText(path));
            if (data is null || data.Version != 1 || data.Connection is not { } value ||
                value.Address is null || value.Scope is null || value.DiscoveryHost is null ||
                value.Scope.Length > 64 || value.DiscoveryHost.Length > 255) throw new InvalidDataException();
            if (value.Address.Length > 0) _ = ParseAddress(value.Address);
            if (value.Address.Length > 0 && !Guid.TryParse(value.Scope, out _)) throw new InvalidDataException();
            Value = value;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException)
        { Warning = "Phone connection settings could not be loaded. The original file is kept; changes cannot be saved."; }
    }
    public void Save(IntifaceConnection next)
    {
        if (Warning is not null) throw new InvalidOperationException(Warning);
        if (next.Address.Length > 0) _ = ParseAddress(next.Address);
        if (path is not null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(temporary, JsonSerializer.Serialize(new Document(1, next)));
                File.Move(temporary, path, true);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw new InvalidOperationException("Phone connection settings could not be saved. Previous settings are kept."); }
            finally { try { File.Delete(temporary); } catch (IOException) { } }
        }
        Value = next;
    }
    internal static Uri ParseAddress(string address)
    {
        address = address.Trim();
        if (!address.Contains("://", StringComparison.Ordinal)) address = "ws://" + address;
        if (address.Length > 2048 || !Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("ws" or "wss") || string.IsNullOrWhiteSpace(uri.Host) ||
            uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 ||
            uri.Host is "0.0.0.0" or "::")
            throw new InvalidOperationException("Enter the server address shown in Intiface, for example ws://192.168.1.20:12345.");
        // A bare phone address uses Intiface's default port, rather than HTTP's port 80.
        var authority = address[(address.IndexOf("://", StringComparison.Ordinal) + 3)..].Split('/')[0];
        var explicitPort = authority.StartsWith('[') ? authority.Contains("]:", StringComparison.Ordinal) : authority.Contains(':');
        if (!explicitPort)
            uri = new UriBuilder(uri) { Port = 12345 }.Uri;
        if (uri.Port is < 1 or > 65535) throw new InvalidOperationException("The Intiface server port must be between 1 and 65535.");
        return uri;
    }
}
