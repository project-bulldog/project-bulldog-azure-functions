using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;


namespace functions.Converters
{
    /// <summary>
    /// JSON converter for non-nullable DateTime values that enforces UTC ISO 8601 formatting.
    /// </summary>
    public class DateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var str = reader.GetString();
            if (string.IsNullOrWhiteSpace(str))
                throw new JsonException("DateTime string was null or empty");

            if (DateTime.TryParse(str, null, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                return dt;

            throw new JsonException($"Invalid DateTime format: '{str}'");
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            writer.WriteStringValue($"{value.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}");
        }
    }

    /// <summary>
    /// JSON converter for nullable DateTime values that enforces UTC ISO 8601 formatting.
    /// </summary>
    public class DateTimeNullableConverter : JsonConverter<DateTime?>
    {
        public override DateTime? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var str = reader.GetString();
            if (string.IsNullOrWhiteSpace(str))
                return null;

            if (DateTime.TryParse(str, null, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
                return dt;

            throw new JsonException($"Invalid nullable DateTime format: '{str}'");
        }

        public override void Write(Utf8JsonWriter writer, DateTime? value, JsonSerializerOptions options)
        {
            if (value.HasValue)
                writer.WriteStringValue($"{value.Value.ToUniversalTime():yyyy-MM-ddTHH:mm:ssZ}");
            else
                writer.WriteNullValue();
        }
    }
}