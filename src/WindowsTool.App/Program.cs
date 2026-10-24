using System.Net;
using WindowsTool.App;
using WindowsTool.App.Endpoints;
using WindowsTool.App.Hosting;
using WindowsTool.App.Plans;
using WindowsTool.App.Scans;
using WindowsTool.App.Search;
using WindowsTool.App.Shell;
using WindowsTool.App.Security;
using WindowsTool.Core.Rules;
using WindowsTool.Core.Storage;

var cli = CommandLineOptions.Parse(args);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = cli.RemainingArgs });
builder.Configuration.AddInMemoryCollection(cli.Overrides);

var options = builder.Configuration.GetSection(AppOptions.SectionName).Get<AppOptions>() ?? new AppOptions();

// 主控台只顯示重要訊息
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);

// 只綁定 127.0.0.1；正式環境 Port = 0，由系統配置隨機埠
builder.WebHost.ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.Port));

var token = SessionToken.Create(builder.Environment.IsDevelopment(), options.DevToken);
builder.Services.AddSingleton(new LocalServerState(token, options.Port));
var paths = AppPaths.Resolve(options.DataDirectory, AppContext.BaseDirectory);
builder.Services.AddSingleton(paths);
builder.Services.AddSingleton(_ => new RuleStore(paths.UserRulesFile, BuiltinRules.All, PathTokens.FromEnvironment(paths.DataDirectory)));
builder.Services.AddSingleton<ScanManager>();
builder.Services.AddExecution(paths);
builder.Services.AddSingleton<SearchManager>();
builder.Services.AddSingleton<IShellLauncher, ShellLauncher>();
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));

var app = builder.Build();

app.UseMiddleware<LocalSecurityMiddleware>();

var files = StaticAssets.CreateFileProvider(app.Environment);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = files,
    // 每次都向伺服器確認（有 ETag，沒變就回 304）：更新程式後瀏覽器不會繼續用舊版的前端檔案
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
});

app.MapSystemEndpoints();
app.MapScanEndpoints();
app.MapSearchEndpoints();
app.MapShellEndpoints();
app.MapRuleEndpoints();
app.MapPlanEndpoints();

app.Lifetime.ApplicationStarted.Register(() => StartupAnnouncer.OnStarted(app, options));

await app.RunAsync();
