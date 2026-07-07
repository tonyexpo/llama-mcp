using LlamaMcp;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Validation.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddOptions<BackendOptions>()
    .Bind(builder.Configuration.GetSection(BackendOptions.SectionName));

builder.Services
    .AddOptions<AuthOptions>()
    .Bind(builder.Configuration.GetSection(AuthOptions.SectionName))
    .Validate(o => !string.IsNullOrWhiteSpace(o.BearerToken), $"{AuthOptions.SectionName}:BearerToken must be set.")
    .ValidateOnStart();

builder.Services.AddHttpClient<LlamaBackendClient>((sp, client) =>
{
    var backendOptions = sp.GetRequiredService<IOptions<BackendOptions>>().Value;
    client.BaseAddress = new Uri(backendOptions.BaseUrl);
});

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<LlamaTools>();

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlite($"Data Source={AppData.DbPath}");
    options.UseOpenIddict();
});

var signingKey = AppData.LoadOrCreateSigningKey();

builder.Services
    .AddOpenIddict()
    .AddCore(options => options.UseEntityFrameworkCore().UseDbContext<AppDbContext>())
    .AddServer(options =>
    {
        options
            .SetAuthorizationEndpointUris("/connect/authorize")
            .SetTokenEndpointUris("/connect/token")
            .SetConfigurationEndpointUris("/.well-known/oauth-authorization-server");

        options
            .AllowAuthorizationCodeFlow()
            .RequireProofKeyForCodeExchange()
            .AllowRefreshTokenFlow();

        options
            .AddSigningKey(signingKey)
            .AddEncryptionKey(signingKey);

        options
            .UseAspNetCore()
            .EnableAuthorizationEndpointPassthrough()
            .EnableTokenEndpointPassthrough()
            // TLS always terminates at the Cloudflare tunnel, never on this
            // process's own Kestrel listener -- it only ever sees plain HTTP,
            // even for real remote/HTTPS traffic. Standard for reverse-proxy setups.
            .DisableTransportSecurityRequirement();
    })
    .AddValidation(options =>
    {
        options.UseLocalServer();
        options.UseAspNetCore();
    });

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
}

var publicPaths = new HashSet<string>(StringComparer.Ordinal)
{
    "/connect/authorize",
    "/connect/token",
    "/register",
    "/.well-known/oauth-authorization-server",
    "/.well-known/oauth-protected-resource",
};

app.Use(async (context, next) =>
{
    if (publicPaths.Contains(context.Request.Path.Value ?? ""))
    {
        await next(context);
        return;
    }

    var expectedToken = app.Services.GetRequiredService<IOptions<AuthOptions>>().Value.BearerToken;
    var providedHeader = context.Request.Headers.Authorization.ToString();

    if (providedHeader == $"Bearer {expectedToken}")
    {
        await next(context);
        return;
    }

    var oauthResult = await context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
    if (oauthResult.Succeeded)
    {
        await next(context);
        return;
    }

    var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    context.Response.Headers.WWWAuthenticate = $"Bearer resource_metadata=\"{baseUrl}/.well-known/oauth-protected-resource\"";
    await context.Response.WriteAsync("Unauthorized");
});

app.MapOAuthEndpoints();
app.MapMcp();

app.Run();
