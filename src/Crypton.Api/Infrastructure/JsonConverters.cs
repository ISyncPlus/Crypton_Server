using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Crypton.Core.Common;

namespace Crypton.Api.Infrastructure;

/// <summary>
/// Money is serialized as a JSON string (e.g. "0.00012345") so JavaScript clients never lose precision.
/// Both strings and numbers are accepted on input.
/// </summary>
public sealed class DecimalStringConverter : JsonConverter<decimal>
{
    public override decimal Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return reader.GetDecimal();
        }

        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        throw new JsonException("Expected a decimal number or numeric string.");
    }

    public override void Write(Utf8JsonWriter writer, decimal value, JsonSerializerOptions options) =>
        writer.WriteStringValue(MoneyMath.ToPlainString(value));
}

public sealed class NullableDecimalStringConverter : JsonConverter<decimal?>
{
    private static readonly DecimalStringConverter Inner = new();

    public override decimal? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType == JsonTokenType.String && string.IsNullOrWhiteSpace(reader.GetString()))
        {
            return null;
        }

        return Inner.Read(ref reader, typeof(decimal), options);
    }

    public override void Write(Utf8JsonWriter writer, decimal? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
        }
        else
        {
            Inner.Write(writer, value.Value, options);
        }
    }
}
