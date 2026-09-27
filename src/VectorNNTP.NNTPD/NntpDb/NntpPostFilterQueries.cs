namespace VectorNNTP.NNTPD.NntpDb;

/// <summary>Authoritative NntpDB queries for the cluster PostFilter policy.</summary>
/// <remarks>
/// NNTPD only <c>SELECT</c>s. Schema creation is an operator/DBA step
/// (see <c>docs/postfilter.md</c>). There is no application migration runner.
/// </remarks>
public static class NntpPostFilterQueries
{
    /// <summary>Singleton policy row. <c>policy_id</c> is always <c>1</c>.</summary>
    public const string SelectPolicy =
        """
        SELECT
            revision,
            updated_utc,
            gate,
            long_window_ms,
            short_window_ms,
            max_messages_long,
            max_bytes_long,
            max_identical_long,
            max_messages_short,
            max_bytes_short,
            max_identical_short,
            sa_enabled,
            sa_on_failure,
            sa_max_article_size,
            sa_port,
            sa_protocol_version,
            sa_max_connections,
            sa_host_selection,
            sa_connect_timeout_ms,
            sa_operation_timeout_ms
        FROM nntppostfilterpolicy
        WHERE policy_id = 1
        """;

    /// <summary>Deny/allow AUTH usernames. <c>list_kind</c> is <c>deny</c> or <c>allow</c>.</summary>
    public const string SelectAccounts =
        "SELECT list_kind, account_name FROM nntppostfilteraccounts ORDER BY list_kind, account_name";

    /// <summary>Deny/allow client CIDRs. <c>list_kind</c> is <c>deny</c> or <c>allow</c>.</summary>
    public const string SelectCidrs =
        "SELECT list_kind, cidr FROM nntppostfiltercidrs ORDER BY list_kind, cidr";

    /// <summary>ArtType lists. <c>list_kind</c> is <c>reject</c> or <c>sa_exclude</c>.</summary>
    public const string SelectArtTypes =
        "SELECT list_kind, art_type FROM nntppostfilterarttypes ORDER BY list_kind, art_type";

    /// <summary>SPAMD hosts in compiled order.</summary>
    public const string SelectHosts =
        "SELECT host FROM nntppostfiltersahosts ORDER BY host_order";

    /// <summary>Account list kind stored for deny.</summary>
    public const string ListKindDeny = "deny";

    /// <summary>Account/CIDR list kind stored for allowlist.</summary>
    public const string ListKindAllow = "allow";

    /// <summary>ArtType list kind stored for Stage 3 reject.</summary>
    public const string ListKindReject = "reject";

    /// <summary>ArtType list kind stored for SpamAssassin exclusion.</summary>
    public const string ListKindSaExclude = "sa_exclude";
}
