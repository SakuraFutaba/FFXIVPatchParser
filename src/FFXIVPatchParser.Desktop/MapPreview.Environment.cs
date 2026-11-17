using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;

namespace FFXIVPatchParser.Desktop;

public partial class MapPreview
{
    private Dictionary<uint, GameSkyTexture> skyTextures = [];

    private sealed record WeatherOption(GameTerritoryWeather Weather, GameEnvironmentWeather? Timeline, BitmapSource? Icon)
    {
        public string Label => $"{(Weather.Name.Length > 0 ? Weather.Name : "未命名天气")} · {Weather.Id}"
            + (Timeline is null ? "（无环境轨道）" : "") + (Weather.HasFlag ? $" [0x{Weather.RawValue:X4}]" : "");
    }

    private static Dictionary<uint, BitmapSource> ReadWeatherIcons(LocalGameSource source, GameTerritoryEnvironment environment,
        CancellationToken token)
    {
        var icons = new Dictionary<uint, BitmapSource>();
        foreach (var weather in environment.Weathers.DistinctBy(weather => weather.IconId))
        {
            token.ThrowIfCancellationRequested();
            if (weather.IconPath is not { } path) continue;
            try { icons.Add(weather.IconId, ReadMapImage(source, path, token)); }
            catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException or NotSupportedException or OverflowException) { }
        }
        return icons;
    }

    private static Dictionary<uint, GameSkyTexture> ReadSkyTextures(LocalGameSource source, GameEnvironmentTimeline timeline,
        CancellationToken token)
    {
        var result = new Dictionary<uint, GameSkyTexture>();
        foreach (var id in timeline.Weathers.Select(weather => weather.CandidateSkyTextureId).OfType<uint>().Distinct())
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (source.ReadExperimentalSkyTexture(id, token) is { } texture) result.Add(id, texture);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException or ArgumentException) { }
        }
        return result;
    }

    private void SetEnvironment(GameTerritoryEnvironment environment, GameEnvironmentTimeline? timeline,
        Dictionary<uint, GameSkyTexture> textures, Dictionary<uint, BitmapSource> icons)
    {
        var options = environment.Weathers.Select(w => new WeatherOption(w, w.HasFlag ? null :
            timeline?.Weathers.FirstOrDefault(t => t.Id == w.Id), icons.GetValueOrDefault(w.IconId))).ToArray();
        skyTextures = textures;
        ExperimentalSky.IsEnabled = textures.Count > 0;
        EnvironmentControls.Visibility = options.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        WeatherChoice.ItemsSource = options;
        WeatherChoice.SelectedItem = options.FirstOrDefault(w => w.Timeline is not null) ?? options.FirstOrDefault();
    }

    private void ClearEnvironment()
    {
        if (WeatherChoice == null) return;
        skyTextures.Clear();
        WeatherChoice.ItemsSource = null;
        EnvironmentControls.Visibility = Visibility.Collapsed;
        MapSurface.Background = new SolidColorBrush(Color.FromRgb(30, 41, 56));
        EnvironmentSunlight.Color = Color.FromRgb(215, 216, 210);
        EnvironmentSkyViewport.Visibility = Visibility.Collapsed;
    }

    private void EnvironmentSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PreviousWeatherButton == null || NextWeatherButton == null) return;
        var index = WeatherChoice.SelectedIndex;
        PreviousWeatherButton.IsEnabled = index > 0;
        NextWeatherButton.IsEnabled = index >= 0 && index < WeatherChoice.Items.Count - 1;
        ApplyEnvironment();
    }

    private void PreviousWeather(object sender, RoutedEventArgs e)
    {
        if (WeatherChoice.SelectedIndex > 0) WeatherChoice.SelectedIndex--;
    }

    private void NextWeather(object sender, RoutedEventArgs e)
    {
        if (WeatherChoice.SelectedIndex >= 0 && WeatherChoice.SelectedIndex < WeatherChoice.Items.Count - 1)
            WeatherChoice.SelectedIndex++;
    }

    private void EnvironmentTimeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => ApplyEnvironment();
    private void ShowExperimentalSkyChanged(object sender, RoutedEventArgs e) => ApplyEnvironment();

    private void OpenWeatherTableOnRightClick(object sender, MouseButtonEventArgs e)
    {
        var item = sender as ComboBoxItem;
        if (item == null && e.OriginalSource is DependencyObject origin)
            item = ItemsControl.ContainerFromElement(WeatherChoice, origin) as ComboBoxItem;
        e.Handled = OpenWeatherTable(item?.Content as WeatherOption ?? WeatherChoice.SelectedItem as WeatherOption);
    }

    private void WeatherChoiceKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Apps || e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift)
            e.Handled = OpenWeatherTable(WeatherChoice.SelectedItem as WeatherOption);
    }

    private bool OpenWeatherTable(WeatherOption? option)
    {
        if (option == null) return false;
        WeatherChoice.IsDropDownOpen = false;
        RelatedTableRequested?.Invoke(this, new ModelTableLinkEventArgs("Weather", option.Weather.Id));
        return true;
    }

    private void ApplyEnvironment()
    {
        if (EnvironmentTime == null || EnvironmentTimeLabel == null || EnvironmentSunlight == null) return;
        var hour = EnvironmentTime.Value;
        EnvironmentTimeLabel.Text = $"时间 {((int)hour % 24):D2}:{(int)((hour % 1) * 60):D2}";
        if (WeatherChoice.SelectedItem is not WeatherOption option) return;
        if (option.Timeline is not { } timeline)
        {
            MapSurface.Background = new SolidColorBrush(Color.FromRgb(30, 41, 56));
            EnvironmentSunlight.Color = Color.FromRgb(215, 216, 210);
            EnvironmentSkyViewport.Visibility = Visibility.Collapsed;
            EnvironmentHint.Text = "所选天气没有可用的环境轨道，显示默认场景光照。";
            EnvironmentHint.ToolTip = null;
            return;
        }
        static Color ConvertColor(System.Numerics.Vector3 v) => Color.FromRgb(
            (byte)Math.Clamp(v.X * 255, 0, 255), (byte)Math.Clamp(v.Y * 255, 0, 255), (byte)Math.Clamp(v.Z * 255, 0, 255));
        var seconds = (float)hour * 3600;
        EnvironmentSunlight.Color = timeline.Sunlight.Count > 0
            ? ConvertColor(LocalGameSource.SampleEnvironmentColor(timeline.Sunlight, seconds)) : Color.FromRgb(215, 216, 210);
        // 背景采用实际 ENVB 雾色；它表示远景雾底色，不替代天空体积纹理或云层。
        MapSurface.Background = timeline.Fog.Count > 0
            ? new SolidColorBrush(ConvertColor(LocalGameSource.SampleEnvironmentColor(timeline.Fog, seconds)))
            : new SolidColorBrush(Color.FromRgb(30, 41, 56));
        if (ExperimentalSky?.IsChecked == true && timeline.CandidateSkyTextureId is { } id &&
            skyTextures.TryGetValue(id, out var sky))
        {
            ShowEnvironmentSky(new ImageBrush(SampleSky(sky, seconds)));
            EnvironmentHint.Text = $"实验预览 · sky_{id:D3}.tex\n使用游戏纹理，编号对应关系和方向、时间、亮度仍采用近似。";
            EnvironmentHint.ToolTip = sky.Path;
            return;
        }
        var fog = ConvertColor(LocalGameSource.SampleEnvironmentColor(timeline.Fog, seconds));
        var cloud = timeline.Cloud.Count > 0
            ? ConvertColor(LocalGameSource.SampleEnvironmentColor(timeline.Cloud, seconds)) : fog;
        ShowEnvironmentSky(cloud, fog);
        EnvironmentHint.Text = "ENVB 渐变预览 · 日光、云色和雾色随天气与时间变化。";
        EnvironmentHint.ToolTip = null;
    }

    private static BitmapSource SampleSky(GameSkyTexture texture, float seconds)
    {
        const int width = 64, height = 64;
        var pixels = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            var elevation = MathF.Cos(MathF.PI * y / (height - 1));
            for (var x = 0; x < width; x++)
            {
                var direction = MathF.Cos(MathF.Tau * x / (width - 1));
                var color = texture.SampleExperimental(direction, elevation, seconds);
                var index = (y * width + x) * 4;
                pixels[index] = (byte)(255 * color.Z);
                pixels[index + 1] = (byte)(255 * color.Y);
                pixels[index + 2] = (byte)(255 * color.X);
                pixels[index + 3] = 255;
            }
        }
        var image = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        image.Freeze();
        return image;
    }

    private void ShowEnvironmentSky(Color cloud, Color fog)
    {
        // 云色与雾色组成可见的环境示意穹顶；此渐变不是游戏的 Sky 着色器。
        var brush = new LinearGradientBrush();
        brush.StartPoint = new Point(0, 0); brush.EndPoint = new Point(0, 1);
        brush.GradientStops.Add(new GradientStop(cloud, 0));
        brush.GradientStops.Add(new GradientStop(fog, 0.5));
        brush.GradientStops.Add(new GradientStop(fog, 1));
        ShowEnvironmentSky(brush);
    }

    private void ShowEnvironmentSky(Brush brush)
    {
        brush.Freeze();
        var material = new EmissiveMaterial(brush); material.Freeze();
        if (EnvironmentSkyVisual.Content is GeometryModel3D existing)
        {
            existing.Material = existing.BackMaterial = material;
        }
        else
        {
            const int rows = 24, columns = 48;
            var mesh = new MeshGeometry3D();
            for (var y = 0; y <= rows; y++)
            for (var x = 0; x <= columns; x++)
            {
                var latitude = Math.PI * y / rows; var longitude = Math.Tau * x / columns;
                mesh.Positions.Add(new Point3D(10 * Math.Sin(latitude) * Math.Cos(longitude),
                    10 * Math.Cos(latitude), 10 * Math.Sin(latitude) * Math.Sin(longitude)));
                mesh.TextureCoordinates.Add(new Point((double)x / columns, (double)y / rows));
            }
            for (var y = 0; y < rows; y++)
            for (var x = 0; x < columns; x++)
            {
                var a = y * (columns + 1) + x; var b = a + columns + 1;
                foreach (var index in new[] { a, b, a + 1, a + 1, b, b + 1 }) mesh.TriangleIndices.Add(index);
            }
            mesh.Freeze();
            EnvironmentSkyVisual.Content = new GeometryModel3D(mesh, material) { BackMaterial = material };
        }
        EnvironmentSkyViewport.Visibility = Visibility.Visible;
    }
}
