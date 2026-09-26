using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using VectorNNTP.NNTPD.Acme;
using VectorNNTP.NNTPD.Cloudflare;
using VectorNNTP.NNTPD.Configuration;

namespace VectorNNTP.Common.Tests.TestDoubles;

internal sealed class TempStateDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(
        System.IO.Path.GetTempPath(),
        "vectornntp-common-" + Guid.NewGuid().ToString("N"));

    public TempStateDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

internal sealed class FakeLocalAssignee : ILocalIpAddressAssignee
{
    private readonly bool _assignAll;
    private readonly IPAddress[] _assigned;

    public FakeLocalAssignee(bool assignAll)
    {
        _assignAll = assignAll;
        _assigned = assignAll
            ? [IPAddress.Parse("198.18.0.10"), IPAddress.Parse("2001:db8::10")]
            : [];
    }

    public FakeLocalAssignee(params IPAddress[] assigned)
    {
        _assignAll = false;
        _assigned = assigned;
    }

    public bool IsLocallyAssigned(IPAddress address)
    {
        ArgumentNullException.ThrowIfNull(address);
        return _assignAll || _assigned.Contains(address);
    }

    public IReadOnlyList<IPAddress> GetAssignedUnicastAddresses() => _assigned;
}

internal static class TestCertificateFactory
{
    public const string Password = "unit-test-pfx-password-not-secret";

    public static CertificateMaterial CreateMaterial(
        IReadOnlyList<string> dnsNames,
        DateTimeOffset notAfter,
        DateTimeOffset? notBefore = null)
    {
        var before = notBefore ?? DateTimeOffset.UtcNow.AddDays(-1);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=" + dnsNames[0],
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        var sanBuilder = new SubjectAlternativeNameBuilder();
        foreach (var name in dnsNames)
        {
            sanBuilder.AddDnsName(name);
        }

        request.CertificateExtensions.Add(sanBuilder.Build());
        using var cert = request.CreateSelfSigned(before.UtcDateTime, notAfter.UtcDateTime);
        var pfx = PfxCrypto.ExportPfx(cert, intermediateCertificates: [], Password);
        return new CertificateMaterial(
            pfx,
            dnsNames.Select(static n => n.ToLowerInvariant()).OrderBy(static n => n).ToArray(),
            before,
            notAfter);
    }
}

internal sealed class InMemoryCloudflareDnsClient : ICloudflareDnsClient
{
    private readonly object _sync = new();
    private readonly List<CloudflareDnsRecord> _records = [];
    private int _nextId = 1;

    public int CreateCallCount { get; private set; }
    public int UpdateCallCount { get; private set; }
    public int DeleteCallCount { get; private set; }
    public Func<CancellationToken, Task>? OnMutate { get; set; }
    public Exception? CreateException { get; set; }

    public IReadOnlyList<CloudflareDnsRecord> Snapshot()
    {
        lock (_sync)
        {
            return _records.Select(Clone).ToArray();
        }
    }

    public void Seed(params CloudflareDnsRecord[] records)
    {
        lock (_sync)
        {
            _records.Clear();
            foreach (var record in records)
            {
                _records.Add(Clone(record));
            }
        }
    }

    public Task<IReadOnlyList<CloudflareDnsRecord>> ListRecordsAsync(
        string zoneId,
        string fqdn,
        string type,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var normalized = fqdn.Trim().TrimEnd('.').ToLowerInvariant();
            return Task.FromResult<IReadOnlyList<CloudflareDnsRecord>>(
                _records
                    .Where(r =>
                        string.Equals(r.Type, type, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(r.Name.Trim().TrimEnd('.'), normalized, StringComparison.OrdinalIgnoreCase))
                    .Select(Clone)
                    .ToArray());
        }
    }

    public Task<IReadOnlyList<CloudflareDnsRecord>> ListAllRecordsForNameAsync(
        string zoneId,
        string fqdn,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var normalized = fqdn.Trim().TrimEnd('.').ToLowerInvariant();
            return Task.FromResult<IReadOnlyList<CloudflareDnsRecord>>(
                _records
                    .Where(r => string.Equals(r.Name.Trim().TrimEnd('.'), normalized, StringComparison.OrdinalIgnoreCase))
                    .Select(Clone)
                    .ToArray());
        }
    }

    public async Task<CloudflareDnsRecord> CreateRecordAsync(
        string zoneId,
        CloudflareDnsRecordWriteRequest request,
        CancellationToken cancellationToken)
    {
        CreateCallCount++;
        cancellationToken.ThrowIfCancellationRequested();
        if (OnMutate is not null)
        {
            await OnMutate(cancellationToken).ConfigureAwait(false);
        }

        if (CreateException is not null)
        {
            throw CreateException;
        }

        lock (_sync)
        {
            var record = new CloudflareDnsRecord
            {
                Id = $"rec-{_nextId++}",
                Type = request.Type,
                Name = request.Name,
                Content = request.Content,
                Ttl = request.Ttl,
                Proxied = request.Proxied,
            };
            _records.Add(record);
            return Clone(record);
        }
    }

    public Task<CloudflareDnsRecord> UpdateRecordAsync(
        string zoneId,
        string recordId,
        CloudflareDnsRecordWriteRequest request,
        CancellationToken cancellationToken)
    {
        UpdateCallCount++;
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            var existing = _records.First(r => r.Id == recordId);
            existing.Type = request.Type;
            existing.Name = request.Name;
            existing.Content = request.Content;
            existing.Ttl = request.Ttl;
            existing.Proxied = request.Proxied;
            return Task.FromResult(Clone(existing));
        }
    }

    public Task DeleteRecordAsync(string zoneId, string recordId, CancellationToken cancellationToken)
    {
        DeleteCallCount++;
        cancellationToken.ThrowIfCancellationRequested();
        lock (_sync)
        {
            _records.RemoveAll(r => r.Id == recordId);
        }

        return Task.CompletedTask;
    }

    private static CloudflareDnsRecord Clone(CloudflareDnsRecord record) =>
        new()
        {
            Id = record.Id,
            Type = record.Type,
            Name = record.Name,
            Content = record.Content,
            Ttl = record.Ttl,
            Proxied = record.Proxied,
        };
}

internal sealed class ImmediateTxtResolver : IAuthoritativeTxtResolver
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);

    public void Set(string name, string value) => _values[name] = value;

    public Task<IReadOnlyList<string>> LookupTxtAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<string>>(
            _values.TryGetValue(name, out var value) ? [value] : []);
    }
}
