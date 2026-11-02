using Factory.Engine.Llm;
using Factory.Engine.Orchestration;
using Factory.Engine.Verification;
using Factory.Web.Components;
using Factory.Web.Data;
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

app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.Run();
