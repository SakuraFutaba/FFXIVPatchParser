using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media.Media3D;

namespace FFXIVPatchParser.Desktop;

public partial class MapPreview
{
    private StartPosition? startPosition;
    private StartPosition? capturedStartPosition;
    private Point3D? sceneEntrance;
    private Point3D? EffectiveStart => startPosition?.Position ??
        (sceneEntrance is { } entrance ? entrance + new Vector3D(0, 1.65, 0) : null);
    private static string StartPositionsPath => Path.Combine(Workspace.Temp, "ui", "map-start-positions.json");

    private void ShowStartPosition(uint territoryId)
    {
        StartPositionControls.Visibility = territoryId != 0 ? Visibility.Visible : Visibility.Collapsed;
        startPosition = null;
        capturedStartPosition = null;
        StartPositionStatus.Text = "";
        if (territoryId == 0) return;
        try
        {
            if (ReadStartPositions().TryGetValue(territoryId, out var saved) && saved != null && IsValidPosition(saved.Position))
                startPosition = saved;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            StartPositionStatus.Text = "无法读取预览起点：" + ex.Message;
        }
        StartPositionInput.Text = startPosition is { } savedStart ? FormatPosition(savedStart.Position) : "";
        ClearStartPositionButton.IsEnabled = startPosition != null;
    }

    private void UseCurrentPosition(object sender, RoutedEventArgs e)
    {
        var view = camera.CaptureView();
        var point = view.Position;
        capturedStartPosition = new(point.X, point.Y, point.Z, view.Yaw, view.Pitch, WritePivot(view.Pivot));
        StartPositionInput.Text = FormatPosition(point);
        StartPositionStatus.Text = "已取得当前镜头位置和朝向，请保存起点";
    }

    private void SaveStartPosition(object sender, RoutedEventArgs e)
    {
        if (!TryParsePosition(StartPositionInput.Text, out var point))
        {
            StartPositionStatus.Text = "请输入三个有效数字，例如 100, 5, 100。";
            return;
        }
        var preset = capturedStartPosition ?? startPosition;
        if (preset == null)
        {
            UpdateStartPosition(new(point.X, point.Y, point.Z));
            return;
        }
        // 坐标框只显示三位小数。没有改动的分量保留原始精度。
        if (IsValidPosition(preset.Position) && TryParsePosition(FormatPosition(preset.Position), out var displayed))
            point = new(point.X == displayed.X ? preset.X : point.X, point.Y == displayed.Y ? preset.Y : point.Y,
                point.Z == displayed.Z ? preset.Z : point.Z);
        var pivot = ReadPivot(preset.Pivot);
        if (pivot is { } center) pivot = center + (point - preset.Position);
        UpdateStartPosition(preset with { X = point.X, Y = point.Y, Z = point.Z, Pivot = WritePivot(pivot) });
    }

    private void ClearStartPosition(object sender, RoutedEventArgs e) => UpdateStartPosition(null);

    private void GoToStart(object sender, RoutedEventArgs e)
    {
        if (ApplyStartPosition()) MapSurface.Focus();
    }

    private bool ApplyStartPosition()
    {
        if (EffectiveStart is not { } point || sceneBounds.IsEmpty) return false;
        if (startPosition is { Yaw: { } yaw, Pitch: { } pitch } && double.IsFinite(yaw) && double.IsFinite(pitch))
            camera.RestoreView(new(point, yaw, pitch, ReadPivot(startPosition.Pivot)));
        else camera.GoTo(point);
        return true;
    }

    private void SetSceneEntrance(Point3D? coordinate)
    {
        sceneEntrance = coordinate;
        SceneEntranceHint.Text = coordinate is { } entrance ? "出生圈  " + FormatPosition(entrance) : "";
        SceneEntranceHint.Visibility = coordinate != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateStartButton();
    }

    private void UpdateStartButton() => GoToStartButton.Visibility = !sceneBounds.IsEmpty && EffectiveStart != null
        ? Visibility.Visible : Visibility.Collapsed;

    private void UpdateStartPosition(StartPosition? preset)
    {
        if (selectedMap is not { } map) return;
        try
        {
            var entries = ReadStartPositions();
            if (preset != null) entries[map.TerritoryId] = preset;
            else entries.Remove(map.TerritoryId);
            WriteStartPositions(entries);
            startPosition = preset;
            capturedStartPosition = null;
            suspendedCamera = null;
            StartPositionInput.Text = preset != null ? FormatPosition(preset.Position) : "";
            ClearStartPositionButton.IsEnabled = preset != null;
            StartPositionStatus.Text = preset != null ? "已保存此区域的预览起点"
                : sceneEntrance != null ? "已恢复出生圈起点" : "已恢复自动视角";
            if (!ApplyStartPosition()) camera.Reset();
            UpdateStartButton();
            if (MapSurface.IsVisible && !sceneBounds.IsEmpty) MapSurface.Focus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            StartPositionStatus.Text = "无法保存预览起点：" + ex.Message;
        }
    }

    private static Dictionary<uint, StartPosition> ReadStartPositions()
    {
        var path = StartPositionsPath;
        return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<uint, StartPosition>>(File.ReadAllText(path), Workspace.Json) ?? [] : [];
    }

    private static void WriteStartPositions(Dictionary<uint, StartPosition> entries)
    {
        var path = StartPositionsPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(entries, Workspace.Json));
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static Point3D? ReadPivot(JsonElement? pivot)
    {
        if (pivot is not { ValueKind: JsonValueKind.Object } value || !TryReadCoordinate(value, "x", out var x) ||
            !TryReadCoordinate(value, "y", out var y) || !TryReadCoordinate(value, "z", out var z)) return null;
        var point = new Point3D(x, y, z);
        return IsValidPosition(point) ? point : null;
    }

    private static bool TryReadCoordinate(JsonElement point, string axis, out double coordinate)
    {
        coordinate = default;
        return point.TryGetProperty(axis, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out coordinate);
    }

    private static JsonElement? WritePivot(Point3D? pivot) =>
        pivot is { } point && IsValidPosition(point) ? JsonSerializer.SerializeToElement(point, Workspace.Json) : null;

    private static bool TryParsePosition(string text, out Point3D point)
    {
        point = default;
        var values = text.Trim().Trim('(', ')').Split([',', '，', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (values.Length != 3 || !double.TryParse(values[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ||
            !double.TryParse(values[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y) ||
            !double.TryParse(values[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var z)) return false;
        point = new Point3D(x, y, z);
        return IsValidPosition(point);
    }

    private static bool IsValidPosition(Point3D point) =>
        Math.Abs(point.X) <= 1_000_000 && Math.Abs(point.Y) <= 1_000_000 && Math.Abs(point.Z) <= 1_000_000;

    private static string FormatPosition(Point3D point) => FormattableString.Invariant($"{point.X:0.###}, {point.Y:0.###}, {point.Z:0.###}");

    private sealed record StartPosition(double X, double Y, double Z,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Yaw = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Pitch = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Pivot = null)
    {
        [JsonIgnore]
        public Point3D Position => new(X, Y, Z);
    }
}
