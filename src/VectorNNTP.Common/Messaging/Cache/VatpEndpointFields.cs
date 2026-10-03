using System.Diagnostics.CodeAnalysis;

namespace VectorNNTP.Common.Messaging.Cache
{
    /// <summary>
    /// Validates the structured VATP dial fields carried by Article Work and storage lookup.
    /// </summary>
    internal static class VatpEndpointFields
    {
        /// <summary>
        /// Returns whether <paramref name="fqdn"/> is a lowercase dotted DNS name.
        /// </summary>
        internal static bool IsCanonicalFqdn([NotNullWhen(true)] string? fqdn)
        {
            if (string.IsNullOrWhiteSpace(fqdn) || fqdn.Length > 253)
            {
                return false;
            }

            var start = 0;
            var labels = 0;
            for (var i = 0; i <= fqdn.Length; i++)
            {
                if (i != fqdn.Length && fqdn[i] != '.')
                {
                    continue;
                }

                var length = i - start;
                if (length is < 1 or > 63 || !IsLabel(fqdn.AsSpan(start, length)))
                {
                    return false;
                }

                labels++;
                start = i + 1;
            }

            return labels >= 2;
        }

        /// <summary>Returns whether <paramref name="port"/> is a TCP port.</summary>
        internal static bool IsCanonicalPort(int port) => port is >= 1 and <= 65535;

        /// <summary>
        /// Returns whether <paramref name="label"/> is a DNS label of lowercase letters, digits, and interior hyphens.
        /// </summary>
        /// <param name="label">One dot-separated label. The caller rejects empty labels and lengths outside 1..63.</param>
        /// <returns><see langword="false"/> when the first or last character is not alphanumeric, or any character is not <c>a-z</c>, <c>0-9</c>, or <c>-</c>.</returns>
        private static bool IsLabel(ReadOnlySpan<char> label)
        {
            if (!IsAlphaNumeric(label[0]) || !IsAlphaNumeric(label[^1]))
            {
                return false;
            }

            for (var i = 0; i < label.Length; i++)
            {
                var c = label[i];
                if (!IsAlphaNumeric(c) && c != '-')
                {
                    return false;
                }
            }

            return true;
        }

        /// <summary>Returns whether <paramref name="c"/> is <c>a-z</c> or <c>0-9</c>. Uppercase letters are rejected.</summary>
        /// <param name="c">Character from an FQDN label.</param>
        /// <returns><see langword="true"/> for a lowercase letter or digit.</returns>
        private static bool IsAlphaNumeric(char c) =>
            c is >= 'a' and <= 'z' or >= '0' and <= '9';
    }
}
