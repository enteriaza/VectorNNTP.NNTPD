namespace VectorNNTP.BackFiller.Nntp;

/// <summary>Local lifecycle of one upstream NNTP provider session.</summary>
internal enum NntpSessionState
{
    /// <summary>Constructed, no socket.</summary>
    Created = 0,

    /// <summary>TCP connect, including optional implicit TLS, is in progress.</summary>
    Connecting = 1,

    /// <summary>
    /// Transport is open and the session is not yet <see cref="Ready"/>.
    /// Greeting, CAPABILITIES, and optional STARTTLS run in this state.
    /// </summary>
    Connected = 2,

    /// <summary>AUTHINFO USER/PASS in progress.</summary>
    Authenticating = 3,

    /// <summary>Idle and reusable.</summary>
    Ready = 4,

    /// <summary>Holds the exclusive command lock for one ARTICLE or DATE.</summary>
    Busy = 5,

    /// <summary>Unusable. Set while closing and when a command leaves the session unhealthy. Must not be leased.</summary>
    Retiring = 6,

    /// <summary>Transport disposed.</summary>
    Closed = 7,
}
