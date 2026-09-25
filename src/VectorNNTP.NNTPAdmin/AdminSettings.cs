using VectorNNTP.NNTPAdmin.PgpVerify;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Session.Commands.Posting;

namespace VectorNNTP.NNTPAdmin;

/// <summary>Resolved runtime settings for one admin invocation.</summary>
internal sealed class AdminSettings
{
    public required string Host { get; init; }

    public required int Port { get; init; }

    public required bool UseTls { get; init; }

    public required string From { get; init; }

    public required IPostingTraceProtector TraceProtector { get; init; }

    public string? NewsmasterUser { get; init; }

    public string? NewsmasterPassword { get; init; }

    public PgpVerifySigner? PgpSigner { get; init; }

    /// <summary>Passphrase retained only so exception text can be redacted. Never printed.</summary>
    public string? PgpPassphrase { get; init; }

    public static bool TryCreate(
        AdminArguments arguments,
        NntpAdminOptions admin,
        NntpdOptions nntpd,
        out AdminSettings settings,
        out string error)
    {
        settings = null!;
        error = string.Empty;

        var host = arguments.Host ?? admin.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            error = "NNTP host is required (NntpAdmin:Host or --host).";
            return false;
        }

        var useTls = arguments.UseTls ?? admin.UseTls ?? nntpd.IsTlsListenerEnabled;
        var port = arguments.Port ?? (admin.Port > 0 ? admin.Port : useTls ? nntpd.BindPortTls : nntpd.BindPort);
        if (port is < 1 or > 65535)
        {
            error = useTls
                ? "TLS was requested but no TLS port is configured (NntpAdmin:Port, --port, or Nntpd:BindPortTls)."
                : "NNTP port is required (NntpAdmin:Port, --port, or Nntpd:BindPort).";
            return false;
        }

        if (!XTraceKeyParser.TryDecode(nntpd.XTraceKey, out _))
        {
            error =
                $"{NntpdOptions.XTraceKeyConfigurationKey} is required (use {NntpdOptions.XTraceKeyEnvironmentVariable} or secrets; the value is never printed).";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(nntpd.XTracePreviousKey)
            && !XTraceKeyParser.TryDecode(nntpd.XTracePreviousKey, out _))
        {
            error =
                $"{NntpdOptions.XTracePreviousKeyConfigurationKey} is not a 32-byte AES-256 key (the value is never printed).";
            return false;
        }

        IPostingTraceProtector protector;
        try
        {
            protector = AesGcmPostingTraceProtector.Create(nntpd);
        }
        catch (InvalidOperationException ex)
        {
            error = ex.Message;
            return false;
        }

        if (string.IsNullOrWhiteSpace(admin.From) || !PostFieldSyntax.IsMailbox(System.Text.Encoding.ASCII.GetBytes(admin.From)))
        {
            error = "NntpAdmin:From must be a mailbox address.";
            return false;
        }

        var user = FirstNonEmpty(arguments.Username, admin.Username, nntpd.NewsmasterUser);
        var password = FirstNonEmptySecret(arguments.Password, admin.Password, nntpd.NewsmasterPassword);
        if (arguments.Cancel)
        {
            if (user is null || password is null)
            {
                error =
                    "--cancel requires --username/--password or configured NntpAdmin:Username/Password " +
                    $"(or {NntpdOptions.NewsmasterUserConfigurationKey} / {NntpdOptions.NewsmasterPasswordConfigurationKey}). " +
                    "The password is never printed. Prefer environment or secrets over command-line --password.";
                return false;
            }

            if (!admin.Pgp.Enabled)
            {
                error = "CANCEL requires PGP signing to be configured.";
                return false;
            }
        }

        PgpVerifySigner? pgpSigner = null;
        var validatePgp = arguments.Cancel
            || (admin.Pgp.Enabled && !string.IsNullOrWhiteSpace(admin.Pgp.PrivateKeyPath));
        if (validatePgp)
        {
            if (!PgpVerifySigner.TryCreate(admin.Pgp, out pgpSigner, out error))
            {
                return false;
            }
        }

        settings = new AdminSettings
        {
            Host = host.Trim(),
            Port = port,
            UseTls = useTls,
            From = admin.From.Trim(),
            TraceProtector = protector,
            NewsmasterUser = user,
            NewsmasterPassword = password,
            PgpSigner = pgpSigner,
            PgpPassphrase = pgpSigner is null ? null : FirstNonEmptySecret(admin.Pgp.PrivateKeyPassphrase),
        };
        return true;
    }

    public bool HasCredentials =>
        !string.IsNullOrEmpty(NewsmasterUser) && NewsmasterPassword is not null;

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string? FirstNonEmptySecret(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrEmpty(value))
            {
                return value;
            }
        }

        return null;
    }
}
