namespace VectorNNTP.Common.NntpDb
{
    /// <summary>
    /// Invalid NntpDB configuration that must fail startup immediately.
    /// </summary>
    /// <remarks>
    /// Messages include the MySqlConnector parse reason and never include the
    /// connection string or credentials.
    /// </remarks>
    internal sealed class NntpDbConfigurationException : Exception
    {
        /// <summary>Initializes a new instance of the <see cref="NntpDbConfigurationException"/> class.</summary>
        /// <param name="reason">Parser or configuration reason without secrets.</param>
        internal NntpDbConfigurationException(string reason)
            : this(reason, innerException: null)
        {
        }

        /// <summary>Initializes a new instance of the <see cref="NntpDbConfigurationException"/> class.</summary>
        /// <param name="reason">Parser or configuration reason without secrets.</param>
        /// <param name="innerException">Optional MySqlConnector parse exception.</param>
        internal NntpDbConfigurationException(string reason, Exception? innerException)
            : base(CreateMessage(reason), innerException)
        {
            Reason = reason;
        }

        /// <summary>Gets the MySqlConnector parse or configuration reason without secrets.</summary>
        internal string Reason { get; }

        /// <summary>
        /// Builds the exception message from <paramref name="reason"/> without appending a connection string.
        /// A blank reason becomes a fixed invalid-connection-string sentence.
        /// </summary>
        /// <param name="reason">Parser or configuration reason without secrets.</param>
        /// <returns>The exception message.</returns>
        private static string CreateMessage(string reason) =>
            string.IsNullOrWhiteSpace(reason)
                ? "NntpDB connection string is invalid."
                : "NntpDB connection string is invalid: " + reason;
    }
}
