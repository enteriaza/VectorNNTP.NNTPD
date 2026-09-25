using System.Net;

namespace VectorNNTP.NNTPD.Authentication;

/// <summary>
/// Process-local atomic admission tracker. Production is a single NNTPD process;
/// cluster-wide Redis admission is not present in VectorNNTP.NNTPD.
/// </summary>
public sealed class InMemoryNntpSessionAdmissionTracker : INntpSessionAdmissionTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AccountAdmission> _accounts = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public NntpSessionAdmissionResult TryAdmit(
        string accountName,
        string sessionId,
        IPAddress sourceAddress,
        int sessionLimit,
        int srcIpLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(sourceAddress);

        var ip = FormatSourceAddress(sourceAddress);
        lock (_gate)
        {
            if (!_accounts.TryGetValue(accountName, out var account))
            {
                account = new AccountAdmission();
                _accounts[accountName] = account;
            }

            if (account.Sessions.TryGetValue(sessionId, out var existingIp))
            {
                _ = existingIp;
                return NntpSessionAdmissionResult.Success;
            }

            if (sessionLimit > 0 && account.Sessions.Count >= sessionLimit)
            {
                return NntpSessionAdmissionResult.MaxSessionsExceeded;
            }

            if (srcIpLimit > 0
                && !account.IpCounts.ContainsKey(ip)
                && account.IpCounts.Count >= srcIpLimit)
            {
                return NntpSessionAdmissionResult.IpLimitExceeded;
            }

            account.Sessions[sessionId] = ip;
            account.IpCounts[ip] = account.IpCounts.GetValueOrDefault(ip) + 1;
            return NntpSessionAdmissionResult.Success;
        }
    }

    /// <inheritdoc />
    public void Release(string accountName, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        lock (_gate)
        {
            if (!_accounts.TryGetValue(accountName, out var account))
            {
                return;
            }

            if (!account.Sessions.Remove(sessionId, out var ip))
            {
                return;
            }

            if (account.IpCounts.TryGetValue(ip, out var count))
            {
                if (count <= 1)
                {
                    account.IpCounts.Remove(ip);
                }
                else
                {
                    account.IpCounts[ip] = count - 1;
                }
            }

            if (account.Sessions.Count == 0)
            {
                _accounts.Remove(accountName);
            }
        }
    }

    /// <summary>Formats the admitted source identity. IPv4-mapped IPv6 becomes IPv4 text.</summary>
    internal static string FormatSourceAddress(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return normalized.ToString();
    }

    private sealed class AccountAdmission
    {
        public Dictionary<string, string> Sessions { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> IpCounts { get; } = new(StringComparer.Ordinal);
    }
}
