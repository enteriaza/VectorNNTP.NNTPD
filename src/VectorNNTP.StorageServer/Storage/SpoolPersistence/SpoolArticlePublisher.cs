using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Storage.SpoolShards;

namespace VectorNNTP.StorageServer.Storage.SpoolPersistence;

/// <summary>Outcome of one publish attempt. A stored or duplicate result is the only success.</summary>
internal enum SpoolPublishKind
{
    /// <summary>The final path was created by this call and contains the supplied bytes.</summary>
    Stored,

    /// <summary>The final path already existed and its bytes match the supplied article.</summary>
    Duplicate,

    /// <summary>The final path exists and its bytes differ. The file was not replaced.</summary>
    Conflict,

    /// <summary>The filesystem reported that the volume is full. Any temp file was removed.</summary>
    DiskFull,

    /// <summary>The filesystem refused access. Any temp file was removed.</summary>
    AccessDenied,

    /// <summary>The publish failed for another I/O reason. Any temp file was removed.</summary>
    IoFailure,
}

/// <summary>One publish attempt.</summary>
/// <param name="Kind">How the attempt ended.</param>
/// <param name="FinalPath">Final article path when one was resolved. Empty when the path itself was rejected.</param>
/// <param name="Detail">Short failure text safe for logs. Null on success.</param>
internal readonly record struct SpoolPublishResult(SpoolPublishKind Kind, string FinalPath, string? Detail);

/// <summary>
/// Writes one rewritten article to <c>{spool}/{n0}/{n1}/{n2}/{articleId}</c> by a temp file
/// in that leaf and a same-directory rename. Readers of the final path do not observe the
/// temp contents. Rename does not flush the device and does not survive power loss by itself.
/// Each attempt deletes only the temp path it created. A <c>.writing-</c> file left by another
/// attempt or another process is not treated as abandoned.
/// </summary>
internal sealed class SpoolArticlePublisher
{
    private readonly ISpoolArticleFiles _files;

    /// <summary>Initializes the publisher.</summary>
    /// <param name="files">Filesystem adapter.</param>
    public SpoolArticlePublisher(ISpoolArticleFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        _files = files;
    }

