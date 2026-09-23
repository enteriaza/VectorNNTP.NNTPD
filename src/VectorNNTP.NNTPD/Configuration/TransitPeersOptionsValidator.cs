using System.Globalization;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Transit;

namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// Validates top-level <see cref="TransitPeersOptions"/> at bind / startup / reload.
/// </summary>
/// <remarks>
/// Failures name the peer and property. A malformed peer is rejected entirely; it never
/// enters the active snapshot. Passwords are never included in failure messages.
/// </remarks>
public sealed class TransitPeersOptionsValidator : IValidateOptions<TransitPeersOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, TransitPeersOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!TryValidate(options, out var failures))
        {
            return ValidateOptionsResult.Fail(failures);
        }

        return ValidateOptionsResult.Success;
    }

    /// <summary>Validates options and returns property-qualified failure messages.</summary>
    public static bool TryValidate(TransitPeersOptions options, out IReadOnlyList<string> failures)
    {
        ArgumentNullException.ThrowIfNull(options);
        var list = new List<string>();
        var parsed = new List<(string Name, TransitPeerOptions Options, List<TransitAllowFromEntry> AllowFrom)>();

        foreach (var (peerName, peer) in options)
        {
            if (!TryValidatePeerName(peerName, out var nameError))
            {
                list.Add(nameError);
                continue;
            }

            if (peer is null)
            {
                list.Add(Format(peerName, null, "peer configuration must not be null."));
                continue;
            }

            ValidatePeer(peerName, peer, list, parsed);
        }

        if (list.Count == 0)
        {
            ValidateOverlaps(parsed, list);
        }

        failures = list;
        return list.Count == 0;
    }

    private static void ValidatePeer(
        string peerName,
        TransitPeerOptions peer,
        List<string> failures,
        List<(string Name, TransitPeerOptions Options, List<TransitAllowFromEntry> AllowFrom)> parsed)
    {
        ValidateConnectionLimit(peerName, nameof(TransitPeerOptions.MaxIncomingConnections), peer.MaxIncomingConnections, failures);
        ValidateConnectionLimit(peerName, nameof(TransitPeerOptions.MaxOutgoingConnections), peer.MaxOutgoingConnections, failures);
        ValidateMaxSize(peerName, peer.MaxSize, failures);
        ValidatePathToken(peerName, peer.PathToken, failures);

        ValidatePairedCredentials(peerName, peer, failures);

        if (!TransitMessageTypesParser.TryParse(peer.MessageTypes, out _, out var messageTypesError))
        {
            failures.Add(Format(peerName, nameof(TransitPeerOptions.MessageTypes), messageTypesError ?? "invalid MessageTypes."));
        }

        if (!TransitSslParser.TryParse(peer.Ssl, out _, out var sslError))
        {
            failures.Add(Format(peerName, nameof(TransitPeerOptions.Ssl), sslError!));
        }

        if (!NewsfeedsPattern.TryParse(peer.Patterns, out _, out var patternError))
        {
            failures.Add(Format(peerName, nameof(TransitPeerOptions.Patterns), patternError!));
        }

        var allowFrom = new List<TransitAllowFromEntry>();
        var entries = peer.AllowFrom ?? [];
        for (var i = 0; i < entries.Length; i++)
        {
            if (!TransitAllowFromEntry.TryParse(entries[i], out var entry, out var error) || entry is null)
            {
                failures.Add(Format(peerName, $"{nameof(TransitPeerOptions.AllowFrom)}[{i}]", error ?? "invalid AllowFrom entry."));
                continue;
            }

            allowFrom.Add(entry);
        }

        var connectTo = peer.ConnectTo ?? [];
        for (var i = 0; i < connectTo.Length; i++)
        {
            if (!TransitConnectEndpoint.TryParse(connectTo[i], out _, out var error))
            {
                failures.Add(Format(peerName, $"{nameof(TransitPeerOptions.ConnectTo)}[{i}]", error ?? "invalid ConnectTo endpoint."));
            }
        }

        parsed.Add((peerName, peer, allowFrom));
    }

    /// <summary>Maximum peer-name length in characters (human-readable labels).</summary>
    public const int MaxPeerNameLength = 256;

    /// <summary>Maximum PathToken length in characters.</summary>
    public const int MaxPathTokenLength = 255;

    private static bool TryValidatePeerName(string? peerName, out string error)
    {
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(peerName))
        {
            error = "Transit peer name must be non-empty and not whitespace-only.";
            return false;
        }

        if (peerName.Length > MaxPeerNameLength)
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"Transit peer name '{peerName}' exceeds the maximum length of {MaxPeerNameLength} characters.");
            return false;
        }

        foreach (var ch in peerName)
        {
            if (char.IsControl(ch))
            {
                error = string.Create(
                    CultureInfo.InvariantCulture,
                    $"Transit peer name '{peerName}' contains a control character.");
                return false;
            }
        }

        return true;
    }

    private static void ValidatePathToken(string peerName, string? pathToken, List<string> failures)
    {
        var value = pathToken ?? string.Empty;
        if (value.Length > MaxPathTokenLength)
        {
            failures.Add(
                Format(
                    peerName,
                    nameof(TransitPeerOptions.PathToken),
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"must be at most {MaxPathTokenLength} characters.")));
            return;
        }

        foreach (var ch in value)
        {
            if (char.IsControl(ch))
            {
                failures.Add(Format(peerName, nameof(TransitPeerOptions.PathToken), "must not contain control characters."));
                return;
            }
        }
    }

    private static void ValidatePairedCredentials(string peerName, TransitPeerOptions peer, List<string> failures)
    {
        var username = (peer.Username ?? string.Empty).Trim();
        var password = peer.Password ?? string.Empty;
        var hasUser = username.Length > 0;
        var hasPassword = password.Length > 0;
        if (hasUser == hasPassword)
        {
            return;
        }

        failures.Add(
            Format(
                peerName,
                hasUser ? nameof(TransitPeerOptions.Password) : nameof(TransitPeerOptions.Username),
                "Username and Password must both be set or both be blank."));
    }

    private static void ValidateMaxSize(string peerName, long maxSize, List<string> failures)
    {
        if (maxSize is < 1 or > TransitPeerOptions.MaxMaxSize)
        {
            failures.Add(
                Format(
                    peerName,
                    nameof(TransitPeerOptions.MaxSize),
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"must be an integer in the range 1–{TransitPeerOptions.MaxMaxSize}.")));
        }
    }

    private static void ValidateConnectionLimit(string peerName, string property, int? value, List<string> failures)
    {
        if (value is null)
        {
            failures.Add(Format(peerName, property, "is required."));
            return;
        }

        if (value < 0 || value > TransitPeerPolicy.MaxConnectionLimit)
        {
            failures.Add(
                Format(
                    peerName,
                    property,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"must be an integer in the range 0–{TransitPeerPolicy.MaxConnectionLimit}.")));
        }
    }

    private static void ValidateOverlaps(
        List<(string Name, TransitPeerOptions Options, List<TransitAllowFromEntry> AllowFrom)> parsed,
        List<string> failures)
    {
        for (var i = 0; i < parsed.Count; i++)
        {
            for (var j = i + 1; j < parsed.Count; j++)
            {
                if (TryDescribeOverlap(parsed[i], parsed[j], out var message))
                {
                    failures.Add(message);
                }
            }
        }
    }

    private static bool TryDescribeOverlap(
        (string Name, TransitPeerOptions Options, List<TransitAllowFromEntry> AllowFrom) left,
        (string Name, TransitPeerOptions Options, List<TransitAllowFromEntry> AllowFrom) right,
        out string message)
    {
        message = string.Empty;
        foreach (var leftEntry in left.AllowFrom)
        {
            foreach (var rightEntry in right.AllowFrom)
            {
                if (leftEntry is TransitAllowFromEntry.Hostname leftHost
                    && rightEntry is TransitAllowFromEntry.Hostname rightHost
                    && leftHost.DnsName.Equals(rightHost.DnsName, StringComparison.OrdinalIgnoreCase))
                {
                    message = string.Create(
                        CultureInfo.InvariantCulture,
                        $"Transit peers '{left.Name}' and '{right.Name}' have overlapping AllowFrom hostname '{leftHost.DnsName}'.");
                    return true;
                }

                if (leftEntry is TransitAllowFromEntry.Literal leftLiteral
                    && rightEntry is TransitAllowFromEntry.Literal rightLiteral
                    && leftLiteral.Prefix.Overlaps(rightLiteral.Prefix))
                {
                    message = string.Create(
                        CultureInfo.InvariantCulture,
                        $"Transit peers '{left.Name}' and '{right.Name}' have overlapping AllowFrom prefixes '{leftLiteral.Original}' and '{rightLiteral.Original}'.");
                    return true;
                }
            }
        }

        return false;
    }

    private static string Format(string peerName, string? property, string detail)
    {
        return property is null
            ? string.Create(CultureInfo.InvariantCulture, $"Transit:{peerName}: {detail}")
            : string.Create(CultureInfo.InvariantCulture, $"Transit:{peerName}:{property} {detail}");
    }
}
