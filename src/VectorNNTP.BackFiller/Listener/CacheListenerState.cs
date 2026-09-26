namespace VectorNNTP.BackFiller.Listener;

/// <summary>Local lifecycle of the cache Listener service.</summary>
public enum CacheListenerState
{
    /// <summary>Constructed and not yet started.</summary>
    Created = 0,

    /// <summary>Binding sockets and loading the TLS certificate.</summary>
    Starting = 1,

    /// <summary>Accepting connections.</summary>
    Running = 2,

    /// <summary>Accept stopped; admitted connections are draining.</summary>
    Retiring = 3,

    /// <summary>Sockets closed; service finished.</summary>
    Stopped = 4,
}
