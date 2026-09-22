using Microsoft.Extensions.Logging;
using VectorNNTP.NNTPD.Networking.Certificates;
using VectorNNTP.NNTPD.Session.Authentication;

namespace VectorNNTP.NNTPD.Session.Commands;

/// <summary>Builds the default NNTP command registry (complete inventory; handlers live in per-command files).</summary>
/// <remarks>
/// See <c>docs/commands.md</c> for the implementation checklist. This type only wires descriptors;
/// it does not contain command business logic.
/// </remarks>
public static class DefaultNntpCommandCatalog
{
    /// <summary>
    /// Expected registry keys for the complete command inventory (stable order for tests).
    /// </summary>
    public static IReadOnlyList<string> InventoryKeys { get; } =
    [
        "ARTICLE",
        "AUTHINFO PASS",
        "AUTHINFO SASL",
        "AUTHINFO USER",
        "BODY",
        "CAPABILITIES",
        "CHECK",
        "COMPRESS DEFLATE",
        "DATE",
        "GROUP",
        "HDR",
        "HEAD",
        "HELP",
        "IHAVE",
        "LAST",
        "LIST",
        "LISTGROUP",
        "MODE READER",
        "MODE STREAM",
        "NEWGROUPS",
        "NEWNEWS",
        "NEXT",
        "OVER",
        "POST",
        "QUIT",
        "STARTTLS",
        "STAT",
        "TAKETHIS",
    ];

    /// <summary>Creates the default registry with the full command inventory.</summary>
    public static NntpCommandRegistry Create(
        ITlsCertificateContextProvider? certificateProvider = null,
        INntpAuthenticationProvider? authenticationProvider = null,
        ILoggerFactory? loggerFactory = null)
    {
        authenticationProvider ??= DenyAllNntpAuthenticationProvider.Instance;
        var registry = new NntpCommandRegistry();

        // Core / session
        registry.Register(new NntpCommandDescriptor("CAPABILITIES", NntpCommandAccess.Public, Capabilities.HandleAsync));
        registry.Register(new NntpCommandDescriptor("MODE", NntpCommandAccess.Public, Mode.HandleReaderAsync, "READER"));
        registry.Register(new NntpCommandDescriptor(
            "MODE",
            NntpCommandAccess.RequiresAuthentication | NntpCommandAccess.RequiresTransit | NntpCommandAccess.RequiresStreaming,
            Mode.HandleStreamAsync,
            "STREAM"));
        registry.Register(new NntpCommandDescriptor("HELP", NntpCommandAccess.Public, Help.HandleAsync));
        registry.Register(new NntpCommandDescriptor("DATE", NntpCommandAccess.Public, Date.HandleAsync));
        registry.Register(new NntpCommandDescriptor("QUIT", NntpCommandAccess.Public, Quit.HandleAsync));
        registry.Register(new NntpCommandDescriptor(
            "STARTTLS",
            NntpCommandAccess.Public,
            (ctx, ct) => StartTls.HandleAsync(ctx, certificateProvider, ct)));
        registry.Register(new NntpCommandDescriptor(
            "COMPRESS",
            NntpCommandAccess.Public,
            Compress.HandleDeflateAsync,
            "DEFLATE"));

        // Authentication
        registry.Register(new NntpCommandDescriptor(
            "AUTHINFO",
            NntpCommandAccess.Public,
            (ctx, ct) => AuthInfo.HandleUserAsync(ctx, authenticationProvider, ct),
            "USER"));
        registry.Register(new NntpCommandDescriptor(
            "AUTHINFO",
            NntpCommandAccess.Public,
            (ctx, ct) => AuthInfo.HandlePassAsync(ctx, authenticationProvider, ct),
            "PASS"));
        registry.Register(new NntpCommandDescriptor(
            "AUTHINFO",
            NntpCommandAccess.Public,
            AuthInfo.HandleSaslAsync,
            "SASL"));

        // Reader / group
        var readerAccess = NntpCommandAccess.RequiresAuthentication | NntpCommandAccess.RequiresReader;
        registry.Register(new NntpCommandDescriptor("LIST", readerAccess, List.HandleAsync));
        registry.Register(new NntpCommandDescriptor("GROUP", readerAccess, Group.HandleAsync));
        registry.Register(new NntpCommandDescriptor("LISTGROUP", readerAccess, ListGroup.HandleAsync));
        registry.Register(new NntpCommandDescriptor("NEWGROUPS", readerAccess, NewGroups.HandleAsync));
        registry.Register(new NntpCommandDescriptor("NEWNEWS", readerAccess, NewNews.HandleAsync));

        // Article retrieval
        registry.Register(new NntpCommandDescriptor("ARTICLE", readerAccess, Article.HandleArticleAsync));
        registry.Register(new NntpCommandDescriptor("HEAD", readerAccess, Article.HandleHeadAsync));
        registry.Register(new NntpCommandDescriptor("BODY", readerAccess, Article.HandleBodyAsync));
        registry.Register(new NntpCommandDescriptor("STAT", readerAccess, Article.HandleStatAsync));

        // Navigation
        registry.Register(new NntpCommandDescriptor("LAST", readerAccess, Last.HandleAsync));
        registry.Register(new NntpCommandDescriptor("NEXT", readerAccess, Next.HandleAsync));

        // Overview / headers
        registry.Register(new NntpCommandDescriptor("OVER", readerAccess, Over.HandleAsync));
        registry.Register(new NntpCommandDescriptor("HDR", readerAccess, Hdr.HandleAsync));

        // Posting
        registry.Register(new NntpCommandDescriptor(
            "POST",
            NntpCommandAccess.RequiresAuthentication | NntpCommandAccess.RequiresReader | NntpCommandAccess.RequiresPosting,
            Post.HandleAsync));

        // Streaming / transit
        var transitAccess = NntpCommandAccess.RequiresAuthentication | NntpCommandAccess.RequiresTransit;
        registry.Register(new NntpCommandDescriptor("IHAVE", transitAccess, IHave.HandleAsync));
        registry.Register(new NntpCommandDescriptor("CHECK", transitAccess, Check.HandleAsync));
        registry.Register(new NntpCommandDescriptor("TAKETHIS", transitAccess, TakeThis.HandleAsync));

        _ = loggerFactory;
        return registry;
    }
}
