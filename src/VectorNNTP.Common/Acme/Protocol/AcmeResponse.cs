using System.Net;

namespace VectorNNTP.Common.Acme.Protocol
{
    /// <summary>Typed ACME HTTP response with Location / Link / Retry-After metadata.</summary>
    /// <typeparam name="T">Deserialized JSON body. Unused when the caller keeps only <see cref="AcmeResponse{T}.RawBody"/>.</typeparam>
    internal sealed class AcmeResponse<T>
    {
        /// <summary>Stores the status, body, and header metadata from one ACME HTTP response.</summary>
        /// <param name="statusCode">HTTP status. Success responses are the only ones returned to callers.</param>
        /// <param name="content">Deserialized body. <see langword="null"/> when the body was empty or no deserializer was supplied.</param>
        /// <param name="location">Location header. <see langword="null"/> when the CA omitted it.</param>
        /// <param name="links">Parsed Link headers. Empty when none were present.</param>
        /// <param name="rawBody">Response text before deserialization. Empty when the body was empty.</param>
        /// <param name="retryAfter">Absolute Retry-After time. <see langword="null"/> when the header was absent.</param>
        internal AcmeResponse(
            HttpStatusCode statusCode,
            T? content,
            Uri? location,
            IReadOnlyList<AcmeLink> links,
            string rawBody,
            DateTimeOffset? retryAfter = null)
        {
            StatusCode = statusCode;
            Content = content;
            Location = location;
            Links = links;
            RawBody = rawBody;
            RetryAfter = retryAfter;
        }

        /// <summary>Gets the HTTP status code.</summary>
        private HttpStatusCode StatusCode { get; }

        /// <summary>Gets the deserialized body when present.</summary>
        internal T? Content { get; }

        /// <summary>Gets the Location header when present.</summary>
        internal Uri? Location { get; }

        /// <summary>Gets parsed Link headers.</summary>
        internal IReadOnlyList<AcmeLink> Links { get; }

        /// <summary>Gets the raw response body.</summary>
        internal string RawBody { get; }

        /// <summary>Gets Retry-After as an absolute UTC time when the authority supplied one.</summary>
        internal DateTimeOffset? RetryAfter { get; }
    }

    /// <summary>One RFC 8288 Link entry from an ACME response.</summary>
    /// <param name="Url">Absolute link target. Relative targets are dropped by the parser.</param>
    /// <param name="Relation">Link <c>rel</c> value. Empty when the parameter was absent.</param>
    internal readonly record struct AcmeLink(Uri Url, string Relation);

    /// <summary>Raw (non-JSON) response body with Link headers (certificate download).</summary>
    /// <param name="Body">Response text. For a certificate download this is the PEM chain, which may be empty.</param>
    /// <param name="Links">Parsed Link headers, including <c>alternate</c> chain URLs when the CA sent them.</param>
    internal readonly record struct AcmeRawResponse(string Body, IReadOnlyList<AcmeLink> Links);

    /// <summary>Order URL plus the latest order resource representation.</summary>
    /// <param name="Location">Order URL from the Location header.</param>
    /// <param name="Resource">Order JSON returned with that response. Later polls replace this view.</param>
    internal sealed record AcmeOrder(Uri Location, AcmeOrderResource Resource);

    /// <summary>Downloaded PEM certificate chain and any alternate chain Link URLs.</summary>
    /// <param name="Pem">PEM chain body. Issuance rejects a blank value.</param>
    /// <param name="Alternates">Link URLs whose relation is <c>alternate</c>. Empty when the CA sent none. Issuance does not download them.</param>
    internal sealed record AcmeCertificate(string Pem, IReadOnlyList<Uri> Alternates);
}
