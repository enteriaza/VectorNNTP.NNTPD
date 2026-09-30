namespace VectorNNTP.StorageServer.Storage;

/// <summary>
/// Resolves a directory to the physical volume that contains it.
/// </summary>
internal interface IStorageVolumeProbe
{
    /// <summary>
    /// Resolves <paramref name="directoryPath"/> to a volume identity.
    /// </summary>
    /// <returns><see langword="false"/> when the volume cannot be resolved.</returns>
    bool TryResolve(string directoryPath, out StorageVolumeIdentity identity);
}
