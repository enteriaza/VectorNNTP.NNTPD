using System.IO.Hashing;
using VectorNNTP.Common.Articles;
using VectorNNTP.StorageServer.Storage.Engine;

namespace VectorNNTP.StorageServer.Storage.Engine.FileSegments;

/// <summary>
/// One article frame's payload and the ArtHash the segment writer must store.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="FromAccept"/> copies the identity <see cref="JournalAcceptRecord"/> already
/// carries. Accept proved that hash before the journal frame was flushed, and the frame
/// CRC covers the stored ulong together with the payload copy. The segment writer does
/// not hash those bytes again.
/// </para>
/// <para>
/// <see cref="Derive"/> is the other entry. It hashes the buffer. Relocation, a raw
/// append, and any caller that does not hold an Accept record use it. Disk read-back,
/// orphan scans, ambiguous tails, and <c>TryReadProven</c> do not use this type to skip
/// their own proof.
/// </para>
/// <para>
/// The constructor is private. <see cref="TryMatch"/> is the only way to supply a hash
/// that was not taken from an Accept record, and it refuses a hash that is not the
/// XXH3-64 of that payload.
/// </para>
/// </remarks>
internal readonly struct AuthoritativeArticleAppend
{
    /// <summary>Stores a payload and a hash the caller has already proved.</summary>
    /// <param name="artData">Payload that will be written.</param>
    /// <param name="artId">Message-ID identity of <paramref name="artData"/>.</param>
    /// <param name="artHash">XXH3-64 of <paramref name="artData"/>.</param>
    /// <param name="acceptedUtc">UTC accept instant stored in the segment record.</param>
    private AuthoritativeArticleAppend(
        ReadOnlyMemory<byte> artData,
        ArticleId artId,
        ulong artHash,
        DateTimeOffset acceptedUtc)
    {
        ArtData = artData;
        ArtId = artId;
        ArtHash = artHash;
        AcceptedUtc = acceptedUtc;
    }

    /// <summary>Payload bytes that will be written after the segment header.</summary>
    internal ReadOnlyMemory<byte> ArtData { get; }

    /// <summary>Article identity that must match the payload Message-ID.</summary>
    internal ArticleId ArtId { get; }

    /// <summary>ArtHash written into the segment header. Not recomputed by the writer.</summary>
    internal ulong ArtHash { get; }

    /// <summary>Original accept instant written into the segment record.</summary>
    internal DateTimeOffset AcceptedUtc { get; }

    /// <summary>Payload length. Equal to <see cref="ArtData"/> length.</summary>
    internal int ArtSize => ArtData.Length;

    /// <summary>
    /// Builds an append from an Accept record without hashing the payload.
    /// </summary>
    /// <param name="accept">Journal Accept whose ArtHash and ArtData were proved before the frame was flushed.</param>
    /// <returns>The append the post-ACK writer stores under <paramref name="accept"/>'s hash.</returns>
    /// <exception cref="ArgumentException">
    /// Thrown when the payload is missing, detached, or a different length from <see cref="JournalAcceptRecord.ArtSize"/>.
    /// </exception>
    internal static AuthoritativeArticleAppend FromAccept(JournalAcceptRecord accept)
    {
        ArgumentNullException.ThrowIfNull(accept);
        var artData = accept.ArtData;
        if (artData.Length != accept.ArtSize
            || artData.Length is < 1 or > ArticleResourceLimits.MaxArticleBytes)
        {
            throw new ArgumentException(
                "Accept ArtData must still be attached and match ArtSize.",
                nameof(accept));
        }

        return new AuthoritativeArticleAppend(
            artData,
            accept.ArtId,
            accept.ArtHash,
            ArticleArrivalTime.RequireStored(accept.AcceptedUtc, nameof(accept)));
    }

    /// <summary>
    /// Hashes <paramref name="artData"/> and returns an append carrying that hash.
    /// </summary>
    /// <param name="artData">Payload that does not already carry an Accept proof.</param>
    /// <param name="acceptedUtc">UTC accept instant stored in the segment record.</param>
    /// <returns>An append whose ArtHash is <c>XxHash3.HashToUInt64</c> of <paramref name="artData"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the payload length is outside the article limit.</exception>
    /// <exception cref="ArgumentException">Thrown when the payload has no Message-ID value.</exception>
    internal static AuthoritativeArticleAppend Derive(ReadOnlyMemory<byte> artData, DateTimeOffset acceptedUtc)
    {
        if (artData.Length is < 1 or > ArticleResourceLimits.MaxArticleBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(artData), "ArtData length out of range.");
        }

        if (!TryReadIdentity(artData, out var artId, out var artHash))
        {
            throw new ArgumentException(
                "ArtData must contain a Message-ID header value.",
                nameof(artData));
        }

        return new AuthoritativeArticleAppend(
            artData,
            artId,
            artHash,
            ArticleArrivalTime.RequireStored(acceptedUtc, nameof(acceptedUtc)));
    }

    /// <summary>
    /// Returns an append only when <paramref name="artHash"/> is the XXH3-64 of
    /// <paramref name="artData"/> and <paramref name="artId"/> is its Message-ID identity.
    /// </summary>
    /// <param name="artData">Payload the caller claims to have hashed.</param>
    /// <param name="artId">Identity the caller claims for that payload.</param>
    /// <param name="artHash">Hash the caller claims for that payload.</param>
    /// <param name="acceptedUtc">UTC accept instant stored in the segment record.</param>
    /// <param name="article">The append when this returns true. Default when it returns false.</param>
    /// <returns>True when the supplied identity matches the payload.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Thrown when the identity matches and <paramref name="acceptedUtc"/> is not a stored UTC instant.
    /// </exception>
    internal static bool TryMatch(
        ReadOnlyMemory<byte> artData,
        ArticleId artId,
        ulong artHash,
        DateTimeOffset acceptedUtc,
        out AuthoritativeArticleAppend article)
    {
        article = default;
        if (!TryReadIdentity(artData, out var actualId, out var actualHash)
            || actualId != artId
            || actualHash != artHash)
        {
            return false;
        }

        article = new AuthoritativeArticleAppend(
            artData,
            artId,
            artHash,
            ArticleArrivalTime.RequireStored(acceptedUtc, nameof(acceptedUtc)));
        return true;
    }

    /// <summary>
    /// Reads the Message-ID identity and the XXH3-64 of <paramref name="artData"/>.
    /// </summary>
    /// <param name="artData">Candidate payload.</param>
    /// <param name="artId">Message-ID identity when this returns true.</param>
    /// <param name="artHash">XXH3-64 of the payload when this returns true.</param>
    /// <returns>False when the length is illegal or the Message-ID value is missing.</returns>
    private static bool TryReadIdentity(ReadOnlyMemory<byte> artData, out ArticleId artId, out ulong artHash)
    {
        artId = default;
        artHash = 0;
        if (artData.Length is < 1 or > ArticleResourceLimits.MaxArticleBytes
            || !ArticleStorageIntegrity.TryExtractMessageIdValue(artData.Span, out var messageId))
        {
            return false;
        }

        artId = ArticleId.FromMessageId(messageId);
        artHash = XxHash3.HashToUInt64(artData.Span);
        return true;
    }
}
