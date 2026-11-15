using System.Buffers;
using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DomainCopilot.Infrastructure;

internal static class InfrastructureJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions();
        options.Converters.Add(new BigIntegerJsonConverter());
        return options;
    }

    private sealed class BigIntegerJsonConverter : JsonConverter<BigInteger>
    {
        public override BigInteger Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.Number || !BigInteger.TryParse(
                    reader.HasValueSequence ? System.Text.Encoding.UTF8.GetString(reader.ValueSequence.ToArray()) : System.Text.Encoding.UTF8.GetString(reader.ValueSpan),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
                throw new JsonException("Expected an integer JSON number.");
            return value;
        }

        public override void Write(Utf8JsonWriter writer, BigInteger value, JsonSerializerOptions options) =>
            writer.WriteRawValue(value.ToString(CultureInfo.InvariantCulture), skipInputValidation: false);
    }
}