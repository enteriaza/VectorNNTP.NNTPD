namespace VectorNNTP.Common.Logging
{
    /// <summary>
    /// Replaces one completed uncompressed log with a gzip file.
    /// </summary>
    public interface ILogFileCompressor
    {
        /// <summary>
        /// Writes <c>{sourceLogPath}.gz</c> and deletes <paramref name="sourceLogPath"/> only after that file is in place.
        /// </summary>
        /// <param name="sourceLogPath">Completed <c>.log</c> file. Not the active file.</param>
        /// <returns>
        /// <see langword="true"/> when the gzip file replaced the source.
        /// <see langword="false"/> when the source is still present because compression did not finish.
        /// </returns>
        bool TryCompressAndReplace(string sourceLogPath);
    }
}
