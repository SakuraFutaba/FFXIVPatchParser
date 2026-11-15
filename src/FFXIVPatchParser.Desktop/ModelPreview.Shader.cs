using System.Text;
using System.Windows;

namespace FFXIVPatchParser.Desktop;

public partial class ModelPreview
{
    private CancellationTokenSource? shaderRequest;

    private void ClearMaterialShader()
    {
        shaderRequest?.Cancel();
        ShaderInspector.IsExpanded = false;
        ShaderInspector.Visibility = Visibility.Collapsed;
        ShaderDetails.Clear();
    }

    private async void ShowMaterialShader(object sender, RoutedEventArgs e)
    {
        if (e.Source != ShaderInspector || scene is not { } preview || source is not { } game ||
            MaterialChoice.SelectedItem is not MaterialChoiceItem { Index: >= 0 } choice) return;
        var material = preview.Materials[choice.Index].Info;
        if (material.ShaderPackage is not { Length: > 0 } shader) return;

        shaderRequest?.Cancel();
        using var request = new CancellationTokenSource();
        shaderRequest = request;
        ShaderDetails.Text = "正在读取着色器包…";
        try
        {
            var package = await Task.Run(() => game.ReadShaderPackage(shader, request.Token), request.Token);
            request.Token.ThrowIfCancellationRequested();
            ShaderDetails.Text = DescribeShader(package, material);
        }
        catch (OperationCanceledException) when (request.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!request.IsCancellationRequested) ShaderDetails.Text = "无法读取着色器包：" + ex.Message;
        }
        finally
        {
            if (shaderRequest == request) shaderRequest = null;
        }
    }

    private static string DescribeShader(GameShaderPackage package, GameModelMaterial material)
    {
        var lines = new StringBuilder()
            .AppendLine(package.Path)
            .AppendLine($"DX{package.DirectXVersion} · SHPK 0x{package.Version:X} · {package.NodeCount:N0} 个变体节点")
            .AppendLine($"{package.VertexShaderCount:N0} 个顶点程序 · {package.PixelShaderCount:N0} 个像素程序");
        var alpha = material.AlphaMode switch
        {
            GameMaterialAlphaMode.Mask => $"Alpha Clip · 阈值 {material.AlphaCutoff:0.###}",
            GameMaterialAlphaMode.Blend => $"Alpha Blend · 阈值 {material.AlphaCutoff:0.###}",
            _ => "按不透明材质预览"
        };
        lines.AppendLine(alpha).AppendLine($"MTRL 标志 0x{material.ShaderFlags:X8}");
        foreach (var key in package.MaterialKeys)
        {
            var hasOverride = material.ShaderKeys.TryGetValue(key.Id, out var value);
            lines.AppendLine($"0x{key.Id:X8} = 0x{(hasOverride ? value : key.DefaultValue):X8} · {(hasOverride ? "材质覆盖" : "包默认")}");
        }
        foreach (var sampler in package.Samplers)
            lines.AppendLine($"采样器 {sampler.Name} · 槽 {sampler.Slot}");
        return lines.ToString().TrimEnd();
    }
}
