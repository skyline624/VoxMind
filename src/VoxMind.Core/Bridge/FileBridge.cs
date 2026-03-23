using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VoxMind.Core.RemoteClients;
using VoxMind.Core.Session;
using VoxMind.Core.Transcription;

namespace VoxMind.Core.Bridge;

public class FileBridge : IExternalBridge
{
    private readonly string _sharedFolder;
    private readonly int _pollIntervalMs;
    private readonly int _statusUpdateIntervalSeconds;
    private readonly ISessionManager _sessionManager;
    private readonly IRemoteClientRegistry _registry;
    private readonly ILogger<FileBridge> _logger;
    private readonly IHostApplicationLifetime? _lifetime;

    private readonly string _commandsFile;
    private readonly string _statusFile;
    private readonly string _responsesFile;

    private Timer? _pollTimer;
    private Timer? _statusTimer;
    private string? _lastCommandId;
    private DateTime _startedAt = DateTime.UtcNow;
    private bool _disposed;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public FileBridge(
        string sharedFolder,
        ISessionManager sessionManager,
        ILogger<FileBridge> logger,
        IHostApplicationLifetime? lifetime = null,
        int pollIntervalMs = 500,
        int statusUpdateIntervalSeconds = 5,
        IRemoteClientRegistry? registry = null)
    {
        _sharedFolder = sharedFolder;
        _sessionManager = sessionManager;
        _logger = logger;
        _lifetime = lifetime;
        _pollIntervalMs = pollIntervalMs;
        _statusUpdateIntervalSeconds = statusUpdateIntervalSeconds;
        _registry = registry ?? new RemoteClientRegistry(Microsoft.Extensions.Logging.Abstractions.NullLogger<RemoteClientRegistry>.Instance);

        _commandsFile = Path.Combine(sharedFolder, "commands_to_voxmind.json");
        _statusFile = Path.Combine(sharedFolder, "status_from_voxmind.json");
        _responsesFile = Path.Combine(sharedFolder, "transcription_output.json");

        Directory.CreateDirectory(sharedFolder);
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        _startedAt = DateTime.UtcNow;
        _pollTimer = new Timer(_ => PollCommands(), null, 0, _pollIntervalMs);
        _statusTimer = new Timer(_ => PublishStatusInternal(), null, 0, _statusUpdateIntervalSeconds * 1000);
        _logger.LogInformation("FileBridge démarré. Polling toutes les {Interval}ms.", _pollIntervalMs);
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        _pollTimer?.Dispose();
        _statusTimer?.Dispose();
        _logger.LogInformation("FileBridge arrêté.");
        return Task.CompletedTask;
    }

    public async Task PublishStatusAsync(SystemStatus status)
    {
        await WriteJsonSafeAsync(_statusFile, status);
    }

    private async void PublishStatusInternal()
    {
        try
        {
            var status = BuildCurrentStatus();
            await WriteJsonSafeAsync(_statusFile, status);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Impossible de publier le statut");
        }
    }

    private void PollCommands()
    {
        try
        {
            if (!File.Exists(_commandsFile)) return;

            string json = ReadFileSafe(_commandsFile);
            if (string.IsNullOrWhiteSpace(json)) return;

            var command = JsonSerializer.Deserialize<BridgeCommand>(json, JsonOptions);
            if (command is null) return;

            // Éviter de traiter la même commande deux fois
            if (command.CommandId == _lastCommandId) return;
            _lastCommandId = command.CommandId;

            _logger.LogInformation("Commande reçue : {Command} (ID={Id})", command.Command, command.CommandId);
            _ = HandleCommandAsync(command);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Erreur lors du polling des commandes");
        }
    }

