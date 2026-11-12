using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Lumina.Excel;

namespace FFXIVPatchParser;

public sealed record NamedModelEntry(string Category, string Name, string SourceSheet, uint SourceRowId,
    uint ModelCharaId, int ModelType, int ModelId, int BodyId, int Variant, string Path, string NameSource,
    uint? NameRowId = null);

public sealed partial class LocalGameSource
{
    private IReadOnlyList<NamedModelEntry>? namedModels;
    // Verified against the exact CHS 2026.11.03.0000.0000 datamining schema and installed EXH.
    // InfSein/ffxiv-datamining-mixed commit 9e2c05c0ed8205d5397031152f62a34900355de4.
    // Hash covers every physical column's ID, type and offset, not just the fields used below.
    private static readonly IReadOnlyDictionary<string, string> NamedModelSchemas = new Dictionary<string, string>
    {
        ["ModelChara"] = "74DFEB27907B1E47C8A225B99EE7420EB5FD8B72B971F64A7313219D9AC62054",
        ["Companion"] = "60FAE1DFE0670A758C97140C927D22BAB43684CB2D96148FB91A3A6F20C2B7A7",
        ["Mount"] = "30ACD83F1712DCE7C7E29E8E3057D4B582E4C9214C93442F346288F6DD6B9443",
        ["Ornament"] = "34602100C2F018C8A862902176ABE0DEC89AC5909ECE1F0A5BDA03D3E4A97BFE",
        ["Pet"] = "4750E5BC0F2DCDCC78CCD848027D91198D970DA664380DACB959ED1379110C95",
        ["BNpcBase"] = "A83C5C3201F0905C8CE24CF6ABDE8954A19F8E5D7706349784D8B1D6DE1E0919",
        ["BNpcName"] = "B6574AAF82F7B118F5270EFEBBD6A1118B734776CFDC60A8DFA1EBC94CD508C3",
        ["ENpcBase"] = "1648F370E115EF42DEE3905E34E54EF3BA4AFB0EEBE6E123B56204CCE202B999",
        ["ENpcResident"] = "0A1539E3852CD0988F1A7AF9715C71A40E79D953BC9E70DEFDA10FF78DB5CE05"
    };

