using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ShockWave.Overlay.Toys;

// Only installation-scoped keys and catalog metadata are saved. Vendor IDs/addresses stay in BLE memory.
internal sealed record RememberedToy(string DeviceKey, string Model, bool AutoConnect, DateTimeOffset LastSeenUtc);

internal interface IRememberedToyStore
{
    IReadOnlyList<RememberedToy> Load();
    void Save(IReadOnlyList<RememberedToy> toys);
}

internal sealed class RememberedToyStore(string path) : IRememberedToyStore
{
    private sealed record Document(int Version, RememberedToy[] Toys);
    // Retain the original encryption context so existing remembered toys still decrypt.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SzebsToys.RememberedToys.v1");

    public IReadOnlyList<RememberedToy> Load()
    {
        if (!File.Exists(path)) return Array.Empty<RememberedToy>();
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("Remembered toy data is too large.");
        var plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        try
        {
            var document = JsonSerializer.Deserialize<Document>(plaintext);
            if (document is null || document.Version != 1 || document.Toys is null)
                throw new InvalidDataException("Unsupported remembered toy data.");
            Validate(document.Toys);
            return document.Toys;
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public void Save(IReadOnlyList<RememberedToy> toys)
    {
        Validate(toys);
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(new Document(1, toys.ToArray()));
        byte[] ciphertext;
        try { ciphertext = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, ciphertext);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void Validate(IReadOnlyList<RememberedToy> toys)
    {
        if (toys.Count > 256 || toys.Any(toy => toy is null ||
            !Regex.IsMatch(toy.DeviceKey ?? "", "^toy-[0-9a-f]{64}$") ||
            toy.Model is null || toy.Model != LovenseModelCatalog.IdentifyModel(toy.Model).Model) ||
            toys.Select(toy => toy.DeviceKey).Distinct(StringComparer.Ordinal).Count() != toys.Count)
            throw new InvalidDataException("Invalid remembered toy data.");
    }
}

internal sealed class MemoryToyStore : IRememberedToyStore
{
    private RememberedToy[] toys = [];
    public IReadOnlyList<RememberedToy> Load() => toys.ToArray();
    public void Save(IReadOnlyList<RememberedToy> value) => toys = value.ToArray();
}
