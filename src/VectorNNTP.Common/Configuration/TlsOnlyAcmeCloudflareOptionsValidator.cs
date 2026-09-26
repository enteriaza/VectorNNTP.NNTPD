using Microsoft.Extensions.Options;

namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// Application policy: TLS is required. <see cref="AcmeCloudflareOptions.BindPortTls"/>
/// must be a usable TCP port. There is no cleartext fallback.
/// </summary>
/// <remarks>
/// Register this validator only in TLS-only hosts (BackFiller). NNTPD retains
/// <c>BindPortTls = 0</c> as "TLS disabled".
/// </remarks>
public sealed class TlsOnlyAcmeCloudflareOptionsValidator : IValidateOptions<AcmeCloudflareOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, AcmeCloudflareOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.BindPortTls is < 1 or > 65535)
        {
            return ValidateOptionsResult.Fail(
                $"{nameof(AcmeCloudflareOptions.BindPortTls)} must be an integer in the range 1–65535 because this application is TLS-only. There is no cleartext listener fallback.");
        }

        return ValidateOptionsResult.Success;
    }
}
