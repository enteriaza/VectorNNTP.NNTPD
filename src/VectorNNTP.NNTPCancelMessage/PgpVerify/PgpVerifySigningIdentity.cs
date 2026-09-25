namespace VectorNNTP.NNTPCancelMessage.PgpVerify;

/// <summary>Public identity of the OpenPGP key used for PGPVERIFY.</summary>
internal sealed class PgpVerifySigningIdentity
{
    public required string Fingerprint { get; init; }

    public string? UserId { get; init; }

    public required string KeyIdHex { get; init; }
}
