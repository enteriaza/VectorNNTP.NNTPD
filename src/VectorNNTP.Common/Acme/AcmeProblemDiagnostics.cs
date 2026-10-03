using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VectorNNTP.Common.Acme.Protocol;

namespace VectorNNTP.Common.Acme
{
    /// <summary>
    /// Formats ACME CA / problem details into sanitized diagnostic strings
    /// (no account keys, tokens, passwords, or PEM material).
    /// </summary>
    internal static partial class AcmeProblemDiagnostics
    {
        /// <summary>Maximum characters kept from one sanitized field before an ellipsis is appended.</summary>
        private const int MaxFieldLength = 400;

        /// <summary>Formats any exception into a safe ACME diagnostic fragment.</summary>
        /// <param name="exception">Failure to describe. An <see cref="AcmeCaException"/> includes problem fields; any other type contributes only its type name.</param>
        /// <returns>A single-line diagnostic with PEM and long tokens redacted.</returns>
        internal static string FormatException(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            if (exception is AcmeCaException ca)
            {
                return FormatCaException(ca);
            }

            return exception.GetType().Name;
        }

        /// <summary>Formats an ACME CA exception / problem document.</summary>
        /// <param name="exception">CA failure. When type, detail, and status are all empty, the sanitized message is used.</param>
        /// <returns>The formatted problem text, or <c>AcmeCaException</c> when nothing usable remains.</returns>
        private static string FormatCaException(AcmeCaException exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            if (string.IsNullOrWhiteSpace(exception.ErrorType)
                && string.IsNullOrWhiteSpace(exception.Detail)
                && exception.StatusCode is null)
            {
                var fallback = SanitizeText(exception.Message);
                return string.IsNullOrEmpty(fallback) ? nameof(AcmeCaException) : fallback;
            }

            return FormatProblemFields(
                preface: null,
                exception.ErrorType,
                exception.StatusCode,
                identifier: null,
                exception.Detail,
                subproblems: null);
        }

        /// <summary>Formats structured ACME problem fields (tests / readiness diagnostics).</summary>
        /// <param name="preface">Optional leading label. A non-empty value is written with a trailing colon.</param>
        /// <param name="type">Problem type. Omitted when null or whitespace.</param>
        /// <param name="status">HTTP status. Omitted when null.</param>
        /// <param name="identifier">Hostname. Omitted when it does not look like a DNS name.</param>
        /// <param name="detail">Problem detail. Omitted when null or whitespace after sanitizing.</param>
        /// <param name="subproblems">Up to five subproblems. <see langword="null"/> or empty omits them.</param>
        /// <returns>Space-separated <c>type=</c>, <c>status=</c>, <c>identifier=</c>, <c>detail=</c>, and <c>subproblem[n]=</c> fields.</returns>
        internal static string FormatProblemFields(
            string? preface,
            string? type,
            int? status,
            string? identifier,
            string? detail,
            IReadOnlyList<(string? Type, string? Detail, string? Identifier)>? subproblems)
        {
            var parts = new List<string>(6);
            if (!string.IsNullOrWhiteSpace(preface))
            {
                parts.Add(preface + ":");
            }

            if (!string.IsNullOrWhiteSpace(type))
            {
                parts.Add("type=" + SanitizeText(type));
            }

            if (status is { } code)
            {
                parts.Add("status=" + code.ToString(CultureInfo.InvariantCulture));
            }

            var sanitizedId = SanitizeHostname(identifier);
            if (sanitizedId is not null)
            {
                parts.Add("identifier=" + sanitizedId);
            }

            var sanitizedDetail = SanitizeText(detail);
            if (sanitizedDetail is not null)
            {
                parts.Add("detail=" + sanitizedDetail);
            }

            if (subproblems is { Count: > 0 })
            {
                for (var i = 0; i < subproblems.Count && i < 5; i++)
                {
                    var sub = subproblems[i];
                    var subType = SanitizeText(sub.Type) ?? "?";
                    var subDetail = SanitizeText(sub.Detail) ?? string.Empty;
                    var subId = SanitizeHostname(sub.Identifier);
                    parts.Add(
                        subId is null
                            ? $"subproblem[{i}]={subType} {subDetail}".TrimEnd()
                            : $"subproblem[{i}]={subId}:{subType} {subDetail}".TrimEnd());
                }
            }

            return parts.Count == 0 ? nameof(AcmeCaException) : string.Join(' ', parts);
        }

        /// <summary>Formats a failed authorization for operator diagnostics.</summary>
        /// <param name="authorization">Authorization whose domain and problem fields are included.</param>
        /// <returns>Text beginning with <c>DNS validation failed for</c>, plus problem fields when the view has them.</returns>
        internal static string FormatAuthorizationFailure(AcmeOrderReadiness.AuthorizationView authorization)
        {
            ArgumentNullException.ThrowIfNull(authorization);
            var domain = SanitizeHostname(authorization.Domain) ?? "unknown";
            var preface = "DNS validation failed for " + domain;
            if (string.IsNullOrWhiteSpace(authorization.ErrorType)
                && string.IsNullOrWhiteSpace(authorization.ErrorDetail))
            {
                return preface + ": status=invalid";
            }

            return FormatProblemFields(
                preface,
                authorization.ErrorType,
                authorization.ErrorStatus,
                authorization.Domain,
                authorization.ErrorDetail,
                subproblems: null);
        }

