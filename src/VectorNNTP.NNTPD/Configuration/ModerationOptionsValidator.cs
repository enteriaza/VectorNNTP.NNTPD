using System.Text;
using Microsoft.Extensions.Options;
using VectorNNTP.NNTPD.Moderation;
using VectorNNTP.NNTPD.Newsgroups;

namespace VectorNNTP.NNTPD.Configuration;

/// <summary>
/// Validates <see cref="ModerationOptions"/> when the section is bound.
/// </summary>
/// <remarks>
/// An omitted or empty catalogue is valid. Duplicate exact patterns, empty
/// pattern/address, malformed wildmats, and malformed address templates fail
/// validation. Username may be omitted for routing-only entries. Overlapping
/// distinct wildmats are permitted; first-match order is the documented
/// resolution rule.
/// </remarks>
public sealed class ModerationOptionsValidator : IValidateOptions<ModerationOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, ModerationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var mappings = options.Moderators;
        if (mappings is null || mappings.Length == 0)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = new List<string>();
        var seenPatterns = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < mappings.Length; i++)
        {
            var mapping = mappings[i];
            if (mapping is null)
            {
                failures.Add($"{nameof(ModerationOptions.Moderators)}[{i}] must not be null.");
                continue;
            }

            var pattern = mapping.Pattern?.Trim() ?? string.Empty;
            var address = mapping.Address?.Trim() ?? string.Empty;

            if (pattern.Length == 0)
            {
                failures.Add($"{nameof(ModerationOptions.Moderators)}[{i}].{nameof(ModeratorMappingOptions.Pattern)} must not be empty.");
            }
            else if (!IsAscii(pattern) || !NntpWildmat.TryValidate(Encoding.ASCII.GetBytes(pattern)))
            {
                failures.Add($"{nameof(ModerationOptions.Moderators)}[{i}].{nameof(ModeratorMappingOptions.Pattern)} is not a valid NNTP wildmat.");
            }
            else if (seenPatterns.TryGetValue(pattern, out var firstIndex))
            {
                failures.Add(
                    $"{nameof(ModerationOptions.Moderators)}[{i}].{nameof(ModeratorMappingOptions.Pattern)} duplicates [{firstIndex}]. Each pattern must have one moderator identity.");
            }
            else
            {
                seenPatterns.Add(pattern, i);
            }

            if (address.Length == 0)
            {
                failures.Add($"{nameof(ModerationOptions.Moderators)}[{i}].{nameof(ModeratorMappingOptions.Address)} must not be empty.");
            }
            else if (!ModeratorAddressTemplate.TryValidate(address))
            {
                failures.Add(
                    $"{nameof(ModerationOptions.Moderators)}[{i}].{nameof(ModeratorMappingOptions.Address)} must be a mailbox identity or a single '%s' INN address template.");
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }

    private static bool IsAscii(string value)
    {
        foreach (var c in value)
        {
            if (c > 127)
            {
                return false;
            }
        }

        return true;
    }
}
