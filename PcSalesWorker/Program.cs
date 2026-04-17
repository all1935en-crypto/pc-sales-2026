using PcSalesWorker;
using PcSalesWorker.Models;
using PcSalesWorker.Services;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

builder.Configuration
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", optional: false, reloadOnChange: true)
    .AddJsonFile($"appsettings.{builder.Environment.EnvironmentName}.json", optional: true, reloadOnChange: true)
    .AddEnvironmentVariables();

Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Logging.AddSerilog();

builder.Services.Configure<AppOptions>(builder.Configuration.GetSection("App"));
builder.Services.AddSingleton<SheetService>();
builder.Services.AddSingleton<PchomeSearchService>();
builder.Services.AddSingleton<PchomeBackendService>();
builder.Services.AddHostedService<Worker>();

var host = builder.Build();
await host.RunAsync();
