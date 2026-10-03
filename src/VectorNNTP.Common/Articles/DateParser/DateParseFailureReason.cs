namespace VectorNNTP.Common.Articles.DateParser
{
    /// <summary>
    /// Classifies why a candidate article date value could not be normalized into canonical UTC.
    /// </summary>
    public enum DateParseFailureReason
    {
        /// <summary>Parsing and canonicalization succeeded.</summary>
        None = 0,

        /// <summary>The candidate value was empty after trimming.</summary>
        Empty = 1,

        /// <summary>The candidate exceeded the configured maximum input length.</summary>
        TooLong = 2,

        /// <summary>The candidate contained bytes outside printable ASCII (<c>0x20..0x7E</c>).</summary>
        NonPrintableAscii = 3,

        /// <summary>Reserved for normalization paths that would need more temporary storage than was available.</summary>
        /// <remarks>The current implementation does not emit this value.</remarks>
        NormalizationBufferTooSmall = 4,

        /// <summary>A trailing timezone abbreviation was present but not recognized while strict abbreviation mode was enabled.</summary>
        UnknownTimezoneAbbreviation = 5,

        /// <summary>No supported parse strategy accepted the normalized value.</summary>
        ParseFailed = 6,
    }
}
