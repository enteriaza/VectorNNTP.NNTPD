namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// One configured moderator route: an NNTP wildmat, the expected <c>Approved:</c> identity,
/// and the AUTHINFO principal authorized to inject that approval.
/// </summary>
/// <remarks>
/// Credentials are not stored here. The AUTHINFO password remains in the existing
/// authentication secret mechanism.
/// </remarks>
public sealed class ModeratorMappingOptions
{
    /// <summary>
    /// Gets or sets the RFC 3977 wildmat matched against the newsgroup name.
    /// </summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the expected <c>Approved:</c> mailbox identity for groups matching
    /// <see cref="Pattern"/>.
    /// </summary>
    public string Address { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the AUTHINFO username authorized to approve groups matching
    /// <see cref="Pattern"/>.
    /// </summary>
    public string Username { get; set; } = string.Empty;
}