    /// <summary>
    /// Publishes <paramref name="content"/> for <paramref name="articleId"/> under <paramref name="spoolRoot"/>.
    /// The identity is the canonical <see cref="ArticleId"/>. This method does not hash it.
    /// </summary>
    /// <param name="spoolRoot">Validated spool root.</param>
    /// <param name="articleId">Canonical article identity from the accepted article.</param>
    /// <param name="content">Path-rewritten article bytes.</param>
    /// <param name="cancellationToken">Cancels before the write and before the rename. A cancelled attempt deletes its temp file.</param>
    /// <returns>The publish outcome. <see cref="SpoolPublishKind.Stored"/> means the rename completed.</returns>
    internal SpoolPublishResult Publish(
        string spoolRoot,
        ArticleId articleId,
        ReadOnlySpan<byte> content,
        CancellationToken cancellationToken)
    {
        string final;
        try
        {
            final = SpoolArticlePath.CombineArticle(spoolRoot, articleId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return new SpoolPublishResult(SpoolPublishKind.IoFailure, string.Empty, ex.Message);
        }

        var directory = final[..(final.Length - ArticleId.HexLength - 1)];
        if (string.IsNullOrEmpty(directory))
        {
            return new SpoolPublishResult(SpoolPublishKind.IoFailure, final, "Article path has no directory.");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            _files.CreateDirectory(directory);
            if (_files.Exists(final))
            {
                return _files.ContentsEqual(final, content)
                    ? new SpoolPublishResult(SpoolPublishKind.Duplicate, final, null)
                    : new SpoolPublishResult(SpoolPublishKind.Conflict, final, "Existing article bytes differ.");
            }

            var temp = CreateTempPath(final);
            try
            {
                _files.WriteAll(temp, content, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    _files.MoveNew(temp, final);
                }
                catch (IOException) when (_files.Exists(final))
                {
                    DeleteQuiet(temp);
                    return _files.ContentsEqual(final, content)
                        ? new SpoolPublishResult(SpoolPublishKind.Duplicate, final, null)
                        : new SpoolPublishResult(SpoolPublishKind.Conflict, final, "Existing article bytes differ.");
                }

                return new SpoolPublishResult(SpoolPublishKind.Stored, final, null);
            }
            catch
            {
                DeleteQuiet(temp);
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            return new SpoolPublishResult(SpoolPublishKind.AccessDenied, final, ex.Message);
        }
        catch (IOException ex) when (IsDiskFull(ex))
        {
            return new SpoolPublishResult(SpoolPublishKind.DiskFull, final, ex.Message);
        }
        catch (IOException ex) when (IsReadOnly(ex))
        {
            return new SpoolPublishResult(SpoolPublishKind.AccessDenied, final, ex.Message);
        }
        catch (IOException ex)
        {
            return new SpoolPublishResult(SpoolPublishKind.IoFailure, final, ex.Message);
        }
    }

    /// <summary>
    /// Looks for an already published final file for <paramref name="articleId"/> on any spool.
    /// Does not create, rename, or delete files. A temp file is not a published article.
    /// </summary>
    /// <param name="spoolRoots">Validated spool roots to scan. Order does not change a match.</param>
    /// <param name="articleId">Canonical article identity.</param>
    /// <param name="content">Path-rewritten bytes that would be published.</param>
    /// <param name="spoolRoot">Spool that already holds the final file when the method returns <see langword="true"/>.</param>
    /// <param name="result">Duplicate or conflict when the method returns <see langword="true"/>.</param>
    /// <returns><see langword="true"/> when a final file already exists on one of the roots.</returns>
    internal bool TryClassifyExisting(
        IReadOnlyList<string> spoolRoots,
        ArticleId articleId,
        ReadOnlySpan<byte> content,
        out string spoolRoot,
        out SpoolPublishResult result)
    {
        ArgumentNullException.ThrowIfNull(spoolRoots);
        foreach (var root in spoolRoots)
        {
            string final;
            try
            {
                final = SpoolArticlePath.CombineArticle(root, articleId);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
            {
                continue;
            }

            if (!_files.Exists(final))
            {
                continue;
            }

            spoolRoot = root;
            result = _files.ContentsEqual(final, content)
                ? new SpoolPublishResult(SpoolPublishKind.Duplicate, final, null)
                : new SpoolPublishResult(SpoolPublishKind.Conflict, final, "Existing article bytes differ.");
            return true;
        }

        spoolRoot = string.Empty;
        result = default;
        return false;
    }

    /// <summary>
    /// Temp path beside the final file. The suffix is <c>.writing-</c> plus 32 lowercase hex
    /// characters from a new GUID. Only the attempt that created the path deletes it.
    /// </summary>
    /// <param name="final">Absolute final article path.</param>
    /// <returns>A unique temp path in the same directory.</returns>
    private static string CreateTempPath(string final)
    {
        const string marker = ".writing-";
        return string.Create(final.Length + marker.Length + 32, final, static (destination, path) =>
        {
            path.AsSpan().CopyTo(destination);
            marker.AsSpan().CopyTo(destination[path.Length..]);
            Span<byte> guid = stackalloc byte[16];
            _ = Guid.NewGuid().TryWriteBytes(guid);
            var index = path.Length + marker.Length;
            for (var i = 0; i < guid.Length; i++)
            {
                var value = guid[i];
                destination[index++] = (char)((value >> 4) < 10 ? '0' + (value >> 4) : 'a' + (value >> 4) - 10);
                destination[index++] = (char)((value & 0x0F) < 10 ? '0' + (value & 0x0F) : 'a' + (value & 0x0F) - 10);
            }
        });
    }

    /// <summary>Deletes a temp file this call created and ignores a second failure.</summary>
    /// <param name="path">Temp path.</param>
    private void DeleteQuiet(string path)
    {
        try
        {
            _files.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Windows <c>ERROR_DISK_FULL</c> is 112. Linux <c>ENOSPC</c> is 28.</summary>
    /// <param name="exception">I/O failure from the filesystem adapter.</param>
    /// <returns><see langword="true"/> when the HRESULT low word is one of those codes.</returns>
    private static bool IsDiskFull(IOException exception)
    {
        var code = exception.HResult & 0xFFFF;
        return code is 112 or 28;
    }

    /// <summary>Windows <c>ERROR_WRITE_PROTECT</c> is 19. Linux <c>EROFS</c> is 30.</summary>
    /// <param name="exception">I/O failure from the filesystem adapter.</param>
    /// <returns><see langword="true"/> when the filesystem rejected the write as read-only.</returns>
    private static bool IsReadOnly(IOException exception)
    {
        var code = exception.HResult & 0xFFFF;
        return code is 19 or 30;
    }
}