    /// <summary>Offline EXD joins and index lookups only; never scans DAT headers or downloads data.</summary>
    public Task<IReadOnlyList<NamedModelEntry>> ListNamedModelsAsync(CancellationToken token = default,
        IProgress<string>? progress = null, bool refresh = false) => Task.Run<IReadOnlyList<NamedModelEntry>>(() =>
    {
        lock (gate)
        {
            token.ThrowIfCancellationRequested();
            if (!refresh && namedModels is not null) return namedModels;
            Dictionary<uint, string[]> Read(string sheet, params int[] columns)
            {
                token.ThrowIfCancellationRequested();
                progress?.Report("正在读取本地模型目录 · " + sheet);
                var (header, language) = ReadSheetHeader(sheet);
                var signature = string.Join(";", header.ColumnDefinitions.Select((c, i) => $"{i}:{c.Type}:{c.Offset}"));
                if (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(signature))) != NamedModelSchemas[sheet])
                    throw new NotSupportedException($"{sheet} 的本地 EXH 与已验证模型定义不一致；请使用深度扫描或更新定义。");
                var rows = new Dictionary<uint, string[]>();
                foreach (var row in game.Excel.GetSheet<RawRow>(language, sheet))
                {
                    token.ThrowIfCancellationRequested();
                    rows.Add(row.RowId, columns.Select(c => Cell(row.ReadColumn(c)).Display).ToArray());
                }
                return rows;
            }
            static int Number(string value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : 0;
            var models = Read("ModelChara", 0, 1, 2, 3);
            var companion = Read("Companion", 0, 8);
            var mounts = Read("Mount", 0, 8);
            var ornaments = Read("Ornament", 8, 0);
            var pets = Read("Pet", 0);
            var battle = Read("BNpcBase", 5);
            var battleNames = Read("BNpcName", 0);
            var events = Read("ENpcBase", 35);
            var eventNames = Read("ENpcResident", 0);
            var hints = NpcNameHints.ReadCached();
            var result = new List<NamedModelEntry>();
            var referenced = new HashSet<uint>();
            var paths = new Dictionary<uint, string[]>();
            string[] Paths(uint modelRow, int type, int model, int body)
            {
                if (paths.TryGetValue(modelRow, out var cached)) return cached;
                IEnumerable<string> candidates = type switch
                {
                    3 => [$"chara/monster/m{model:D4}/obj/body/b{body:D4}/model/m{model:D4}b{body:D4}.mdl"],
                    2 => EquipmentSlots.Select(slot => $"chara/demihuman/d{model:D4}/obj/equipment/e{body:D4}/model/d{model:D4}e{body:D4}_{slot}.mdl"),
                    4 => [$"chara/weapon/w{model:D4}/obj/body/b{body:D4}/model/w{model:D4}b{body:D4}.mdl"],
                    _ => [] // Human NPCs need appearance/equipment assembly, not a fabricated single MDL.
                };
                return paths[modelRow] = candidates.Where(game.FileExists).ToArray();
            }
            void Add(string category, string name, string sourceSheet, uint sourceId, uint modelRow, string nameSource, uint? nameRowId = null)
            {
                token.ThrowIfCancellationRequested();
                if (modelRow == 0 || !models.TryGetValue(modelRow, out var row)) return;
                var type = Number(row[0]); var model = Number(row[1]); var body = Number(row[2]); var variant = Number(row[3]);
                foreach (var path in Paths(modelRow, type, model, body))
                {
                    result.Add(new(category, string.IsNullOrWhiteSpace(name) ? $"{sourceSheet} #{sourceId}" : name,
                        sourceSheet, sourceId, modelRow, type, model, body, variant, path, nameSource, nameRowId));
                    referenced.Add(modelRow);
                }
            }
            foreach (var (category, sheet, rows) in new[] { ("宠物", "Companion", companion), ("坐骑", "Mount", mounts), ("时尚配饰", "Ornament", ornaments) })
                foreach (var (id, cells) in rows) Add(category, cells[0], sheet, id, (uint)Number(cells[1]), sheet + ".Singular (本地CHS)");

            // TexTools Companions.GetUncachedPetList verifies these model/body identities.
            // Pet itself has NO ModelChara foreign key. Only verified identities are named here.
            // Keep material variants separate rather than assigning a color by guesswork.
            foreach (var (id, row) in models)
            {
                if (Number(row[0]) != 3) continue;
                uint petId = (Number(row[1]), Number(row[2])) switch
                {
                    (7001, 1) => 6, (7001, 2) => 7, (7002, 1) => 23,
                    (7003, 1) => 27, (7004, 1) => 28, (7005, 1) => 29,
                    (7101, 1) => 8, (7101, 2) => 9, (7102, 1) => 10, (7105, 1) => 15, _ => 0
                };
                if (petId != 0 && pets.TryGetValue(petId, out var pet) && pet[0].Length > 0)
                    Add("召唤兽", pet[0], "Pet", petId, id, "TexTools 已验证模型身份 → Pet.Name (本地CHS)");
            }
            foreach (var (id, row) in battle)
            {
                var name = ""; var provenance = "无可靠名字映射"; uint? nameRowId = null;
                if (hints.TryGetValue($"B:{id:D7}", out var hint))
                {
                    if (hint.StartsWith("N:", StringComparison.Ordinal) && uint.TryParse(hint.AsSpan(2), out var nameId) && battleNames.TryGetValue(nameId, out var named))
                    { name = named[0]; nameRowId = nameId; provenance = $"Anamnesis BNpcBase→BNpcName #{nameId} (本地CHS)"; }
                    else if (hint.StartsWith("E:", StringComparison.Ordinal) && uint.TryParse(hint.AsSpan(2), out var eventId) && eventNames.TryGetValue(eventId, out var eventName))
                    { name = eventName[0]; nameRowId = eventId; provenance = $"Anamnesis BNpcBase→ENpcResident #{eventId} (本地CHS)"; }
                    else if (!hint.Contains(':')) { name = hint; provenance = "Anamnesis 手工名字"; }
                }
                Add("战斗NPC", name, "BNpcBase", id, (uint)Number(row[0]), provenance, nameRowId);
            }
            foreach (var (id, row) in events)
                Add("事件NPC", eventNames.GetValueOrDefault(id)?[0] ?? "", "ENpcBase", id, (uint)Number(row[0]), "ENpcResident 同行ID (本地CHS)", eventNames.ContainsKey(id) ? id : null);
            foreach (var (id, row) in models)
                if (!referenced.Contains(id)) Add("其他模型", "", "ModelChara", id, id, "仅模型编号");
            namedModels = result.OrderBy(e => e.Category, StringComparer.Ordinal).ThenBy(e => e.Name, StringComparer.CurrentCulture)
                .ThenBy(e => e.ModelCharaId).ThenBy(e => e.Path, StringComparer.Ordinal).ToArray();
            return namedModels;
        }
    }, token);
}
