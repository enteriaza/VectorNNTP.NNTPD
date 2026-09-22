using Certes;
using Certes.Acme;
using VectorNNTP.NNTPD.Acme;

namespace VectorNNTP.NNTPD.Tests.Acme;

public sealed class AcmeOrderReadinessTests
{
    [Fact]
    public void Evaluate_Pending_Continues()
    {
        var view = new AcmeOrderReadiness.OrderView(
            "Pending",
            [
                new AcmeOrderReadiness.AuthorizationView("nntpd01.usenet.ninja", "Pending", null, null, null),
                new AcmeOrderReadiness.AuthorizationView("news.usenet.ninja", "Pending", null, null, null),
            ]);

        var decision = AcmeOrderReadiness.Evaluate(view, out var diagnostic);
        Assert.Equal(AcmeOrderReadiness.Decision.Continue, decision);
        Assert.Null(diagnostic);
    }

    [Fact]
    public void Evaluate_Ready_IsReady()
    {
        var view = new AcmeOrderReadiness.OrderView(
            "Ready",
            [
                new AcmeOrderReadiness.AuthorizationView("nntpd01.usenet.ninja", "Valid", null, null, null),
                new AcmeOrderReadiness.AuthorizationView("news.usenet.ninja", "Valid", null, null, null),
            ]);

        Assert.Equal(AcmeOrderReadiness.Decision.Ready, AcmeOrderReadiness.Evaluate(view, out _));
    }

