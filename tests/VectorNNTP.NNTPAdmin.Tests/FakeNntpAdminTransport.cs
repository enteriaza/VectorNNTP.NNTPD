using VectorNNTP.NNTPAdmin;

namespace VectorNNTP.NNTPAdmin.Tests;

internal sealed class FakeNntpAdminTransport : INntpAdminTransport
{
    public bool Connected { get; private set; }

    public bool Authenticated { get; private set; }

    public string? LastAuthenticatedUsername { get; private set; }

    public Exception? AuthenticateException { get; init; }

    public string? LastHeadMessageId { get; private set; }

    public string? LastPostedArticle { get; private set; }

    public int HeadCalls { get; private set; }

    public int PostCalls { get; private set; }

    public Exception? ConnectException { get; init; }

    public Exception? HeadException { get; init; }

    public HeadExchange HeadResponse { get; init; } = new()
    {
        Code = 221,
        StatusLine = "221 0 <abc@example.com>",
        Headers = [],
    };

    public PostExchange PostResponse { get; init; } = new()
    {
        Code = 240,
        StatusLine = "240 Article received OK",
    };

    public Task ConnectAsync(AdminSettings settings, CancellationToken cancellationToken)
    {
        if (ConnectException is not null)
        {
            throw ConnectException;
        }

        Connected = true;
        return Task.CompletedTask;
    }

    public Task AuthenticateAsync(string username, string password, CancellationToken cancellationToken)
    {
        if (AuthenticateException is not null)
        {
            throw AuthenticateException;
        }

        LastAuthenticatedUsername = username;
        Authenticated = true;
        _ = password;
        return Task.CompletedTask;
    }

    public Task<HeadExchange> HeadAsync(string messageId, CancellationToken cancellationToken)
    {
        HeadCalls++;
        LastHeadMessageId = messageId;
        if (HeadException is not null)
        {
            throw HeadException;
        }

        return Task.FromResult(HeadResponse);
    }

    public Task<PostExchange> PostAsync(string article, CancellationToken cancellationToken)
    {
        PostCalls++;
        LastPostedArticle = article;
        return Task.FromResult(PostResponse);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
