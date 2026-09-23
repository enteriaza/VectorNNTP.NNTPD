namespace VectorNNTP.NNTPD.Acme;

/// <summary>One DNS-01 challenge to publish as a TXT record.</summary>
/// <param name="Domain">Authorization DNS name.</param>
/// <param name="Validation">TXT RDATA (key authorization digest).</param>
public sealed record Dns01ChallengeSpec(string Domain, string Validation)
{
    /// <summary>Gets the <c>_acme-challenge.</c> record name.</summary>
    public string RecordName =>
        "_acme-challenge." + Domain.Trim().TrimEnd('.').ToLowerInvariant();
}

/// <summary>Looks up TXT RDATA for authoritative visibility checks (injectable).</summary>
public interface IAuthoritativeTxtResolver
{
    /// <summary>Returns TXT strings for <paramref name="name"/> (maybe empty).</summary>
    Task<IReadOnlyList<string>> LookupTxtAsync(string name, CancellationToken cancellationToken);
}

/// <summary>
/// Creates, awaits, and cleans up ACME DNS-01 TXT records via Cloudflare with a durable journal.
/// </summary>
public sealed class Dns01Solver
{
    /// <summary>TTL used for challenge TXT records (matches pyNNTPD).</summary>
    public const int ChallengeTtlSeconds = 120;

    private readonly Cloudflare.ICloudflareDnsClient _client;
    private readonly string _zoneId;
    private readonly IAuthoritativeTxtResolver _resolver;
    private readonly string? _journalDir;
    private readonly TimeSpan _propagationTimeout;
    private readonly TimeSpan _propagationInterval;
    private readonly List<PlacedChallenge> _placed = [];
    private bool _recovered;

    /// <summary>Initializes a new instance of the <see cref="Dns01Solver"/> class.</summary>
    public Dns01Solver(
        Cloudflare.ICloudflareDnsClient client,
        string zoneId,
        IAuthoritativeTxtResolver resolver,
        string? stateDir = null,
        TimeSpan? propagationTimeout = null,
        TimeSpan? propagationInterval = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(zoneId);
        ArgumentNullException.ThrowIfNull(resolver);

        _client = client;
        _zoneId = zoneId;
        _resolver = resolver;
        _propagationTimeout = propagationTimeout ?? TimeSpan.FromSeconds(120);
        _propagationInterval = propagationInterval ?? TimeSpan.FromSeconds(2);

        if (stateDir is not null)
        {
            AcmePaths.EnsureStateLayout(stateDir);
            _journalDir = AcmePaths.Dns01JournalDir(stateDir);
            Directory.CreateDirectory(_journalDir);
        }
    }

    /// <summary>Idempotent startup recovery of journalled TXT records.</summary>
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        if (_journalDir is null)
        {
            _recovered = true;
            return;
        }

