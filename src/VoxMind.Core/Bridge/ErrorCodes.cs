namespace VoxMind.Core.Bridge;

public static class ErrorCodes
{
    public const int Success              = 0;
    public const int ErrorGeneral         = 1;
    public const int ErrorAudioDevice     = 2;
    public const int ErrorAudioDisconnected = 3;
    public const int ErrorMlModel         = 4;
    public const int ErrorTranscription   = 5;
    public const int ErrorSpeakerId       = 6;
    public const int ErrorDatabase        = 7;
    public const int ErrorSession         = 8;
    public const int ErrorBridge          = 9;
    public const int ErrorPyAnnote        = 10;
    public const int ErrorUnknown         = 99;
}
