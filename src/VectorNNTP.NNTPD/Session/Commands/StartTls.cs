using System.IO.Pipelines;
using VectorNNTP.NNTPD.Networking.Certificates;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>STARTTLS command (RFC 4642 / RFC 8143); TLS handshake is transport-owned.</summary>
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

        // RFC 8143 §4: discard any NNTP command pipelined between STARTTLS and TLS negotiation.
        DiscardBufferedApplicationInput(context.Connection.Input);

        var idleVersionBefore382 = context.Connection.OutboundIdleVersion;
        await context.Response
            .WriteLineAsync(NntpReplyCodes.ContinueWithTls, "Continue with TLS negotiation", cancellationToken)
            .ConfigureAwait(false);

        // Deliver 382 then pause reads in one step so ClientHello cannot race into Input.
        await context.Connection
            .WaitForOutboundDeliveryAndPauseReadsAsync(idleVersionBefore382, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            await context.Connection.UpgradeToTlsAsync(certificateProvider, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            context.Session.RequestClose();
            // Handshake failures complete the connection; do not rethrow into the dispatcher error path.
            // Precondition failures leave the connection usable — rethrow so the dispatcher can respond.
            if (context.Connection.IsCompleted)
            {
                return;
            }

            throw;
        }
    }

    /// <summary>
    /// Consumes any octets already buffered in application <see cref="PipeReader"/> without waiting for more.
    /// </summary>
    private static void DiscardBufferedApplicationInput(PipeReader input)
    {
        while (input.TryRead(out var result))
        {
            var isCompleted = result.IsCompleted;
            var isEmpty = result.Buffer.IsEmpty;
            input.AdvanceTo(result.Buffer.End);
            if (isEmpty || isCompleted)
            {
                break;
            }
        }
    }
}
