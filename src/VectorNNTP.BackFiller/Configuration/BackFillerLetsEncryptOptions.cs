namespace VectorNNTP.BackFiller.Configuration;

/// <summary>
/// Let's Encrypt / ACME and Cloudflare DNS-01 settings bound from <c>BackFiller:LetsEncrypt</c>.
/// </summary>
/// <remarks>
/// Never log <see cref="CloudFlareApiToken"/> or <see cref="PfxExportPassword"/>.
/// Those secrets have no committed defaults.
/// </remarks>
public sealed class BackFillerLetsEncryptOptions
{
    /// <summary>ACME account contact email.</summary>
    public string AcmeAccountEmail { get; set; } = "security@usenet.ninja";

    /// <summary>ACME account key file name or path relative to the certificate directory.</summary>
    public string AcmeAccountKeyPem { get; set; } = "account.key";

    /// <summary>Maximum ACME transient retries.</summary>
    public int? AcmeTransientRetryMaxAttempts { get; set; } = 5;

    /// <summary>Clock-skew check TTL in minutes.</summary>
    public int? ClockSkewCheckTtlMinutes { get; set; } = 5;

    /// <summary>Maximum accepted clock skew in minutes.</summary>
    public int? ClockSkewMaxMinutes { get; set; } = 10;

    /// <summary>Authoritative NS cache TTL in minutes.</summary>
    public int? DnsAuthoritativeNsCacheMinutes { get; set; } = 5;

    /// <summary>Authoritative DNS quorum ratio (0, 1].</summary>
    public double? DnsAuthoritativeQuorumRatio { get; set; } = 0.7;

    /// <summary>DNS-01 propagation delay in seconds.</summary>
    public int? DnsPropagationDelaySeconds { get; set; } = 15;

    /// <summary>TXT poll interval in seconds.</summary>
    public int? DnsTxtPollIntervalSeconds { get; set; } = 3;

    /// <summary>TXT poll timeout in seconds.</summary>
    public int? DnsTxtPollTimeoutSeconds { get; set; } = 600;

    /// <summary>Optional additional certificate DNS names. Empty means FQDN only.</summary>
    public string[]? DomainNames { get; set; }

    /// <summary>PKCS#12 export password. Secret. No default.</summary>
    public string? PfxExportPassword { get; set; }

    /// <summary>Renewal check interval in hours.</summary>
    public int? RenewalCheckIntervalHours { get; set; } = 6;

    /// <summary>Renewal jitter ratio in [0, 1).</summary>
    public double? RenewalJitterRatio { get; set; } = 0.1;

    /// <summary>Days before expiry to renew.</summary>
    public int? RenewBeforeExpiryDays { get; set; } = 7;

    /// <summary>When <see langword="true"/>, use the Let's Encrypt staging directory.</summary>
    public bool UseStagingDirectory { get; set; }

    /// <summary>Cloudflare API token. Secret. No default.</summary>
    public string? CloudFlareApiToken { get; set; }

    /// <summary>Cloudflare zone id. No default.</summary>
    public string? CloudFlareZoneId { get; set; }
}
