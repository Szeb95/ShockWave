using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShockWave.Overlay;

internal sealed record RemoteFriend(string Id, string Name, string Secret)
{
    public override string ToString() => Name;
}
internal sealed record RemoteFriendsData(int Version, string Id, RemoteFriend[] Friends)
{
    public static RemoteFriendsData Empty() => new(1, "sw-" + Guid.NewGuid().ToString("N"), []);
    public override string ToString() => "Saved friends";
}
internal interface IRemoteFriendStore
{
    RemoteFriendsData Load();
    void Save(RemoteFriendsData value);
}

internal sealed class RemoteFriendStore(string path) : IRemoteFriendStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ShockWave.RemoteFriends.v1");
    public RemoteFriendsData Load()
    {
        if (!File.Exists(path)) return RemoteFriendsData.Empty();
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Saved friend data is too large.");
        var clear = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var value = JsonSerializer.Deserialize<RemoteFriendsData>(clear) ?? throw new InvalidDataException("Invalid saved friends.");
            Validate(value); return value;
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }
    public void Save(RemoteFriendsData value)
    {
        Validate(value);
        var clear = JsonSerializer.SerializeToUtf8Bytes(value);
        byte[] encrypted;
        try { encrypted = ProtectedData.Protect(clear, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(clear); }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, encrypted); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static bool ValidId(string? value) => Regex.IsMatch(value ?? "", "^sw-[a-f0-9]{32}$", RegexOptions.CultureInvariant);
    internal static void Validate(RemoteFriendsData value)
    {
        if (value.Version != 1 || !ValidId(value.Id) || value.Friends is null || value.Friends.Length > 32 ||
            value.Friends.Any(f => f is null || !ValidId(f.Id) || f.Id == value.Id ||
                !Regex.IsMatch(f.Secret ?? "", "^[a-f0-9]{64}$", RegexOptions.CultureInvariant) ||
                string.IsNullOrWhiteSpace(f.Name) || f.Name.Length > 48 || f.Name.Any(char.IsControl)) ||
            value.Friends.Select(f => f.Id).Distinct(StringComparer.Ordinal).Count() != value.Friends.Length)
            throw new InvalidDataException("Invalid saved friends.");
    }
}

// Tests use a separate book. A failed/corrupt real book is never silently replaced.
internal sealed class MemoryRemoteFriendStore : IRemoteFriendStore
{
    private RemoteFriendsData data = RemoteFriendsData.Empty();
    public RemoteFriendsData Load() => data with { Friends = data.Friends.ToArray() };
    public void Save(RemoteFriendsData value) { RemoteFriendStore.Validate(value); data = value with { Friends = value.Friends.ToArray() }; }
}

internal sealed class RemoteFriendBook
{
    private readonly IRemoteFriendStore store;
    private RemoteFriendsData data = RemoteFriendsData.Empty();
    public string Id => data.Id;
    public IReadOnlyList<RemoteFriend> Friends => data.Friends.ToArray();
    public string Notice { get; private set; } = "";
    public bool Available { get; private set; } = true;
    public RemoteFriendBook(IRemoteFriendStore store)
    {
        this.store = store;
        try { data = store.Load(); RemoteFriendStore.Validate(data); }
        catch (Exception error) when (StorageError(error))
        { Available = false; Notice = "Could not read saved friends. The existing file has been kept; Connect is off."; }
    }
    public bool Prepare() => Persist(data);
    public bool Remember(string id, string secret, string? name)
    {
        var old = data.Friends.FirstOrDefault(f => f.Id == id);
        if (old is not null && old.Secret != secret)
        { Notice = "This friend is already saved. Use the saved friend, or forget them before pairing again."; return false; }
        var label = string.IsNullOrWhiteSpace(name) ? old?.Name ?? $"Friend {data.Friends.Length + 1}" : name.Trim();
        var friend = new RemoteFriend(id, label, secret);
        return Persist(data with { Friends = data.Friends.Where(f => f.Id != id).Append(friend).ToArray() });
    }
    public bool Rename(string id, string name)
    {
        var friend = data.Friends.FirstOrDefault(f => f.Id == id);
        return friend is not null && Remember(id, friend.Secret, name);
    }
    public bool Forget(string id) => Persist(data with { Friends = data.Friends.Where(f => f.Id != id).ToArray() });
    private bool Persist(RemoteFriendsData candidate)
    {
        if (!Available) return false;
        try { RemoteFriendStore.Validate(candidate); store.Save(candidate); data = candidate; Notice = ""; return true; }
        catch (Exception error) when (StorageError(error))
        { Notice = "Could not save friends. Existing saved data was kept. Check the friend name and Windows file access."; return false; }
    }
    private static bool StorageError(Exception error) => error is IOException or InvalidDataException or UnauthorizedAccessException or CryptographicException or JsonException or ArgumentException;
}
