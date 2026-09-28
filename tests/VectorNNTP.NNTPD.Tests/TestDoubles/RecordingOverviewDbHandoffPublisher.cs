using VectorNNTP.NNTPD.ArticleIngestion.OverviewDb;

namespace VectorNNTP.NNTPD.Tests.TestDoubles;

/// <summary>Captures OverviewDB handoff payloads and can nack a budgeted number of publishes.</summary>
internal sealed class RecordingOverviewDbHandoffPublisher : IOverviewDbHandoffPublisher
{
    private int _attempts;
    private readonly TaskCompletionSource<int> _firstAttempt =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Gets captured successfully confirmed payloads in publish order.</summary>
    public List<byte[]> Payloads { get; } = [];

    /// <summary>Gets the number of publish attempts, including nacks.</summary>
    public int AttemptCount => Volatile.Read(ref _attempts);

    /// <summary>Completes when the first publish attempt begins.</summary>
    public Task<int> FirstAttempt => _firstAttempt.Task;

    /// <summary>Remaining attempts that throw before a publication is recorded.</summary>
    public int RemainingFailures { get; set; }

    /// <summary>
    /// Exception thrown for each <see cref="RemainingFailures"/> budgeted attempt.
    /// Unlike <see cref="PublishException"/>, this is not sticky after the budget is spent.
    /// </summary>
    public Exception? TransientPublishException { get; set; }

    /// <summary>When set, every attempt throws this exception after the remaining-failure budget.</summary>
    public Exception? PublishException { get; set; }

    /// <summary>
    /// When set, a nacking attempt waits on this source after recording the attempt
    /// and before throwing. Used to observe the worker without a tight retry loop.
    /// </summary>
    public TaskCompletionSource? BlockOnFailure { get; set; }

    /// <inheritdoc />
    public async Task PublishConfirmedAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var attempt = Interlocked.Increment(ref _attempts);
        _firstAttempt.TrySetResult(attempt);

        if (RemainingFailures > 0)
        {
            RemainingFailures--;
            if (BlockOnFailure is not null)
            {
                await BlockOnFailure.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            throw TransientPublishException
                ?? PublishException
                ?? new InvalidOperationException("RabbitMQ negatively acknowledged the OverviewDB handoff.");
        }

        if (PublishException is not null)
        {
            if (BlockOnFailure is not null)
            {
                await BlockOnFailure.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            throw PublishException;
        }

        Payloads.Add(payload.ToArray());
    }
}
