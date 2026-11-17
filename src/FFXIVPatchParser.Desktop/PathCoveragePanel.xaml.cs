using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;

namespace FFXIVPatchParser.Desktop;

public partial class PathCoveragePanel : UserControl
{
    private const string ResLoggerListUrl = "https://rl2.perchbird.dev/download/CurrentPathList.gz";
    private const long MaxListBytes = 512L * 1024 * 1024;
    private readonly Window owner;
    private SqPackPathCoverage? coverage;
    private ScanPosition? position;
    private CancellationTokenSource? operation;
    private Task operationTask = Task.CompletedTask;
    private bool closed;

    internal PathCoveragePanel(LocalGameSource source, Window owner)
    {
        Source = source;
        this.owner = owner;
        InitializeComponent();
    }

    internal LocalGameSource Source { get; }
    internal bool PathsChanged { get; private set; }
    internal event Action? CloseRequested;

    internal void Open()
    {
        CloseCoverageButton.Focus();
        Run(async (token, _) =>
        {
            await RefreshCoverage(token);
            position = ReadPosition();
            return position?.Complete == true ? "当前构建的引用扫描已完成" : "";
        }, "正在统计当前索引…");
    }

    internal Task StopAsync()
    {
        closed = true;
        operation?.Cancel();
        return operationTask;
    }

    private void ClosePanel(object sender, RoutedEventArgs e) => CloseRequested?.Invoke();

    private void PanelKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        CloseRequested?.Invoke();
    }

    private void ImportLocal(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Title = "选择 SqPack 路径清单", Filter = "路径清单|*.txt;*.csv;*.tsv|所有文件|*.*" };
        if (dialog.ShowDialog(owner) == true)
            Run((token, progress) => ImportPaths(dialog.FileName, Path.GetFileName(dialog.FileName), token, progress), "正在验证路径清单…");
    }

    private void ImportCommunity(object sender, RoutedEventArgs e) =>
        Run(async (token, progress) =>
        {
            var path = Path.Combine(Workspace.Temp, "file-paths", "downloads", "CurrentPathList.txt");
            if (!File.Exists(path))
            {
                OperationStatus.Text = "正在下载 ResLogger2 路径清单…";
                await DownloadCommunityList(path, token);
            }
            OperationStatus.Text = "正在与本地索引匹配…";
            return await ImportPaths(path, ResLoggerListUrl, token, progress);
        }, "正在准备 ResLogger2 路径清单…");

    private async Task<string> ImportPaths(string path, string label, CancellationToken token, IProgress<string> progress)
    {
        var before = coverage!.Named;
        var import = await Task.Run(() => Source.ImportFileSystemPaths(path, label, token, progress), token);
        PathsChanged |= import.Added != 0;
        await RefreshCoverage(token);
        return $"新增 {coverage!.Named - before:N0} 条已知路径，{import.Ambiguous:N0} 条候选存在歧义";
    }

    private void Scan(object sender, RoutedEventArgs e) =>
        Run(async (token, progress) =>
        {
            var before = coverage!.Named;
            var scan = await Task.Run(() => Source.ScanFileSystemPathReferences(maxFiles: 100000, maxTotalBytes: MaxListBytes,
                afterLocator: position?.LastLocator, token: token, progress: progress), token);
            PathsChanged |= scan.Import.Added != 0;
            position = new(scan.LastLocator, scan.Complete);
            await Workspace.SaveJson(PositionPath, position, CancellationToken.None);
            await RefreshCoverage(token);
            var phase = scan.Complete ? "已扫到索引末尾" : "已保存扫描进度";
            return $"检查 {scan.Examined:N0} 个条目，新增 {coverage!.Named - before:N0} 条已知路径，读取失败 {scan.Failed:N0} 个。{phase}。";
        }, "正在读取下一批资源引用…");

    private async Task RefreshCoverage(CancellationToken token)
    {
        coverage = await Task.Run(() => Source.GetFileSystemPathCoverage(token), token);
        var ratio = coverage.Total == 0 ? 0 : (double)coverage.Named / coverage.Total;
        CoverageSummary.Text = $"{coverage.Build} · {coverage.Named:N0} / {coverage.Total:N0} 条已知路径（{ratio:P2}） · {coverage.Unknown:N0} 条待补全";
        Containers.ItemsSource = coverage.Containers.OrderByDescending(row => row.Unknown).ToArray();
    }

    private string PositionPath => Path.Combine(Workspace.Temp, "file-paths", "verified", coverage!.IndexFingerprint, "scan-position.json");

    private ScanPosition? ReadPosition()
    {
        try { return File.Exists(PositionPath) ? JsonSerializer.Deserialize<ScanPosition>(File.ReadAllBytes(PositionPath), Workspace.Json) : null; }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    private void Run(Func<CancellationToken, IProgress<string>, Task<string>> run, string initialStatus)
    {
        if (!closed && operation == null) operationTask = RunAsync(run, initialStatus);
    }

    private async Task RunAsync(Func<CancellationToken, IProgress<string>, Task<string>> run, string initialStatus)
    {
        using var pending = new CancellationTokenSource();
        operation = pending;
        UpdateButtons();
        OperationStatus.Text = initialStatus;
        var progress = new Progress<string>(message =>
        {
            if (operation == pending && !pending.IsCancellationRequested) OperationStatus.Text = message;
        });
        try { OperationStatus.Text = await run(pending.Token, progress); }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { OperationStatus.Text = "操作已取消"; }
        catch (Exception ex) { OperationStatus.Text = $"操作失败 · {ex.Message}"; }
        finally
        {
            operation = null;
            UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        var ready = operation == null && coverage != null;
        ImportCommunityButton.IsEnabled = ImportLocalButton.IsEnabled = ready;
        ScanButton.IsEnabled = ready && coverage!.Unknown > 0 && position?.Complete != true;
        CancelButton.Visibility = operation == null ? Visibility.Collapsed : Visibility.Visible;
        CancelButton.IsEnabled = true;
    }

    private void CancelOperation(object sender, RoutedEventArgs e)
    {
        operation?.Cancel();
        CancelButton.IsEnabled = false;
        OperationStatus.Text = "正在取消…";
    }

    private static async Task DownloadCommunityList(string path, CancellationToken token)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pending = path + "." + Guid.NewGuid().ToString("N") + ".partial";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromMinutes(3));
        var download = timeout.Token;
        try
        {
            using var response = await Workspace.Http.GetAsync(ResLoggerListUrl, HttpCompletionOption.ResponseHeadersRead, download);
            response.EnsureSuccessStatusCode();
            await using var network = await response.Content.ReadAsStreamAsync(download);
            await using var gzip = new GZipStream(network, CompressionMode.Decompress);
            await using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                var buffer = new byte[65536];
                while (await gzip.ReadAsync(buffer, download) is > 0 and var read)
                {
                    if (output.Position + read > MaxListBytes) throw new InvalidDataException("路径清单解压后超过 512 MiB。");
                    await output.WriteAsync(buffer.AsMemory(0, read), download);
                }
                if (output.Length == 0) throw new InvalidDataException("ResLogger2 返回了空清单。");
            }
            download.ThrowIfCancellationRequested();
            File.Move(pending, path, overwrite: true);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { throw new IOException("下载 ResLogger2 路径清单超时。"); }
        finally { File.Delete(pending); }
    }

    private sealed record ScanPosition(string? LastLocator, bool Complete);
}
