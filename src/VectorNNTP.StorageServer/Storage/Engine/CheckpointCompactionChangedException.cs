namespace VectorNNTP.StorageServer.Storage.Engine;

/// <summary>
/// Compaction journal state changed while a checkpoint image was being built.
/// The replacement was not installed. The caller should try again on a later cycle.
/// </summary>
internal sealed class CheckpointCompactionChangedException : Exception
{
    /// <summary>Creates the deferral.</summary>
    /// <param name="message">Why this attempt was abandoned.</param>
    internal CheckpointCompactionChangedException(string message)
        : base(message)
    {
    }
}
