namespace VectorNNTP.NNTPD.NntpDb;

/// <summary>MySQL infrastructure failure that is not an authentication error.</summary>
public sealed class NntpDbUnavailableException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="NntpDbUnavailableException"/> class.</summary>
    public NntpDbUnavailableException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="NntpDbUnavailableException"/> class.</summary>
    public NntpDbUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// Invalid NntpDB configuration that must fail startup immediately.
/// </summary>
/// <remarks>
/// Messages include the MySqlConnector parse reason and never include the
/// connection string or credentials.
/// </remarks>
public sealed class NntpDbConfigurationException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="NntpDbConfigurationException"/> class.</summary>
    /// <param name="reason">Parser or configuration reason without secrets.</param>
    public NntpDbConfigurationException(string reason)
        : this(reason, innerException: null)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="NntpDbConfigurationException"/> class.</summary>
    /// <param name="reason">Parser or configuration reason without secrets.</param>
    /// <param name="innerException">Optional MySqlConnector parse exception.</param>
    public NntpDbConfigurationException(string reason, Exception? innerException)
        : base(CreateMessage(reason), innerException)
    {
        Reason = reason;
    }

    /// <summary>Gets the MySqlConnector parse or configuration reason without secrets.</summary>
    public string Reason { get; }

    private static string CreateMessage(string reason) =>
        string.IsNullOrWhiteSpace(reason)
            ? "NntpDB connection string is invalid."
            : "NntpDB connection string is invalid: " + reason;
}

/// <summary>MySQL authentication or authorization failure.</summary>
public sealed class NntpDbAuthenticationException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="NntpDbAuthenticationException"/> class.</summary>
    public NntpDbAuthenticationException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="NntpDbAuthenticationException"/> class.</summary>
    public NntpDbAuthenticationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
