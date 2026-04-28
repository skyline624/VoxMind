namespace VoxMind.CLI.Commands;

public enum ExitCode
{
    Success = 0,
    GeneralError = 1,
    AudioDeviceError = 2,
    ModelError = 3,
    TranscriptionError = 4,
    DatabaseError = 5,
    SessionError = 6,
    BridgeError = 7,
    InvalidArguments = 8,
    NotListening = 9,
    AlreadyListening = 10,
    PyAnnoteError = 11,
    CanceledByUser = 12,
    TtsError = 13
}
