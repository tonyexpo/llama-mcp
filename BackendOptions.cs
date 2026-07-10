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
