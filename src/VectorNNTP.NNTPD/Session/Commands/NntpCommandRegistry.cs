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
    private readonly HashSet<string> _verbsWithSubcommands = new(StringComparer.Ordinal);

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

            _verbsWithSubcommands.Add(descriptor.Name);
        }

        _knownVerbs.Add(descriptor.Name);
        return this;
    }

    /// <summary>
    /// Gets all registered registry keys (verb, or <c>VERB SUBCOMMAND</c>), ordered for stable inventory tests.
    /// </summary>
    public IReadOnlyList<string> GetRegisteredKeys()
    {
        var keys = new List<string>(_exact.Count + _verbOnly.Count);
        keys.AddRange(_exact.Keys);
        keys.AddRange(_verbOnly.Keys);
        keys.Sort(StringComparer.Ordinal);
        return keys;
    }

    /// <summary>
    /// Attempts to resolve a parsed command. Prefer verb+first-token exact match (e.g. AUTHINFO USER), then verb-only.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when a descriptor was found; <paramref name="descriptor"/> is then non-null.
    /// When <see langword="false"/>, <paramref name="status"/> is <see cref="NntpCommandResolveStatus.UnknownCommand"/>
    /// or <see cref="NntpCommandResolveStatus.UnknownSubcommand"/>.
    /// </returns>
    public bool TryResolve(
        NntpParsedCommand parsed,
        [NotNullWhen(true)] out NntpCommandDescriptor? descriptor,
        out IReadOnlyList<string> handlerArguments,
        out NntpCommandResolveStatus status)
    {
        if (parsed.Tokens.Count > 0)
        {
            var key = $"{parsed.Verb} {parsed.Tokens[0].ToUpperInvariant()}";
            if (_exact.TryGetValue(key, out descriptor))
            {
                handlerArguments = parsed.Tokens.Count == 1
                    ? Array.Empty<string>()
                    : parsed.Tokens.Skip(1).ToArray();
                status = NntpCommandResolveStatus.Found;
                return true;
            }

            // Verb has registered subcommand variants, but this token is not one of them.
            // Do not fall through to a verb-only handler (e.g. bare LIST) for unknown variants.
            if (_verbsWithSubcommands.Contains(parsed.Verb))
            {
                descriptor = null;
                handlerArguments = Array.Empty<string>();
                status = NntpCommandResolveStatus.UnknownSubcommand;
                return false;
            }
        }

        if (_verbOnly.TryGetValue(parsed.Verb, out descriptor))
        {
            handlerArguments = parsed.Tokens;
            status = NntpCommandResolveStatus.Found;
            return true;
        }

        descriptor = null;
        handlerArguments = Array.Empty<string>();
        status = _knownVerbs.Contains(parsed.Verb)
            ? NntpCommandResolveStatus.UnknownSubcommand
            : NntpCommandResolveStatus.UnknownCommand;
        return false;
    }
}
