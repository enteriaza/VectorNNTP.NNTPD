using System.Runtime.CompilerServices;

namespace VectorNNTP.Common.Articles.Validation
{
    /// <summary>
    /// Scalar character classes for an RFC 5536 <c>msg-id</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>atext</c> is the RFC 5322 §3.2.3 set cited by RFC 5536: ASCII letters and digits, plus
    /// <c>!#$%&amp;'*+-/=?^_`{|}~</c>. Specials, including <c>.</c>, <c>@</c>, quotes, parentheses,
    /// comma, colon, semicolon, brackets, and backslash, are not <c>atext</c>.
    /// </para>
    /// <para>
    /// <c>mdtext</c> is RFC 5536 <c>%d33-61 / %d63-90 / %d94-126</c>. That excludes SP, controls,
    /// DEL, non-ASCII, <c>&gt;</c>, <c>[</c>, <c>]</c>, and <c>\</c>.
    /// </para>
    /// </remarks>
    internal static class NntpMessageIdCharClasses
    {
        /// <summary>
        /// Returns whether <paramref name="value"/> is RFC 5322 <c>atext</c>.
        /// </summary>
        /// <param name="value">Candidate octet.</param>
        /// <returns><see langword="true"/> for an atom character. <c>.</c> and <c>@</c> are not atom characters.</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsAtext(byte value) =>
            value == 0x21
            || (uint)(value - 0x23) <= (uint)(0x27 - 0x23)
            || (uint)(value - 0x2A) <= (uint)(0x2B - 0x2A)
            || value == 0x2D
            || value == 0x2F
            || (uint)(value - 0x30) <= 9u
            || value == 0x3D
            || value == 0x3F
            || (uint)(value - 0x41) <= (uint)(0x5A - 0x41)
            || (uint)(value - 0x5E) <= (uint)(0x7E - 0x5E);

        /// <summary>
        /// Returns whether <paramref name="value"/> is RFC 5536 <c>mdtext</c>.
        /// </summary>
        /// <param name="value">Candidate octet inside a <c>no-fold-literal</c>.</param>
        /// <returns>
        /// <see langword="true"/> for <c>%d33-61</c>, <c>%d63-90</c>, or <c>%d94-126</c>.
        /// <c>@</c> and <c>&lt;</c> are included. <c>&gt;</c>, <c>[</c>, <c>]</c>, and <c>\</c> are not.
        /// </returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool IsMdtext(byte value) =>
            (uint)(value - 33) <= (uint)(61 - 33)
            || (uint)(value - 63) <= (uint)(90 - 63)
            || (uint)(value - 94) <= (uint)(126 - 94);
    }
}
