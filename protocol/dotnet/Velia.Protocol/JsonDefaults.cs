using System.Text.Json;
using System.Text.Json.Serialization;

namespace Velia.Protocol;

public static class JsonDefaults
{
    public static JsonSerializerOptions Strict { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
}
