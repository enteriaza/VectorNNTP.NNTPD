using System.Text.Json;
using System.Text.Json.Serialization;

namespace VectorNNTP.NNTPD.Acme.Protocol;

/// <summary>STJ source-generation context for ACME wire DTOs.</summary>
[JsonSourceGenerationOptions(
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(AcmeDirectoryResource))]
[JsonSerializable(typeof(AcmeAccountResource))]
[JsonSerializable(typeof(AcmeOrderResource))]
[JsonSerializable(typeof(AcmeAuthorizationResource))]
[JsonSerializable(typeof(AcmeChallengeResource))]
[JsonSerializable(typeof(AcmeProblem))]
[JsonSerializable(typeof(AcmeEmptyResource))]
internal sealed partial class AcmeJsonContext : JsonSerializerContext;
