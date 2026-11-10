using System.Text.Json;
using System.Text.Json.Serialization;

namespace Vigiloom.Contracts.Agent;

internal static class AgentBatchJson
{
    internal static JsonSerializerOptions CreateOptions<T>(int maximumItems = AgentBatchCodec.MaximumItems) where T : class
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = false,
            AllowDuplicateProperties = false,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            MaxDepth = 8
        };
        options.Converters.Add(new BoundedItemsConverter<T>(maximumItems));
        return options;
    }

    private sealed class BoundedItemsConverter<T>(int maximumItems) : JsonConverter<IReadOnlyList<T>> where T : class
    {
        public override IReadOnlyList<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Agent batch items must be an array.");
            var items = new List<T>();
            while (reader.Read())
            {
                if (reader.TokenType == JsonTokenType.EndArray) return items;
                if (items.Count >= maximumItems) throw new JsonException("Too many Agent batch items.");
                items.Add(JsonSerializer.Deserialize<T>(ref reader, options) ?? throw new JsonException("Agent batch item is null."));
            }
            throw new JsonException("Incomplete Agent batch items.");
        }

        public override void Write(Utf8JsonWriter writer, IReadOnlyList<T> value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            foreach (var item in value) JsonSerializer.Serialize(writer, item, options);
            writer.WriteEndArray();
        }
    }
}
