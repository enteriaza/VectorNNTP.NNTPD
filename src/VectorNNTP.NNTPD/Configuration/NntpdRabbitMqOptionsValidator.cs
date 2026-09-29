using Microsoft.Extensions.Options;
using VectorNNTP.Common.Messaging.RabbitMq;

namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// NNTPD RabbitMQ options validator: Common connection rules plus required Management HTTP settings.
/// </summary>
/// <remarks>
/// NNTPD ArticleWork availability discovery requires Management BaseUrl and broker credentials.
/// Connectivity-only hosts should register <see cref="RabbitMqOptionsValidator"/> alone.
/// </remarks>
public sealed class NntpdRabbitMqOptionsValidator : IValidateOptions<RabbitMqOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, RabbitMqOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        RabbitMqOptionsValidator.ValidateConnection(options, failures);
        ValidateRequiredManagement(options, failures);
        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static void ValidateRequiredManagement(RabbitMqOptions rabbitMq, List<string> failures)
    {
        rabbitMq.Management ??= new RabbitMqManagementOptions();
        var management = rabbitMq.Management;

        if (string.IsNullOrWhiteSpace(management.BaseUrl))
        {
            failures.Add("RabbitMQ:Management:BaseUrl is required.");
            return;
        }

        RabbitMqOptionsValidator.ValidateManagementShape(management, failures);

        if (string.IsNullOrWhiteSpace(rabbitMq.Username) || rabbitMq.Password is null)
        {
            failures.Add(
                "RabbitMQ:Username and RabbitMQ:Password are required for Management API availability discovery "
                + $"(supply via {RabbitMqOptions.UsernameEnvironmentVariable} / {RabbitMqOptions.PasswordEnvironmentVariable}).");
        }
    }
}
