namespace VectorNNTP.NNTPCancelMessage;

/// <summary>Process exit codes for the newsmaster utility.</summary>
internal static class AdminExitCode
{
    public const int Success = 0;
    public const int Usage = 1;
    public const int Configuration = 2;
    public const int Connection = 3;
    public const int HeadFailed = 4;
    public const int TraceFailed = 5;
    public const int CancelFailed = 6;
    public const int AuthenticationFailed = 7;
}
