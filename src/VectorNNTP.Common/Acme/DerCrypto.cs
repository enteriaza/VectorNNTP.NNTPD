namespace VectorNNTP.NNTPD.Acme;

/// <summary>
/// Helpers for ACME account private-key DER (PKCS#8). Not used for the TLS server credential.
/// </summary>
public static class DerCrypto
{
    private static readonly byte[] PemBeginMarker = "-----BEGIN"u8.ToArray();

    /// <summary>Returns whether <paramref name="data"/> looks like PEM text rather than DER.</summary>
    public static bool LooksLikePem(ReadOnlySpan<byte> data)
    {
        if (data.Length < PemBeginMarker.Length)
        {
            return false;
        }

        var offset = 0;
        while (offset < data.Length && (data[offset] is (byte)'\r' or (byte)'\n' or (byte)' ' or (byte)'\t'))
        {
            offset++;
        }

        if (offset + 3 <= data.Length && data[offset] == 0xEF && data[offset + 1] == 0xBB && data[offset + 2] == 0xBF)
        {
            offset += 3;
        }

        return data[offset..].StartsWith(PemBeginMarker);
    }
}
