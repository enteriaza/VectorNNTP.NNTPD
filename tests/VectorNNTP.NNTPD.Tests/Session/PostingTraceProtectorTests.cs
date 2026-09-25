using System.Net;
using System.Reflection;
using System.Text;
using Microsoft.Extensions.Logging;
using VectorNNTP.NNTPD.Configuration;
using VectorNNTP.NNTPD.Session.Commands.Posting;
using VectorNNTP.NNTPD.Tests.Fixtures;

namespace VectorNNTP.NNTPD.Tests.Session;

public sealed class PostingTraceProtectorTests
{
    private static readonly byte[] CurrentKey = Convert.FromHexString(TestHostFactory.TestXTraceKey);
    private static readonly byte[] PreviousKey = Convert.FromHexString(
        "ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff");

    [Fact]
    public void Protect_RoundTripsTransportIdentity()
    {
        var protector = new AesGcmPostingTraceProtector(CurrentKey);
        var payload = new PostingTracePayload(
            IPAddress.Parse("198.18.0.123"),
            1199,
            new DateTimeOffset(2026, 9, 25, 12, 0, 0, TimeSpan.Zero),
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"));

        var token = protector.Protect(payload);
        Assert.StartsWith(AesGcmPostingTraceProtector.TokenPrefix, token, StringComparison.Ordinal);
        Assert.DoesNotContain("198.18.0.123", token, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(payload.Address.GetAddressBytes()), token, StringComparison.Ordinal);
        Assert.DoesNotContain(Convert.ToBase64String(Encoding.ASCII.GetBytes("198.18.0.123")), token, StringComparison.Ordinal);
        Assert.True(protector.TryUnprotect(token, out var recovered));
        Assert.Equal(payload.Address, recovered.Address);
        Assert.Equal(payload.Port, recovered.Port);
        Assert.Equal(payload.InjectedAtUtc, recovered.InjectedAtUtc);
        Assert.Equal(payload.TraceId, recovered.TraceId);
    }

    [Fact]
    public void Protect_DoesNotReuseToken()
    {
        var protector = new AesGcmPostingTraceProtector(CurrentKey);
        var payload = new PostingTracePayload(IPAddress.Loopback, 119, DateTimeOffset.UnixEpoch, Guid.Empty);
        Assert.NotEqual(protector.Protect(payload), protector.Protect(payload));
    }

    [Fact]
    public void TamperedToken_IsRejected()
    {
        var protector = new AesGcmPostingTraceProtector(CurrentKey);
        var token = protector.Protect(new PostingTracePayload(IPAddress.Loopback, 119, DateTimeOffset.UnixEpoch, Guid.NewGuid()));
        var chars = token.ToCharArray();
        chars[^1] = chars[^1] == 'A' ? 'B' : 'A';
        Assert.False(protector.TryUnprotect(new string(chars), out _));
    }

    [Fact]
    public void PreviousKey_CanDecryptRotatedToken()
    {
        var oldProtector = new AesGcmPostingTraceProtector(PreviousKey);
        var token = oldProtector.Protect(new PostingTracePayload(IPAddress.Parse("192.0.2.55"), 563, DateTimeOffset.UnixEpoch, Guid.NewGuid()));
        var rotated = new AesGcmPostingTraceProtector(CurrentKey, PreviousKey);
        Assert.True(rotated.TryUnprotect(token, out var recovered));
        Assert.Equal(IPAddress.Parse("192.0.2.55"), recovered.Address);
        Assert.False(new AesGcmPostingTraceProtector(CurrentKey).TryUnprotect(token, out _));
    }

    [Fact]
    public void Protector_DoesNotAcceptLogger()
    {
        var ctors = typeof(AesGcmPostingTraceProtector).GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        Assert.DoesNotContain(ctors, static ctor => ctor.GetParameters().Any(static p =>
            typeof(ILogger).IsAssignableFrom(p.ParameterType)));
    }
}
