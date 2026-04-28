using FFMpegCore;
using Microsoft.EntityFrameworkCore;
using Serilog;
using VoxMind.Api.Endpoints;
using VoxMind.Core.Configuration;
using VoxMind.Core.Extensions;

// ── FFMpeg — recherche du binaire ─────────────────────────────────────────────
// Remonter jusqu'à trouver tools/ (project root, 2-4 niveaux au-dessus du binaire)
static string? FindProjectRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    for (int i = 0; i < 6 && dir != null; i++, dir = dir.Parent)
        if (Directory.Exists(Path.Combine(dir.FullName, "tools"))) return dir.FullName;
    return null;
}
var projectRoot = FindProjectRoot();
var ffmpegCandidates = new[]
{
    projectRoot != null ? Path.Combine(projectRoot, "tools", "ffmpeg") : null,
    Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg"),
    Path.Combine(Directory.GetCurrentDirectory(), "tools", "ffmpeg"),
    "/usr/bin/ffmpeg",
    "/usr/local/bin/ffmpeg",
}.Where(p => p != null).Cast<string>();
var ffmpegBinary = ffmpegCandidates.FirstOrDefault(File.Exists);
if (ffmpegBinary != null)
    GlobalFFOptions.Configure(opts => opts.BinaryFolder = Path.GetDirectoryName(ffmpegBinary)!);

// ── Répertoires de données ────────────────────────────────────────────────────
var dataDir = AppConfiguration.GetDataDirectory();
Directory.CreateDirectory(Path.Combine(dataDir, "profiles"));
Directory.CreateDirectory(Path.Combine(dataDir, "sessions"));
Directory.CreateDirectory(Path.Combine(dataDir, "logs"));

// ── Configuration ──────────────────────────────────────────────────────────────
var appConfig = ConfigurationLoader.LoadOrDefault();

// ── Serilog ────────────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Is(Enum.TryParse<Serilog.Events.LogEventLevel>(
        appConfig.Logging.Level, ignoreCase: true, out var lvl)
        ? lvl
        : Serilog.Events.LogEventLevel.Information)
    .WriteTo.Console()
    .WriteTo.File(
        appConfig.Logging.File.Path.Replace("{date}", DateTime.Now.ToString("yyyyMMdd")),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: appConfig.Logging.File.RetainedFileCount)
    .CreateLogger();

try
{
    Log.Information("Démarrage VoxMind.Api v{Version} sur le port {Port}",
        appConfig.Application.Version, appConfig.Api.Port);

    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog();

    // ── Services ───────────────────────────────────────────────────────────────
    builder.Services.AddVoxMind(appConfig);

    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new() { Title = "VoxMind API", Version = "v1" });
    });

    builder.Services.AddHealthChecks();
    builder.Services.AddProblemDetails();

    // Upload de fichiers audio (limite 500 MB)
    builder.WebHost.ConfigureKestrel(opt =>
        opt.Limits.MaxRequestBodySize = 500 * 1024 * 1024);

    // ── Pipeline ───────────────────────────────────────────────────────────────
    var app = builder.Build();

    app.UseExceptionHandler();
    app.UseSerilogRequestLogging();

    // ── Authentification API key ──────────────────────────────────────────────
    // Headers X-Api-Key obligatoire sauf sur /health, /swagger. Si ApiKey n'est pas
    // configurée, l'auth est désactivée (warning au démarrage) — pratique pour le dev,
    // mais à imposer en production via voice_data/config/config.json.
    var apiKey = appConfig.Api.ApiKey;
    if (string.IsNullOrEmpty(apiKey))
    {
        Log.Warning("VoxMind.Api : aucune ApiKey configurée — l'API est OUVERTE. " +
                    "Configurez api.api_key dans config.json pour activer l'authentification.");
    }
    else
    {
        app.Use(async (ctx, next) =>
        {
            var path = ctx.Request.Path.Value ?? string.Empty;
            if (path.StartsWith("/health", StringComparison.OrdinalIgnoreCase) ||
                path.StartsWith("/swagger", StringComparison.OrdinalIgnoreCase))
            {
                await next();
                return;
            }

            if (!ctx.Request.Headers.TryGetValue("X-Api-Key", out var provided) ||
                !string.Equals(provided.ToString(), apiKey, StringComparison.Ordinal))
            {
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                await ctx.Response.WriteAsJsonAsync(new
                {
                    type = "https://tools.ietf.org/html/rfc7235#section-3.1",
                    title = "Unauthorized",
                    status = 401,
                    detail = "Header X-Api-Key manquant ou invalide."
                });
                return;
            }

            await next();
        });
    }

    if (appConfig.Api.EnableSwagger)
    {
        app.UseSwagger();
        app.UseSwaggerUI(c => c.SwaggerEndpoint("/swagger/v1/swagger.json", "VoxMind API v1"));
    }

    // Initialiser la DB (crée les tables si elles n'existent pas)
    var dbFactory = app.Services.GetRequiredService<IDbContextFactory<VoxMind.Core.Database.VoxMindDbContext>>();
    await using (var db = await dbFactory.CreateDbContextAsync())
        await db.Database.EnsureCreatedAsync();

    // Charger les embeddings en mémoire pour l'identification des locuteurs
    var speakerSvc = app.Services.GetRequiredService<VoxMind.Core.SpeakerRecognition.ISpeakerIdentificationService>();
    if (speakerSvc is VoxMind.Core.SpeakerRecognition.SherpaOnnxSpeakerService sherpaSvc)
        await sherpaSvc.InitializeAsync();

    // Endpoints
    app.MapTranscriptionEndpoints();
    app.MapSpeakerEndpoints();
    app.MapModelEndpoints();
    app.MapStatusEndpoints();
    app.MapSpeechEndpoints();
    app.MapVoiceEndpoints();
    app.MapHealthChecks("/health");

    app.Run($"http://0.0.0.0:{appConfig.Api.Port}");
}
catch (Exception ex)
{
    Log.Fatal(ex, "VoxMind.Api s'est arrêté de façon inattendue.");
    throw;
}
finally
{
    Log.CloseAndFlush();
}
