using Factory.Engine.AdHoc;
using Factory.Engine.Llm;
using Factory.Engine.Orchestration;
using Factory.Engine.Skills;
using Factory.Engine.Verification;
using Factory.Web.Components;
using Factory.Web.Data;
using Factory.Web.Mcp;
using Factory.Web.Services;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// Hatchet's data folder: the database, the MCP token and ad hoc work. Hatchet:DataDirectory moves all of it, for tests.
var hatchetData = builder.Configuration["Hatchet:DataDirectory"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hatchet");
Directory.CreateDirectory(hatchetData);
var connectionString = builder.Configuration.GetConnectionString("Factory");
if (string.IsNullOrEmpty(connectionString)) connectionString = $"Data Source={Path.Combine(hatchetData, "hatchet.db")}";
builder.Services.AddSingleton(new McpToken(hatchetData));

// The spec editor sends a pasted architecture spec as one SignalR message, and the default limit is 32 KB.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options => options.MaximumReceiveMessageSize = 16 * 1024 * 1024);
builder.Services.AddDbContextFactory<FactoryDbContext>(options => options.UseSqlite(connectionString));

builder.Services.AddSingleton<IRunStore, EfRunStore>();
builder.Services.AddSingleton<EfSettingsStore>();
builder.Services.AddSingleton<ISettingsStore>(sp => sp.GetRequiredService<EfSettingsStore>());
builder.Services.AddSingleton<FactoryEventBus>();
builder.Services.AddSingleton<ICommandRunner, ShellCommandRunner>();
// Skills in %LOCALAPPDATA%\Hatchet\skills apply to every repository, after the repository's own.
builder.Services.AddSingleton(_ => new SkillLibrary(SkillLibrary.DefaultUserDirectory));
builder.Services.AddSingleton<FactoryOrchestrator>();

// Ad hoc requests: status reports, running the software, commands and questions, kept per repository. Commands run
// in isolated copies of the latest commit under Hatchet's data folder.
builder.Services.AddSingleton<IAdHocStore, EfAdHocStore>();
builder.Services.AddSingleton<IClaudeAsker>(_ => new ClaudeCodeAsker(Path.Combine(hatchetData, "claude-code")));
builder.Services.AddSingleton(sp => new AdHocManager(
    sp.GetRequiredService<IAdHocStore>(), sp.GetRequiredService<IRunStore>(), sp.GetRequiredService<ISettingsStore>(),
    sp.GetRequiredService<ICommandRunner>(), sp.GetRequiredService<IClaudeAsker>(), sp.GetRequiredService<FactoryEventBus>(),
    sp.GetRequiredService<ILogger<AdHocManager>>(), hatchetData, sp.GetRequiredService<FactoryOrchestrator>()));

// Local models can take minutes per response. The client enforces its own stall timeout instead.
builder.Services.AddSingleton<ILlmClient>(_ => new RoutingLlmClient(
    new OpenAiCompatibleLlmClient(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }),
    // Claude Code runs in a folder of its own, so it never picks up a repository's CLAUDE.md or settings.
    new ClaudeCodeLlmClient(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hatchet", "claude-code"))));
builder.Services.AddSingleton(_ => new LmStudioModelLoader(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }));

// MCP endpoint for Claude Code and other clients. The tools never call back into the client, so no sessions are kept.
builder.Services.AddMcpServer(options => options.ServerInfo = new() { Name = "hatchet", Version = "1.0.0" })
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<HatchetTools>()
    .WithTools<AdHocTools>();

builder.Services.AddSingleton<ModelHealthService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ModelHealthService>());
builder.Services.AddHostedService<OrchestratorLifetime>();

var app = builder.Build();

// Hatchet is for this machine only. A null address is the in-memory test server. This runs before anything else.
app.Use(async (context, next) =>
{
    if (context.Connection.RemoteIpAddress is { } address && !Program.IsLoopback(address))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});

app.Use(async (context, next) =>
{
    // Set as the response starts, so a header the framework adds itself doesn't end up twice.
    context.Response.OnStarting(() =>
    {
        var headers = context.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers["Referrer-Policy"] = "no-referrer";
        headers.ContentSecurityPolicy = "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; connect-src 'self' ws: wss:; object-src 'none'; base-uri 'self'; frame-ancestors 'self'";
        return Task.CompletedTask;
    });
    await next();
});

await using (var scope = app.Services.CreateAsyncScope())
{
    var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<FactoryDbContext>>();
    await using var db = await factory.CreateDbContextAsync();
    await db.Database.MigrateAsync();
}

// Requests that were running when Hatchet last stopped can't still be tracked.
await app.Services.GetRequiredService<AdHocManager>().ReconcileAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error", createScopeForErrors: true);
}

// A web page in the user's browser could otherwise reach Hatchet, including pages of apps it builds and serves on other
// localhost ports. Browsers always send an Origin on cross-site requests and WebSockets, so only Hatchet's own pages get
// through. Clients that send none, such as MCP clients and curl, pass here, and MCP then needs its token.
app.Use(async (context, next) =>
{
    if (context.Request.Headers.Origin.Count > 0
        && !string.Equals(context.Request.Headers.Origin.ToString(), $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return;
    }
    await next();
});

// MCP tools can run commands, so every request to them must carry the install's token.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments(McpPath)
        && !context.RequestServices.GetRequiredService<McpToken>().Matches(context.Request.Headers.Authorization.ToString()))
    {
        context.Response.Headers.WWWAuthenticate = "Bearer";
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next();
});

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// The screenshots that came with a report, for its page. Only a saved file's exact name is served, and only as a picture.
app.MapGet("/requests/{id:int}/screenshots/{name}", (int id, string name, AdHocManager manager, HttpContext context) =>
{
    if (manager.GetAttachmentPath(id, name) is not { } path) return Results.NotFound();
    context.Response.Headers.XContentTypeOptions = "nosniff";
    var type = Path.GetExtension(path).ToLowerInvariant() switch { ".png" => "image/png", ".jpg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp", _ => null };
    return type is null ? Results.NotFound() : Results.File(path, type);
});
app.MapMcp(McpPath);

app.Run();

public partial class Program
{
    public const string McpPath = "/mcp";

    /// <summary>Loopback, including an IPv4 address carried inside an IPv6 one.</summary>
    public static bool IsLoopback(System.Net.IPAddress address) =>
        System.Net.IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    /// <summary>The first address Hatchet listens on, for links and setup commands.</summary>
    public static string BaseUrl(IConfiguration configuration) =>
        (configuration["Urls"] ?? "http://localhost:5199").Split(';')[0].TrimEnd('/');
}
