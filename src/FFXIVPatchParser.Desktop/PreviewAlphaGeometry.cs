using System.Numerics;

namespace FFXIVPatchParser.Desktop;

/// <summary>把遮罩孔洞转换成几何边界，避免 WPF 的透明像素仍占用深度。</summary>
internal static class PreviewAlphaGeometry
{
    public static GameModelMesh Clip(GameModelMesh mesh, GameTexture texture, byte cutoff, CancellationToken token)
    {
        var result = ClipAtResolution(mesh, texture, cutoff, 16, token);
        // 窄小叶片可能落在粗网格的空格里，保留一次更细的回退。
        return result.TriangleIndices.Length == 0 ? ClipAtResolution(mesh, texture, cutoff, 64, token) : result;
    }

    internal readonly record struct VertexSource(int A, int B, int C, Vector3 Weights)
    {
        public Vector3 Position(Vector3[] positions) => positions[A] * Weights.X + positions[B] * Weights.Y + positions[C] * Weights.Z;
    }

    internal static (GameModelMesh Mesh, VertexSource[] Sources) ClipAnimated(GameModelMesh mesh, GameTexture texture, byte cutoff, CancellationToken token)
    {
        var sources = new List<VertexSource>();
        var clipped = ClipAtResolution(mesh, texture, cutoff, 64, token, sources);
        return (clipped, sources.ToArray());
    }

    private static GameModelMesh ClipAtResolution(GameModelMesh mesh, GameTexture texture, byte cutoff, int size, CancellationToken token,
        List<VertexSource>? sources = null)
    {
        // 场景预览使用覆盖率近似：过半 texel 通过阈值才保留该格。
        // 这是几何轮廓精度的取舍；原始纹理和模型不变。
        var width = Math.Min(texture.Width, size); var height = Math.Min(texture.Height, size);
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            token.ThrowIfCancellationRequested();
            var solid = 0; var total = 0;
            for (var py = y * texture.Height / height; py < (y + 1) * texture.Height / height; py++)
            for (var px = x * texture.Width / width; px < (x + 1) * texture.Width / width; px++)
            {
                var alpha = texture.Bgra32[(py * texture.Width + px) * 4 + 3];
                if (alpha > 0 && alpha >= cutoff) solid++;
                total++;
            }
            pixels[(y * width + x) * 4 + 3] = solid * 2 >= total ? (byte)255 : (byte)0;
        }
        var masks = BuildMask(new GameTexture(texture.Path, width, height, pixels), 128, token);
        var positions = new List<Vector3>();
        var coordinates = new List<Vector2>();
        var indices = new List<int>();
        var vertices = new Dictionary<Vertex, int>();
        var polygon = new List<Vertex>(8);
        var scratch = new List<Vertex>(8);
        for (var i = 0; i < mesh.TriangleIndices.Length; i += 3)
        {
            token.ThrowIfCancellationRequested();
            var ia = mesh.TriangleIndices[i]; var ib = mesh.TriangleIndices[i + 1]; var ic = mesh.TriangleIndices[i + 2];
            var a = VertexAt(ia, new(ia, ib, ic, Vector3.UnitX));
            var b = VertexAt(ib, new(ia, ib, ic, Vector3.UnitY));
            var c = VertexAt(ic, new(ia, ib, ic, Vector3.UnitZ));
            var min = Vector2.Min(a.Uv, Vector2.Min(b.Uv, c.Uv));
            var max = Vector2.Max(a.Uv, Vector2.Max(b.Uv, c.Uv));
            if (!float.IsFinite(min.X + min.Y + max.X + max.Y)) continue;
            // 异常 UV 不展开成无限个重复贴图。
            if (max.X - min.X > 16 || max.Y - min.Y > 16 || Math.Abs(min.X) > 1_000_000 || Math.Abs(min.Y) > 1_000_000) continue;
            if (sources != null && IsSolidRegion(min, max))
            {
                Add(a); Add(b); Add(c);
                continue;
            }
            for (var ty = (int)MathF.Floor(min.Y); ty <= (int)MathF.Floor(max.Y); ty++)
            for (var tx = (int)MathF.Floor(min.X); tx <= (int)MathF.Floor(max.X); tx++)
            foreach (var mask in masks)
            {
                var offset = new Vector2(tx, ty);
                if (mask.Max.X + tx < min.X || mask.Min.X + tx > max.X || mask.Max.Y + ty < min.Y || mask.Min.Y + ty > max.Y) continue;
                polygon.Clear(); polygon.Add(a); polygon.Add(b); polygon.Add(c);
                Cut(mask.A + offset, mask.B + offset);
                Cut(mask.B + offset, mask.C + offset);
                Cut(mask.C + offset, mask.A + offset);
                if (polygon.Count < 3) continue;
                for (var j = 1; j + 1 < polygon.Count; j++)
                {
                    var v0 = polygon[0]; var v1 = polygon[j]; var v2 = polygon[j + 1];
                    if (Vector3.Cross(v1.Position - v0.Position, v2.Position - v0.Position).LengthSquared() < 1e-16f) continue;
                    Add(v0); Add(v1); Add(v2);
                }
            }
        }
        return new GameModelMesh(positions.ToArray(), indices.ToArray())
        {
            TextureCoordinates = coordinates.ToArray(), MaterialIndex = mesh.MaterialIndex
        };

