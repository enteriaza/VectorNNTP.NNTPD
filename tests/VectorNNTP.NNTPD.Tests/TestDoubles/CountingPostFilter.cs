using VectorNNTP.NNTPD.PostFilter;

namespace VectorNNTP.NNTPD.Tests.TestDoubles;

/// <summary>Counts <see cref="IPostFilter.EvaluateAsync"/> calls. Accepts without reserving.</summary>
internal sealed class CountingPostFilter : IPostFilter
{
    public int EvaluateCalls { get; private set; }

    public ValueTask<PostFilterResult> EvaluateAsync(
        PostFilterRequest request,
        CancellationToken cancellationToken = default)
    {
        _ = request;
        cancellationToken.ThrowIfCancellationRequested();
        EvaluateCalls++;
        return ValueTask.FromResult(PostFilterResult.AcceptedDisabled());
    }
}
