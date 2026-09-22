using VectorNNTP.NNTPD.Networking.Certificates;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>STARTTLS command (RFC 4642); TLS handshake is transport-owned.</summary>
internal static class StartTls
{
    /// <summary>Handles <c>STARTTLS</c>.</summary>
    public static async ValueTask HandleAsync(
        NntpCommandContext context,
        ITlsCertificateContextProvider? certificateProvider,
        CancellationToken cancellationToken)
    {
        if (context.Connection.IsTls)
        {
            await context.Response
                .WriteLineAsync(NntpReplyCodes.CommandUnavailable, "TLS already active", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (certificateProvider is null)
        {
            await context.Response
                .WriteLineAsync(NntpReplyCodes.CommandUnavailable, "TLS provider unavailable", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await context.Response
            .WriteLineAsync(NntpReplyCodes.ContinueWithTls, "Continue with TLS negotiation", cancellationToken)
            .ConfigureAwait(false);
        try
        {
            await context.Connection.UpgradeToTlsAsync(certificateProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            context.Session.RequestClose();
            throw;
        }
    }
}
