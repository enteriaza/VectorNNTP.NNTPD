namespace VectorNNTP.Common.Messaging.Cache;

/// <summary>
/// One StorageServer fleet advertisement (heartbeat + capacity announcement).
/// </summary>
/// <param name="Version">Wire protocol version. Current is <c>1</c>.</param>
/// <param name="ServerId">Numeric StorageServer identity.</param>
/// <param name="Fqdn">Generated StorageServer FQDN (for example <c>cache01.usenet.ninja</c>).</param>
/// <param name="TotalBytes">Authoritative total storage bytes for the cache volume.</param>
/// <param name="UsedBytes">Authoritative used storage bytes for the cache volume.</param>
/// <param name="AvailableBytes">Authoritative available storage bytes for the cache volume.</param>
/// <param name="Timestamp">UTC timestamp when the advertisement was produced.</param>
/// <param name="VatpPort">
/// TLS VATP listen port (<c>1</c>–<c>65535</c>) when the server is a placement target.
/// Omitted advertisements remain valid liveness records and are not placement targets.
/// </param>
public sealed record StorageServerAdvertisement(
    int Version,
    int ServerId,
    string Fqdn,
    long TotalBytes,
    long UsedBytes,
    long AvailableBytes,
    DateTimeOffset Timestamp,
    int? VatpPort = null);
