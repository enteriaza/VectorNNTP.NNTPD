using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>Metadata and handler for one NNTP command (and optional subcommand).</summary>
public sealed class NntpCommandDescriptor
{
    /// <summary>Initializes a new instance of the <see cref="NntpCommandDescriptor"/> class.</summary>
    public NntpCommandDescriptor(
        string name,
        NntpCommandAccess access,
        Func<NntpCommandContext, CancellationToken, ValueTask> handler,
        string? subcommand = null,
        ILogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(handler);
        Name = name.ToUpperInvariant();
        Subcommand = subcommand?.ToUpperInvariant();
        Access = access;
        Handler = handler;
        Logger = logger ?? NullLogger.Instance;
    }

    /// <summary>Gets the primary command verb (ASCII uppercase).</summary>
    public string Name { get; }

    /// <summary>Gets the optional subcommand (ASCII uppercase), e.g. <c>USER</c> for <c>AUTHINFO USER</c>.</summary>
    public string? Subcommand { get; }

    /// <summary>Gets access requirements evaluated before the handler runs.</summary>
    public NntpCommandAccess Access { get; }

    /// <summary>Gets the handler invoked after validation succeeds.</summary>
    public Func<NntpCommandContext, CancellationToken, ValueTask> Handler { get; }

    /// <summary>
    /// Gets the command-module logger used for gate/reject TX completion records
    /// (same category as the command implementation).
    /// </summary>
    public ILogger Logger { get; }

    /// <summary>Gets a stable registry key for this descriptor.</summary>
    public string RegistryKey => Subcommand is null ? Name : $"{Name} {Subcommand}";
}
