using VectorNNTP.NNTPD.Session;
using VectorNNTP.NNTPD.Session.Commands.Posting;

namespace VectorNNTP.NNTPAdmin;

/// <summary>
/// Parses newsmaster CLI arguments.
/// </summary>
/// <remarks>
/// Canonical syntax matches other VectorNNTP tools (<c>--flag</c>).
/// <c>-cancel</c> is accepted as an alias because it is the explicit destructive confirmation.
/// </remarks>
internal static class AdminCliParser
{
    public static string Usage { get; } =
        """
        VectorNNTP.NNTPAdmin — inspect an article by Message-ID; cancel only with --cancel.

        Usage:
          VectorNNTP.NNTPAdmin [options] <message-id>
          VectorNNTP.NNTPAdmin [options] --cancel <message-id>
          VectorNNTP.NNTPAdmin [options] -cancel <message-id>

        Examples:
          VectorNNTP.NNTPAdmin --host nntpd01.usenet.ninja --port 563 --tls \\
            --username newsmaster --password secret <message-id>
          VectorNNTP.NNTPAdmin --host nntpd01.usenet.ninja --port 563 --tls \\
            --username newsmaster --password secret --cancel <message-id>

        Message-ID:
          <abc@example.com>   used as-is
          abc@example.com     wrapped once as <abc@example.com>

        Without --cancel / -cancel:
          optional AUTHINFO when credentials are supplied, then HEAD only.
          Never POSTs. Never sends a cancel article.

        With --cancel / -cancel:
          credentials are mandatory. PGPVERIFY signing is mandatory. AUTHINFO
          must succeed, then HEAD, display headers and decrypted X-Trace, then
          POST an RFC 5537 cancel article with a PGPVERIFY X-PGP-Sig. There is
          no --no-sign / --unsigned / --skip-signature option. The flag is the
          confirmation; there is no interactive prompt. Failed AUTHINFO or
          PGP configuration stops the utility before POST.

        Cancel is refused when authentication fails, HEAD fails, the article is
        missing (430), X-Trace cannot be decrypted, or PGPVERIFY cannot be
        configured or used.

        Options:
          --host <name>         NNTP server hostname or address
          --port <n>            TCP port
          --username <name>     AUTHINFO username (overrides configuration)
          --password <secret>   AUTHINFO password (overrides configuration)
          --tls                 Use TLS (never falls back to plaintext)
          --plaintext           Use cleartext NNTP
          --cancel, -cancel     POST a cancel after successful inspect
          --help, -h, -?        Show this text

        Command-line --password can appear in OS process listings and shell
        history. Prefer NntpAdmin:Password / Nntpd:NewsmasterPassword via
        environment or secrets for safer deployment. The password is never
        printed, logged, or included in exception messages.

        Configuration (appsettings.json / environment):
          NntpAdmin:Host, NntpAdmin:Port, NntpAdmin:UseTls, NntpAdmin:From
          NntpAdmin:Username, NntpAdmin:Password
          NntpAdmin:Pgp:Enabled, PrivateKeyPath, PrivateKeyPassphrase, KeyId
          Nntpd:BindPort, Nntpd:BindPortTls
          Nntpd:XTraceKey, Nntpd:XTracePreviousKey   (secrets; never printed)
          Nntpd:NewsmasterUser, Nntpd:NewsmasterPassword  (required for --cancel)

        PGPVERIFY is a de-facto Netnews convention, not an RFC 5537 protocol.
        The private key must remain outside Git. Passphrase and key material
        are never printed.
        """;

