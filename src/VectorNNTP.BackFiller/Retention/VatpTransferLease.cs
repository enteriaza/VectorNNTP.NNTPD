using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Articles.Parsing;

namespace VectorNNTP.BackFiller.Retention;

/// <summary>
/// Outcome of a VATP OPEN against a pending RequestId.
/// </summary>
public enum VatpOpenKind : byte
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
public readonly struct VatpOpenResult : IDisposable
{
    private VatpOpenResult(VatpOpenKind kind, VatpTransferLease? lease)
    {
        Kind = kind;
        Lease = lease;
    }

    /// <summary>Gets the OPEN classification.</summary>
    public VatpOpenKind Kind { get; }

    /// <summary>Gets the transfer lease when <see cref="Kind"/> is <see cref="VatpOpenKind.Opened"/>.</summary>
    public VatpTransferLease? Lease { get; }

    /// <summary>Creates an opened result.</summary>
    public static VatpOpenResult Opened(VatpTransferLease lease) => new(VatpOpenKind.Opened, lease);

    /// <summary>Creates a rejected result (no lease).</summary>
    public static VatpOpenResult Rejected() => new(VatpOpenKind.Rejected, null);

    /// <inheritdoc />
    public void Dispose() => Lease?.Dispose();
}

/// <summary>
/// Lease over a CanonicalV1 <see cref="ArticleRecord"/> for one VATP transfer.
/// Dispose releases the retention reader lease; it does not delete the Message-ID entry.
/// </summary>
public sealed class VatpTransferLease : IDisposable
{
    private readonly Action? _release;
    private int _disposed;

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
    public ArticleRecord Record { get; }

    /// <summary>Gets the Date-family header name required for VATP META.</summary>
    public NntpArticleHeaderName SelectedDateHeaderName { get; }

    /// <summary>Gets the Message-ID / ArticleId identity.</summary>
    public ArticleIdentity Identity { get; }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _release?.Invoke();
    }
}
