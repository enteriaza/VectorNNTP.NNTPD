namespace VectorNNTP.BackFiller.Hosting
{
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
    /// <remarks>
    /// <see cref="Record"/> and <see cref="Stages"/> share <see cref="_gate"/>.
    /// Recorded names are kept in call order, including duplicates. There is no remove or reset operation.
    /// </remarks>
    internal sealed class BackFillerStartupJournal : IBackFillerStartupJournal
    {
        /// <summary>Stage names in <see cref="Record"/> call order.</summary>
        private readonly List<string> _stages = [];

        /// <summary>Lock for <see cref="_stages"/>.</summary>
        private readonly object _gate = new();

        /// <summary>Appends <paramref name="stage"/> under <see cref="_gate"/>.</summary>
        /// <param name="stage">Stage identifier. Must not be null or whitespace.</param>
        /// <exception cref="ArgumentException">Thrown when <paramref name="stage"/> is null or whitespace.</exception>
        public void Record(string stage)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(stage);
            lock (_gate)
            {
                _stages.Add(stage);
            }
        }

        /// <summary>Gets the recorded stages in order.</summary>
        /// <value>A copy taken under <see cref="_gate"/>. Later <see cref="Record"/> calls do not mutate the returned list.</value>
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
}
