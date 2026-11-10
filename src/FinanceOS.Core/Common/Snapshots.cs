using System.Text.Json;
using System.Text.Json.Serialization;

namespace FinanceOS.Core.Common;

/// <summary>
/// JSON snapshots of entities: what <c>activity_log</c> stores in before/after, and how two snapshots
/// are compared (records holding lists only compare list references, JSON compares content).
/// </summary>
public static class Snapshots
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string? Json<T>(T? value)
        where T : class =>
        value is null ? null : JsonSerializer.Serialize(value, Options);

    public static bool Same<T>(T? a, T? b)
        where T : class =>
        Json(a) == Json(b);
}

/// <summary>Ids and timestamps for new rows.</summary>
public static class Ids
{
    /// <summary>UUID v7: unique and sortable by creation time (ARCHITECTURE §5).</summary>
    public static string New(TimeProvider time) => Guid.CreateVersion7(time.GetUtcNow()).ToString("D");

    /// <summary>Now in UTC, truncated to milliseconds: exactly what the database stores and reads back.</summary>
    public static DateTimeOffset Now(TimeProvider time)
    {
        var now = time.GetUtcNow().UtcDateTime;
        return new DateTimeOffset(now.Ticks - (now.Ticks % TimeSpan.TicksPerMillisecond), TimeSpan.Zero);
    }
}
