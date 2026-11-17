using System.Windows;

namespace FFXIVPatchParser.Desktop;

internal enum ImagePage { Textures, Icons, Uld }

public partial class SqPackBrowser
{
    private ImagePage imagePage;
    private CancellationTokenSource? imageCatalogCancellation;
    private int imageCatalogRevision = -1;

    internal void ShowImagePage(ImagePage page)
    {
        if (imagePage == page) return;
        imagePage = page;
        UpdateVisiblePanels();
    }

    private void LocalImagesVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!LocalImagesPanel.IsVisible) imageCatalogCancellation?.Cancel();
        else if (initialized) StartImageCatalogIfVisible();
    }

    private void StartImageCatalogIfVisible()
    {
        if (!closing && currentPage == SqPackPage.Images && LocalImagesPanel.IsVisible && game != null && (texturePaths == null || imageCatalogRevision != game.MediaCatalogRevision) &&
            imageCatalogCancellation is not { IsCancellationRequested: false }) _ = LoadImageCatalog();
    }

    private async Task LoadImageCatalog()
    {
        if (game is not { } source || closing) return;
        imageCatalogCancellation?.Cancel();
        textureSearchCancellation?.Cancel();
        using var current = new CancellationTokenSource();
        imageCatalogCancellation = current;
        var token = current.Token;
        var revision = source.MediaCatalogRevision;
        texturePaths = null;
        ShowTextureTree([], IconSearch.Text);
        TextureTreeCount.Text = "正在读取已知图片目录…";
        try
        {
            // 目录读取独立于预览操作，表格图标跳转仍可立即打开指定纹理。
            var paths = await Task.Run(() => source.ListKnownLocalMediaLocations("image", token).Select(entry => entry.Path).ToArray(), token);
            token.ThrowIfCancellationRequested();
            var query = IconSearch.Text;
            var tree = await Task.Run(() => PathTree.Build(paths, path => path, query, token: token), token);
            token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(game, source) || closing) return;
            texturePaths = paths;
            imageCatalogRevision = revision;
            if (query == IconSearch.Text) ShowTextureTree(tree, query);
            else await ApplyIconFilterAsync(debounce: false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (imageCatalogCancellation == current && ReferenceEquals(game, source) && !closing)
                TextureTreeCount.Text = "读取图片目录失败：" + ex.Message;
        }
        finally
        {
            if (imageCatalogCancellation == current) imageCatalogCancellation = null;
        }
    }
}
