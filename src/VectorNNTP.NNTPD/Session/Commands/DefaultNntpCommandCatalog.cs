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
        "COMPRESS",
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
        NntpCommandLoggers.Configure(loggerFactory);
        var registry = new NntpCommandRegistry();

        var capabilitiesLog = NntpCommandLoggers.For(typeof(Capabilities));
        var modeLog = NntpCommandLoggers.For(typeof(Mode));
        var helpLog = NntpCommandLoggers.For(typeof(Help));
        var dateLog = NntpCommandLoggers.For(typeof(Date));
        var quitLog = NntpCommandLoggers.For(typeof(Quit));
        var startTlsLog = NntpCommandLoggers.For(typeof(StartTls));
        var compressLog = NntpCommandLoggers.For(typeof(Compress));
        var authInfoLog = NntpCommandLoggers.For(typeof(AuthInfo));
        var listLog = NntpCommandLoggers.For(typeof(List));
        var groupLog = NntpCommandLoggers.For(typeof(Group));
        var listGroupLog = NntpCommandLoggers.For(typeof(ListGroup));
        var newGroupsLog = NntpCommandLoggers.For(typeof(NewGroups));
        var newNewsLog = NntpCommandLoggers.For(typeof(NewNews));
        var articleLog = NntpCommandLoggers.For(typeof(Article));
        var lastLog = NntpCommandLoggers.For(typeof(Last));
        var nextLog = NntpCommandLoggers.For(typeof(Next));
        var overLog = NntpCommandLoggers.For(typeof(Over));
        var hdrLog = NntpCommandLoggers.For(typeof(Hdr));
        var postLog = NntpCommandLoggers.For(typeof(Post));
        var iHaveLog = NntpCommandLoggers.For(typeof(IHave));
        var checkLog = NntpCommandLoggers.For(typeof(Check));
        var takeThisLog = NntpCommandLoggers.For(typeof(TakeThis));

        // Core / session
        registry.Register(new NntpCommandDescriptor(
            "CAPABILITIES", NntpCommandAccess.Public, Capabilities.HandleAsync, logger: capabilitiesLog));
        registry.Register(new NntpCommandDescriptor(
            "MODE", NntpCommandAccess.Public, Mode.HandleReaderAsync, "READER", modeLog));
        registry.Register(new NntpCommandDescriptor(
            "MODE",
            NntpCommandAccess.RequiresAuthentication | NntpCommandAccess.RequiresTransit | NntpCommandAccess.RequiresStreaming,
            Mode.HandleStreamAsync,
            "STREAM",
            modeLog));
        registry.Register(new NntpCommandDescriptor("HELP", NntpCommandAccess.Public, Help.HandleAsync, logger: helpLog));
        registry.Register(new NntpCommandDescriptor("DATE", NntpCommandAccess.Public, Date.HandleAsync, logger: dateLog));
        registry.Register(new NntpCommandDescriptor("QUIT", NntpCommandAccess.Public, Quit.HandleAsync, logger: quitLog));
        registry.Register(new NntpCommandDescriptor(
            "STARTTLS",
            NntpCommandAccess.Public,
            (ctx, ct) => StartTls.HandleAsync(ctx, certificateProvider, ct),
            logger: startTlsLog));
        // Verb-only so algorithm case/syntax is validated in Compress (RFC 8054 §5.3 case-sensitive).
        registry.Register(new NntpCommandDescriptor(
            "COMPRESS",
            NntpCommandAccess.Public,
            Compress.HandleAsync,
            logger: compressLog));

        // Authentication
        registry.Register(new NntpCommandDescriptor(
            "AUTHINFO",
            NntpCommandAccess.Public,
            (ctx, ct) => AuthInfo.HandleUserAsync(ctx, authenticationProvider, ct),
            "USER",
            authInfoLog));
        registry.Register(new NntpCommandDescriptor(
            "AUTHINFO",
            NntpCommandAccess.Public,
            (ctx, ct) => AuthInfo.HandlePassAsync(ctx, authenticationProvider, ct),
            "PASS",
            authInfoLog));
        registry.Register(new NntpCommandDescriptor(
            "AUTHINFO",
            NntpCommandAccess.Public,
            AuthInfo.HandleSaslAsync,
            "SASL",
            authInfoLog));

        // Reader / group
        var readerAccess = NntpCommandAccess.RequiresAuthentication | NntpCommandAccess.RequiresReader;
        registry.Register(new NntpCommandDescriptor("LIST", readerAccess, List.HandleAsync, logger: listLog));
        registry.Register(new NntpCommandDescriptor("GROUP", readerAccess, Group.HandleAsync, logger: groupLog));
        registry.Register(new NntpCommandDescriptor("LISTGROUP", readerAccess, ListGroup.HandleAsync, logger: listGroupLog));
        registry.Register(new NntpCommandDescriptor("NEWGROUPS", readerAccess, NewGroups.HandleAsync, logger: newGroupsLog));
        registry.Register(new NntpCommandDescriptor("NEWNEWS", readerAccess, NewNews.HandleAsync, logger: newNewsLog));

        // Article retrieval
        registry.Register(new NntpCommandDescriptor("ARTICLE", readerAccess, Article.HandleArticleAsync, logger: articleLog));
        registry.Register(new NntpCommandDescriptor("HEAD", readerAccess, Article.HandleHeadAsync, logger: articleLog));
        registry.Register(new NntpCommandDescriptor("BODY", readerAccess, Article.HandleBodyAsync, logger: articleLog));
        registry.Register(new NntpCommandDescriptor("STAT", readerAccess, Article.HandleStatAsync, logger: articleLog));

        // Navigation
        registry.Register(new NntpCommandDescriptor("LAST", readerAccess, Last.HandleAsync, logger: lastLog));
        registry.Register(new NntpCommandDescriptor("NEXT", readerAccess, Next.HandleAsync, logger: nextLog));

        // Overview / headers
        registry.Register(new NntpCommandDescriptor("OVER", readerAccess, Over.HandleAsync, logger: overLog));
        registry.Register(new NntpCommandDescriptor("HDR", readerAccess, Hdr.HandleAsync, logger: hdrLog));

        // Posting
        registry.Register(new NntpCommandDescriptor(
            "POST",
            NntpCommandAccess.RequiresAuthentication | NntpCommandAccess.RequiresReader | NntpCommandAccess.RequiresPosting,
            Post.HandleAsync,
            logger: postLog));

        // Streaming / transit
        var transitAccess = NntpCommandAccess.RequiresAuthentication | NntpCommandAccess.RequiresTransit;
        registry.Register(new NntpCommandDescriptor("IHAVE", transitAccess, IHave.HandleAsync, logger: iHaveLog));
        registry.Register(new NntpCommandDescriptor("CHECK", transitAccess, Check.HandleAsync, logger: checkLog));
        registry.Register(new NntpCommandDescriptor("TAKETHIS", transitAccess, TakeThis.HandleAsync, logger: takeThisLog));

        return registry;
    }
}
