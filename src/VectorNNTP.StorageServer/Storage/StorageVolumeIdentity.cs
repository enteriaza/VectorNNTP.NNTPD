namespace VectorNNTP.StorageServer.Storage;

/// <summary>
/// Stable identity of one physical filesystem volume.
/// </summary>
/// <remarks>
/// Equality is the volume, not a directory path. Two directories on the same volume compare equal.
/// </remarks>
/// <param name="Value">Platform volume name or device identity. Never a directory path.</param>
internal readonly record struct StorageVolumeIdentity(string Value);
