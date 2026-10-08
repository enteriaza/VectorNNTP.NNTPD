using Microsoft.Extensions.Logging;

namespace VectorNNTP.StorageServer.Storage.Cache.Egress;

/// <summary>Source-generated diagnostics for the disposable egress cache.</summary>
internal static partial class ArticleEgressCacheLogMessages
{
    /// <summary>The cache could not prepare <c>live/</c> and will stay off for this process.</summary>
    [LoggerMessage(
        EventId = 3440,
        Level = LogLevel.Warning,
        Message = "Egress cache disabled (controlDir={ControlDir}, reason={Reason})")]
    public static partial void Disabled(ILogger logger, string ControlDir, string Reason);

    /// <summary>A cache write failed. Further fills are stopped for this process.</summary>
    [LoggerMessage(
        EventId = 3441,
        Level = LogLevel.Warning,
        Message = "Egress cache fills stopped after a write failure (controlDir={ControlDir})")]
    public static partial void FillsStopped(ILogger logger, string ControlDir, Exception exception);
}
