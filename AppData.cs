using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace LlamaMcp;

public static class AppData
{
    public static string DataDirectory { get; } = ResolveDataDirectory();

    public static string DbPath => Path.Combine(DataDirectory, "oauth.db");

    private static string ResolveDataDirectory()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "llama-mcp");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Loads a persisted RSA key (or generates + saves one on first run) so tokens
    // issued by the OAuth server stay valid across restarts.
    public static RsaSecurityKey LoadOrCreateSigningKey()
    {
        var path = Path.Combine(DataDirectory, "signing.key");
        using var rsa = RSA.Create(2048);

        if (File.Exists(path))
        {
            rsa.ImportRSAPrivateKey(Convert.FromBase64String(File.ReadAllText(path)), out _);
        }
        else
        {
            File.WriteAllText(path, Convert.ToBase64String(rsa.ExportRSAPrivateKey()));
        }

        return new RsaSecurityKey(rsa.ExportParameters(includePrivateParameters: true));
    }
}
