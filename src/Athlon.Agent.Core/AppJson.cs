using System.Text.Json;
using System.Text.Json.Serialization;

namespace Athlon.Agent.Core;

/// <summary>
/// Single, shared JSON serializer settings for app-level DTO serialization.
/// Previously several services each declared a near-identical
/// <c>JsonSerializerOptions</c> (CamelCase + ignore-null), risking drift.
/// </summary>
public static class AppJson
{
    /// <summary>CamelCase property naming with null properties omitted.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}
