using System.Text;
using VectorNNTP.BackFiller.ArticleWork;
using VectorNNTP.BackFiller.Tests.Fixtures;

using VectorNNTP.Common.Messaging.RabbitMq;

namespace VectorNNTP.BackFiller.Tests.ArticleWork
{
    public sealed class ArticleWorkRequestParserTests
    {
        private const int MaxPayload = 1024;

        [Fact]
        public void Canonical_protocol_payload_is_valid()
        {
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                MaxPayload);

            Assert.True(parsed.IsValid);
            Assert.NotNull(parsed.Request);
            Assert.Equal(1, parsed.Request.Version);
            Assert.Equal(Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId), parsed.Request.RequestId);
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalMessageId, parsed.Request.MessageId);
            Assert.Equal("Giganews", parsed.Request.Backbone);
        }

        [Fact]
        public void Protocol_valid_example_is_accepted()
        {
            var delivery = ArticleWorkTestDeliveries.Create(
                ArticleWorkTestDeliveries.ProtocolValidExampleJson,
                requestIdHeader: "d0648b54-b1b8-4717-95e1-7b31bf7fd1bd");

            var parsed = ArticleWorkRequestParser.Parse(delivery, "Eweka", MaxPayload);

            Assert.True(parsed.IsValid);
            Assert.Equal("<abc@example.invalid>", parsed.Request!.MessageId);
            Assert.Equal("Eweka", parsed.Request.Backbone);
        }

        [Fact]
        public void Unknown_json_fields_are_ignored()
        {
            var json =
                """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"Giganews","extra":true,"CorrelationId":"ignore"}""";

            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(json),
                "Giganews",
                MaxPayload);

            Assert.True(parsed.IsValid);
            Assert.Equal(ArticleWorkTestDeliveries.CanonicalMessageId, parsed.Request!.MessageId);
        }

        [Fact]
        public void Property_names_are_case_sensitive()
        {
            var json =
                """{"Version":1,"RequestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","MessageId":"<12345@example.invalid>","Backbone":"Giganews"}""";

            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(json),
                "Giganews",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("version", parsed.Failure!.Reason, StringComparison.Ordinal);
            Assert.Null(parsed.Failure.Identities.RequestId);
        }

        [Fact]
        public void Malformed_json_is_invalid_request()
        {
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create("{not-json"),
                "Giganews",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("not valid JSON", parsed.Failure!.Reason, StringComparison.Ordinal);
            Assert.Null(parsed.Failure.Identities.RequestId);
        }

        [Fact]
        public void Empty_payload_is_invalid_request()
        {
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(string.Empty),
                "Giganews",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("empty", parsed.Failure!.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void Missing_version_is_invalid_request()
        {
            var json =
                """{"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"Giganews"}""";

            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(json),
                "Giganews",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("'version'", parsed.Failure!.Reason, StringComparison.Ordinal);
            Assert.Equal(Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId), parsed.Failure.Identities.RequestId);
        }

        [Fact]
        public void Protocol_invalid_example_is_rejected_for_unsupported_version()
        {
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(ArticleWorkTestDeliveries.ProtocolInvalidExampleJson),
                "Giganews",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("unsupported version", parsed.Failure!.Reason, StringComparison.Ordinal);
            Assert.Null(parsed.Failure.Identities.RequestId);
            Assert.Null(parsed.Failure.Identities.MessageId);
            Assert.Equal("WrongBackbone", parsed.Failure.Identities.Backbone);
        }

        [Fact]
        public void Missing_request_id_is_invalid_and_not_synthesized()
        {
            var json =
                """{"version":1,"messageId":"<12345@example.invalid>","backbone":"Giganews"}""";

            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(json),
                "Giganews",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("requestId", parsed.Failure!.Reason, StringComparison.Ordinal);
            Assert.Null(parsed.Failure.Identities.RequestId);
        }

        [Fact]
        public void Empty_and_nil_request_ids_are_invalid()
        {
            var empty = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    """{"version":1,"requestId":"","messageId":"<12345@example.invalid>","backbone":"Giganews"}"""),
                "Giganews",
                MaxPayload);
            var nil = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    """{"version":1,"requestId":"00000000-0000-0000-0000-000000000000","messageId":"<12345@example.invalid>","backbone":"Giganews"}"""),
                "Giganews",
                MaxPayload);

            Assert.False(empty.IsValid);
            Assert.False(nil.IsValid);
            Assert.Null(empty.Failure!.Identities.RequestId);
            Assert.Null(nil.Failure!.Identities.RequestId);
        }

        [Fact]
        public void Missing_and_invalid_message_ids_are_rejected_without_normalization()
        {
            var missing = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","backbone":"Giganews"}"""),
                "Giganews",
                MaxPayload);
            var unbracketed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"12345@example.invalid","backbone":"Giganews"}"""),
                "Giganews",
                MaxPayload);

            Assert.False(missing.IsValid);
            Assert.False(unbracketed.IsValid);
            Assert.Null(missing.Failure!.Identities.MessageId);
            Assert.Null(unbracketed.Failure!.Identities.MessageId);
        }

        [Fact]
        public void Exact_message_id_is_preserved()
        {
            const string wire = "<AbC@Example.INVALID>";
            var json =
                $$"""{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"{{wire}}","backbone":"Giganews"}""";

            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(json),
                "Giganews",
                MaxPayload);

            Assert.True(parsed.IsValid);
            Assert.Equal(wire, parsed.Request!.MessageId);
            Assert.NotEqual(wire.ToLowerInvariant(), parsed.Request.MessageId);
        }

        [Fact]
        public void Missing_backbone_is_invalid_request()
        {
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>"}"""),
                "Giganews",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("backbone", parsed.Failure!.Reason, StringComparison.Ordinal);
            Assert.Null(parsed.Failure.Identities.Backbone);
        }

        [Fact]
        public void Backbone_mismatch_is_invalid_request()
        {
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Canonical(),
                "Eweka",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("does not match", parsed.Failure!.Reason, StringComparison.Ordinal);
            Assert.Equal("Giganews", parsed.Failure.Identities.Backbone);
        }

        [Fact]
        public void Backbone_match_is_ordinal_ignore_case_and_preserves_json_value()
        {
            var json =
                """{"version":1,"requestId":"7c1cb8a0-95f9-4c13-8e53-339773e3afaa","messageId":"<12345@example.invalid>","backbone":"giganews"}""";

            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(json),
                "Giganews",
                MaxPayload);

            Assert.True(parsed.IsValid);
            Assert.Equal("giganews", parsed.Request!.Backbone);
        }

        [Fact]
        public void Missing_correlation_id_is_invalid_request()
        {
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    ArticleWorkTestDeliveries.CanonicalRequestJson,
                    correlationId: null),
                "Giganews",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("CorrelationId", parsed.Failure!.Reason, StringComparison.Ordinal);
            Assert.Equal(Guid.Parse(ArticleWorkTestDeliveries.CanonicalRequestId), parsed.Failure.Identities.RequestId);
        }

        [Fact]
        public void Missing_reply_to_is_invalid_request()
        {
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    ArticleWorkTestDeliveries.CanonicalRequestJson,
                    replyTo: " "),
                "Giganews",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("ReplyTo", parsed.Failure!.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void ContentType_is_required_only_when_present()
        {
            var absent = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    ArticleWorkTestDeliveries.CanonicalRequestJson,
                    contentType: null),
                "Giganews",
                MaxPayload);
            var empty = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    ArticleWorkTestDeliveries.CanonicalRequestJson,
                    contentType: string.Empty),
                "Giganews",
                MaxPayload);
            var wrong = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(
                    ArticleWorkTestDeliveries.CanonicalRequestJson,
                    contentType: "text/plain"),
                "Giganews",
                MaxPayload);

            Assert.True(absent.IsValid);
            Assert.True(empty.IsValid);
            Assert.False(wrong.IsValid);
            Assert.Contains("ContentType", wrong.Failure!.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void RequestId_header_is_required_only_when_present()
        {
            var absent = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Canonical(requestIdHeader: null),
                "Giganews",
                MaxPayload);
            var mismatch = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Canonical(requestIdHeader: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
                "Giganews",
                MaxPayload);
            var empty = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Canonical(requestIdHeader: " "),
                "Giganews",
                MaxPayload);

            Assert.True(absent.IsValid);
            Assert.False(mismatch.IsValid);
            Assert.True(empty.IsValid);
            Assert.Contains("RequestId", mismatch.Failure!.Reason, StringComparison.Ordinal);
        }

        [Fact]
        public void RequestId_is_distinct_from_correlation_id_and_delivery_tag()
        {
            var delivery = ArticleWorkTestDeliveries.Canonical(deliveryTag: 99);
            var parsed = ArticleWorkRequestParser.Parse(delivery, "Giganews", MaxPayload);

            Assert.True(parsed.IsValid);
            Assert.NotEqual(parsed.Request!.RequestId.ToString(), delivery.CorrelationId);
            Assert.NotEqual(parsed.Request.RequestId.ToString(), delivery.DeliveryTag.ToString());
            Assert.NotEqual(delivery.CorrelationId, delivery.DeliveryTag.ToString());
        }

        [Fact]
        public void Oversized_payload_is_invalid_without_json_identities()
        {
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                maxPayloadBytes: 8);

            Assert.False(parsed.IsValid);
            Assert.Contains("WorkRequestMaxPayloadBytes", parsed.Failure!.Reason, StringComparison.Ordinal);
            Assert.Null(parsed.Failure.Identities.RequestId);
        }

        [Fact]
        public void Message_id_uses_common_validator()
        {
            Assert.True(ParseMessageId("<a@b>").IsValid);
            Assert.True(ParseMessageId("<foo.bar@example.com>").IsValid);
            Assert.True(ParseMessageId("<foo@[IPv6:2001:db8::1]>").IsValid);
            Assert.Equal("<a@b>", ParseMessageId("<a@b>").Request!.MessageId);
            Assert.True(ParseMessageId("<" + new string('a', 246) + "@b>").IsValid);

            Assert.False(ParseMessageId("<x>").IsValid);
            Assert.False(ParseMessageId("<@>").IsValid);
            Assert.False(ParseMessageId("<@b>").IsValid);
            Assert.False(ParseMessageId("<x@@y>").IsValid);
            Assert.False(ParseMessageId("<double..dot@example.com>").IsValid);
            Assert.False(ParseMessageId("<a b@c>").IsValid);
            Assert.False(ParseMessageId("<" + new string('a', 247) + "@b>").IsValid);
        }

        [Fact]
        public void Parser_does_not_decode_body_as_string_before_json()
        {
            var body = Encoding.UTF8.GetBytes(ArticleWorkTestDeliveries.CanonicalRequestJson);
            var delivery = new VectorNNTP.Common.Messaging.RabbitMq.RabbitMqManualAckDelivery(
                1,
                body,
                ArticleWorkTestDeliveries.CanonicalCorrelationId,
                ArticleWorkTestDeliveries.CanonicalReplyTo,
                ArticleWorkRequestParser.JsonContentType,
                ArticleWorkTestDeliveries.CanonicalRequestId,
                false,
                "backfiller.giganews",
                "backfiller.giganews",
                "ctag",
                1);

            var parsed = ArticleWorkRequestParser.Parse(delivery, "Giganews", MaxPayload);
            Assert.True(parsed.IsValid);
            Assert.Equal(body.Length, delivery.Body.Length);
        }

        /// <summary>
        /// Regression test: oversized message must be rejected before JSON parsing,
        /// with a bounded, specific diagnostic reason, never by reparsinig with an unbounded limit.
        /// </summary>
        [Fact]
        public void Oversized_payload_rejection_reason_is_bounded_and_specific()
        {
            // The rejection reason must come from the initial size check, not a second parse.
            // This prevents unbounded JSON parsing on an error path.
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Canonical(),
                "Giganews",
                maxPayloadBytes: 8);

            Assert.False(parsed.IsValid);
            var reason = parsed.Failure!.Reason;
            Assert.NotNull(reason);
            Assert.Contains("WorkRequestMaxPayloadBytes", reason, StringComparison.Ordinal);
            // The reason must not require parsing the entire body; it must be deterministic from size check alone.
            Assert.DoesNotContain("JSON", reason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("schema", reason, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("field", reason, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Boundary test: maximum + 1 byte is rejected; maximum byte is accepted for further parsing.
        /// </summary>
        [Theory]
        [InlineData(1023, true)]  // At maximum - 1
        [InlineData(1024, true)]  // At maximum
        [InlineData(1025, false)] // At maximum + 1
        public void Payload_size_boundary_is_enforced(int payloadSize, bool shouldBeValid)
        {
            const int configuredMaximum = 1024;
            var json = ArticleWorkTestDeliveries.CanonicalRequestJson;
            // Pad the backbone field to reach the desired size
            var padding = new string(' ', Math.Max(0, payloadSize - json.Length));
            var paddedJson = json.Replace("\"Giganews\"", $"\"{padding}Giganews\"");
            var body = Encoding.UTF8.GetBytes(paddedJson);

            var delivery = new VectorNNTP.Common.Messaging.RabbitMq.RabbitMqManualAckDelivery(
                1,
                body,
                ArticleWorkTestDeliveries.CanonicalCorrelationId,
                ArticleWorkTestDeliveries.CanonicalReplyTo,
                ArticleWorkRequestParser.JsonContentType,
                ArticleWorkTestDeliveries.CanonicalRequestId,
                false,
                "backfiller.giganews",
                "backfiller.giganews",
                "ctag",
                1);

            var parsed = ArticleWorkRequestParser.Parse(delivery, "Giganews", configuredMaximum);

            if (shouldBeValid)
            {
                // At or below maximum, parsing should continue past size check.
                // It may fail for other reasons (schema, content), but not for size.
                Assert.DoesNotContain("WorkRequestMaxPayloadBytes", parsed.Failure?.Reason ?? string.Empty, StringComparison.Ordinal);
            }
            else
            {
                // Above maximum, must be rejected on size.
                Assert.False(parsed.IsValid);
                Assert.Contains("WorkRequestMaxPayloadBytes", parsed.Failure!.Reason, StringComparison.Ordinal);
            }
        }

        /// <summary>
        /// Regression test: malformed JSON within the size limit must produce a distinct error from oversized,
        /// not be mistaken for an unbounded reparse scenario.
        /// </summary>
        [Fact]
        public void Malformed_json_within_size_limit_produces_distinct_error()
        {
            var json = "{invalid-json";
            var parsed = ArticleWorkRequestParser.Parse(
                ArticleWorkTestDeliveries.Create(json),
                "Giganews",
                MaxPayload);

            Assert.False(parsed.IsValid);
            Assert.Contains("not valid JSON", parsed.Failure!.Reason, StringComparison.Ordinal);
            Assert.DoesNotContain("WorkRequestMaxPayloadBytes", parsed.Failure.Reason, StringComparison.Ordinal);
        }

        private static ArticleWorkParseResult ParseMessageId(string messageId)
        {
            var json = "{\"version\":1,\"requestId\":\"7c1cb8a0-95f9-4c13-8e53-339773e3afaa\",\"messageId\":\""
                + messageId
                + "\",\"backbone\":\"Giganews\"}";
            return ArticleWorkRequestParser.Parse(ArticleWorkTestDeliveries.Create(json), "Giganews", 8192);
        }
    }
}
