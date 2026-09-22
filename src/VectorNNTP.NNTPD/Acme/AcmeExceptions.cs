namespace VectorNNTP.NNTPD.Acme;

/// <summary>Base type for ACME subsystem failures (sanitized messages; never include key material).</summary>
public class AcmeException : Exception
{
    /// <summary>Initializes a new instance of the <see cref="AcmeException"/> class.</summary>
    public AcmeException(string category, string message)
        : base($"{category}: {message}")
    {
        Category = category;
    }

    /// <summary>Gets a stable failure category (for logging/tests).</summary>
    public string Category { get; }
}

/// <summary>Configuration prerequisite failure for ACME / DNS-01.</summary>
public sealed class AcmeConfigurationException : AcmeException
{
    /// <inheritdoc cref="AcmeException(string, string)"/>
    public AcmeConfigurationException(string category, string message)
        : base(category, message)
    {
    }
}

/// <summary>ACME account persistence or registration failure.</summary>
public sealed class AcmeAccountException : AcmeException
{
    /// <inheritdoc cref="AcmeException(string, string)"/>
    public AcmeAccountException(string category, string message)
        : base(category, message)
    {
    }
}

/// <summary>Server certificate validation or persistence failure.</summary>
public sealed class AcmeCertificateException : AcmeException
{
    /// <inheritdoc cref="AcmeException(string, string)"/>
    public AcmeCertificateException(string category, string message)
        : base(category, message)
    {
    }
}

/// <summary>ACME order / challenge protocol failure.</summary>
public sealed class AcmeOrderException : AcmeException
{
    /// <inheritdoc cref="AcmeException(string, string)"/>
    public AcmeOrderException(string category, string message)
        : base(category, message)
    {
    }
}

/// <summary>DNS-01 challenge placement, propagation, or cleanup failure.</summary>
public sealed class AcmeChallengeException : AcmeException
{
    /// <inheritdoc cref="AcmeException(string, string)"/>
    public AcmeChallengeException(string category, string message)
        : base(category, message)
    {
    }
}

/// <summary>Filesystem persistence failure for ACME state.</summary>
public sealed class AcmeStorageException : AcmeException
{
    /// <inheritdoc cref="AcmeException(string, string)"/>
    public AcmeStorageException(string category, string message)
        : base(category, message)
    {
    }
}

/// <summary>
/// Helpers that sanitize exception text for logs (categories + safe diagnostics; no key material).
/// </summary>
public static class AcmeFailureSanitizer
{
    private const int MaxSanitizedLength = 512;

    /// <summary>Returns a short sanitized description of <paramref name="exception"/>.</summary>
    public static string Sanitize(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception is AcmeException acme)
        {
            // AcmeException messages are constructed from sanitized fragments only.
            var message = acme.Message;
            if (string.IsNullOrWhiteSpace(message))
            {
                return $"{acme.Category}:{acme.GetType().Name}";
            }

            var cleaned = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
            if (cleaned.Length > MaxSanitizedLength)
            {
                cleaned = cleaned[..MaxSanitizedLength] + "...";
            }

            return cleaned;
        }

        return exception.GetType().Name;
    }
}