        var entries = LoadJournalEntries(_journalDir);
        var failures = 0;
        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await RecoverEntryAsync(entry, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AcmeChallengeException)
            {
                throw;
            }
            catch
            {
                failures++;
            }
        }

        if (failures > 0)
        {
            throw new AcmeChallengeException("txt_recovery_failed", $"failed_entries={failures}");
        }

        _recovered = true;
    }

    /// <summary>Creates TXT records for each challenge and journals ownership.</summary>
    public async Task PlaceAsync(
        IReadOnlyList<Dns01ChallengeSpec> challenges,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(challenges);
        if (!_recovered)
        {
            await RecoverAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            foreach (var spec in challenges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = await PlaceOneAsync(spec, cancellationToken).ConfigureAwait(false);
                _placed.Add(item);
            }
        }
        catch (OperationCanceledException)
        {
            await BestEffortCleanupAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (AcmeChallengeException)
        {
            await BestEffortCleanupAsync(cancellationToken).ConfigureAwait(false);
            throw;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await BestEffortCleanupAsync(cancellationToken).ConfigureAwait(false);
            throw new AcmeChallengeException("txt_create_failed", ex.GetType().Name);
        }
    }

    /// <summary>Waits until authoritative resolvers report each challenge TXT value.</summary>
    public async Task WaitPropagatedAsync(
        IReadOnlyList<Dns01ChallengeSpec> challenges,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(challenges);
        var deadline = DateTimeOffset.UtcNow + _propagationTimeout;
        var pending = challenges.ToDictionary(
            static c => c.RecordName,
            static c => c.Validation,
            StringComparer.OrdinalIgnoreCase);

        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new AcmeChallengeException("propagation_timeout", $"remaining={pending.Count}");
            }

            var resolved = new List<string>();
            foreach (var (name, expected) in pending)
            {
                IReadOnlyList<string> values;
                try
                {
                    values = await _resolver.LookupTxtAsync(name, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (AcmeChallengeException)
                {
                    throw;
                }
                catch
                {
                    continue;
                }

                var normalized = values.Select(NormalizeTxt).ToHashSet(StringComparer.Ordinal);
                if (normalized.Contains(expected))
                {
                    resolved.Add(name);
                }
            }

            foreach (var name in resolved)
            {
                pending.Remove(name);
            }

            if (pending.Count > 0)
            {
                await Task.Delay(_propagationInterval, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Deletes only TXT records created by this solver (by record id).</summary>
    public async Task CleanupAsync(CancellationToken cancellationToken)
    {
        var remaining = new List<PlacedChallenge>();
        foreach (var item in _placed.ToArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DeleteOwnedRecordAsync(item.ZoneId, item.RecordId, cancellationToken)
                    .ConfigureAwait(false);
                if (!string.IsNullOrEmpty(item.EntryId) && _journalDir is not null)
                {
                    RemoveJournalFile(_journalDir, item.EntryId);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                remaining.Add(item);
            }
        }

        _placed.Clear();
        _placed.AddRange(remaining);
        if (remaining.Count > 0)
        {
            throw new AcmeChallengeException("txt_cleanup_failed", $"remaining={remaining.Count}");
        }
    }

    private async Task<PlacedChallenge> PlaceOneAsync(
        Dns01ChallengeSpec spec,
        CancellationToken cancellationToken)
    {
        var entryId = Guid.NewGuid().ToString("N");
        var name = spec.RecordName;
        var content = spec.Validation;

        if (_journalDir is not null)
        {
            WriteJournalEntry(
                _journalDir,
                new JournalEntry(entryId, "creating", _zoneId, name, content, RecordId: null));
        }

        var record = await _client.CreateRecordAsync(
                _zoneId,
                new Cloudflare.CloudflareDnsRecordWriteRequest
                {
                    Type = Cloudflare.CloudflareDnsRecordTypes.TXT,
                    Name = name,
                    Content = content,
                    Ttl = ChallengeTtlSeconds,
                    Proxied = false,
                },
                cancellationToken)
            .ConfigureAwait(false);

        if (_journalDir is not null)
        {
            WriteJournalEntry(
                _journalDir,
                new JournalEntry(entryId, "placed", _zoneId, name, content, record.Id));
        }

        return new PlacedChallenge(spec, record.Id, _zoneId, entryId);
    }

    private async Task RecoverEntryAsync(JournalEntry entry, CancellationToken cancellationToken)
    {
        if (_journalDir is null)
        {
            return;
        }

        if (entry.Phase == "placed")
        {
            if (string.IsNullOrEmpty(entry.RecordId))
            {
                throw new AcmeChallengeException(
                    "malformed_journal",
                    $"placed without record_id entry={entry.EntryId}");
            }

            await DeleteOwnedRecordAsync(entry.ZoneId, entry.RecordId, cancellationToken)
                .ConfigureAwait(false);
            RemoveJournalFile(_journalDir, entry.EntryId);
            return;
        }

        var matches = await FindRecordsByContentAsync(
                entry.ZoneId,
                entry.Name,
                entry.Content,
                cancellationToken)
            .ConfigureAwait(false);
        foreach (var recordId in matches)
        {
            await DeleteOwnedRecordAsync(entry.ZoneId, recordId, cancellationToken).ConfigureAwait(false);
        }

        RemoveJournalFile(_journalDir, entry.EntryId);
    }

    private async Task<IReadOnlyList<string>> FindRecordsByContentAsync(
        string zoneId,
        string name,
        string content,
        CancellationToken cancellationToken)
    {
        var records = await _client.ListAllRecordsForNameAsync(zoneId, name, cancellationToken)
            .ConfigureAwait(false);
        return records
            .Where(r =>
                string.Equals(r.Type, Cloudflare.CloudflareDnsRecordTypes.TXT, StringComparison.OrdinalIgnoreCase)
                && string.Equals(r.Content, content, StringComparison.Ordinal))
            .Select(r => r.Id)
            .ToArray();
    }

    private async Task DeleteOwnedRecordAsync(
        string zoneId,
        string recordId,
        CancellationToken cancellationToken)
    {
        try
        {
            await _client.DeleteRecordAsync(zoneId, recordId, cancellationToken).ConfigureAwait(false);
        }
        catch (Cloudflare.CloudflareDnsException ex) when (ex.StatusCode == 404 || IsNotFound(ex))
        {
            // Idempotent success.
        }
    }

    private static bool IsNotFound(Cloudflare.CloudflareDnsException ex) =>
        ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase)
        || ex.CloudflareErrorCodes.Contains(81044);

    private async Task BestEffortCleanupAsync(CancellationToken cancellationToken)
    {
        try
        {
            await CleanupAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Logged by callers via category.
        }
    }

    private static string NormalizeTxt(string value)
    {
        var cleaned = value.Trim();
        if (cleaned is ['"', _, ..] && cleaned[^1] == '"')
        {
            cleaned = cleaned[1..^1];
        }

        return cleaned;
    }

    private sealed record PlacedChallenge(
        Dns01ChallengeSpec Spec,
        string RecordId,
        string ZoneId,
        string EntryId);

    private sealed record JournalEntry(
        string EntryId,
        string Phase,
        string ZoneId,
        string Name,
        string Content,
        string? RecordId);

    private static void WriteJournalEntry(string journalDir, JournalEntry entry)
    {
        var payload = new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["entry_id"] = entry.EntryId,
            ["phase"] = entry.Phase,
            ["zone_id"] = entry.ZoneId,
            ["name"] = entry.Name,
            ["content"] = entry.Content,
            ["record_id"] = entry.RecordId,
        };
        var path = Path.Combine(journalDir, entry.EntryId + ".json");
        AtomicFile.WriteText(
            path,
            System.Text.Json.JsonSerializer.Serialize(
                payload,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
            + Environment.NewLine);
    }

    private static void RemoveJournalFile(string journalDir, string entryId) =>
        AtomicFile.TryDelete(Path.Combine(journalDir, entryId + ".json"));

    private static List<JournalEntry> LoadJournalEntries(string journalDir)
    {
        if (!Directory.Exists(journalDir))
        {
            return [];
        }

        var entries = new List<JournalEntry>();
        foreach (var path in Directory.EnumerateFiles(journalDir, "*.json").OrderBy(static p => p, StringComparer.Ordinal))
        {
            entries.Add(ParseJournalFile(path));
        }

        return entries;
    }

    private static JournalEntry ParseJournalFile(string path)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            var version = root.GetProperty("version").GetInt32();
            if (version != 1)
            {
                throw new AcmeChallengeException("malformed_journal", $"unsupported_version={version}");
            }

            var entryId = root.GetProperty("entry_id").GetString() ?? string.Empty;
            var phase = root.GetProperty("phase").GetString() ?? string.Empty;
            var zoneId = root.GetProperty("zone_id").GetString() ?? string.Empty;
            var name = root.GetProperty("name").GetString() ?? string.Empty;
            var content = root.GetProperty("content").GetString() ?? string.Empty;
            string? recordId = null;
            if (root.TryGetProperty("record_id", out var recordIdElement)
                && recordIdElement.ValueKind != System.Text.Json.JsonValueKind.Null)
            {
                recordId = recordIdElement.GetString();
            }

            if (Path.GetFileNameWithoutExtension(path) != entryId)
            {
                throw new AcmeChallengeException("malformed_journal", "entry_id mismatch");
            }

            if (phase is not ("creating" or "placed"))
            {
                throw new AcmeChallengeException("malformed_journal", $"bad_phase={phase}");
            }

            if (phase == "placed" && string.IsNullOrEmpty(recordId))
            {
                throw new AcmeChallengeException("malformed_journal", "placed without record_id");
            }

            return new JournalEntry(entryId, phase, zoneId, name, content, recordId);
        }
        catch (AcmeChallengeException)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or KeyNotFoundException)
        {
            throw new AcmeChallengeException("malformed_journal", ex.GetType().Name);
        }
    }
}
