using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace FFXIVPatchParser;

public sealed partial class LocalGameSource
{
    private (string Bone, Matrix4x4 Offset)? ReadWeaponAttachment(string code, string path, bool sheathed)
    {
        var weapon = WeaponPartPath.Match(path);
        if (!weapon.Success || !int.TryParse(weapon.Groups[1].Value.AsSpan(1), out var id)) return null;
        byte[]? types;
        byte[]? points;
        lock (gate)
        {
            types = game.GetFile("chara/xls/weapontype/attach.wtd")?.Data;
            points = game.GetFile($"chara/xls/attachoffset/{code}.atch")?.Data;
        }
        if (types == null || types.Length < 4 || points == null || points.Length < 4) return null;
        static int U16(byte[] data, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
        static uint U32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
        var count = U16(types, 2);
        if (U16(types, 0) != 1 || 4L + count * 8L > types.Length) return null;
        uint type = 0;
        for (var i = 0; i < count; i++)
        {
            var index = 4 + i * 8;
            if (U32(types, index) > id) break;
            type = U32(types, index + 4);
        }
        var rows = U16(points, 0);
        var states = U16(points, 2);
        // ATCH 在类型表之后保存 256 位标记，再按类型和状态排列 32 字节的挂点。
        var records = 4 + rows * 4 + 32;
        var strings = records + (long)rows * states * 32;
        if (states != 6 || strings >= points.Length) return null;
        for (var row = 0; row < rows; row++)
        {
            if (U32(points, 4 + row * 4) != type) continue;
            var entry = records + (row * states + (sheathed ? 1 : 0)) * 32;
            var name = U32(points, entry);
            if (name < strings || name >= points.Length) return null;
            var end = Array.IndexOf(points, (byte)0, (int)name);
            if (end <= name) return null;
            float Value(int i) => BinaryPrimitives.ReadSingleLittleEndian(points.AsSpan(entry + 4 + i * 4));
            var scale = Value(0);
            var translation = new Vector3(Value(1), Value(2), Value(3));
            var rotation = new Vector3(Value(4), Value(5), Value(6));
            var matrix = Matrix4x4.CreateScale(scale) * Matrix4x4.CreateRotationX(rotation.X)
                * Matrix4x4.CreateRotationY(rotation.Y) * Matrix4x4.CreateRotationZ(rotation.Z)
                * Matrix4x4.CreateTranslation(translation);
            return (Encoding.ASCII.GetString(points, (int)name, end - (int)name), matrix);
        }
        return null;
    }
}
