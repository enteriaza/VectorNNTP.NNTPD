namespace VectorNNTP.BackFiller.Hosting;

/// <summary>Named BackFiller startup stages used by the journal and tests.</summary>
internal static class BackFillerStartupStages
{
    /// <summary>Shared and application configuration has been bound and validated.</summary>
    internal const string Configuration = "configuration";

    /// <summary>A usable ACME certificate has been loaded or issued.</summary>
    internal const string AcmeCertificateReady = "acme-certificate-ready";

    /// <summary>The TLS listener has bound and is accepting.</summary>
    internal const string ListenerStarted = "listener-started";
}

/// <summary>Records BackFiller startup stages for deterministic tests.</summary>
internal interface IBackFillerStartupJournal
{
    /// <summary>Appends a stage name in call order.</summary>
    /// <param name="stage">Stage identifier.</param>
    void Record(string stage);

    /// <summary>Gets the recorded stages in order.</summary>
    IReadOnlyList<string> Stages { get; }
}

/// <summary>Thread-safe in-process startup journal.</summary>
internal sealed class BackFillerStartupJournal : IBackFillerStartupJournal
{
    private readonly List<string> _stages = [];
    private readonly object _gate = new();

    /// <inheritdoc />
    public void Record(string stage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stage);
        lock (_gate)
        {
            _stages.Add(stage);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<string> Stages
    {
        get
        {
            lock (_gate)
            {
                return [.. _stages];
            }
        }
    }
}
