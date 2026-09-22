using System.Diagnostics.CodeAnalysis;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>Result of resolving a parsed command against the registry.</summary>
public enum NntpCommandResolveStatus
{
    /// <summary>No registered descriptor matches the verb.</summary>
    UnknownCommand = 0,

    /// <summary>The verb is known but the subcommand/variant is not registered.</summary>
    UnknownSubcommand = 1,

    /// <summary>A descriptor was matched.</summary>
    Found = 2,
}

/// <summary>Registry of NNTP command descriptors keyed by verb or verb+subcommand.</summary>
public sealed class NntpCommandRegistry
{
    private readonly Dictionary<string, NntpCommandDescriptor> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, NntpCommandDescriptor> _verbOnly = new(StringComparer.Ordinal);
    private readonly HashSet<string> _knownVerbs = new(StringComparer.Ordinal);

    /// <summary>Registers a command descriptor.</summary>
    public NntpCommandRegistry Register(NntpCommandDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (descriptor.Subcommand is null)
        {
            if (!_verbOnly.TryAdd(descriptor.Name, descriptor))
            {
                throw new InvalidOperationException($"Command '{descriptor.Name}' is already registered.");
            }
        }
        else
        {
            if (!_exact.TryAdd(descriptor.RegistryKey, descriptor))
            {
                throw new InvalidOperationException($"Command '{descriptor.RegistryKey}' is already registered.");
            }
        }

        _knownVerbs.Add(descriptor.Name);
        return this;
    }

    /// <summary>
    /// Resolves a parsed command. Prefer verb+first-token exact match (e.g. AUTHINFO USER), then verb-only.
    /// </summary>
    public NntpCommandResolveStatus Resolve(
        NntpParsedCommand parsed,
        [NotNullWhen(true)] out NntpCommandDescriptor? descriptor,
        out IReadOnlyList<string> handlerArguments)
    {
        if (parsed.Tokens.Count > 0)
        {
            var key = $"{parsed.Verb} {parsed.Tokens[0].ToUpperInvariant()}";
            if (_exact.TryGetValue(key, out descriptor))
            {
                handlerArguments = parsed.Tokens.Count == 1
                    ? Array.Empty<string>()
                    : parsed.Tokens.Skip(1).ToArray();
                return NntpCommandResolveStatus.Found;
            }
        }

        if (_verbOnly.TryGetValue(parsed.Verb, out descriptor))
        {
            handlerArguments = parsed.Tokens;
            return NntpCommandResolveStatus.Found;
        }

        descriptor = null;
        handlerArguments = Array.Empty<string>();
        if (_knownVerbs.Contains(parsed.Verb))
        {
            return NntpCommandResolveStatus.UnknownSubcommand;
        }

        return NntpCommandResolveStatus.UnknownCommand;
    }
}
