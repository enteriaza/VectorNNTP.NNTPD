namespace VectorNNTP.NNTPD.NntpDb;

/// <summary>Authoritative NntpDB query for reader account authentication.</summary>
public static class NntpUserQueries
{
    /// <summary>
    /// Looks up one <c>nntpusers</c> row. <c>account_name</c> is stored as MD5 hex of the
    /// plaintext wire username. <c>account_pass</c> is decrypted in SQL with
    /// <c>AES_DECRYPT(..., UNHEX(SHA2(account_name, 256)))</c> using the stored hash column.
    /// </summary>
    public const string SelectUserByName =
        "SELECT "
        + "CAST(AES_DECRYPT(account_pass, UNHEX(SHA2(account_name, 256))) AS CHAR) AS account_pass, "
        + "scram_salt, scram_iterations, scram_stored_key, scram_server_key, "
        + "allow_auth_plain, allow_auth_scram256, "
        + "account_type, account_rate_limit, account_byte_limit, account_session_limit, account_srcip_limit, "
        + "is_enabled, customer_id "
        + "FROM nntpusers "
        + "WHERE account_name = MD5(@account_name)";
}
