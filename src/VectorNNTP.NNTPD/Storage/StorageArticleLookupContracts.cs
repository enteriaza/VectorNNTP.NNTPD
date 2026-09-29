using System.Collections.Concurrent;
using VectorNNTP.Common.Articles;
using VectorNNTP.Common.Messaging.Cache;

namespace VectorNNTP.NNTPD.Storage;

/// <summary>Outcome of a StorageServer fleet article-presence lookup.</summary>
public enum StorageArticleLookupOutcome
{
    /// <summary>At least one StorageServer responded positively.</summary>
    Found = 0,

    /// <summary>No positive response arrived before the lookup timeout.</summary>
    NotFound = 1,
}

/// <summary>Result of a StorageServer fleet article-presence lookup.</summary>
/// <param name="Outcome">Classified outcome.</param>
/// <param name="RequestId">Logical lookup identity.</param>
/// <param name="ArticleId">Queried article identity.</param>
/// <param name="ServerId">Winning StorageServer id when found.</param>
/// <param name="Fqdn">Winning StorageServer FQDN when found.</param>
/// <param name="Uri">Cache URI for subsequent VATP retrieval when found.</param>
/// <param name="Error">Detail when not found.</param>
public sealed record StorageArticleLookupResult(
    StorageArticleLookupOutcome Outcome,
    Guid RequestId,
    ArticleId ArticleId,
    int? ServerId,
    string? Fqdn,
    string? Uri,
    string? Error)
{
    /// <summary>Builds a not-found result.</summary>
    public static StorageArticleLookupResult NotFound(Guid requestId, ArticleId articleId, string error) =>
        new(StorageArticleLookupOutcome.NotFound, requestId, articleId, null, null, null, error);
}

/// <summary>NNTPD client for StorageServer fleet article-presence lookups.</summary>
public interface IStorageArticleLookupClient
{
    /// <summary>
    /// Asks all StorageServers "who has this article?" and completes on the first valid
    /// positive response or the lookup timeout.
    /// </summary>
    Task<StorageArticleLookupResult> LookupAsync(ArticleId articleId, CancellationToken cancellationToken);
}

/// <summary>
/// Correlates StorageServer lookup responses by AMQP <c>CorrelationId</c>.
/// First valid positive response wins; duplicates and unknowns are ignored.
/// </summary>
internal sealed class StorageArticleLookupResponseRouter
{
    private readonly ConcurrentDictionary<string, PendingLookup> _pending = new(StringComparer.Ordinal);

    /// <summary>Gets outstanding correlation count (tests).</summary>
    internal int OutstandingCount => _pending.Count;

    /// <summary>Registers a lookup awaiting responses.</summary>
    internal bool TryRegister(string correlationId, StorageArticleLookupOperation operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.IsCompleted)
        {
            return false;
        }

        if (!_pending.TryAdd(correlationId, new PendingLookup(operation)))
        {
            throw new InvalidOperationException($"Duplicate storage lookup correlation '{correlationId}'.");
        }

        if (operation.IsCompleted)
        {
            _pending.TryRemove(correlationId, out _);
            return false;
        }

        return true;
    }

    /// <summary>Removes a registration.</summary>
    internal void Unregister(string correlationId)
    {
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            _pending.TryRemove(correlationId, out _);
        }
    }

    /// <summary>Cancels all outstanding lookups.</summary>
    internal void CancelAll()
    {
        foreach (var pair in _pending)
        {
            if (_pending.TryRemove(pair.Key, out var pending))
            {
                pending.Operation.TrySetCanceled();
            }
        }
    }

    /// <summary>
    /// Dispatches a parsed positive response. Returns <see langword="true"/> when it
    /// completed an outstanding lookup.
    /// </summary>
    internal bool TryComplete(string? correlationId, StorageArticleLookupResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (string.IsNullOrWhiteSpace(correlationId))
        {
            return false;
        }

        if (!_pending.TryGetValue(correlationId, out var pending))
        {
            return false;
        }

        if (pending.Operation.RequestId != response.RequestId)
        {
            return false;
        }

        if (pending.Operation.ArticleId != response.ArticleId)
        {
            return false;
        }

        var result = new StorageArticleLookupResult(
            StorageArticleLookupOutcome.Found,
            response.RequestId,
            response.ArticleId,
            response.ServerId,
            response.Fqdn,
            response.Uri,
            Error: null);

        if (!pending.Operation.TrySetResult(result))
        {
            return false;
        }

        _pending.TryRemove(correlationId, out _);
        return true;
    }

    private sealed record PendingLookup(StorageArticleLookupOperation Operation);
}

/// <summary>One in-flight StorageServer fleet lookup.</summary>
internal sealed class StorageArticleLookupOperation
{
    private readonly TaskCompletionSource<StorageArticleLookupResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Initializes a lookup operation.</summary>
    internal StorageArticleLookupOperation(Guid requestId, ArticleId articleId)
    {
        RequestId = requestId;
        ArticleId = articleId;
    }

    /// <summary>Logical request identity.</summary>
    internal Guid RequestId { get; }

    /// <summary>Queried article identity.</summary>
    internal ArticleId ArticleId { get; }

    /// <summary>Completion task.</summary>
    internal Task<StorageArticleLookupResult> Completion => _completion.Task;

    /// <summary>Whether the lookup already completed.</summary>
    internal bool IsCompleted => _completion.Task.IsCompleted;

    /// <summary>Attempts to complete with a result (first wins).</summary>
    internal bool TrySetResult(StorageArticleLookupResult result) =>
        _completion.TrySetResult(result);

    /// <summary>Attempts to cancel the lookup.</summary>
    internal bool TrySetCanceled() => _completion.TrySetCanceled();

    /// <summary>Attempts to read the completed result.</summary>
    internal bool TryGetResult(out StorageArticleLookupResult result)
    {
        if (_completion.Task.IsCompletedSuccessfully)
        {
            result = _completion.Task.Result;
            return true;
        }

        result = null!;
        return false;
    }
}
