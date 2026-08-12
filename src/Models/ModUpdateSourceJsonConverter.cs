using System.Text.Json;
using System.Text.Json.Serialization;

namespace DDS2ModManager.Models;

/// Reads ModUpdateSource, tolerating the shape an earlier build wrote.
///
/// ModInfo.UpdateSource used to be an ENUM ("ModActor" / "Manifest" / "None") describing only
/// where the URL came from; it is now the resolved source object. A registry written by the older
/// build therefore contains a bare string where an object is now expected.
///
/// Without this, deserialising one of those files throws - and ModRegistryService catches that and
/// starts with an empty list, so the user silently loses every mod the manager was tracking. The
/// mod FILES are untouched (nothing here deletes anything), but the list goes blank and each mod
/// has to be re-imported, which looks exactly like the app having eaten them.
///
/// Returning null is the correct migration, not a workaround: the old value recorded only the
/// declaration KIND, and the owner, repo and version the new type needs were never in that file.
/// A null source means "not known yet", and the next update check re-reads it from the mod on
/// disk - which is where it came from in the first place.
public class ModUpdateSourceJsonConverter : JsonConverter<ModUpdateSource?>
{
    public override ModUpdateSource? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;

            // The old enum, written either as a name or as an ordinal. Skip it and move on.
            case JsonTokenType.String:
            case JsonTokenType.Number:
                reader.Skip();
                return null;

            case JsonTokenType.StartObject:
                // Deserialise without this converter, or it would recurse into itself.
                var plain = new JsonSerializerOptions(options);
                plain.Converters.Remove(plain.Converters.FirstOrDefault(c => c is ModUpdateSourceJsonConverter)!);
                return JsonSerializer.Deserialize<ModUpdateSource>(ref reader, plain);

            default:
                reader.Skip();
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, ModUpdateSource? value, JsonSerializerOptions options)
    {
        if (value == null)
        {
            writer.WriteNullValue();
            return;
        }

        var plain = new JsonSerializerOptions(options);
        plain.Converters.Remove(plain.Converters.FirstOrDefault(c => c is ModUpdateSourceJsonConverter)!);
        JsonSerializer.Serialize(writer, value, plain);
    }
}
