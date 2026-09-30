using System.Buffers.Binary;
using VectorNNTP.Common.Articles;

namespace VectorNNTP.NNTPD.Storage;

/// <summary>Whether a durable second-copy pin is still waiting on its target.</summary>
public enum ReplicationIntentState : byte
{
    /// <summary>The target is pinned and the second copy is not yet confirmed.</summary>
    Pending = 1,

    /// <summary>The pinned target accepted this canonical article. The target does not change.</summary>
    Completed = 2,
}

/// <summary>Durable second-copy pin for one article.</summary>
/// <param name="ArticleId">Article whose second copy is pinned.</param>
/// <param name="SourceServerId">StorageServer that held the primary copy when the pin was created.</param>
/// <param name="TargetServerId">The only StorageServer that may receive the second copy.</param>
/// <param name="State">Pending until the pinned target accepts the article.</param>
public readonly record struct ReplicationIntent(
    ArticleId ArticleId,
    int SourceServerId,
    int TargetServerId,
    ReplicationIntentState State);

/// <summary>Result of attempting to pin a second-copy target.</summary>
/// <param name="Created">True when this call wrote the pin. False when an existing pin won.</param>
/// <param name="Intent">The durable pin. This is the existing pin when <paramref name="Created"/> is false.</param>
public readonly record struct ReplicationPinResult(bool Created, ReplicationIntent Intent);

/// <summary>
/// Durable map from <see cref="ArticleId"/> to one second-copy <see cref="ReplicationIntent"/>.
/// </summary>
public interface IReplicationIntentStore
{
    /// <summary>True when this process is allowed to create second-copy pins.</summary>
    bool IsSecondCopySender { get; }

    /// <summary>Reads a pin that has already been flushed.</summary>
    bool TryGet(ArticleId articleId, out ReplicationIntent intent);

    /// <summary>
    /// Persists <paramref name="targetServerId"/> when this article has no pin.
    /// When a pin already exists, returns that pin and does not change it.
    /// The method returns only after the winning pin is durable.
    /// A new pin is <see cref="ReplicationIntentState.Pending"/>.
    /// </summary>
    ReplicationPinResult TryEstablish(ArticleId articleId, int sourceServerId, int targetServerId);

    /// <summary>
    /// Marks an existing pin <see cref="ReplicationIntentState.Completed"/> without changing its target.
    /// Returns false when no pin exists. A completed pin stays completed.
    /// </summary>
    bool TryComplete(ArticleId articleId);
}

/// <summary>
/// File-per-article replication pins. Silence, timeout, and conflict do not retarget.
/// </summary>
public sealed class ReplicationIntentStore : IReplicationIntentStore
{
    private const int Version = 1;
    private readonly object _gate = new();
    private readonly string _directory;

    private ReplicationIntentStore(string directory)
    {
        _directory = directory;
    }

    /// <inheritdoc />
    public bool IsSecondCopySender => true;

    /// <summary>Gets the directory that holds pin files.</summary>
    public string Directory => _directory;

    /// <summary>Opens or creates the intent directory under <paramref name="replicationDirectory"/>.</summary>
    public static ReplicationIntentStore Open(string replicationDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(replicationDirectory);
        var directory = Path.Combine(replicationDirectory, "intent");
        System.IO.Directory.CreateDirectory(directory);
        return new ReplicationIntentStore(directory);
    }

    /// <inheritdoc />
    public bool TryGet(ArticleId articleId, out ReplicationIntent intent)
    {
        lock (_gate)
        {
            return TryRead(PathFor(articleId), articleId, out intent);
        }
    }

