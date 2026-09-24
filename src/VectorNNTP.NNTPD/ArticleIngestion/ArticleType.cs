namespace VectorNNTP.NNTPD.ArticleIngestion;

/// <summary>
/// Diablo-mapped article classification flags used by the IHAVE reader.
/// </summary>
/// <remarks>
/// Bit values match <see cref="Configuration.TransitMessageTypes"/> except
/// <see cref="YEncoded"/>, which is the IHAVE name for Diablo <c>ARTTYPE_YENC</c>
/// / <c>yenc</c> (same bit as <see cref="Configuration.TransitMessageTypes.Yenc"/>).
/// Classification is deterministic and does not decode article bodies.
/// </remarks>
[Flags]
public enum ArticleType
{
    /// <summary>Diablo <c>none</c>.</summary>
    None = 0,

    /// <summary>Diablo <c>default</c> — ordinary text with no other marker.</summary>
    Default = 1 << 0,

    /// <summary>Diablo <c>control</c>.</summary>
    Control = 1 << 1,

    /// <summary>Diablo <c>cancel</c>.</summary>
    Cancel = 1 << 2,

    /// <summary>Diablo <c>mime</c>.</summary>
    Mime = 1 << 3,

    /// <summary>Diablo <c>binary</c> / <c>binaries</c>.</summary>
    Binary = 1 << 4,

    /// <summary>Diablo <c>uuencode</c>.</summary>
    UuEncode = 1 << 5,

    /// <summary>Diablo <c>base64</c>.</summary>
    Base64 = 1 << 6,

    /// <summary>
    /// Diablo <c>yenc</c> / IHAVE <c>YEncoded</c>. Detected from <c>=ybegin</c>; the
    /// stored body remains the received (wire yEnc) representation, not decoded.
    /// </summary>
    YEncoded = 1 << 7,

    /// <summary>Diablo <c>bommanews</c>.</summary>
    BommaNews = 1 << 8,

    /// <summary>Diablo <c>unidata</c>.</summary>
    UniData = 1 << 9,

    /// <summary>Diablo <c>multipart</c>.</summary>
    Multipart = 1 << 10,

    /// <summary>Diablo <c>html</c>.</summary>
    Html = 1 << 11,

    /// <summary>Diablo <c>ps</c>.</summary>
    PostScript = 1 << 12,

    /// <summary>Diablo <c>binhex</c>.</summary>
    BinHex = 1 << 13,

    /// <summary>Diablo <c>partial</c>.</summary>
    Partial = 1 << 14,

    /// <summary>Diablo <c>pgp</c>.</summary>
    PgpMessage = 1 << 15,
}
