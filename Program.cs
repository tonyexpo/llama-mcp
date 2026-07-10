using System.Threading.Channels;
using LlamaMcp;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OpenIddict.Server;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Server.OpenIddictServerEvents;

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
    client.Timeout = TimeSpan.FromSeconds(backendOptions.TimeoutSeconds);
});

builder.Services
    .AddMcpServer()
    .WithHttpTransport()
    .WithTools<LlamaTools>()
    .WithTools<JobTools>();

builder.Services.AddDbContext<AppDbContext>(options =>
{
    options.UseSqlite($"Data Source={AppData.DbPath}");
    options.UseOpenIddict();
});

// Separate store from AppDbContext/oauth.db -- see AppData.JobsDbPath.
// AddDbContextFactory (not AddDbContext): JobProcessor below is a singleton
// BackgroundService and can't safely hold a scoped DbContext.
builder.Services.AddDbContextFactory<JobDbContext>(options =>
    options.UseSqlite($"Data Source={AppData.JobsDbPath}"));

// Unbounded, never Complete()-d: submit_job and the startup requeue sweep
// are both producers, JobProcessor is the sole consumer.
var jobChannel = Channel.CreateUnbounded<Guid>();
builder.Services.AddSingleton(jobChannel.Reader);
builder.Services.AddSingleton(jobChannel.Writer);
builder.Services.AddHostedService<JobProcessor>();

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

        // MCP clients send a `resource` indicator (RFC 8707) naming this
        // server's own tunnel URL. OpenIddict rejects any resource that
        // isn't pre-registered via RegisterResources(), but we can't
        // pre-register one: the quick tunnel's URL is only known once
        // cloudflared assigns it, after this process has already started.
        // Safe to disable here since there's exactly one resource (this
        // server) -- the confused-deputy scenario resource indicators
        // guard against needs multiple distinct resources to matter.
        options.DisableResourceValidation();

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

        // OpenIddict doesn't know about our hand-rolled /register endpoint
        // (RFC 7591), so it never advertises it -- without this, clients that
        // support dynamic client registration (e.g. claude.ai) can't find it
        // and fall back to asking the user for a manually-entered client ID.
        options.AddEventHandler<HandleConfigurationRequestContext>(builder =>
            builder.UseInlineHandler(context =>
            {
                var request = context.Transaction.GetHttpRequest()
                    ?? throw new InvalidOperationException("The ASP.NET Core request cannot be retrieved.");

                context.Metadata["registration_endpoint"] = $"{request.Scheme}://{request.Host}/register";
                return default;
            }));
    })
    .AddValidation(options =>
    {
        options.UseLocalServer();
        options.UseAspNetCore();
    });

var app = builder.Build();

// cloudflared always forwards to this process over plain HTTP on loopback,
// even for genuine remote HTTPS traffic -- without this, every URL we or
// OpenIddict generate (issuer, endpoints, redirects) comes out as http://
// instead of https://. KnownNetworks/KnownProxies are cleared because the
// only thing that can ever reach Kestrel here is cloudflared on localhost;
// there's no untrusted network hop where header spoofing could matter.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

// Required for OpenIddict's own non-passthrough endpoints (e.g. the
// discovery document at /.well-known/oauth-authorization-server) to run
// through the pipeline at all -- OpenIddict Server registers itself as an
// authentication scheme, and without this middleware those endpoints were
// still answering, just without picking up the rewritten scheme/host from
// UseForwardedHeaders above (every URL in the discovery doc came out as
// http:// instead of https://). Not needed for our own passthrough
// endpoints in OAuthEndpoints.cs, which run through normal minimal-API
// routing regardless.
app.UseAuthentication();

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();

    var jobDbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<JobDbContext>>();
    await using var jobDb = await jobDbFactory.CreateDbContextAsync();
    await jobDb.Database.EnsureCreatedAsync();
    // JobProcessor writes results while get_job_status/get_job_result read
    // concurrently from tool calls -- WAL avoids "database is locked" under
    // that access pattern (oauth.db never needed this; its write volume is negligible).
    await jobDb.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
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