    /// <inheritdoc />
    public ReplicationPinResult TryEstablish(ArticleId articleId, int sourceServerId, int targetServerId)
    {
        lock (_gate)
        {
            var finalPath = PathFor(articleId);
            if (TryRead(finalPath, articleId, out var existing))
            {
                return new ReplicationPinResult(false, existing);
            }

            var temporary = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                WriteDurable(
                    temporary,
                    articleId,
                    sourceServerId,
                    targetServerId,
                    ReplicationIntentState.Pending);
                File.Move(temporary, finalPath, overwrite: false);
            }
            catch (IOException) when (File.Exists(finalPath))
            {
                TryDelete(temporary);
                if (!TryRead(finalPath, articleId, out existing))
                {
                    throw;
                }

                return new ReplicationPinResult(false, existing);
            }
            catch
            {
                TryDelete(temporary);
                throw;
            }

            if (!TryRead(finalPath, articleId, out var written))
            {
                throw new IOException("Replication pin was not readable after durable write.");
            }

            return new ReplicationPinResult(true, written);
        }
    }

    /// <inheritdoc />
    public bool TryComplete(ArticleId articleId)
    {
        lock (_gate)
        {
            var finalPath = PathFor(articleId);
            if (!TryRead(finalPath, articleId, out var existing))
            {
                return false;
            }

            if (existing.State == ReplicationIntentState.Completed)
            {
                return true;
            }

            var temporary = finalPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                WriteDurable(
                    temporary,
                    existing.ArticleId,
                    existing.SourceServerId,
                    existing.TargetServerId,
                    ReplicationIntentState.Completed);
                File.Move(temporary, finalPath, overwrite: true);
            }
            catch
            {
                TryDelete(temporary);
                throw;
            }

            if (!TryRead(finalPath, articleId, out var written)
                || written.State != ReplicationIntentState.Completed
                || written.SourceServerId != existing.SourceServerId
                || written.TargetServerId != existing.TargetServerId)
            {
                throw new IOException("Replication pin completion was not durable.");
            }

            return true;
        }
    }

    private string PathFor(ArticleId articleId) =>
        Path.Combine(_directory, articleId.ToLowerHexString() + ".pin");

    private static bool TryRead(string path, ArticleId articleId, out ReplicationIntent intent)
    {
        if (!File.Exists(path))
        {
            intent = default;
            return false;
        }

        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != 20 + ArticleId.Length
            || bytes[0] != (byte)'V'
            || bytes[1] != (byte)'N'
            || bytes[2] != (byte)'P'
            || bytes[3] != (byte)'I'
            || BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4)) != Version)
        {
            throw new InvalidDataException("Replication pin file is corrupt.");
        }

        var source = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8));
        var target = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12));
        var stateValue = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16));
        if (stateValue != (int)ReplicationIntentState.Pending
            && stateValue != (int)ReplicationIntentState.Completed)
        {
            throw new InvalidDataException("Replication pin file state is corrupt.");
        }

        var state = (ReplicationIntentState)stateValue;

        var id = ArticleId.FromSpan(bytes.AsSpan(20, ArticleId.Length));
        if (id != articleId)
        {
            throw new InvalidDataException("Replication pin file identity does not match its name.");
        }

        intent = new ReplicationIntent(id, source, target, state);
        return true;
    }

    private static void WriteDurable(
        string path,
        ArticleId articleId,
        int sourceServerId,
        int targetServerId,
        ReplicationIntentState state)
    {
        Span<byte> buffer = stackalloc byte[20 + ArticleId.Length];
        buffer[0] = (byte)'V';
        buffer[1] = (byte)'N';
        buffer[2] = (byte)'P';
        buffer[3] = (byte)'I';
        BinaryPrimitives.WriteInt32LittleEndian(buffer[4..], Version);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[8..], sourceServerId);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[12..], targetServerId);
        BinaryPrimitives.WriteInt32LittleEndian(buffer[16..], (int)state);
        articleId.CopyTo(buffer[20..]);
        using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.WriteThrough);
        stream.Write(buffer);
        stream.Flush(flushToDisk: true);
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
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Intent store used when this NNTPD is not the second-copy sender.</summary>
public sealed class DisabledReplicationIntentStore : IReplicationIntentStore
{
    /// <summary>Shared instance.</summary>
    public static DisabledReplicationIntentStore Instance { get; } = new();

    private DisabledReplicationIntentStore()
    {
    }

    /// <inheritdoc />
    public bool IsSecondCopySender => false;

    /// <inheritdoc />
    public bool TryGet(ArticleId articleId, out ReplicationIntent intent)
    {
        intent = default;
        return false;
    }

    /// <inheritdoc />
    public ReplicationPinResult TryEstablish(ArticleId articleId, int sourceServerId, int targetServerId) =>
        throw new InvalidOperationException(
            "This NNTPD is not the second-copy sender. Set Nntpd:Replication:SecondCopySender on exactly one instance.");

    /// <inheritdoc />
    public bool TryComplete(ArticleId articleId) =>
        throw new InvalidOperationException(
            "This NNTPD is not the second-copy sender. Set Nntpd:Replication:SecondCopySender on exactly one instance.");
}
