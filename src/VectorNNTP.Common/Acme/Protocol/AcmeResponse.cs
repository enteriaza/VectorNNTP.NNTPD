using System.Net;

namespace VectorNNTP.Common.Acme.Protocol
{
    /// <summary>Typed ACME HTTP response with Location / Link / Retry-After metadata.</summary>
    internal sealed class AcmeResponse<T>
    {
        /// <summary>Initializes a new instance of the <see cref="AcmeResponse{T}"/> class.</summary>
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
    internal readonly record struct AcmeLink(Uri Url, string Relation);

    /// <summary>Raw (non-JSON) response body with Link headers (certificate download).</summary>
    internal readonly record struct AcmeRawResponse(string Body, IReadOnlyList<AcmeLink> Links);

    /// <summary>Order URL plus the latest order resource representation.</summary>
    internal sealed record AcmeOrder(Uri Location, AcmeOrderResource Resource);

    /// <summary>Downloaded PEM certificate chain and any alternate chain Link URLs.</summary>
    internal sealed record AcmeCertificate(string Pem, IReadOnlyList<Uri> Alternates);
}