    private async Task HandleCommandAsync(BridgeCommand command)
    {
        var response = new BridgeResponse { CommandId = command.CommandId };

        try
        {
            switch (command.ParsedCommand)
            {
                case CommandType.StartListening:
                    var name = command.Parameters?.TryGetValue("session_name", out var n) == true ? n?.ToString() : null;
                    var session = await _sessionManager.StartSessionAsync(name);
                    response.Data = new { session_id = session.Id, started_at = session.StartedAt };
                    break;

                case CommandType.StopListening:
                    var stopped = await _sessionManager.StopSessionAsync();
                    response.Data = new { session_id = stopped.Id, ended_at = stopped.EndedAt, duration_seconds = stopped.Duration.TotalSeconds };
                    break;

                case CommandType.PauseListening:
                    await _sessionManager.PauseSessionAsync();
                    response.Data = new { status = "paused" };
                    break;

                case CommandType.ResumeListening:
                    await _sessionManager.ResumeSessionAsync();
                    response.Data = new { status = "listening" };
                    break;

                case CommandType.GetStatus:
                    response.Data = BuildCurrentStatus();
                    break;

                case CommandType.GetLastSummary:
                    var summary = _sessionManager.CurrentSession?.Summary;
                    response.Data = summary;
                    break;

                case CommandType.Shutdown:
                    response.Data = new { message = "VoxMind s'arrête..." };
                    await WriteJsonSafeAsync(_statusFile, response);
                    _lifetime?.StopApplication();
                    break;

                case CommandType.StartRemoteListening:
                    // Identifier le client (par ID ou par nom)
                    string? rclientId = command.Parameters?.TryGetValue("client_id", out var rcid) == true ? rcid?.ToString() : null;
                    string? rclientName = command.Parameters?.TryGetValue("client_name", out var rcname) == true ? rcname?.ToString() : null;

                    if (string.IsNullOrEmpty(rclientId) && string.IsNullOrEmpty(rclientName))
                    {
                        response.Status = "error";
                        response.Error = "client_id ou client_name requis pour START_REMOTE_LISTENING.";
                        response.ErrorCode = ErrorCodes.ErrorGeneral;
                        break;
                    }

                    var rclient = !string.IsNullOrEmpty(rclientId)
                        ? _registry.Get(rclientId)
                        : _registry.GetByName(rclientName!);

                    if (rclient is null)
                    {
                        response.Status = "error";
                        response.Error = $"Client '{rclientId ?? rclientName}' non connecté.";
                        response.ErrorCode = ErrorCodes.ErrorGeneral;
                        break;
                    }

                    var rSource = command.Parameters?.TryGetValue("source", out var rsrc) == true
                        ? rsrc?.ToString() ?? "microphone"
                        : "microphone";

                    // Démarrer la session distante côté serveur
                    var rsession = await _sessionManager.StartRemoteListeningAsync(rclient.Name);

                    // Notifier le client via son canal de commandes
                    var startCmd = new VoxMind.Grpc.ServerCommand
                    {
                        Command = "START",
                        PayloadJson = JsonSerializer.Serialize(new
                        {
                            session_id = rsession.Id.ToString(),
                            source = rSource
                        })
                    };
                    rclient.CommandChannel.Writer.TryWrite(startCmd);

                    response.Data = new
                    {
                        session_id = rsession.Id,
                        started_at = rsession.StartedAt,
                        client_id = rclient.ClientId,
                        client_name = rclient.Name,
                        message = $"Écoute distante démarrée sur '{rclient.Name}'"
                    };
                    break;

                case CommandType.StopRemoteListening:
                    var rs = await _sessionManager.StopSessionAsync();
                    response.Data = new { session_id = rs.Id, ended_at = rs.EndedAt, duration_seconds = rs.Duration.TotalSeconds };
                    break;

                case CommandType.ListRemoteClients:
                    response.Data = _registry.GetAll().Select(c => new { c.ClientId, c.Name, c.Platform, c.RegisteredAt, c.LastHeartbeatAt });
                    break;

                default:
                    response.Status = "error";
                    response.Error = $"Commande '{command.Command}' non gérée par FileBridge.";
                    response.ErrorCode = ErrorCodes.ErrorGeneral;
                    break;
            }
        }
        catch (InvalidOperationException ex)
        {
            response.Status = "error";
            response.Error = ex.Message;
            response.ErrorCode = ErrorCodes.ErrorSession;
            _logger.LogWarning(ex, "Erreur de session lors du traitement de {Command}", command.Command);
        }
        catch (Exception ex)
        {
            response.Status = "error";
            response.Error = ex.Message;
            response.ErrorCode = ErrorCodes.ErrorGeneral;
            _logger.LogError(ex, "Erreur lors du traitement de {Command}", command.Command);
        }

        await WriteJsonSafeAsync(_statusFile, response);
    }

    private SystemStatus BuildCurrentStatus()
    {
        var session = _sessionManager.CurrentSession;
        return new SystemStatus
        {
            Status = _sessionManager.Status.ToString().ToLowerInvariant(),
            UptimeSeconds = (DateTime.UtcNow - _startedAt).TotalSeconds,
            CurrentSession = session is null ? null : new CurrentSessionStatus
            {
                Id = session.Id.ToString(),
                Name = session.Name,
                StartedAt = session.StartedAt,
                ElapsedSeconds = session.Duration.TotalSeconds,
                SegmentsProcessed = session.SegmentCount
            },
            Compute = new ComputeStatus
            {
                Backend = ComputeBackendDetector.DetectBestAvailable().ToString()
            },
            LastActivity = DateTime.UtcNow,
            VoxMindVersion = "1.0.0"
        };
    }

    private static string ReadFileSafe(string path)
    {
        for (int retry = 0; retry < 3; retry++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            catch (IOException) when (retry < 2)
            {
                Thread.Sleep(50);
            }
        }
        return string.Empty;
    }

    private static async Task WriteJsonSafeAsync(string path, object data)
    {
        var json = JsonSerializer.Serialize(data, new JsonSerializerOptions { WriteIndented = true });
        var tmp = path + ".tmp";
        await File.WriteAllTextAsync(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer?.Dispose();
        _statusTimer?.Dispose();
    }
}
