using System.Security.Cryptography;
using System.Text;

namespace ShockWave.Overlay;

internal sealed class TokenStore(string path)
{
    // Retain the original encryption context so existing saved tokens still decrypt.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("SzebsToys.OpenShock.Token.v1");

    public string Load()
    {
        if (!File.Exists(path)) return "";
        var plaintext = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    public void Save(string token, bool remember)
    {
        if (!remember || token.Length == 0)
        {
            if (File.Exists(path)) File.Delete(path);
            return;
        }
        var plaintext = Encoding.UTF8.GetBytes(token);
        byte[] ciphertext;
        try { ciphertext = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, ciphertext);
        File.Move(temporary, path, true);
    }
}
