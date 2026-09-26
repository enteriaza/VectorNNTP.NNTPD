namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// Resolves application-local runtime filesystem paths against the binary directory.
/// </summary>
/// <remarks>
/// Relative paths resolve against <see cref="AppContext.BaseDirectory"/> (or an explicit
/// application-base argument). The process working directory, IDE project folder, and
/// host content root are never used as an implicit fallback.
/// </remarks>
public static class ApplicationLocalPath
{
    /// <summary>
    /// Resolves a configured application-local filesystem path.
    /// </summary>
    /// <param name="path">Configured path (relative or absolute). Null or whitespace is rejected.</param>
    /// <param name="applicationBaseDirectory">
    /// Application binary directory. Production callers must pass
    /// <see cref="AppContext.BaseDirectory"/>. When omitted or whitespace,
    /// <see cref="AppContext.BaseDirectory"/> is used. Do not pass an IDE/project content root.
    /// </param>
    /// <returns>
    /// A fully qualified path with trailing directory separators removed.
    /// Absolute <paramref name="path"/> values stay absolute.
    /// </returns>
    /// <exception cref="ArgumentException">Thrown when <paramref name="path"/> is null or whitespace.</exception>
    public static string ResolveApplicationLocalPath(string path, string? applicationBaseDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var trimmed = path.Trim();
        var root = string.IsNullOrWhiteSpace(applicationBaseDirectory)
            ? AppContext.BaseDirectory
            : applicationBaseDirectory;
        return Path.GetFullPath(trimmed, Path.GetFullPath(root))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }
}
