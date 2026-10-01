namespace VectorNNTP.StorageServer.Storage.Engine;

/// <summary>
/// Classifies a <see cref="System.IO.FileStream.Flush(bool)"/> failure that may already
/// have extended a file. Callers inspect the file length and the bytes at the append offset.
/// </summary>
internal static class AmbiguousAppend
{
    internal enum Growth
    {
        /// <summary>File length did not advance past the append start.</summary>
        NoGrowth = 0,

        /// <summary>The file grew, but the expected frame or record is not fully present.</summary>
        IncompleteGrowth = 1,

        /// <summary>The expected bytes are present at the append offset.</summary>
        CompleteExpected = 2,
    }

    /// <summary>
    /// Compares the observed file length and the bytes read at <paramref name="startOffset"/>
    /// with the payload that was written.
    /// </summary>
    /// <remarks>
    /// A longer file whose prefix matches is still <see cref="Growth.CompleteExpected"/>.
    /// The caller truncates only the unexpected suffix. A mismatched prefix is incomplete.
    /// </remarks>
    internal static Growth Classify(
        long startOffset,
        long lengthNow,
        ReadOnlySpan<byte> expected,
        ReadOnlySpan<byte> observedAtOffset)
    {
        if (expected.Length == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(expected));
        }

        if (lengthNow <= startOffset)
        {
            return Growth.NoGrowth;
        }

        if (lengthNow < startOffset + expected.Length
            || observedAtOffset.Length != expected.Length
            || !observedAtOffset.SequenceEqual(expected))
        {
            return Growth.IncompleteGrowth;
        }

        return Growth.CompleteExpected;
    }
}