        /// <summary>Formats an invalid order when per-authz detail is unavailable.</summary>
        /// <param name="order">Order snapshot. The first authorization whose status is <c>invalid</c> is formatted; otherwise the result is <c>order status=invalid</c>.</param>
        /// <returns>The authorization failure text, or <c>order status=invalid</c>.</returns>
        internal static string FormatOrderInvalid(AcmeOrderReadiness.OrderView order)
        {
            ArgumentNullException.ThrowIfNull(order);
            foreach (var authz in order.Authorizations)
            {
                if (string.Equals(NormalizeStatus(authz.Status), "invalid", StringComparison.Ordinal))
                {
                    return FormatAuthorizationFailure(authz);
                }
            }

            return "order status=invalid";
        }

        /// <summary>Builds a timeout diagnostic summarizing the last observed states.</summary>
        /// <param name="order">Last poll snapshot. Status strings are sanitized; domains that fail hostname checks become <c>?</c>.</param>
        /// <returns><c>order=</c> plus one <c>authz[domain]=status</c> segment per authorization.</returns>
        internal static string FormatTimeout(AcmeOrderReadiness.OrderView order)
        {
            ArgumentNullException.ThrowIfNull(order);
            var builder = new StringBuilder();
            builder.Append("order=").Append(SanitizeText(order.Status) ?? "unknown");
            foreach (var authz in order.Authorizations)
            {
                builder.Append(" authz[")
                    .Append(SanitizeHostname(authz.Domain) ?? "?")
                    .Append("]=")
                    .Append(SanitizeText(authz.Status) ?? "?");
            }

            return builder.ToString();
        }

        /// <summary>Sanitizes free-form ACME text for logs and exception messages.</summary>
        /// <param name="text">Raw CA or exception text.</param>
        /// <returns><see langword="null"/> when <paramref name="text"/> is null or whitespace. Otherwise one line with PEM blocks and tokens of 80 or more characters redacted, truncated to <see cref="MaxFieldLength"/>.</returns>
        private static string? SanitizeText(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            var cleaned = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
            cleaned = PemBlockRegex().Replace(cleaned, "[redacted-pem]");
            cleaned = LongTokenRegex().Replace(cleaned, "[redacted-token]");
            if (cleaned.Length > MaxFieldLength)
            {
                cleaned = cleaned[..MaxFieldLength] + "...";
            }

            return cleaned;
        }

        /// <summary>Sanitizes a DNS hostname (rejects material that does not look like a name).</summary>
        /// <param name="hostname">Candidate name.</param>
        /// <returns>The trimmed, trailing-dot-stripped, lowercased name, or <see langword="null"/> when it is empty, longer than 253 characters, contains a space, or contains <c>begin </c> or <c>private</c>.</returns>
        private static string? SanitizeHostname(string? hostname)
        {
            if (string.IsNullOrWhiteSpace(hostname))
            {
                return null;
            }

            var cleaned = hostname.Trim().TrimEnd('.').ToLowerInvariant();
            if (cleaned.Length is 0 or > 253 || cleaned.Contains(' ', StringComparison.Ordinal))
            {
                return null;
            }

            if (cleaned.Contains("begin ", StringComparison.OrdinalIgnoreCase)
                || cleaned.Contains("private", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return cleaned;
        }

        /// <summary>Trims and lowercases an ACME status for comparison.</summary>
        /// <param name="status">Status string from an order or authorization.</param>
        /// <returns>An empty string when <paramref name="status"/> is null or whitespace.</returns>
        internal static string NormalizeStatus(string? status) =>
            string.IsNullOrWhiteSpace(status) ? string.Empty : status.Trim().ToLowerInvariant();

        /// <summary>Matches a PEM block so <see cref="SanitizeText"/> can replace it with <c>[redacted-pem]</c>.</summary>
        /// <returns>The compiled PEM matcher.</returns>
        [GeneratedRegex(
            @"-----BEGIN [^-]+-----.*?-----END [^-]+-----",
            RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
        private static partial Regex PemBlockRegex();

        /// <summary>Matches a token of 80 or more URL-safe characters so <see cref="SanitizeText"/> can replace it with <c>[redacted-token]</c>.</summary>
        /// <returns>The compiled token matcher.</returns>
        [GeneratedRegex(
            @"\b[A-Za-z0-9_-]{80,}\b",
            RegexOptions.CultureInvariant)]
        private static partial Regex LongTokenRegex();
    }
}
