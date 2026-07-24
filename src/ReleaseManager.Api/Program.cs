using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Serialization;
using ReleaseManager.Api.Api;
using ReleaseManager.Api.Data;
using ReleaseManager.Api.Domain;
using ReleaseManager.Api.Hubs;
using ReleaseManager.Api.Infrastructure;
using ReleaseManager.Api.Services;

var startupLogPath = StartupDiagnostics.ResolveLogPath(args);
try
{
var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService(o => o.ServiceName = builder.Configuration["ServiceName"] ?? "ReleaseManager");
builder.Host.UseSystemd();
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff "; });
builder.Services.Configure<PlatformOptions>(builder.Configuration.GetSection("Platform"));
builder.Services.AddSingleton<DataPaths>();
var bootstrapPaths = new DataPaths(builder.Configuration, builder.Environment); bootstrapPaths.EnsureCreated();
var connection = builder.Configuration.GetConnectionString("Default") ?? "Data Source=data/database/release-manager.db";
if (connection.Contains("Data Source=data", StringComparison.OrdinalIgnoreCase)) connection = $"Data Source={Path.Combine(bootstrapPaths.Database, "release-manager.db")}";
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(connection));
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(bootstrapPaths.Keys)).SetApplicationName("ReleaseManager");
builder.Services.AddAuthentication("Session").AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>("Session", null);
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy(Roles.Admin, p => p.RequireRole(Roles.Admin));
    o.AddPolicy("CanOperate", p => p.RequireRole(Roles.Admin, Roles.Operator));
});
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new UtcDateTimeJsonConverter()));
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new UtcDateTimeJsonConverter());
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});
builder.Services.AddScoped<ICommandRunner, CommandRunner>();
builder.Services.AddScoped<ReleaseOrchestrator>();
builder.Services.AddScoped<RuntimeService>();
builder.Services.AddScoped<PackageService>();
builder.Services.AddHostedService<ReleaseWorker>();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins("http://localhost:5173").AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

var app = builder.Build();
await DatabaseInitializer.InitializeAsync(app.Services);
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    context.Response.StatusCode = 500; context.Response.ContentType = "application/json";
    await context.Response.WriteAsJsonAsync(new { code = "INTERNAL_ERROR", message = "操作失败，请查看平台日志或联系管理员。" });
}));
app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapPlatformApi();
app.MapHub<ReleaseHub>("/hubs/releases");
app.MapFallbackToFile("index.html");
app.Run();
}
catch (Exception ex)
{
    StartupDiagnostics.Write(startupLogPath, ex);
    throw;
}

public partial class Program;
