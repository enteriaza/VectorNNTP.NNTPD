using System.IO.Compression;

namespace VectorNNTP.Common.Logging
{
    /// <summary>
    /// Gzip-compresses a completed log beside itself using a temporary file and an atomic move.
    /// </summary>
    public sealed class GzipLogFileCompressor : ILogFileCompressor
    {
        /// <summary>Suffix of the temporary file that becomes <c>.gz</c> only after the write finishes.</summary>
        public const string PartialSuffix = ".gz.partial";

        /// <inheritdoc />
        public bool TryCompressAndReplace(string sourceLogPath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(sourceLogPath);
            if (!File.Exists(sourceLogPath))
            {
                return false;
            }

            var destination = sourceLogPath + ".gz";
            if (File.Exists(destination))
            {
                TryDelete(sourceLogPath);
                return !File.Exists(sourceLogPath);
            }

            var partial = sourceLogPath + PartialSuffix;
            try
            {
                TryDelete(partial);
                using (var input = new FileStream(
                    sourceLogPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete))
                using (var output = new FileStream(
                    partial,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None))
                {
                    using var gzip = new GZipStream(output, CompressionLevel.Fastest, leaveOpen: true);
                    input.CopyTo(gzip);
                }

                File.Move(partial, destination, overwrite: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                TryDelete(partial);
                return false;
            }

            try
            {
                File.Delete(sourceLogPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The gzip file is complete. A later open deletes the source once it can.
            }

            return File.Exists(destination);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}
