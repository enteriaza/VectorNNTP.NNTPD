using System.Text.Json.Serialization;

namespace VectorNNTP.NNTPD.Acme.Protocol;

/// <summary>ACME directory document (RFC 8555 §7.1.1).</summary>
internal sealed class AcmeDirectoryResource
{
    [JsonPropertyName("newNonce")]
    public Uri? NewNonce { get; set; }

    [JsonPropertyName("newAccount")]
    public Uri? NewAccount { get; set; }

    [JsonPropertyName("newOrder")]
    public Uri? NewOrder { get; set; }

    [JsonPropertyName("newAuthz")]
    public Uri? NewAuthorization { get; set; }

    [JsonPropertyName("revokeCert")]
    public Uri? RevokeCertificate { get; set; }

    [JsonPropertyName("keyChange")]
    public Uri? KeyChange { get; set; }

    [JsonPropertyName("renewalInfo")]
    public Uri? RenewalInfo { get; set; }

    [JsonPropertyName("meta")]
    public AcmeDirectoryMeta? Meta { get; set; }
}

/// <summary>Directory <c>meta</c> object.</summary>
internal sealed class AcmeDirectoryMeta
{
    [JsonPropertyName("termsOfService")]
    public Uri? TermsOfService { get; set; }

    [JsonPropertyName("website")]
    public Uri? Website { get; set; }

    [JsonPropertyName("caaIdentities")]
    public IReadOnlyList<string>? CaaIdentities { get; set; }

    [JsonPropertyName("externalAccountRequired")]
    public bool ExternalAccountRequired { get; set; }

    [JsonPropertyName("profiles")]
    public IReadOnlyDictionary<string, string>? Profiles { get; set; }
}

/// <summary>ACME account resource.</summary>
internal sealed class AcmeAccountResource
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("contact")]
    public IReadOnlyList<string>? Contact { get; set; }

    [JsonPropertyName("orders")]
    public Uri? Orders { get; set; }
}

/// <summary>ACME order resource.</summary>
internal sealed class AcmeOrderResource
{
    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("expires")]
    public DateTimeOffset? Expires { get; set; }

    [JsonPropertyName("identifiers")]
    public IReadOnlyList<AcmeIdentifier>? Identifiers { get; set; }

    [JsonPropertyName("authorizations")]
    public IReadOnlyList<Uri>? Authorizations { get; set; }

    [JsonPropertyName("finalize")]
    public Uri? Finalize { get; set; }

    [JsonPropertyName("certificate")]
    public Uri? Certificate { get; set; }

    [JsonPropertyName("error")]
    public AcmeProblem? Error { get; set; }
}

/// <summary>ACME identifier (dns / ip).</summary>
internal sealed class AcmeIdentifier
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = AcmeIdentifierTypes.Dns;

    [JsonPropertyName("value")]
    public string Value { get; set; } = string.Empty;
}

/// <summary>ACME authorization resource.</summary>
internal sealed class AcmeAuthorizationResource
{
    [JsonPropertyName("identifier")]
    public AcmeIdentifier? Identifier { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("expires")]
    public DateTimeOffset? Expires { get; set; }

    [JsonPropertyName("challenges")]
    public IReadOnlyList<AcmeChallengeResource>? Challenges { get; set; }

    [JsonPropertyName("wildcard")]
    public bool Wildcard { get; set; }
}

/// <summary>ACME challenge resource.</summary>
internal sealed class AcmeChallengeResource
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("url")]
    public Uri? Url { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("token")]
    public string? Token { get; set; }

    [JsonPropertyName("validated")]
    public DateTimeOffset? Validated { get; set; }

    [JsonPropertyName("error")]
    public AcmeProblem? Error { get; set; }
}

/// <summary>ACME / HTTP problem details document.</summary>
internal sealed class AcmeProblem
{
    [JsonPropertyName("type")]
    public string? Type { get; set; }

    [JsonPropertyName("title")]
    public string? Title { get; set; }

    [JsonPropertyName("detail")]
    public string? Detail { get; set; }

    [JsonPropertyName("status")]
    public int? Status { get; set; }

    [JsonPropertyName("subproblems")]
    public IReadOnlyList<AcmeProblem>? Subproblems { get; set; }

    [JsonPropertyName("identifier")]
    public AcmeIdentifier? Identifier { get; set; }
}

/// <summary>Empty JSON body (successful POST with no resource).</summary>
internal sealed class AcmeEmptyResource;

/// <summary>ACME identifier type wire values.</summary>
internal static class AcmeIdentifierTypes
{
    public const string Dns = "dns";
    public const string Ip = "ip";
}

/// <summary>ACME status wire values used by order / authorization polling.</summary>
internal static class AcmeStatus
{
    public const string Pending = "pending";
    public const string Ready = "ready";
    public const string Processing = "processing";
    public const string Valid = "valid";
    public const string Invalid = "invalid";
    public const string Deactivated = "deactivated";
    public const string Expired = "expired";
    public const string Revoked = "revoked";
}

/// <summary>Common ACME problem type URNs.</summary>
internal static class AcmeErrorTypes
{
    public const string BadNonce = "urn:ietf:params:acme:error:badNonce";
    public const string RateLimited = "urn:ietf:params:acme:error:rateLimited";
    public const string AccountDoesNotExist = "urn:ietf:params:acme:error:accountDoesNotExist";
    public const string OrderNotReady = "urn:ietf:params:acme:error:orderNotReady";
    public const string Dns = "urn:ietf:params:acme:error:dns";
    public const string Unauthorized = "urn:ietf:params:acme:error:unauthorized";
    public const string IncorrectResponse = "urn:ietf:params:acme:error:incorrectResponse";
}
