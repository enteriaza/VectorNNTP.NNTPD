using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;

namespace VectorNNTP.BackFiller.Retention;

/// <summary>
/// Outcome of a VATP OPEN against a pending RequestId.
/// </summary>
internal enum VatpOpenKind : byte
{
    /// <summary>RequestId+ArticleId matched; lease acquired; that RequestId consumed.</summary>
    Opened = 0,

    /// <summary>
    /// OPEN rejected (unknown/expired/cancelled/consumed/wrong ArticleId/reservation failed).
    /// Wrong ArticleId and reservation failure do not consume RequestId.
    /// </summary>
    Rejected = 1,
}

/// <summary>Result of <see cref="IArticleRetentionAuthority.TryOpenTransfer"/>.</summary>
internal readonly struct VatpOpenResult : IDisposable
{
    /// <summary>Pairs an OPEN classification with the lease that classification owns.</summary>
    /// <param name="kind"><see cref="VatpOpenKind.Opened"/> or <see cref="VatpOpenKind.Rejected"/>.</param>
    /// <param name="lease">
    /// Transfer lease when <paramref name="kind"/> is <see cref="VatpOpenKind.Opened"/>;
    /// <see langword="null"/> when rejected.
    /// </param>
    private VatpOpenResult(VatpOpenKind kind, VatpTransferLease? lease)
    {
        Kind = kind;
        Lease = lease;
    }

    /// <summary>Gets the OPEN classification.</summary>
    internal VatpOpenKind Kind { get; }

    /// <summary>Gets the transfer lease when <see cref="Kind"/> is <see cref="VatpOpenKind.Opened"/>.</summary>
    internal VatpTransferLease? Lease { get; }

    /// <summary>Creates an opened result.</summary>
    /// <param name="lease">Reader lease for the consumed RequestId.</param>
    /// <returns>A result whose <see cref="Kind"/> is <see cref="VatpOpenKind.Opened"/>.</returns>
    internal static VatpOpenResult Opened(VatpTransferLease lease) => new(VatpOpenKind.Opened, lease);

    /// <summary>Creates a rejected result (no lease).</summary>
    /// <returns>
    /// A result whose <see cref="Kind"/> is <see cref="VatpOpenKind.Rejected"/> and whose
    /// <see cref="Lease"/> is <see langword="null"/>.
    /// </returns>
    internal static VatpOpenResult Rejected() => new(VatpOpenKind.Rejected, null);

    /// <summary>Disposes <see cref="Lease"/> when this result opened a transfer.</summary>
    /// <remarks>
    /// A rejected result has no lease, so dispose does nothing. Disposing twice follows
    /// <see cref="VatpTransferLease.Dispose"/>, which runs the release callback only once.
    /// </remarks>
    public void Dispose() => Lease?.Dispose();
}

/// <summary>
/// Lease over a CanonicalV1 <see cref="ArticleRecord"/> for one VATP transfer.
/// Dispose releases the retention reader lease; it does not delete the Message-ID entry.
/// </summary>
internal sealed class VatpTransferLease : IDisposable
{
    /// <summary>
    /// Authority callback that drops the reader count. Invoked at most once.
    /// A null callback is ignored.
    /// </summary>
    private readonly Action? _release;

    /// <summary>0 until the first <see cref="Dispose"/>; 1 afterward.</summary>
    private int _disposed;

    /// <summary>Binds a retained record to the authority callback that drops its reader lease.</summary>
    /// <param name="record">CanonicalV1 record. ArtData stays owned by retention; this lease does not copy it.</param>
    /// <param name="selectedDateHeaderName">Date-family header name required for VATP META.</param>
    /// <param name="identity">Message-ID and ArticleId of the retained entry.</param>
    /// <param name="release">
    /// Called once from the first <see cref="Dispose"/>. The authority uses it to drop the reader
    /// and, when the entry was already logically removed and no readers remain, to release ArtData.
    /// </param>
    internal VatpTransferLease(
        ArticleRecord record,
        NntpArticleHeaderName selectedDateHeaderName,
        ArticleIdentity identity,
        Action release)
    {
        Record = record;
        SelectedDateHeaderName = selectedDateHeaderName;
        Identity = identity;
        _release = release;
    }

    /// <summary>Gets the retained CanonicalV1 record (ArtData owned by retention until release).</summary>
    internal ArticleRecord Record { get; }

    /// <summary>Gets the Date-family header name required for VATP META.</summary>
    internal NntpArticleHeaderName SelectedDateHeaderName { get; }

    /// <summary>Gets the Message-ID / ArticleId identity.</summary>
    internal ArticleIdentity Identity { get; }

    /// <summary>Releases the retention reader lease once. Does not delete the Message-ID entry.</summary>
    /// <remarks>
    /// The first call invokes <see cref="_release"/>. Later calls return without invoking it.
    /// When the authority supplied that callback, physical ArtData release happens only if the
    /// entry was already logically removed and this dispose drops the last reader.
    /// </remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _release?.Invoke();
    }
}