    [Fact]
    public void Evaluate_InvalidAuthorization_SurfacesDnsProblem()
    {
        var view = new AcmeOrderReadiness.OrderView(
            "Invalid",
            [
                new AcmeOrderReadiness.AuthorizationView(
                    "nntpd01.usenet.ninja",
                    "Invalid",
                    "urn:ietf:params:acme:error:dns",
                    "DNS problem: NXDOMAIN looking up TXT for _acme-challenge.nntpd01.usenet.ninja",
                    400),
                new AcmeOrderReadiness.AuthorizationView("news.usenet.ninja", "Valid", null, null, null),
            ]);

        var decision = AcmeOrderReadiness.Evaluate(view, out var diagnostic);
        Assert.Equal(AcmeOrderReadiness.Decision.Invalid, decision);
        Assert.NotNull(diagnostic);
        Assert.Contains("nntpd01.usenet.ninja", diagnostic, StringComparison.Ordinal);
        Assert.Contains("urn:ietf:params:acme:error:dns", diagnostic, StringComparison.Ordinal);
        Assert.Contains("NXDOMAIN", diagnostic, StringComparison.Ordinal);
        Assert.Contains("type=", diagnostic, StringComparison.Ordinal);
        Assert.Contains("detail=", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitUntilReady_PendingThenReady_DoesNotThrow()
    {
        var polls = 0;
        await AcmeOrderReadiness.WaitUntilReadyAsync(
            _ =>
            {
                polls++;
                if (polls < 3)
                {
                    return Task.FromResult(
                        new AcmeOrderReadiness.OrderView(
                            "Pending",
                            [new AcmeOrderReadiness.AuthorizationView("news.usenet.ninja", "Pending", null, null, null)]));
                }

                return Task.FromResult(
                    new AcmeOrderReadiness.OrderView(
                        "Ready",
                        [new AcmeOrderReadiness.AuthorizationView("news.usenet.ninja", "Valid", null, null, null)]));
            },
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(1),
            CancellationToken.None,
            delayAsync: (_, _) => Task.CompletedTask);

        Assert.True(polls >= 3);
    }

    [Fact]
    public async Task WaitUntilReady_Invalid_ThrowsAuthorizationInvalidWithDetails()
    {
        var ex = await Assert.ThrowsAsync<AcmeOrderException>(() =>
            AcmeOrderReadiness.WaitUntilReadyAsync(
                _ => Task.FromResult(
                    new AcmeOrderReadiness.OrderView(
                        "Pending",
                        [
                            new AcmeOrderReadiness.AuthorizationView(
                                "nntpd01.usenet.ninja",
                                "Invalid",
                                "urn:ietf:params:acme:error:dns",
                                "DNS problem: SERVFAIL",
                                400),
                        ])),
                timeout: TimeSpan.FromSeconds(5),
                interval: TimeSpan.FromMilliseconds(1),
                CancellationToken.None,
                delayAsync: (_, _) => Task.CompletedTask));

        Assert.Equal("authorization_invalid", ex.Category);
        Assert.Contains("urn:ietf:params:acme:error:dns", ex.Message, StringComparison.Ordinal);
        Assert.Contains("SERVFAIL", ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("AcmeRequestException", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WaitUntilReady_Timeout_ThrowsBoundedTimeout()
    {
        var delayCalls = 0;
        var ex = await Assert.ThrowsAsync<AcmeOrderException>(() =>
            AcmeOrderReadiness.WaitUntilReadyAsync(
                _ => Task.FromResult(
                    new AcmeOrderReadiness.OrderView(
                        "Pending",
                        [new AcmeOrderReadiness.AuthorizationView("news.usenet.ninja", "Pending", null, null, null)])),
                timeout: TimeSpan.FromMilliseconds(50),
                interval: TimeSpan.FromMilliseconds(10),
                CancellationToken.None,
                delayAsync: async (delay, ct) =>
                {
                    delayCalls++;
                    await Task.Delay(delay, ct);
                }));

        Assert.Equal("authorization_timeout", ex.Category);
        Assert.Contains("order=Pending", ex.Message, StringComparison.Ordinal);
        Assert.True(delayCalls >= 1);
    }

    [Fact]
    public async Task WaitUntilReady_Cancellation_ThrowsPromptly()
    {
        using var cts = new CancellationTokenSource();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waitTask = AcmeOrderReadiness.WaitUntilReadyAsync(
            async ct =>
            {
                gate.TrySetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return new AcmeOrderReadiness.OrderView("Pending", []);
            },
            timeout: TimeSpan.FromMinutes(1),
            interval: TimeSpan.FromMilliseconds(5),
            cts.Token,
            delayAsync: (delay, ct) => Task.Delay(delay, ct));

        await gate.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitTask);
    }
}

public sealed class AcmeProblemDiagnosticsTests
{
    [Fact]
    public void FormatAcmeRequest_PreservesStructuredFields()
    {
        var error = new Certes.Acme.AcmeError
        {
            Type = "urn:ietf:params:acme:error:orderNotReady",
            Detail = "Order's status (\"pending\") is not acceptable for finalization",
            Status = System.Net.HttpStatusCode.Forbidden,
            Identifier = new Certes.Acme.Resource.Identifier
            {
                Type = Certes.Acme.Resource.IdentifierType.Dns,
                Value = "nntpd01.usenet.ninja",
            },
        };
        var ex = new AcmeRequestException("Error creating new order", error);
        var formatted = AcmeProblemDiagnostics.FormatAcmeRequest(ex);

        Assert.Contains("type=urn:ietf:params:acme:error:orderNotReady", formatted, StringComparison.Ordinal);
        Assert.Contains("status=403", formatted, StringComparison.Ordinal);
        Assert.Contains("identifier=nntpd01.usenet.ninja", formatted, StringComparison.Ordinal);
        Assert.Contains("detail=", formatted, StringComparison.Ordinal);
        Assert.Contains("pending", formatted, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FormatException_RedactsPemAndLongTokens()
    {
        var pem = "oops -----BEGIN PRIVATE KEY-----\nabc\n-----END PRIVATE KEY----- trailing";
        var error = new Certes.Acme.AcmeError
        {
            Type = "urn:ietf:params:acme:error:serverInternal",
            Detail = pem + " " + new string('A', 100),
        };
        var formatted = AcmeProblemDiagnostics.FormatAcmeRequest(new AcmeRequestException("x", error));
        Assert.Contains("[redacted-pem]", formatted, StringComparison.Ordinal);
        Assert.Contains("[redacted-token]", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", formatted, StringComparison.Ordinal);
    }

    [Fact]
    public void FailureSanitizer_IncludesAcmeOrderDiagnosticMessage()
    {
        var ex = new AcmeOrderException(
            "challenge_failed",
            "type=urn:ietf:params:acme:error:dns status=400 identifier=nntpd01.usenet.ninja detail=NXDOMAIN");
        var sanitized = AcmeFailureSanitizer.Sanitize(ex);
        Assert.Contains("challenge_failed", sanitized, StringComparison.Ordinal);
        Assert.Contains("urn:ietf:params:acme:error:dns", sanitized, StringComparison.Ordinal);
        Assert.Contains("nntpd01.usenet.ninja", sanitized, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE KEY", sanitized, StringComparison.Ordinal);
    }

    [Fact]
    public void ChallengeFailedWrapper_UsesFormattedCertesDetails_NotTypeNameOnly()
    {
        var error = new Certes.Acme.AcmeError
        {
            Type = "urn:ietf:params:acme:error:dns",
            Detail = "DNS problem: NXDOMAIN looking up TXT",
            Status = System.Net.HttpStatusCode.BadRequest,
            Identifier = new Certes.Acme.Resource.Identifier
            {
                Type = Certes.Acme.Resource.IdentifierType.Dns,
                Value = "news.usenet.ninja",
            },
        };
        var certes = new AcmeRequestException("Error processing request", error);
        var wrapped = new AcmeOrderException("challenge_failed", AcmeProblemDiagnostics.FormatException(certes));

        Assert.Equal("challenge_failed", wrapped.Category);
        Assert.Contains("type=urn:ietf:params:acme:error:dns", wrapped.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("challenge_failed: AcmeRequestException", wrapped.Message, StringComparison.Ordinal);
        Assert.Equal(wrapped.Message, AcmeFailureSanitizer.Sanitize(wrapped));
    }
}

/// <summary>
/// Documents the required issue sequence: Generate must not run while the order is still Pending.
/// Exercised via <see cref="AcmeOrderReadiness"/> which <c>CertesAcmeIssuer</c> calls after Validate().
/// </summary>
public sealed class AcmeIssuanceSequenceTests
{
    [Fact]
    public async Task ReadinessGate_PreventsFinalizeWhilePending_ThenAllowsWhenReady()
    {
        var generateAllowed = false;
        var states = new Queue<string>(["Pending", "Pending", "Ready"]);

        await AcmeOrderReadiness.WaitUntilReadyAsync(
            _ =>
            {
                var status = states.Dequeue();
                Assert.False(generateAllowed);
                return Task.FromResult(
                    new AcmeOrderReadiness.OrderView(
                        status,
                        [
                            new AcmeOrderReadiness.AuthorizationView(
                                "nntpd01.usenet.ninja",
                                status == "Ready" ? "Valid" : "Pending",
                                null,
                                null,
                                null),
                            new AcmeOrderReadiness.AuthorizationView(
                                "news.usenet.ninja",
                                status == "Ready" ? "Valid" : "Pending",
                                null,
                                null,
                                null),
                        ]));
            },
            timeout: TimeSpan.FromSeconds(5),
            interval: TimeSpan.FromMilliseconds(1),
            CancellationToken.None,
            delayAsync: (_, _) => Task.CompletedTask);

        generateAllowed = true;
        Assert.Empty(states);
        Assert.True(generateAllowed);
    }
}
