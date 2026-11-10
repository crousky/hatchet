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

var connectionString = builder.Configuration.GetConnectionString("Factory");
if (string.IsNullOrEmpty(connectionString))
{
    var dataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hatchet");
    Directory.CreateDirectory(dataDirectory);
    connectionString = $"Data Source={Path.Combine(dataDirectory, "hatchet.db")}";
}

// A pasted architecture spec arrives as one SignalR message, and the default limit is 32 KB.
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
var hatchetData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hatchet");
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

// A web page in the user's browser could otherwise reach the MCP tools, including pages of apps Hatchet builds and
// serves on other localhost ports. MCP clients send no Origin; browsers always do, so only Hatchet's own pages get through.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments(McpPath) && context.Request.Headers.Origin.Count > 0
        && !string.Equals(context.Request.Headers.Origin.ToString(), $"{context.Request.Scheme}://{context.Request.Host}", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
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

    /// <summary>The first address Hatchet listens on, for links and setup commands.</summary>
    public static string BaseUrl(IConfiguration configuration) =>
        (configuration["Urls"] ?? "http://localhost:5199").Split(';')[0].TrimEnd('/');
}
