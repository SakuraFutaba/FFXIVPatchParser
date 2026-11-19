using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FFXIVPatchParser;

public sealed record OutfitTemplate(string Name, IReadOnlyList<GlamourDesignSlot> Slots, bool HasFacewear)
{
    public override string ToString() => Name;

    public static IReadOnlyList<OutfitTemplate> ReadFile(string path)
    {
        using var input = File.OpenRead(path);
        if (input.Length > 16 * 1024 * 1024) throw new InvalidDataException("模板文件超过 16 MiB。");
        using var reader = new StreamReader(input, new UTF8Encoding(false, true));
        var text = reader.ReadToEnd().Trim();
        if (text.StartsWith("HGG", StringComparison.Ordinal)) return [ReadCode(text)];
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (!root.TryGetProperty("format", out var format))
            return [new("Glamourer 设计", GlamourDesign.Read(path), false)];
        if (format.GetString() != "HGG-templates" || root.GetProperty("version").GetInt32() != 1)
            throw new InvalidDataException("不支持此模板文件版本。");
        var codes = root.GetProperty("codes");
        if (codes.GetArrayLength() is < 1 or > 10000) throw new InvalidDataException("模板数量无效。");
        return codes.EnumerateArray().Select(c => ReadCode(c.GetString() ?? "")).ToArray();
    }

    public static OutfitTemplate ReadCode(string text)
    {
        if (text.Length > 4096) throw new InvalidDataException("模板码超过 4096 字符。");
        var parts = string.Concat(text.Where(c => !char.IsWhiteSpace(c))).Split('.');
        if (parts.Length != 3 || parts[0] is not ("HGG1" or "HGG2" or "HGG3"))
            throw new InvalidDataException("模板码版本或格式无效。");
        var version = parts[0][3] - '0';
        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        var raw = Convert.FromBase64String(payload.PadRight((payload.Length + 3) / 4 * 4, '='));
        if (Convert.ToHexStringLower(SHA256.HashData(raw))[..16] != parts[2])
            throw new InvalidDataException("模板码校验失败，请完整复制。");
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;
        if (root.GetProperty("v").GetInt32() != version || root.GetProperty("region").GetString() != "CN" ||
            root.EnumerateObject().Count() != 3 + version)
            throw new InvalidDataException("模板版本或区服无效。");
        var name = root.GetProperty("name").GetString()?.Trim() ?? "";
        if (name.Length == 0 || name.EnumerateRunes().Count() > 32 || name.Any(c => c < 32))
            throw new InvalidDataException("模板名称无效。");
        List<GlamourDesignSlot> slots = [];
        ReadSlots(root.GetProperty("gear"), ["met","top","glv","dwn","sho","ear","nek","wrs","rir","ril"]);
        var facewear = false;
        if (version >= 2)
        {
            var faces = root.GetProperty("facewear");
            if (faces.GetArrayLength() != 2) throw new InvalidDataException("面饰槽位无效。");
            foreach (var face in faces.EnumerateArray()) facewear |= face.GetUInt16() != 0;
        }
        if (version == 3) ReadSlots(root.GetProperty("weapons"), ["weapon","offhand"]);
        return new(name, slots, facewear);

        void ReadSlots(JsonElement gear, string[] keys)
        {
            if (gear.GetArrayLength() != keys.Length) throw new InvalidDataException("模板部位数无效。");
            for (var index = 0; index < keys.Length; index++)
            {
                var fields = gear[index];
                if (fields.GetArrayLength() != 3) throw new InvalidDataException("装备字段数无效。");
                var item = fields[0].GetUInt32();
                var first = fields[1].GetByte();
                var second = fields[2].GetByte();
                if (item > 1000000 || first == 255 || second == 255 || item == 0 && (first | second) != 0)
                    throw new InvalidDataException("装备或染色编号无效。");
                slots.Add(new(keys[index], item, true, true, first, second));
            }
        }
    }
}
