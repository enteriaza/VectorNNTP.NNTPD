using System.ComponentModel.DataAnnotations;

namespace VectorNNTP.NNTPCancelMessage;

/// <summary>Client connection options for the newsmaster utility.</summary>
public sealed class NntpCancelMessageOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "NntpCancelMessage";

    /// <summary>Gets or sets the NNTP server hostname or address.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the TCP port. <c>0</c> means derive from
    /// <c>Nntpd:BindPortTls</c> or <c>Nntpd:BindPort</c> according to TLS.
    /// </summary>
    [Range(0, 65535)]
    public int Port { get; set; }

    /// <summary>
    /// Gets or sets whether to use TLS. When unset, TLS is used only if
    /// <c>Nntpd:BindPortTls</c> is enabled.
    /// </summary>
    public bool? UseTls { get; set; }

    /// <summary>Gets or sets the <c>From:</c> mailbox on generated cancel articles.</summary>
    public string From { get; set; } = "newsmaster@usenet.ninja";

    /// <summary>
    /// Gets or sets the AUTHINFO username. Prefer environment or secrets over
    /// committing this value. Command-line <c>--username</c> overrides this setting.
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the AUTHINFO password. Prefer environment or secrets over
    /// command-line <c>--password</c>, which can appear in process listings and shell history.
    /// The value is never written to logs or exception messages.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>Gets or sets PGPVERIFY signing options for CANCEL.</summary>
    public PgpOptions Pgp { get; set; } = new();
}

/// <summary>
/// PGPVERIFY private-key settings. Secrets must come from environment or a file
/// outside Git. Values are never logged.
/// </summary>
public sealed class PgpOptions
{
    /// <summary>Gets or sets whether PGPVERIFY signing is configured.</summary>
    public bool Enabled { get; set; }

    /// <summary>Gets or sets the path to an ASCII-armored OpenPGP secret key or keyring.</summary>
    public string PrivateKeyPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the passphrase that unlocks the secret key.
    /// Prefer <c>NntpCancelMessage__Pgp__PrivateKeyPassphrase</c> over committing this value.
    /// </summary>
    public string PrivateKeyPassphrase { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the signing-key fingerprint or 16-hex Key ID.
    /// Required when the file contains more than one signing-capable secret key.
    /// </summary>
    public string KeyId { get; set; } = string.Empty;
}
