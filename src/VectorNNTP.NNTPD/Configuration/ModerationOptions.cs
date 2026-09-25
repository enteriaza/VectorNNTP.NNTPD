namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// Top-level ordinary-newsgroup moderation options.
/// </summary>
/// <remarks>
/// <para>
/// The configuration section name is <see cref="SectionName"/> (<c>Moderation</c>;
/// case-insensitive). This section is not <c>Control:PgpAuthorities</c>. PGP control
/// authorities do not authorize moderated-newsgroup approval.
/// </para>
/// <para>
/// An omitted or empty section is valid. Missing moderator routes reject moderated
/// POST; they do not bypass moderation. Malformed mappings fail startup validation.
/// </para>
/// <para>
/// Pattern matching is first-match in configuration order (RFC 6048 §3 moderators
/// list). List more specific wildmats before general ones.
/// </para>
/// </remarks>
public sealed class ModerationOptions
{
    /// <summary>Top-level configuration section name.</summary>
    public const string SectionName = "Moderation";

    /// <summary>
    /// Gets or sets provenance for the imported INN <c>samples/moderators</c> snapshot.
    /// </summary>
    public ModerationSourceOptions Source { get; set; } = new();

    /// <summary>
    /// Gets or sets the moderator mappings. First matching <see cref="ModeratorMappingOptions.Pattern"/>
    /// wins.
    /// </summary>
    public ModeratorMappingOptions[] Moderators { get; set; } = [];
}
