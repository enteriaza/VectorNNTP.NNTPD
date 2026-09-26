namespace VectorNNTP.BackFiller.Nntp;

/// <summary>Local lifecycle of one upstream NNTP provider session.</summary>
public enum NntpSessionState
{
    /// <summary>Constructed, no socket.</summary>
    Created = 0,

    /// <summary>TCP and optional TLS in progress.</summary>
    Connecting = 1,

    /// <summary>Transport open; greeting not yet accepted.</summary>
    Connected = 2,

    /// <summary>AUTHINFO USER/PASS in progress.</summary>
    Authenticating = 3,

    /// <summary>Idle and reusable.</summary>
    Ready = 4,

    /// <summary>Leased for one ARTICLE.</summary>
    Busy = 5,

    /// <summary>Being closed; must not be leased.</summary>
    Retiring = 6,

    /// <summary>Transport disposed.</summary>
    Closed = 7,
}
