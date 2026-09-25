using System.Globalization;
using VectorNNTP.NNTPCancelMessage.PgpVerify;
using VectorNNTP.NNTPD.Session.Commands.Posting;

namespace VectorNNTP.NNTPCancelMessage;

/// <summary>Formats newsmaster terminal output. Does not write to application logs.</summary>
internal static class AdminReport
{
    public static void WriteTransport(TextWriter writer, AdminSettings settings)
    {
        writer.WriteLine($"Server:    {settings.Host}:{settings.Port}");
        writer.WriteLine(settings.UseTls ? "Transport: TLS" : "Transport: plaintext");
    }

    public static void WriteAuthenticated(TextWriter writer, string username)
    {
        writer.WriteLine($"Authenticated as: {username}");
    }

    public static void WriteArticleHeaders(TextWriter writer, IReadOnlyList<string> headers)
    {
        writer.WriteLine();
        writer.WriteLine("Article headers");
        writer.WriteLine("--------------");
        if (headers.Count == 0)
        {
            writer.WriteLine("(none)");
            return;
        }

        foreach (var header in headers)
        {
            writer.WriteLine(header);
        }
    }

    public static void WriteTrace(
        TextWriter writer,
        string? rawToken,
        bool present,
        bool decrypted,
        PostingTracePayload payload)
    {
        writer.WriteLine();
        writer.WriteLine("Decrypted X-Trace");
        writer.WriteLine("-----------------");
        if (!present)
        {
            writer.WriteLine("X-Trace header is missing.");
            return;
        }

        if (!decrypted)
        {
            writer.WriteLine("X-Trace could not be decrypted (tampered, malformed, or encrypted with an unavailable key).");
            writer.WriteLine("The encrypted token is not useful by itself and is not shown as the lookup result.");
            _ = rawToken;
            return;
        }

        writer.WriteLine($"  Peer IP:       {payload.Address}");
        writer.WriteLine($"  Peer Port:     {payload.Port}");
        writer.WriteLine(
            $"  Injected UTC:  {payload.InjectedAtUtc.ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture)}");
        writer.WriteLine($"  Trace ID:      {payload.TraceId:D}");
        writer.WriteLine(
            $"  Authenticated user: {(string.IsNullOrEmpty(payload.AuthenticatedUsername) ? "(unauthenticated)" : payload.AuthenticatedUsername)}");
    }

    public static void WriteCancelRequested(TextWriter writer, string originalMessageId, string? submissionUsername)
    {
        writer.WriteLine();
        writer.WriteLine("Cancellation requested");
        writer.WriteLine("----------------------");
        writer.WriteLine($"Target Message-ID: {originalMessageId}");
        writer.WriteLine("A cancel control article will be POSTed for this Message-ID only.");
        writer.WriteLine();
        writer.WriteLine("CANCEL submission identity");
        writer.WriteLine("--------------------------");
        writer.WriteLine(
            $"  Authenticated user: {(string.IsNullOrEmpty(submissionUsername) ? "(missing)" : submissionUsername)}");
        writer.WriteLine("  The server generates a distinct X-Trace for the cancel article.");
    }

    public static void WriteCancelArticle(
        TextWriter writer,
        string cancelMessageId,
        string originalMessageId,
        string newsgroups)
    {
        writer.WriteLine();
        writer.WriteLine("CANCEL");
        writer.WriteLine("------");
        writer.WriteLine($"  Message-ID: {cancelMessageId}");
        writer.WriteLine($"  Control: cancel {originalMessageId}");
        writer.WriteLine($"  Newsgroups: {newsgroups}");
    }

    public static void WritePgpVerify(TextWriter writer, PgpVerifySigningIdentity identity)
    {
        writer.WriteLine();
        writer.WriteLine("PGPVERIFY");
        writer.WriteLine("---------");
        writer.WriteLine($"  Signing key: {identity.Fingerprint}");
        if (!string.IsNullOrEmpty(identity.UserId))
        {
            writer.WriteLine($"  User ID:     {identity.UserId}");
        }
    }

    public static void WriteCancelResult(TextWriter writer, string response, bool success)
    {
        writer.WriteLine($"Server response: {response}");
        writer.WriteLine(success ? "Cancel POST succeeded." : "Cancel POST failed.");
    }

    public static bool TryFindHeader(IReadOnlyList<string> headers, string name, out string value)
    {
        value = string.Empty;
        foreach (var header in headers)
        {
            var colon = header.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var headerName = header[..colon];
            if (!headerName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            value = header[(colon + 1)..].Trim();
            return value.Length > 0;
        }

        return false;
    }
}
