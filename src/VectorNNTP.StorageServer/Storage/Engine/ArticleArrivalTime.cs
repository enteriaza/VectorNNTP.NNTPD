namespace VectorNNTP.StorageServer.Storage.Engine;

/// <summary>
/// UTC instant of local article acceptance. This is not a Message-ID or Date header.
/// </summary>
/// <remarks>
/// A stored arrival has offset zero and is not <see cref="DateTimeOffset.MinValue"/>.
/// Journal, segment, and index writers reject any other value. Decoders reject it as corruption.
/// Nothing substitutes a clock reading, a publication hint, or a fixed date for a missing instant.
/// </remarks>
internal static class ArticleArrivalTime
{
    /// <summary>True when <paramref name="value"/> is a UTC instant other than <see cref="DateTimeOffset.MinValue"/>.</summary>
    /// <param name="value">Candidate arrival.</param>
    /// <returns>True when the instant can be stored and recovered.</returns>
    internal static bool IsStored(DateTimeOffset value) =>
        value.Offset == TimeSpan.Zero && value != DateTimeOffset.MinValue;

    /// <summary>
    /// Returns <paramref name="value"/> as a UTC instant, or throws when it is not stored.
    /// </summary>
    /// <param name="value">Candidate arrival.</param>
    /// <param name="paramName">Parameter name for the exception.</param>
    /// <returns>The same instant with offset zero.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when <paramref name="value"/> is <see cref="DateTimeOffset.MinValue"/> or is not UTC.
    /// </exception>
    internal static DateTimeOffset RequireStored(DateTimeOffset value, string paramName)
    {
        if (!IsStored(value))
        {
            throw new ArgumentOutOfRangeException(
                paramName,
                "Article arrival must be a UTC instant other than DateTimeOffset.MinValue.");
        }

        return new DateTimeOffset(value.UtcTicks, TimeSpan.Zero);
    }

    /// <summary>Reads a stored UTC instant from little-endian ticks.</summary>
    /// <param name="utcTicks">Ticks written by a current journal, index, or segment frame.</param>
    /// <param name="value">The instant when this returns true.</param>
    /// <returns>False when the ticks are <see cref="DateTimeOffset.MinValue"/> or out of range.</returns>
    internal static bool TryStored(long utcTicks, out DateTimeOffset value)
    {
        value = default;
        if (utcTicks == DateTimeOffset.MinValue.UtcTicks)
        {
            return false;
        }

        try
        {
            value = new DateTimeOffset(utcTicks, TimeSpan.Zero);
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }

        return IsStored(value);
    }
}
