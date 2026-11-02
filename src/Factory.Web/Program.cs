using Factory.Engine.Llm;
using Factory.Engine.Orchestration;
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
builder.Services.AddSingleton<FactoryOrchestrator>();

// Local models can take minutes per response. The client enforces its own stall timeout instead.
builder.Services.AddSingleton<ILlmClient>(_ => new RoutingLlmClient(
    new OpenAiCompatibleLlmClient(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }),
    // Claude Code runs in a folder of its own, so it never picks up a repository's CLAUDE.md or settings.
    new ClaudeCodeLlmClient(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hatchet", "claude-code"))));
builder.Services.AddSingleton(_ => new LmStudioModelLoader(new HttpClient { Timeout = Timeout.InfiniteTimeSpan }));

// MCP endpoint for Claude Code and other clients. The tools never call back into the client, so no sessions are kept.
builder.Services.AddMcpServer(options => options.ServerInfo = new() { Name = "hatchet", Version = "1.0.0" })
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<HatchetTools>();

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
app.MapMcp(McpPath);

app.Run();

public partial class Program
{
    public const string McpPath = "/mcp";

    /// <summary>The first address Hatchet listens on, for links and setup commands.</summary>
    public static string BaseUrl(IConfiguration configuration) =>
        (configuration["Urls"] ?? "http://localhost:5199").Split(';')[0].TrimEnd('/');
}
