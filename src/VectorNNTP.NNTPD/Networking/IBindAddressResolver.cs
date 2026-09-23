using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.NNTPD.Networking;

/// <summary>
/// Resolves configured <see cref="NntpdOptions.BindAddress"/> entries into the eligible IP set
/// the host will use for listen sockets and authoritative DNS publication.
/// </summary>
public interface IBindAddressResolver
{
    /// <summary>
    /// Resolves <paramref name="options"/> bind entries into eligible IPv4/IPv6 addresses.
    /// </summary>
    /// <param name="options">Validated NNTPD options.</param>
    /// <returns>The resolved address set (maybe empty when no eligible addresses exist).</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="options"/> is null.</exception>
    ResolvedBindAddresses Resolve(NntpdOptions options);
}
