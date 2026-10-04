namespace VectorNNTP.Common.NntpDb
{
    /// <summary>MySQL infrastructure failure that is not an authentication error.</summary>
    public sealed class NntpDbUnavailableException : Exception
    {
        /// <summary>Initializes a new instance of the <see cref="NntpDbUnavailableException"/> class.</summary>
        /// <param name="message">Failure description. Must not include a connection string or credentials.</param>
        public NntpDbUnavailableException(string message)
            : base(message)
        {
        }

        /// <summary>Initializes a new instance of the <see cref="NntpDbUnavailableException"/> class.</summary>
        /// <param name="message">Failure description. Must not include a connection string or credentials.</param>
        /// <param name="innerException">The provider exception.</param>
        public NntpDbUnavailableException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }

    /// <summary>MySQL authentication or authorization failure.</summary>
    public sealed class NntpDbAuthenticationException : Exception
    {
        /// <summary>Initializes a new instance of the <see cref="NntpDbAuthenticationException"/> class.</summary>
        /// <param name="message">Failure description. Must not include a connection string or credentials.</param>
        public NntpDbAuthenticationException(string message)
            : base(message)
        {
        }

        /// <summary>Initializes a new instance of the <see cref="NntpDbAuthenticationException"/> class.</summary>
        /// <param name="message">Failure description. Must not include a connection string or credentials.</param>
        /// <param name="innerException">The provider exception.</param>
        public NntpDbAuthenticationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
