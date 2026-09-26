using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Certes;

namespace VectorNNTP.NNTPD.Acme;

/// <summary>
/// Formats Certes / ACME problem details into sanitized diagnostic strings
/// (no account keys, tokens, passwords, or PEM material).
/// </summary>
internal static partial class AcmeProblemDiagnostics
{
    private const int MaxFieldLength = 400;

    /// <summary>Formats any exception into a safe ACME diagnostic fragment.</summary>
    public static string FormatException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is AcmeRequestException request)
        {
            return FormatAcmeRequest(request);
        }

        if (exception is Certes.AcmeException)
        {
            var message = SanitizeText(exception.Message);
            return string.IsNullOrEmpty(message) ? exception.GetType().Name : message;
        }

        return exception.GetType().Name;
    }

    /// <summary>Formats a Certes <see cref="AcmeRequestException"/> problem document.</summary>
    public static string FormatAcmeRequest(AcmeRequestException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var error = exception.Error;
        if (error is null)
        {
            var fallback = SanitizeText(exception.Message);
            return string.IsNullOrEmpty(fallback) ? "AcmeRequestException" : fallback;
        }

        return FormatAcmeError(error, preface: null);
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

        var error = new Certes.Acme.AcmeError
        {
            Type = authorization.ErrorType,
            Detail = authorization.ErrorDetail,
            Status = authorization.ErrorStatus is { } code
                ? (HttpStatusCode)code
                : default,
            Identifier = authorization.Domain is null
                ? null
                : new Certes.Acme.Resource.Identifier
                {
                    Type = Certes.Acme.Resource.IdentifierType.Dns,
                    Value = authorization.Domain,
                },
        };
        return FormatAcmeError(error, preface);
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

    private static string FormatAcmeError(Certes.Acme.AcmeError error, string? preface)
    {
        var parts = new List<string>(6);
        if (!string.IsNullOrWhiteSpace(preface))
        {
            parts.Add(preface + ":");
        }

        if (!string.IsNullOrWhiteSpace(error.Type))
        {
            parts.Add("type=" + SanitizeText(error.Type));
        }

        if (error.Status != default)
        {
            parts.Add(
                "status=" + ((int)error.Status).ToString(CultureInfo.InvariantCulture));
        }

        var identifier = SanitizeHostname(error.Identifier?.Value);
        if (identifier is not null)
        {
            parts.Add("identifier=" + identifier);
        }

        var detail = SanitizeText(error.Detail);
        if (detail is not null)
        {
            parts.Add("detail=" + detail);
        }

        if (error.Subproblems is { Count: > 0 })
        {
            for (var i = 0; i < error.Subproblems.Count && i < 5; i++)
            {
                var sub = error.Subproblems[i];
                var subType = SanitizeText(sub.Type) ?? "?";
                var subDetail = SanitizeText(sub.Detail) ?? string.Empty;
                var subId = SanitizeHostname(sub.Identifier?.Value);
                parts.Add(
                    subId is null
                        ? $"subproblem[{i}]={subType} {subDetail}".TrimEnd()
                        : $"subproblem[{i}]={subId}:{subType} {subDetail}".TrimEnd());
            }
        }

        return parts.Count == 0 ? "AcmeRequestException" : string.Join(' ', parts);
    }

    [GeneratedRegex(
        @"-----BEGIN [^-]+-----.*?-----END [^-]+-----",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex PemBlockRegex();

    [GeneratedRegex(
        @"\b[A-Za-z0-9_-]{80,}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex LongTokenRegex();
}
