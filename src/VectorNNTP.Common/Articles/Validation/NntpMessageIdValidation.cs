using System.Runtime.CompilerServices;

namespace VectorNNTP.Common.Articles.Validation
{
    /// <summary>
    /// Canonical RFC 5536 §3.1.3 <c>msg-id</c> validation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The only entry point is <see cref="IsValidMessageId(ReadOnlySpan{byte})"/>. It allocates nothing.
    /// When at least 16 interior octets remain and the hardware supports <c>Vector128</c>, those octets
    /// are classified together; the scalar path covers shorter tokens and the tail.
    /// </para>
    /// <para>
    /// A token must be <see cref="MinMessageIdLength"/>–<see cref="MaxMessageIdLength"/> octets,
    /// begin with <c>&lt;</c>, and end with <c>&gt;</c>. The interior is
    /// <c>dot-atom-text "@" (dot-atom-text / no-fold-literal)</c>.
    /// <c>dot-atom-text</c> is <c>1*atext *("." 1*atext)</c> from RFC 5322 §3.2.3, which RFC 5536 cites.
    /// <c>no-fold-literal</c> is <c>"[" *mdtext "]"</c> with <c>mdtext</c> equal to
    /// <c>%d33-61 / %d63-90 / %d94-126</c>. The literal is not required to be an IP address.
    /// </para>
    /// <para>
    /// Quoted strings, comments, CFWS, and obsolete mailbox syntax are rejected.
    /// <see cref="MaxMessageIdLength"/> is the only Message-ID length limit.
    /// The grammar cannot accept a token shorter than <c>&lt;a@b&gt;</c>, so lengths 3 and 4 fail
    /// even though they are inside the length window.
    /// RFC 3977's opaque message-id description is not this grammar.
    /// </para>
    /// </remarks>
    public static class NntpMessageIdValidation
    {
        /// <summary>Minimum candidate length in octets, including the angle brackets.</summary>
        /// <remarks>
        /// RFC 5536 limits the maximum, not this minimum. Lengths below 3 are rejected here.
        /// Lengths 3 and 4 still fail <c>dot-atom-text "@" id-right</c>.
        /// </remarks>
        public const int MinMessageIdLength = 3;

        /// <summary>Maximum RFC 5536 <c>msg-id</c> length in octets, including the angle brackets.</summary>
        public const int MaxMessageIdLength = 250;

        /// <summary>
        /// Determines whether <paramref name="messageId"/> is an RFC 5536 <c>msg-id</c>.
        /// </summary>
        /// <param name="messageId">Candidate octets, including the angle brackets. An empty span is invalid.</param>
        /// <returns>
        /// <see langword="true"/> when the span is 3–250 octets and matches
        /// <c>"&lt;" dot-atom-text "@" (dot-atom-text / no-fold-literal) "&gt;"</c>.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsValidMessageId(ReadOnlySpan<byte> messageId)
        {
            var length = messageId.Length;
            if ((uint)(length - MinMessageIdLength) > (uint)(MaxMessageIdLength - MinMessageIdLength))
            {
                return false;
            }

            if (messageId[0] != (byte)'<' || messageId[length - 1] != (byte)'>')
            {
                return false;
            }

            return NntpMessageIdValidationSimd.IsRfc5536MsgId(messageId);
        }
    }
}
