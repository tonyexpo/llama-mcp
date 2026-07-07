using LlamaMcp;
using Microsoft.Extensions.Options;

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

var app = builder.Build();

app.Use(async (context, next) =>
{
    var expectedToken = app.Services.GetRequiredService<IOptions<AuthOptions>>().Value.BearerToken;
    var providedHeader = context.Request.Headers.Authorization.ToString();

    if (providedHeader != $"Bearer {expectedToken}")
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsync("Unauthorized");
        return;
    }

    await next(context);
});

app.MapMcp();

app.Run();
