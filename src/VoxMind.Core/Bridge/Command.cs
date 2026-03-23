using System.Text.Json.Serialization;

namespace VoxMind.Core.Bridge;

public enum CommandType
{
    StartListening,
    StopListening,
    PauseListening,
    ResumeListening,
    GetStatus,
    GetSession,
    ListSessions,
    GetSummary,
    GetLastSummary,
    TranscribeFile,
    SaveSpeaker,
    MergeSpeakers,
    LinkSpeakers,
    RenameSpeaker,
    DeleteSpeaker,
    ListSpeakers,
    GetSpeaker,
    ImportSpeaker,
    ExportSpeaker,
    Shutdown,
    StartRemoteListening,
    StopRemoteListening,
    ListRemoteClients
}

public class BridgeCommand
{
    [JsonPropertyName("command_id")]
    public string CommandId { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("parameters")]
    public Dictionary<string, object?>? Parameters { get; set; }

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public CommandType ParsedCommand => Command.ToUpperInvariant() switch
    {
        "START_LISTENING" => CommandType.StartListening,
        "STOP_LISTENING" => CommandType.StopListening,
        "PAUSE_LISTENING" => CommandType.PauseListening,
        "RESUME_LISTENING" => CommandType.ResumeListening,
        "GET_STATUS" => CommandType.GetStatus,
        "GET_SESSION" => CommandType.GetSession,
        "LIST_SESSIONS" => CommandType.ListSessions,
        "GET_SUMMARY" => CommandType.GetSummary,
        "GET_LAST_SUMMARY" => CommandType.GetLastSummary,
        "TRANSCRIBE_FILE" => CommandType.TranscribeFile,
        "SAVE_SPEAKER" => CommandType.SaveSpeaker,
        "MERGE_SPEAKERS" => CommandType.MergeSpeakers,
        "LINK_SPEAKERS" => CommandType.LinkSpeakers,
        "RENAME_SPEAKER" => CommandType.RenameSpeaker,
        "DELETE_SPEAKER" => CommandType.DeleteSpeaker,
        "LIST_SPEAKERS" => CommandType.ListSpeakers,
        "GET_SPEAKER" => CommandType.GetSpeaker,
        "IMPORT_SPEAKER" => CommandType.ImportSpeaker,
        "EXPORT_SPEAKER" => CommandType.ExportSpeaker,
        "SHUTDOWN" => CommandType.Shutdown,
        "START_REMOTE_LISTENING" => CommandType.StartRemoteListening,
        "STOP_REMOTE_LISTENING" => CommandType.StopRemoteListening,
        "LIST_REMOTE_CLIENTS" => CommandType.ListRemoteClients,
        _ => throw new ArgumentException($"Commande inconnue : {Command}")
    };
}

public class BridgeResponse
{
    [JsonPropertyName("response_id")]
    public string ResponseId { get; set; } = Guid.NewGuid().ToString();

    [JsonPropertyName("command_id")]
    public string CommandId { get; set; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; set; } = "success";    // "success" | "error"

    [JsonPropertyName("data")]
    public object? Data { get; set; }

    [JsonPropertyName("error")]
    public string? Error { get; set; }

    [JsonPropertyName("error_code")]
    public int ErrorCode { get; set; } = ErrorCodes.Success;

    [JsonPropertyName("timestamp")]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}
