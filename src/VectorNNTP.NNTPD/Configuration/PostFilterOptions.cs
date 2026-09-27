using System.ComponentModel.DataAnnotations;
using VectorNNTP.Common.Articles;

namespace VectorNNTP.NNTPD.Configuration;

/// <summary>Operational gate for NNTPD PostFilter.</summary>
public enum PostFilterGateState
{
    /// <summary>Skip remaining PostFilter stages, including quota and SpamAssassin.</summary>
    Disabled = 0,

    /// <summary>Evaluate deny, ArtType, quotas, and SpamAssassin.</summary>
    Active = 1,

    /// <summary>Reject every POST that reaches PostFilter.</summary>
    Closed = 2,
}

/// <summary>
/// SpamAssassin scanner-fault action. Required when
/// <see cref="PostFilterSpamAssassinOptions.Enabled"/> is <see langword="true"/>.
/// </summary>
public enum PostFilterSpamOnFailure
{
    /// <summary>Scanner fault rejects the article (<c>441</c>).</summary>
    Reject = 0,

    /// <summary>Scanner fault continues with the existing reservation.</summary>
    Accept = 1,
}

/// <summary>Deterministic SPAMD host selection compiled into the PostFilter snapshot.</summary>
public enum PostFilterSpamAssassinHostSelection
{
    /// <summary>Rotate across <see cref="PostFilterSpamAssassinOptions.Hosts"/>; failover on connect failure only.</summary>
    RoundRobin = 0,

    /// <summary>Always try hosts in list order; failover on connect failure only.</summary>
    Failover = 1,
}

/// <summary>NNTPD POST PostFilter policy bound from <c>Nntpd:PostFilter</c>.</summary>
/// <remarks>
/// Compiled into an immutable snapshot at startup and every five minutes.
/// POST does not parse this object. Default gate is <see cref="PostFilterGateState.Disabled"/>
/// so existing deployments keep current POST behaviour until operators enable the filter.
/// </remarks>
public sealed class PostFilterOptions
{
    /// <summary>Gets or sets the operational gate.</summary>
    public PostFilterGateState Gate { get; set; } = PostFilterGateState.Disabled;

    /// <summary>Gets or sets exact authenticated account names that are denied.</summary>
    public string[] DeniedAccounts { get; set; } = [];

    /// <summary>Gets or sets client CIDRs that are denied.</summary>
    public string[] DeniedCidrs { get; set; } = [];

    /// <summary>
    /// Gets or sets authenticated account names that skip SpamAssassin only.
    /// Does not skip the gate, deny, ArtType, or quotas.
    /// </summary>
    public string[] AllowlistedAccounts { get; set; } = [];

    /// <summary>Gets or sets client CIDRs that skip SpamAssassin only.</summary>
    public string[] AllowlistedCidrs { get; set; } = [];

    /// <summary>
    /// Gets or sets <see cref="ArticleType"/> names rejected by Stage 3.
    /// Empty means no type policy.
    /// </summary>
    public string[] RejectArtTypes { get; set; } = [];

    /// <summary>Gets or sets accept-quota windows and ceilings.</summary>
    [Required]
    public PostFilterQuotaOptions Quota { get; set; } = new();

    /// <summary>Gets or sets SpamAssassin eligibility and failure policy.</summary>
    [Required]
    public PostFilterSpamAssassinOptions SpamAssassin { get; set; } = new();
}

/// <summary>Quota windows and ceilings compiled into the PostFilter snapshot.</summary>
public sealed class PostFilterQuotaOptions
{
    /// <summary>Gets or sets the sustained window. Must be positive.</summary>
    public TimeSpan LongWindow { get; set; } = TimeSpan.FromDays(1);

