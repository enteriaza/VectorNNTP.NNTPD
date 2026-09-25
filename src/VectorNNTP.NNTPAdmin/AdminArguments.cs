namespace VectorNNTP.NNTPAdmin;

/// <summary>Parsed command line for the newsmaster utility.</summary>
internal sealed class AdminArguments
{
    public required string MessageId { get; init; }

    public required string RawMessageId { get; init; }

    public bool Cancel { get; init; }

    public string? Host { get; init; }

    public int? Port { get; init; }

    public bool? UseTls { get; init; }

    public string? Username { get; init; }

    public string? Password { get; init; }

    public bool Help { get; init; }
}
