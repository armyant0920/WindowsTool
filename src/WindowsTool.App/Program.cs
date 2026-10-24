using System.Net;
using WindowsTool.App;
using WindowsTool.App.Endpoints;
using WindowsTool.App.Hosting;
using WindowsTool.App.Scans;
using WindowsTool.App.Search;
using WindowsTool.App.Shell;
using WindowsTool.App.Security;

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
builder.Services.AddSingleton<ScanManager>();
builder.Services.AddSingleton<SearchManager>();
builder.Services.AddSingleton<IShellLauncher, ShellLauncher>();
builder.Services.ConfigureHttpJsonOptions(json =>
    json.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default));

var app = builder.Build();

app.UseMiddleware<LocalSecurityMiddleware>();

var files = StaticAssets.CreateFileProvider(app.Environment);
app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = files });
app.UseStaticFiles(new StaticFileOptions { FileProvider = files });

app.MapSystemEndpoints();
app.MapScanEndpoints();
app.MapSearchEndpoints();
app.MapShellEndpoints();

app.Lifetime.ApplicationStarted.Register(() => StartupAnnouncer.OnStarted(app, options));

await app.RunAsync();
