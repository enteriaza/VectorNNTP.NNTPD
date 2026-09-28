namespace VectorNNTP.Common.Transport.ArticleTransfer;

/// <summary>
/// Runtime resource policy defaults for VATP. Not wire-protocol invariants.
/// </summary>
/// <remarks>
/// Applications bind these through options. Common exposes the defaults only.
/// Do not treat these as immutable protocol requirements.
/// </remarks>
public sealed class ArticleTransferLimits
{
    /// <summary>Creates limits with production-sensible defaults.</summary>
    public ArticleTransferLimits()
    {
    }

    /// <summary>Maximum concurrent transfer streams per connection (default 64).</summary>
    public int MaxStreamsPerConnection { get; init; } = 64;

    /// <summary>Initial per-stream send/receive WINDOW credit in bytes (default 256 KiB).</summary>
    public int InitialStreamWindowBytes { get; init; } = 256 * 1024;

    /// <summary>Default HELLO-advertised max DATA payload (default 64 KiB).</summary>
    public uint DefaultMaxFramePayload { get; init; } = VatpProtocol.DefaultMaxFramePayload;

    /// <summary>Maximum credit a stream may hold after WINDOW adds (default 64 MiB).</summary>
    public long MaxStreamCreditBytes { get; init; } = 64L * 1024 * 1024;

    /// <summary>Gets the shared default instance.</summary>
    public static ArticleTransferLimits Default { get; } = new();
}
