using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace LlamaMcp;

// Minimal OAuth 2.1 surface so remote MCP clients (Claude.ai, ChatGPT) that
// insist on an OAuth dance can connect. The actual security boundary is
// still the single shared token from AuthOptions -- the "consent screen"
// below just asks for that same token instead of a real login system,
// since this server has exactly one owner.
public static class OAuthEndpoints
{
    public static void MapOAuthEndpoints(this WebApplication app)
    {
        app.MapMethods("/connect/authorize", [HttpMethods.Get, HttpMethods.Post], HandleAuthorize);
        app.MapMethods("/connect/token", [HttpMethods.Post], (Delegate)HandleToken);
        app.MapPost("/register", HandleDynamicClientRegistration);
        app.MapGet("/.well-known/oauth-protected-resource", HandleProtectedResourceMetadata);
    }

    private static IResult HandleAuthorize(HttpContext context)
    {
        var request = context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OAuth request cannot be retrieved.");

        if (context.Request.HasFormContentType &&
            context.Request.Form.TryGetValue("token", out var submitted))
        {
            var expectedToken = context.RequestServices
                .GetRequiredService<IOptions<AuthOptions>>().Value.BearerToken;

            if (submitted == expectedToken)
            {
                var identity = new ClaimsIdentity(
                    authenticationType: "llama-mcp",
                    nameType: Claims.Name,
                    roleType: Claims.Role);

                identity.SetClaim(Claims.Subject, "owner");
                identity.SetScopes(request.GetScopes());
                identity.SetDestinations(_ => [Destinations.AccessToken]);

                return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            }

            return Results.Content(RenderConsentPage(context.Request.Query, error: "Token errato."), "text/html");
        }

        return Results.Content(RenderConsentPage(context.Request.Query, error: null), "text/html");
    }

    private static async Task<IResult> HandleToken(HttpContext context)
    {
        var request = context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("The OAuth request cannot be retrieved.");

        if (request.IsAuthorizationCodeGrantType() || request.IsRefreshTokenGrantType())
        {
            var result = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
            if (!result.Succeeded || result.Principal is null)
            {
                return Results.Unauthorized();
            }

            return Results.SignIn(result.Principal, authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
        }

        return Results.BadRequest(new { error = "unsupported_grant_type" });
    }

    // RFC 7591 subset: enough for Claude.ai/ChatGPT to self-register a public client.
    private static async Task<IResult> HandleDynamicClientRegistration(HttpContext context, IOpenIddictApplicationManager applications)
    {
        using var doc = await JsonDocument.ParseAsync(context.Request.Body);
        var root = doc.RootElement;

        if (!root.TryGetProperty("redirect_uris", out var redirectUrisElement) || redirectUrisElement.GetArrayLength() == 0)
        {
            return Results.BadRequest(new { error = "invalid_client_metadata", error_description = "redirect_uris is required." });
        }

        var redirectUris = redirectUrisElement.EnumerateArray().Select(e => e.GetString()!).ToList();
        var clientName = root.TryGetProperty("client_name", out var nameElement) ? nameElement.GetString() : "MCP client";
        var clientId = Guid.NewGuid().ToString("N");

        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = clientName,
        };

        foreach (var uri in redirectUris)
        {
            descriptor.RedirectUris.Add(new Uri(uri));
        }

        descriptor.Permissions.Add(Permissions.Endpoints.Authorization);
        descriptor.Permissions.Add(Permissions.Endpoints.Token);
        descriptor.Permissions.Add(Permissions.GrantTypes.AuthorizationCode);
        descriptor.Permissions.Add(Permissions.GrantTypes.RefreshToken);
        descriptor.Permissions.Add(Permissions.ResponseTypes.Code);
        descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);

        // MCP clients send a `resource` indicator (RFC 8707) matching this
        // server's own base URL. DisableResourceValidation() (Program.cs)
        // only lifts the server-wide "is this resource known at all" check;
        // OpenIddict still requires each application to be explicitly
        // permitted to request a given resource, hence this per-client grant.
        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}/";
        descriptor.Permissions.Add(Permissions.Prefixes.Resource + baseUrl);

        await applications.CreateAsync(descriptor);

        return Results.Json(new
        {
            client_id = clientId,
            client_id_issued_at = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            redirect_uris = redirectUris,
            token_endpoint_auth_method = "none",
            grant_types = new[] { "authorization_code", "refresh_token" },
            response_types = new[] { "code" },
        }, statusCode: StatusCodes.Status201Created);
    }

    private static IResult HandleProtectedResourceMetadata(HttpContext context)
    {
        var baseUrl = $"{context.Request.Scheme}://{context.Request.Host}";
        return Results.Json(new
        {
            resource = baseUrl + "/",
            authorization_servers = new[] { baseUrl },
        });
    }

    private static string RenderConsentPage(IQueryCollection query, string? error)
    {
        // The POST must replay every original OAuth query param in the body
        // alongside the token, since OpenIddict reads the request from the
        // form body (not the URL) on POST -- a plain <form method="post">
        // only carries what's in its own inputs.
        var hiddenFields = string.Concat(query
            .Where(kv => kv.Key != "token")
            .Select(kv => $"""<input type="hidden" name="{System.Net.WebUtility.HtmlEncode(kv.Key)}" value="{System.Net.WebUtility.HtmlEncode(kv.Value.ToString())}" />{Environment.NewLine}"""));

        return $"""
            <!doctype html>
            <html>
            <body style="font-family: sans-serif; max-width: 28rem; margin: 4rem auto;">
              <h1>llama-mcp</h1>
              <p>Autorizza questo client inserendo il bearer token del server.</p>
              {(error is null ? "" : $"<p style=\"color:red\">{error}</p>")}
              <form method="post">
                {hiddenFields}
                <input type="password" name="token" placeholder="Bearer token" style="width: 100%; padding: 0.5rem;" autofocus />
                <button type="submit" style="margin-top: 1rem; padding: 0.5rem 1rem;">Autorizza</button>
              </form>
            </body>
            </html>
            """;
    }
}
