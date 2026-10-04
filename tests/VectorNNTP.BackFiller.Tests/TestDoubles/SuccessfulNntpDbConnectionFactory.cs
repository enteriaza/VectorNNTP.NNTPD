using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using VectorNNTP.Common.NntpDb;

namespace VectorNNTP.BackFiller.Tests.TestDoubles
{
    /// <summary>
    /// Replaces the production MySQL factory after hosting registration so host tests can start
    /// <see cref="NntpDbService"/> without a server.
    /// </summary>
    internal static class SuccessfulNntpDbConnectionFactory
    {
        internal static void Replace(IServiceCollection services)
        {
            ArgumentNullException.ThrowIfNull(services);
            for (var i = services.Count - 1; i >= 0; i--)
            {
                if (services[i].ServiceType == typeof(INntpDbConnectionFactory))
                {
                    services.RemoveAt(i);
                }
            }

            services.AddSingleton<INntpDbConnectionFactory, Factory>();
        }

        private sealed class Factory : INntpDbConnectionFactory
        {
            public Task<INntpDbSession> OpenAsync(string connectionString, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<INntpDbSession>(new Session());
            }
        }

        private sealed class Session : INntpDbSession, INntpSharedConfigurationRowSource
        {
            public ValueTask<int> SelectOneAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return ValueTask.FromResult(1);
            }

            public ValueTask<IReadOnlyList<NntpSharedConfigurationCandidate>> ReadCandidatesAsync(
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                IReadOnlyList<NntpSharedConfigurationCandidate> rows =
                [
                    new NntpSharedConfigurationCandidate(1024, "news.usenet.ninja", null),
                ];
                return ValueTask.FromResult(rows);
            }

            public MySqlCommand CreateCommand() =>
                throw new NotSupportedException("This test session does not execute SQL.");

            public ValueTask<MySqlTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new NotSupportedException("This test session does not execute SQL.");
            }

            public ValueTask<MySqlTransaction> BeginTransactionAsync(
                System.Data.IsolationLevel isolationLevel,
                CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                throw new NotSupportedException(
                    "This test session does not execute SQL at isolation " + isolationLevel + ".");
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
