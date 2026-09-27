using System.Net.Security;
using System.Security.Authentication;

namespace VectorNNTP.BackFiller.Nntp;

/// <summary>
/// Shared outbound TLS client options and handshake used by implicit TLS connect
/// and STARTTLS upgrade. Platform certificate validation is the default.
/// </summary>
internal static class NntpTlsClient
{
    /// <summary>Builds the production client TLS options for <paramref name="host"/>.</summary>
    internal static SslClientAuthenticationOptions CreateClientOptions(
        string host,
        RemoteCertificateValidationCallback? serverCertificateValidationCallback)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var options = new SslClientAuthenticationOptions
        {
            TargetHost = host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
        };
        if (serverCertificateValidationCallback is not null)
        {
            options.RemoteCertificateValidationCallback = serverCertificateValidationCallback;
        }

        return options;
    }

    /// <summary>
    /// Wraps <paramref name="inner"/> in <see cref="SslStream"/> and authenticates as client.
    /// On failure the wrapper is disposed and owns (closes) <paramref name="inner"/>.
    /// </summary>
    internal static async Task<SslStream> AuthenticateAsClientAsync(
        Stream inner,
        string host,
        TimeSpan timeout,
        RemoteCertificateValidationCallback? serverCertificateValidationCallback,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inner);
        var ssl = new SslStream(inner, leaveInnerStreamOpen: false);
        try
        {
            using var tlsCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            tlsCts.CancelAfter(timeout);
            await ssl.AuthenticateAsClientAsync(
                    CreateClientOptions(host, serverCertificateValidationCallback),
                    tlsCts.Token)
                .ConfigureAwait(false);
            return ssl;
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