        bool IsSolidRegion(Vector2 min, Vector2 max)
        {
            var left = (int)MathF.Floor(min.X * width); var right = (int)MathF.Floor(max.X * width);
            var top = (int)MathF.Floor(min.Y * height); var bottom = (int)MathF.Floor(max.Y * height);
            if ((long)(right - left + 1) * (bottom - top + 1) > 4096) return false;
            for (var y = top; y <= bottom; y++)
            for (var x = left; x <= right; x++)
                if (pixels[(((y % height + height) % height) * width + (x % width + width) % width) * 4 + 3] == 0)
                    return false;
            return true;
        }

        Vertex VertexAt(int index, VertexSource source) => new(mesh.Positions[index], mesh.TextureCoordinates[index], sources == null ? default : source);
        void Add(Vertex vertex)
        {
            if (sources != null)
            {
                // 未被边界切开的原顶点仍跨三角形复用，避免蒙皮与 WPF 顶点量成倍增加。
                var source = vertex.Source;
                var sourceIndex = source.Weights == Vector3.UnitX ? source.A
                    : source.Weights == Vector3.UnitY ? source.B
                    : source.Weights == Vector3.UnitZ ? source.C : -1;
                if (sourceIndex >= 0) vertex = vertex with { Source = new(sourceIndex, sourceIndex, sourceIndex, Vector3.UnitX) };
            }
            if (!vertices.TryGetValue(vertex, out var index))
            {
                index = positions.Count;
                vertices.Add(vertex, index); positions.Add(vertex.Position); coordinates.Add(vertex.Uv);
                sources?.Add(vertex.Source);
            }
            indices.Add(index);
        }
        void Cut(Vector2 edgeStart, Vector2 edgeEnd)
        {
            scratch.Clear();
            if (polygon.Count == 0) return;
            var from = polygon[^1];
            var fromDistance = Cross(edgeEnd - edgeStart, from.Uv - edgeStart);
            foreach (var to in polygon)
            {
                var toDistance = Cross(edgeEnd - edgeStart, to.Uv - edgeStart);
                if ((fromDistance >= 0) != (toDistance >= 0))
                {
                    var t = fromDistance / (fromDistance - toDistance);
                    scratch.Add(new(Vector3.Lerp(from.Position, to.Position, t), Vector2.Lerp(from.Uv, to.Uv, t),
                        from.Source with { Weights = Vector3.Lerp(from.Source.Weights, to.Source.Weights, t) }));
                }
                if (toDistance >= 0) scratch.Add(to);
                from = to; fromDistance = toDistance;
            }
            (polygon, scratch) = (scratch, polygon);
        }
    }

    private static List<MaskTriangle> BuildMask(GameTexture texture, byte cutoff, CancellationToken token)
    {
        // 原始 texel 的连续区间串成无分叉条带；孔洞和分叉处另起条带。
        // 因而每条轮廓都是简单多边形，不需要处理带孔多边形的桥接。
        var result = new List<MaskTriangle>();
        var previous = new List<Strip>();
        for (var y = 0; y <= texture.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            var runs = new List<(int Left, int Right)>();
            if (y < texture.Height)
            for (var x = 0; x < texture.Width;)
            {
                if (!Solid(x, y)) { x++; continue; }
                var left = x++;
                while (x < texture.Width && Solid(x, y)) x++;
                runs.Add((left, x));
            }
            var next = new List<Strip>();
            var continued = new HashSet<Strip>();
            foreach (var run in runs)
            {
                var matches = previous.Where(s => Overlap(run, s.Rows[^1])).ToArray();
                var strip = matches.Length == 1 && runs.Count(r => Overlap(r, matches[0].Rows[^1])) == 1
                    ? matches[0] : new Strip(y);
                continued.Add(strip);
                strip.Rows.Add(run);
                next.Add(strip);
            }
            foreach (var strip in previous)
                if (!continued.Contains(strip)) Finish(strip);
            previous = next;
        }
        return result;

        bool Solid(int x, int y)
        {
            var alpha = texture.Bgra32[(y * texture.Width + x) * 4 + 3];
            return alpha > 0 && alpha >= cutoff;
        }
        static bool Overlap((int Left, int Right) a, (int Left, int Right) b) => a.Left < b.Right && b.Left < a.Right;
        void Finish(Strip strip)
        {
            var contour = new List<Vector2>();
            for (var row = 0; row < strip.Rows.Count; row++)
            {
                contour.Add(new(strip.Rows[row].Left, strip.Top + row));
                contour.Add(new(strip.Rows[row].Left, strip.Top + row + 1));
            }
            for (var row = strip.Rows.Count - 1; row >= 0; row--)
            {
                contour.Add(new(strip.Rows[row].Right, strip.Top + row + 1));
                contour.Add(new(strip.Rows[row].Right, strip.Top + row));
            }
            contour.Reverse();
            RemoveCollinearPoints(contour);
            while (contour.Count >= 3)
            {
                var found = false;
                for (var i = 0; i < contour.Count; i++)
                {
                    if (!IsEar(contour, i)) continue;
                    var a = contour[(i + contour.Count - 1) % contour.Count];
                    var b = contour[i]; var c = contour[(i + 1) % contour.Count];
                    var scale = new Vector2(texture.Width, texture.Height);
                    result.Add(new(a / scale, b / scale, c / scale));
                    contour.RemoveAt(i); found = true; break;
                }
                if (!found) break;
            }
        }
    }

    private static void RemoveCollinearPoints(List<Vector2> polygon)
    {
        var changed = true;
        while (changed && polygon.Count > 3)
        {
            changed = false;
            for (var i = polygon.Count - 1; i >= 0 && polygon.Count > 3; i--)
            {
                var a = polygon[(i + polygon.Count - 1) % polygon.Count];
                var b = polygon[i]; var c = polygon[(i + 1) % polygon.Count];
                if (Cross(b - a, c - b) == 0)
                { polygon.RemoveAt(i); changed = true; }
            }
        }
    }

    private static bool IsEar(List<Vector2> polygon, int index)
    {
        var before = (index + polygon.Count - 1) % polygon.Count;
        var after = (index + 1) % polygon.Count;
        var a = polygon[before]; var b = polygon[index]; var c = polygon[after];
        if (Cross(b - a, c - b) <= 0) return false;
        for (var i = 0; i < polygon.Count; i++)
        {
            if (i == before || i == index || i == after) continue;
            var p = polygon[i];
            if (Cross(b - a, p - a) >= 0 && Cross(c - b, p - b) >= 0 && Cross(a - c, p - c) >= 0) return false;
        }
        return true;
    }

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
    private sealed class Strip(int top)
    {
        public int Top { get; } = top;
        public List<(int Left, int Right)> Rows { get; } = [];
    }
    private readonly record struct MaskTriangle(Vector2 A, Vector2 B, Vector2 C)
    {
        public Vector2 Min => Vector2.Min(A, Vector2.Min(B, C));
        public Vector2 Max => Vector2.Max(A, Vector2.Max(B, C));
    }
    private readonly record struct Vertex(Vector3 Position, Vector2 Uv, VertexSource Source);
}


