using Microsoft.Extensions.Options;
using VectorNNTP.Common.NntpDb;

namespace VectorNNTP.Common.Configuration
{
    /// <summary>Validates <see cref="NntpDbOptions"/> at bind / startup time.</summary>
    /// <remarks>Never includes the connection string or credentials in failure messages.</remarks>
    internal sealed class NntpDbOptionsValidator : IValidateOptions<NntpDbOptions>
    {
        /// <inheritdoc />
        public ValidateOptionsResult Validate(string? name, NntpDbOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);

            var failures = new List<string>();
            if (string.IsNullOrWhiteSpace(options.ConnectionString))
            {
                failures.Add($"ConnectionStrings:{NntpDbOptions.ConnectionStringName} must be configured.");
            }
            else
            {
                try
                {
                    NntpDbConnectionString.Validate(options.ConnectionString);
                }
                catch (NntpDbConfigurationException ex)
                {
                    failures.Add(ex.Message);
                }
            }

            if (options.StartupTimeout <= TimeSpan.Zero)
            {
                failures.Add($"{nameof(NntpDbOptions.StartupTimeout)} must be greater than zero.");
            }

            return failures.Count > 0
                ? ValidateOptionsResult.Fail(failures)
                : ValidateOptionsResult.Success;
        }
    }
}