    /// <summary>Gets or sets the burst window. Must be positive.</summary>
    public TimeSpan ShortWindow { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Gets or sets the L message ceiling. <c>0</c> disables the dimension.</summary>
    public long MaxMessagesLong { get; set; }

    /// <summary>Gets or sets the L byte ceiling. <c>0</c> disables the dimension.</summary>
    public long MaxBytesLong { get; set; }

    /// <summary>Gets or sets the L identical-body ceiling. <c>0</c> disables the dimension.</summary>
    public long MaxIdenticalLong { get; set; }

    /// <summary>Gets or sets the S message ceiling. <c>0</c> disables the dimension.</summary>
    public long MaxMessagesShort { get; set; }

    /// <summary>Gets or sets the S byte ceiling. <c>0</c> disables the dimension.</summary>
    public long MaxBytesShort { get; set; }

    /// <summary>Gets or sets the S identical-body ceiling. <c>0</c> disables the dimension.</summary>
    public long MaxIdenticalShort { get; set; }
}

/// <summary>SpamAssassin stage options. Disabled until explicitly enabled.</summary>
/// <remarks>
/// SpamAssassin is a targeted small-article scanner. The default eligibility
/// matches the historical production boundary: <see cref="DefaultMaxArticleSize"/>
/// (131072) exclusive maximum and <see cref="ArticleType.YEncoded"/> excluded.
/// <c>MaxArticleSize = 0</c> disables the size gate. An empty
/// <see cref="ExcludeArtTypes"/> array disables type exclusion.
/// Ineligible articles skip CHECK; that is not an <see cref="OnFailure"/> event.
/// Spam findings always reject in v1 (<c>OnSpam</c> is not independently configurable).
/// <see cref="OnFailure"/> is required when <see cref="Enabled"/> is <see langword="true"/>.
/// </remarks>
public sealed class PostFilterSpamAssassinOptions
{
    /// <summary>Historical exclusive ArtSize maximum used as the safe default (131072).</summary>
    public const int DefaultMaxArticleSize = 131_072;

    /// <summary>Default pooled concurrent SPAMD connections.</summary>
    public const int DefaultMaxConnections = 4;

    /// <summary>Minimum <see cref="MaxConnections"/> when SpamAssassin is enabled.</summary>
    public const int MinMaxConnections = 1;

    /// <summary>Maximum <see cref="MaxConnections"/> when SpamAssassin is enabled.</summary>
    public const int MaxMaxConnections = 32;

    /// <summary>Default SPAMC protocol version sent on CHECK.</summary>
    public const string DefaultProtocolVersion = "1.5";

    /// <summary>Gets or sets whether CHECK is invoked for eligible articles.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets scanner-fault behaviour. Required when <see cref="Enabled"/> is true.
    /// </summary>
    public PostFilterSpamOnFailure? OnFailure { get; set; }

    /// <summary>
    /// Gets or sets the exclusive ArtSize eligibility maximum.
    /// Articles with <c>ArtSize &gt;= MaxArticleSize</c> skip CHECK.
    /// <c>0</c> disables the size gate. Default is <see cref="DefaultMaxArticleSize"/>.
    /// </summary>
    public int MaxArticleSize { get; set; } = DefaultMaxArticleSize;

    /// <summary>
    /// Gets or sets <see cref="ArticleType"/> names excluded from CHECK.
    /// Default is <see cref="ArticleType.YEncoded"/>. Empty means no type exclusion.
    /// </summary>
    public string[] ExcludeArtTypes { get; set; } = ["YEncoded"];

    /// <summary>Gets or sets SPAMD hosts. Required when <see cref="Enabled"/> is true.</summary>
    public string[] Hosts { get; set; } = [];

    /// <summary>Gets or sets the shared SPAMD port for every host.</summary>
    public int Port { get; set; } = 783;

    /// <summary>Gets or sets the SPAMC protocol version used in the CHECK request line.</summary>
    public string ProtocolVersion { get; set; } = DefaultProtocolVersion;

    /// <summary>
    /// Gets or sets the maximum number of concurrent persistent SPAMD connections.
    /// Each connection serializes CHECK requests; concurrency is the pool size.
    /// </summary>
    public int MaxConnections { get; set; } = DefaultMaxConnections;

    /// <summary>Gets or sets deterministic host selection.</summary>
    public PostFilterSpamAssassinHostSelection HostSelection { get; set; } =
        PostFilterSpamAssassinHostSelection.RoundRobin;

    /// <summary>Gets or sets the connect timeout.</summary>
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets the CHECK operation timeout.</summary>
    /// <remarks>
    /// This is the wall-clock budget for one CHECK, including connect.
    /// When SpamAssassin is enabled, the PostFilter reservation hold is
    /// <c>max(10s, OperationTimeout + 1s)</c> so the reservation cannot expire
    /// during a legitimate CHECK.
    /// </remarks>
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Minimum <see cref="OperationTimeout"/> when SpamAssassin is enabled.</summary>
    public static readonly TimeSpan MinOperationTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Maximum <see cref="OperationTimeout"/> when SpamAssassin is enabled.</summary>
    /// <remarks>Caps derived reservation hold so crash-recovery TTL cannot become unbounded.</remarks>
    public static readonly TimeSpan MaxOperationTimeout = TimeSpan.FromMinutes(2);
}
