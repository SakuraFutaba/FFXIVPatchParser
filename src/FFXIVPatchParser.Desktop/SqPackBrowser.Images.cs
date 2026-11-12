using System.Windows;

namespace FFXIVPatchParser.Desktop;

public partial class SqPackBrowser
{
    private CancellationTokenSource? imageCatalogCancellation;
    private bool imageIndexScanned;

    private void LocalImagesVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (initialized) StartImageCatalogIfVisible();
    }

    private void StartImageCatalogIfVisible()
    {
        if (!closing && LocalImagesPanel.IsVisible && game != null && texturePaths == null &&
            imageCatalogCancellation is not { IsCancellationRequested: false }) _ = LoadImageCatalog();
    }

    private async void ReadIconCatalog(object sender, RoutedEventArgs e) => await LoadImageCatalog();

    private async Task LoadImageCatalog()
    {
        if (game is not { } source || closing) return;
        imageCatalogCancellation?.Cancel();
        textureSearchCancellation?.Cancel();
        using var current = new CancellationTokenSource();
        imageCatalogCancellation = current;
        var token = current.Token;
        texturePaths = null;
        imageIndexScanned = false;
        ShowTextureTree([]);
        TextureTreeCount.Text = "正在读取已知图标目录…";
        try
        {
            // 目录读取独立于预览操作，表格图标跳转仍可立即打开指定纹理。
            var paths = await Task.Run(() => source.ListIcons(token), token);
            token.ThrowIfCancellationRequested();
            var query = IconSearch.Text;
            var tree = await Task.Run(() => PathTree.Build(paths, path => path, query, token: token), token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(game, source) || closing) return;
            texturePaths = paths;
            imageIndexScanned = false;
            ShowTextureTree(tree);
            if (query != IconSearch.Text) FilterIcons(IconSearch, null!);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (imageCatalogCancellation == current && ReferenceEquals(game, source) && !closing)
                TextureTreeCount.Text = "读取图标目录失败：" + ex.Message;
        }
        finally
        {
            if (imageCatalogCancellation == current) imageCatalogCancellation = null;
        }
    }
}