    public static bool TryParse(string[] args, out AdminArguments arguments, out string error)
    {
        arguments = null!;
        error = string.Empty;
        if (args is null || args.Length == 0)
        {
            error = "Message-ID is required.";
            return false;
        }

        string? rawId = null;
        var cancel = false;
        string? host = null;
        int? port = null;
        bool? useTls = null;
        string? username = null;
        string? password = null;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "--help":
                case "-h":
                case "-?":
                    arguments = new AdminArguments
                    {
                        MessageId = string.Empty,
                        RawMessageId = string.Empty,
                        Help = true,
                    };
                    return true;
                case "--cancel":
                case "-cancel":
                    if (cancel)
                    {
                        error = "Duplicate --cancel argument.";
                        return false;
                    }

                    cancel = true;
                    break;
                case "--host":
                    if (host is not null)
                    {
                        error = "Duplicate --host argument.";
                        return false;
                    }

                    if (!TryTakeValue(args, ref i, out host))
                    {
                        error = "--host requires a value.";
                        return false;
                    }

                    break;
                case "--port":
                    if (port is not null)
                    {
                        error = "Duplicate --port argument.";
                        return false;
                    }

                    if (!TryTakeValue(args, ref i, out var portText)
                        || !int.TryParse(portText, out var parsedPort)
                        || parsedPort is < 1 or > 65535)
                    {
                        error = "--port requires an integer in 1–65535.";
                        return false;
                    }

                    port = parsedPort;
                    break;
                case "--tls":
                    if (useTls is not null)
                    {
                        error = "TLS mode specified more than once.";
                        return false;
                    }

                    useTls = true;
                    break;
                case "--plaintext":
                    if (useTls is not null)
                    {
                        error = "TLS mode specified more than once.";
                        return false;
                    }

                    useTls = false;
                    break;
                case "--username":
                    if (username is not null)
                    {
                        error = "Duplicate --username argument.";
                        return false;
                    }

                    if (!TryTakeRawValue(args, ref i, out username))
                    {
                        error = "--username requires a value.";
                        return false;
                    }

                    break;
                case "--password":
                    if (password is not null)
                    {
                        error = "Duplicate --password argument.";
                        return false;
                    }

                    if (!TryTakeRawValue(args, ref i, out password))
                    {
                        error = "--password requires a value.";
                        return false;
                    }

                    break;
                default:
                    if (arg.StartsWith('-'))
                    {
                        error = $"Unknown argument: {arg}";
                        return false;
                    }

                    if (rawId is not null)
                    {
                        error = "Only one Message-ID argument is accepted.";
                        return false;
                    }

                    rawId = arg;
                    break;
            }
        }

        if (rawId is null)
        {
            error = "Message-ID is required.";
            return false;
        }

        if (!MessageIdArgument.TryNormalize(rawId, out var messageId, out error))
        {
            return false;
        }

        arguments = new AdminArguments
        {
            MessageId = messageId,
            RawMessageId = rawId,
            Cancel = cancel,
            Host = host,
            Port = port,
            UseTls = useTls,
            Username = username,
            Password = password,
        };
        return true;
    }

    private static bool TryTakeValue(string[] args, ref int index, out string value)
    {
        if (!TryTakeRawValue(args, ref index, out value))
        {
            return false;
        }

        return !value.StartsWith('-');
    }

    /// <summary>
    /// Takes the next token as-is so <c>--password</c> (and <c>--username</c>)
    /// may start with <c>-</c>. Empty values are rejected.
    /// </summary>
    private static bool TryTakeRawValue(string[] args, ref int index, out string value)
    {
        if (index + 1 >= args.Length)
        {
            value = string.Empty;
            return false;
        }

        index++;
        value = args[index];
        return value.Length > 0;
    }
}

/// <summary>Normalizes a CLI Message-ID exactly once for the NNTP command.</summary>
internal static class MessageIdArgument
{
    public static bool TryNormalize(string? raw, out string messageId, out string error)
    {
        messageId = string.Empty;
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Message-ID is required.";
            return false;
        }

        var trimmed = raw.Trim();
        if (trimmed.Length >= 2 && trimmed[0] == '<' && trimmed[^1] == '>')
        {
            messageId = trimmed;
        }
        else
        {
            messageId = "<" + trimmed + ">";
        }

        var bytes = System.Text.Encoding.ASCII.GetBytes(messageId);
        if (!NntpMessageId.IsWellFormed(bytes) || !PostFieldSyntax.IsMessageId(bytes))
        {
            error = "Message-ID is not well-formed.";
            messageId = string.Empty;
            return false;
        }

        return true;
    }
}
