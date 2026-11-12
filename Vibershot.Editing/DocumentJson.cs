using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;

namespace Vibershot.Editing;

/// <summary>Serializer settings shared by the project file and the item clipboard.</summary>
public static class DocumentJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new RectConverter(), new SizeConverter(), new PointConverter(), new ColorConverter() },
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static string ColorToString(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

    public static Color ParseColor(string text)
    {
        var hex = text.TrimStart('#');
        if (hex.Length == 6) hex = "FF" + hex;
        if (hex.Length != 8) throw new FormatException($"Color '{text}' must be #RRGGBB or #AARRGGBB.");
        uint value = uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }

    private static double[] ReadNumbers(ref Utf8JsonReader reader, int count)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected an array of numbers.");
        var values = new double[count];
        for (int i = 0; i < count; i++)
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.Number) throw new JsonException("Expected a number.");
            values[i] = reader.GetDouble();
        }
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Expected the end of the array.");
        return values;
    }

    private static void WriteNumbers(Utf8JsonWriter writer, params double[] values)
    {
        writer.WriteStartArray();
        foreach (var value in values) writer.WriteNumberValue(Math.Round(value, 3));
        writer.WriteEndArray();
    }

    private sealed class RectConverter : JsonConverter<Rect>
    {
        public override Rect Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var v = ReadNumbers(ref reader, 4);
            return new Rect(v[0], v[1], Math.Max(0, v[2]), Math.Max(0, v[3]));
        }
        public override void Write(Utf8JsonWriter writer, Rect value, JsonSerializerOptions options) =>
            WriteNumbers(writer, value.X, value.Y, value.Width, value.Height);
    }

    private sealed class SizeConverter : JsonConverter<Size>
    {
        public override Size Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var v = ReadNumbers(ref reader, 2);
            return new Size(Math.Max(0, v[0]), Math.Max(0, v[1]));
        }
        public override void Write(Utf8JsonWriter writer, Size value, JsonSerializerOptions options) =>
            WriteNumbers(writer, value.Width, value.Height);
    }

    private sealed class PointConverter : JsonConverter<Point>
    {
        public override Point Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var v = ReadNumbers(ref reader, 2);
            return new Point(v[0], v[1]);
        }
        public override void Write(Utf8JsonWriter writer, Point value, JsonSerializerOptions options) =>
            WriteNumbers(writer, value.X, value.Y);
    }

    private sealed class ColorConverter : JsonConverter<Color>
    {
        public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            ParseColor(reader.GetString() ?? throw new JsonException("Expected a color string."));
        public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options) =>
            writer.WriteStringValue(ColorToString(value));
    }
}
