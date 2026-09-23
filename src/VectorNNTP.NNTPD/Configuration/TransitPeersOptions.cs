namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// Top-level <c>Transit</c> configuration: named peers as dictionary keys.
/// </summary>
/// <remarks>
/// <para>
/// Binds the exact JSON shape <c>Transit:{peer-name}:{fields}</c>. There is no nested
/// <c>Transit:Peers</c> layer. Distinct from <see cref="TransitOptions"/> under
/// <c>Nntpd:Transit</c> (STREAM TX depth only).
/// </para>
/// <para>
/// An empty dictionary is valid (no configured peers). Peer names are not normalized.
/// </para>
/// </remarks>
public sealed class TransitPeersOptions : Dictionary<string, TransitPeerOptions>
{
    /// <summary>Top-level configuration section name.</summary>
    public const string SectionName = "Transit";

    /// <summary>Initializes an empty peer dictionary.</summary>
    public TransitPeersOptions()
        : base(StringComparer.Ordinal)
    {
    }
}
