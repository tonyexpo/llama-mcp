using System.Security.Cryptography;
using System.Text;

namespace LlamaMcp;

public sealed class BackendOptions
{
    public const string SectionName = "Backend";

    public string BaseUrl { get; set; } = "http://localhost:1234";
    public string DefaultModel { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 600;
}

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public string BearerToken { get; set; } = "";
}

// Shared by the MCP auth middleware (Program.cs) and the OAuth consent
// screen (OAuthEndpoints.cs) -- both compare an untrusted caller-supplied
// token against the configured secret, and a plain == leaks timing info
// proportional to the first mismatched byte.
public static class TokenComparer
{
    public static bool TokensEqual(string? a, string? b)
    {
        if (a is null || b is null)
        {
            return false;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    }
}
