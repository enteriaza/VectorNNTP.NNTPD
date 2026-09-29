using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using VectorNNTP.NNTPD.Acme.Protocol;

namespace VectorNNTP.NNTPD.Acme;

/// <summary>
/// Formats ACME CA / problem details into sanitized diagnostic strings
/// (no account keys, tokens, passwords, or PEM material).
/// </summary>
internal static partial class AcmeProblemDiagnostics
{
    private const int MaxFieldLength = 400;

    /// <summary>Formats any exception into a safe ACME diagnostic fragment.</summary>
    public static string FormatException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is AcmeCaException ca)
        {
            return FormatCaException(ca);
        }

        return exception.GetType().Name;
    }

    /// <summary>Formats an ACME CA exception / problem document.</summary>
    public static string FormatCaException(AcmeCaException exception)
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
    public static string FormatProblemFields(
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
    public static string FormatAuthorizationFailure(AcmeOrderReadiness.AuthorizationView authorization)
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
    public static string FormatOrderInvalid(AcmeOrderReadiness.OrderView order)
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
    public static string FormatTimeout(AcmeOrderReadiness.OrderView order)
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
    public static string? SanitizeText(string? text)
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
    public static string? SanitizeHostname(string? hostname)
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

    internal static string NormalizeStatus(string? status) =>
        string.IsNullOrWhiteSpace(status) ? string.Empty : status.Trim().ToLowerInvariant();

    [GeneratedRegex(
        @"-----BEGIN [^-]+-----.*?-----END [^-]+-----",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex PemBlockRegex();

    [GeneratedRegex(
        @"\b[A-Za-z0-9_-]{80,}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex LongTokenRegex();
}
