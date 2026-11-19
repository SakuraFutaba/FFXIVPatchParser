using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace FFXIVPatchParser;

public sealed partial class LocalGameSource
{
    private sealed record PapTimelineReference(string Scope, string Folder, uint Timeline, IReadOnlyList<GameAnimationReference> References)
    {
        public bool Matches(string kind, string code, string key, string? set)
        {
            if (Scope != kind && Scope != code) return false;
            if (kind == "facial") return Scope == kind;
            if (kind == "weapon") return key == "resident/weapon" || key.StartsWith("body/", StringComparison.Ordinal);
            if (Folder.Length != 0 && key.StartsWith(Folder, StringComparison.Ordinal)) return true;
            // The shared human magic bundle and per-monster bundles do not use the timeline's own directory.
            return (kind == "human" && Folder.StartsWith("magic/", StringComparison.Ordinal) && set == "bt_common" && key == "resident/action")
                || (kind == "monster" && Scope == code && key == "resident/monster");
        }
    }
    private static readonly Regex SpecificTimelineActor = new(@"(?:^|/)(?<body>[cdmw]\d{4})(?=[/_]|$)", RegexOptions.CultureInvariant);
    private ILookup<string, PapTimelineReference>? papTimelineReferences;

    private ILookup<string, PapTimelineReference> GetPapTimelineReferences(AnimationNames index, CancellationToken token)
    {
        lock (gate)
        {
            if (papTimelineReferences != null) return papTimelineReferences;
            var entries = new List<(string, PapTimelineReference)>();
            foreach (var timeline in index.ActionKeys)
            {
                token.ThrowIfCancellationRequested();
                var rows = timeline.Select(id => (Id: id, Users: index.TimelineUsers[id].Where(r => r.Sheet is "Action" or "Emote").ToArray()))
                    .Where(r => r.Users.Length > 0).ToArray();
                if (rows.Length == 0) continue;
                var key = timeline.Key;
                // Namespace shared slot names by actor. Unknown families are not matched on slot name alone.
                var actor = SpecificTimelineActor.Match(key);
                var slash = key.IndexOf('/');
                var family = slash < 0 ? key : key[..slash];
                var folder = key[..(key.LastIndexOf('/') + 1)];
                var scope = actor.Success ? actor.Groups["body"].Value : family switch
                {
                    "magic" or "emote" or "ws" or "ability" or "limitbreak" or "rol_common" => "human",
                    "facial" => "facial",
                    _ => ""
                };
                foreach (var target in Read(key))
                {
                    if (!target.IsPath && scope.Length == 0) continue;
                    var lookupKey = (target.IsPath ? "path:" : "slot:") + target.Value;
                    foreach (var row in rows)
                    {
                        var references = new[] { new GameAnimationReference("ActionTimeline", row.Id, "时间线动作", key, target.Value) }
                            .Concat(row.Users.Select(r => r with { TimelineSlot = target.Value })).ToArray();
                        entries.Add((lookupKey, new(scope, folder, row.Id, references)));
                    }
                }
            }
            foreach (var weapon in index.OtherKeys.Where(p => p.Key.StartsWith("weapon:", StringComparison.Ordinal)))
            {
                token.ThrowIfCancellationRequested();
                foreach (var target in Read(weapon.Key[7..]))
                {
                    var lookupKey = (target.IsPath ? "path:" : "slot:") + target.Value;
                    foreach (var reference in weapon)
                        entries.Add((lookupKey, new("weapon", "", reference.RowId, [reference with { TimelineSlot = target.Value }])));
                }
            }
            return papTimelineReferences = entries.ToLookup(p => p.Item1, p => p.Item2, StringComparer.Ordinal);

            IReadOnlyList<(bool IsPath, string Value)> Read(string key)
            {
                try { return game.GetFile($"chara/action/{key}.tmb")?.Data is { } data ? ReadTmbPapTargets(data) : []; }
                catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException
                    or IndexOutOfRangeException or OverflowException) { return []; }
            }
        }
    }

    private static IReadOnlyList<(bool IsPath, string Value)> ReadTmbPapTargets(byte[] data)
    {
        if (data.Length is < 12 or > 4194304 || !data.AsSpan(0, 4).SequenceEqual("TMLB"u8)) return [];
        int Int(int at) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(at, 4));
        var count = Int(8);
        if (Int(4) != data.Length || count < 0 || count > (data.Length - 12) / 8) return [];
        var offsets = new List<long>();
        var cursor = 12;
        for (var n = 0; n < count; n++)
        {
            if (cursor > data.Length - 8) return [];
            var size = Int(cursor + 4);
            if (size < 8 || size > data.Length - cursor) return [];
            var tag = data.AsSpan(cursor, 4);
            var field = tag.SequenceEqual("C009"u8) && size >= 24 ? 20 :
                tag.SequenceEqual("C010"u8) && size >= 40 ? 32 : 0;
            if (field != 0) offsets.Add((long)cursor + 8 + Int(cursor + field));
            cursor += size;
        }
        var targets = new HashSet<(bool, string)>();
        foreach (var at in offsets)
        {
            if (at < cursor || at >= data.Length) continue;
            var bytes = data.AsSpan((int)at, Math.Min(257, data.Length - (int)at));
            var length = bytes.IndexOf((byte)0);
            if (length is < 1 or > 256) continue;
            var value = Encoding.ASCII.GetString(bytes[..length]).ToLowerInvariant();
            if (value.EndsWith(".pap", StringComparison.Ordinal) && NormalizeFileSystemPath(value) is { } path)
                targets.Add((true, path));
            else if (value.Length <= 32 && value.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                targets.Add((false, value));
        }
        return targets.ToArray();
    }
}
