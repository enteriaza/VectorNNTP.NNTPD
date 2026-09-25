using System.Net;

namespace VectorNNTP.NNTPD.SessionState;

/// <summary>
/// Process-local atomic admission tracker used by unit tests. Production cluster
/// session and source-IP admission uses <see cref="DistributedSessionStateTracker"/>.
/// </summary>
public sealed class InMemorySessionStateTracker : ISessionStateTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, AccountAdmission> _accounts = new(StringComparer.Ordinal);

    /// <summary>Gets how many admitted sessions this tracker actually released.</summary>
    internal int ReleaseCalls { get; private set; }

    /// <inheritdoc />
    public ValueTask<SessionAdmissionResult> TryAdmitAsync(
        string accountName,
        string sessionId,
        IPAddress sourceAddress,
        int sessionLimit,
        int srcIpLimit,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<SessionAdmissionResult>(
            TryAdmit(accountName, sessionId, sourceAddress, sessionLimit, srcIpLimit));
    }

    /// <inheritdoc />
    public ValueTask ReleaseAsync(
        string accountName,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Release(accountName, sessionId);
        return ValueTask.CompletedTask;
    }

    /// <summary>Synchronous admit used by existing in-process tests.</summary>
    public SessionAdmissionResult TryAdmit(
        string accountName,
        string sessionId,
        IPAddress sourceAddress,
        int sessionLimit,
        int srcIpLimit)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountName);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(sourceAddress);

        var ip = SourceAddressIdentity.Format(sourceAddress);
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
                return SessionAdmissionResult.Success;
            }

            if (sessionLimit > 0 && account.Sessions.Count >= sessionLimit)
            {
                return SessionAdmissionResult.SessionLimitExceeded;
            }

            if (srcIpLimit > 0
                && !account.IpCounts.ContainsKey(ip)
                && account.IpCounts.Count >= srcIpLimit)
            {
                return SessionAdmissionResult.SourceAddressLimitExceeded;
            }

            account.Sessions[sessionId] = ip;
            account.IpCounts[ip] = account.IpCounts.GetValueOrDefault(ip) + 1;
            return SessionAdmissionResult.Success;
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

            ReleaseCalls++;

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
    internal static string FormatSourceAddress(IPAddress address) => SourceAddressIdentity.Format(address);

    private sealed class AccountAdmission
    {
        public Dictionary<string, string> Sessions { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> IpCounts { get; } = new(StringComparer.Ordinal);
    }
}
