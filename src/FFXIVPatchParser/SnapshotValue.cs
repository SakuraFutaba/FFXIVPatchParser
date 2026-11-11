using System.Globalization;
using System.Text.Json;
using Lumina.Text.ReadOnly;

namespace FFXIVPatchParser;

// The schema carries numeric types; strings carry both a readable view and lossless bytes.
// 64-bit integers are decimal strings so JavaScript-based diff UIs cannot round them.
internal static class SnapshotValue
{
    public static void Write(Utf8JsonWriter writer, object value)
    {
        switch (value)
        {
            case ReadOnlySeString text:
                writer.WriteStartObject();
                writer.WriteString("text", text.ExtractText());
                writer.WriteBase64String("raw", text.Data.Span);
                writer.WriteEndObject();
                break;
            case bool v: writer.WriteBooleanValue(v); break;
            case sbyte v: writer.WriteNumberValue(v); break;
            case byte v: writer.WriteNumberValue(v); break;
            case short v: writer.WriteNumberValue(v); break;
            case ushort v: writer.WriteNumberValue(v); break;
            case int v: writer.WriteNumberValue(v); break;
            case uint v: writer.WriteNumberValue(v); break;
            case long v: writer.WriteStringValue(v.ToString(CultureInfo.InvariantCulture)); break;
            case ulong v: writer.WriteStringValue(v.ToString(CultureInfo.InvariantCulture)); break;
            case float v:
                writer.WriteStartObject();
                if (float.IsFinite(v)) writer.WriteNumber("value", v);
                else writer.WriteString("value", v.ToString("R", CultureInfo.InvariantCulture));
                writer.WriteString("bits", BitConverter.SingleToUInt32Bits(v).ToString("X8", CultureInfo.InvariantCulture));
                writer.WriteEndObject();
                break;
            default: throw new NotSupportedException($"Unsupported raw value type {value.GetType()}.");
        }
    }

    public static void WriteRow(Stream output, uint rowId, ushort? subrowId, int columns, Func<int, object> read)
    {
        using (var writer = new Utf8JsonWriter(output))
        {
            writer.WriteStartObject();
            writer.WriteNumber("rowId", rowId);
            if (subrowId.HasValue) writer.WriteNumber("subrowId", subrowId.Value);
            else writer.WriteNull("subrowId");
            writer.WriteStartArray("values");
            for (var i = 0; i < columns; i++) Write(writer, read(i));
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        output.WriteByte((byte)'\n');
    }
}
