using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace FFXIVPatchParser.Desktop;

public partial class HexDiffView : UserControl, IDisposable
{
    private const int PageSize = 512;
    private const int BytesPerRow = 16;
    private static readonly Brush RemovedBackground = ColorBrush("#FFCECB");
    private static readonly Brush AddedBackground = ColorBrush("#ACEEBB");
    private static readonly Brush OffsetColor = ColorBrush("#667085");
    private string[] archives = [];
    private PatchResource? resource;
    private PatchHexSession? session;
    private CancellationTokenSource? request;
    private CancellationTokenSource? verification;
    private string? oldDirectory;
    private bool updating;

    public HexDiffView()
    {
        InitializeComponent();
        UpdateControls();
    }

    public Func<string?>? GetTargetDirectory { get; set; }

    public void SetPatch(string[] files)
    {
        archives = files.ToArray();
        SelectResource(null);
    }

    public void SelectResource(PatchResource? item)
    {
        request?.Cancel();
        request = null;
        verification?.Cancel();
        verification = null;
        session?.Dispose();
        session = null;
        resource = item;
        updating = true;
        try
        {
            HexMode.SelectedIndex = 0;
            WriteRanges.ItemsSource = null;
            ((ComboBoxItem)HexMode.Items[1]).IsEnabled = false;
            ((ComboBoxItem)HexMode.Items[2]).IsEnabled = false;
        }
        finally { updating = false; }
        HexBytes.Document.Blocks.Clear();
        ReadStatus.Text = item == null
            ? archives.Length == 0 ? "在补丁目录打开本地 ZIP 或选用已下载的补丁。" : "从左侧清单选择一个文件。"
            : item.Available ? "" : "此资源缺少补丁分卷。";
        OffsetInput.Clear();
        UpdateControls();
        if (item is { Available: true }) _ = LoadSessionAsync();
    }

    public void RefreshLocalFiles()
    {
        if (resource != null) SelectResource(resource);
    }

    private async Task LoadSessionAsync()
    {
        if (resource is not { Available: true } item || request != null) return;
        var files = archives;
        using var pending = new CancellationTokenSource();
        var token = pending.Token;
        request = pending;
        UpdateControls();
        ReadStatus.Text = "正在读取补丁差分…";
        var progress = new Progress<string>(message => { if (request == pending) ReadStatus.Text = message; });
        PatchHexSession? loaded = null;
        try
        {
            loaded = await Task.Run(() => PatchHexSession.OpenAsync(files, item, null, null, progress, token), token);
            token.ThrowIfCancellationRequested();
            if (request != pending) return;
            await ReadPageCore(loaded, 0, token);
            session = loaded;
            loaded = null;
            updating = true;
            try
            {
                WriteRanges.ItemsSource = session.Ranges;
                WriteRanges.SelectedIndex = -1;
            }
            finally { updating = false; }
            ReadStatus.Text = session.RangeStatus;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (request == pending) ReadStatus.Text = "已取消读取。";
        }
        catch (Exception ex)
        {
            if (request == pending) ReadStatus.Text = "无法读取差分：" + ex.Message;
        }
        finally
        {
            loaded?.Dispose();
            if (request == pending)
            {
                request = null;
                UpdateControls();
            }
        }
        if (ReferenceEquals(resource, item) && session is { } ready && !token.IsCancellationRequested)
            await VerifyLocalFilesAsync(ready, item);
    }

    private async Task VerifyLocalFilesAsync(PatchHexSession current, PatchResource item, bool showComparison = false)
    {
        if (verification != null) return;
        using var pending = new CancellationTokenSource();
        var token = pending.Token;
        verification = pending;
        UpdateControls();
        var progress = new Progress<string>(message =>
        {
            if (verification == pending && !token.IsCancellationRequested) ReadStatus.Text = message;
        });
        try
        {
            var target = GetTargetDirectory?.Invoke();
            var origin = oldDirectory;
            await Task.Run(() => current.VerifyLocalFilesAsync(item, target, origin, progress, token), token);
            token.ThrowIfCancellationRequested();
            if (verification != pending) return;
            ((ComboBoxItem)HexMode.Items[1]).IsEnabled = current.TargetLength.HasValue;
            ((ComboBoxItem)HexMode.Items[2]).IsEnabled = current.TargetLength.HasValue && current.OriginLength.HasValue;
            ReadStatus.Text = $"{current.TargetStatus} · {current.OriginStatus} · {current.RangeStatus}";
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if (verification == pending) ReadStatus.Text = "已取消文件校验。";
        }
        catch (Exception ex)
        {
            if (verification == pending) ReadStatus.Text = "无法校验游戏文件：" + ex.Message;
        }
        finally
        {
            if (verification == pending)
            {
                verification = null;
                UpdateControls();
            }
        }
        if (showComparison && !token.IsCancellationRequested && ReferenceEquals(session, current) && current.TargetLength.HasValue)
        {
            var mode = current.OriginLength.HasValue ? 2 : 1;
            if (HexMode.SelectedIndex != mode) HexMode.SelectedIndex = mode;
            else await ShowOffsetAsync(SelectedRangeStart());
        }
    }

    private async Task ShowOffsetAsync(long next)
    {
        if (session is not { } current || request != null) return;
        using var pending = new CancellationTokenSource();
        request = pending;
        UpdateControls();
        try { await ReadPageCore(current, next, pending.Token); }
        catch (OperationCanceledException) when (pending.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (request == pending) ReadStatus.Text = "无法读取该偏移：" + ex.Message;
        }
        finally
        {
            if (request == pending)
            {
                request = null;
                UpdateControls();
            }
        }
    }

    private async Task ReadPageCore(PatchHexSession current, long next, CancellationToken token)
    {
        var mode = HexMode.SelectedIndex;
        var length = ViewLength(current);
        next = Math.Clamp(next, 0, Math.Max(0, length - 1));
        var newBytes = await current.ReadPageAsync(mode == 0 ? PatchHexSource.Delta : PatchHexSource.Target, next, PageSize, token);
        var oldBytes = mode == 2 ? await current.ReadPageAsync(PatchHexSource.Origin, next, PageSize, token) : null;
        token.ThrowIfCancellationRequested();
        OffsetInput.Text = next.ToString("X", CultureInfo.InvariantCulture);
        RenderBytes(next, newBytes, oldBytes);
    }

    private void RenderBytes(long start, byte[] bytes, byte[]? before)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0), LineHeight = 19 };
        var count = Math.Max(bytes.Length, before?.Length ?? 0);
        for (var row = 0; row < count; row += BytesPerRow)
        {
            if (before != null) AddHexRow(paragraph, start, row, before, bytes, '−', RemovedBackground);
            AddHexRow(paragraph, start, row, bytes, before, before == null ? ' ' : '+', AddedBackground);
        }
        if (count == 0) paragraph.Inlines.Add(new Run("此文件没有可显示的字节。") { Foreground = OffsetColor });
        HexBytes.Document.Blocks.Clear();
        HexBytes.Document.Blocks.Add(paragraph);
        HexBytes.ScrollToHome();
    }

    private static void AddHexRow(Paragraph paragraph, long start, int row, byte[] bytes, byte[]? other, char sign, Brush highlight)
    {
        paragraph.Inlines.Add(new Run($"{sign} {start + row:X12}  ") { Foreground = OffsetColor });
        var ascii = new StringBuilder(BytesPerRow);
        for (var column = 0; column < BytesPerRow; column++)
        {
            var i = row + column;
            if (column == 8) paragraph.Inlines.Add(new Run(" "));
            if (i >= bytes.Length)
            {
                paragraph.Inlines.Add(new Run("   "));
                continue;
            }
            var value = bytes[i];
            var run = new Run($"{value:X2}");
            if (other != null && (i >= other.Length || value != other[i])) run.Background = highlight;
            paragraph.Inlines.Add(run);
            paragraph.Inlines.Add(new Run(" "));
            ascii.Append(value is >= 32 and < 127 ? (char)value : '·');
        }
        paragraph.Inlines.Add(new Run($"  {ascii}") { Foreground = OffsetColor });
        paragraph.Inlines.Add(new LineBreak());
    }

    private long ViewLength(PatchHexSession current) => HexMode.SelectedIndex switch
    {
        2 => Math.Max(current.TargetLength ?? 0, current.OriginLength ?? 0),
        1 => current.TargetLength ?? 0,
        _ => current.DeltaLength
    };

    private void UpdateControls()
    {
        var busy = request != null;
        var checking = verification != null;
        var ready = !busy && session != null && (!checking || !session.TargetLength.HasValue);
        var length = session == null ? 0 : ViewLength(session);
        HexMode.IsEnabled = ready;
        WriteRanges.IsEnabled = ready && session!.Ranges.Count > 0;
        ChooseOriginButton.IsEnabled = ready && !checking;
        PreviousHexRange.IsEnabled = ready && WriteRanges.SelectedIndex > 0;
        NextHexRange.IsEnabled = ready && WriteRanges.SelectedIndex + 1 < WriteRanges.Items.Count;
        OffsetInput.IsEnabled = JumpToHexOffset.IsEnabled = ready && length > 0;
        CancelReadButton.Visibility = busy || checking ? Visibility.Visible : Visibility.Collapsed;
        RetryReadButton.Visibility = !busy && session == null && resource is { Available: true }
            ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RetryRead(object sender, RoutedEventArgs e) => await LoadSessionAsync();

    private async void ChooseOrigin(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFolderDialog { Title = "选择旧版安装目录、game 或 sqpack" };
            if (Directory.Exists(oldDirectory)) dialog.InitialDirectory = oldDirectory;
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;
            oldDirectory = dialog.FolderName;
            if (session is { } current && resource is { } item) await VerifyLocalFilesAsync(current, item, showComparison: true);
        }
        catch (Exception ex) when (ex is ExternalException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            ReadStatus.Text = "无法选择旧版目录：" + ex.Message;
        }
    }

    private async void ChangeMode(object sender, SelectionChangedEventArgs e)
    {
        if (!updating && session != null) await ShowOffsetAsync(HexMode.SelectedIndex == 0 ? 0 : SelectedRangeStart());
    }

    private long SelectedRangeStart() => (WriteRanges.SelectedItem as PatchHexRange)?.Start ?? 0;

    private async void SelectRange(object sender, SelectionChangedEventArgs e)
    {
        if (updating || session == null || WriteRanges.SelectedItem is not PatchHexRange range) return;
        if (!session.TargetLength.HasValue)
        {
            UpdateControls();
            return;
        }
        updating = true;
        if (HexMode.SelectedIndex == 0) HexMode.SelectedIndex = 1;
        updating = false;
        await ShowOffsetAsync(range.Start);
    }

    private void PreviousRange(object sender, RoutedEventArgs e) => WriteRanges.SelectedIndex--;
    private void NextRange(object sender, RoutedEventArgs e) => WriteRanges.SelectedIndex++;
    private async void JumpToOffset(object sender, RoutedEventArgs e) => await JumpAsync();
    private async void OffsetKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await JumpAsync();
    }

    private async Task JumpAsync()
    {
        if (session == null) return;
        var text = OffsetInput.Text.Trim();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
        if (!long.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            || value < 0 || value >= ViewLength(session))
        {
            ReadStatus.Text = $"请输入文件范围内的十六进制偏移（0 – {Math.Max(0, ViewLength(session) - 1):X}）。";
            return;
        }
        await ShowOffsetAsync(value);
    }

    private void CancelRead(object sender, RoutedEventArgs e)
    {
        if (request != null) request.Cancel();
        else verification?.Cancel();
    }

    public void Dispose()
    {
        request?.Cancel();
        request = null;
        verification?.Cancel();
        verification = null;
        session?.Dispose();
        session = null;
    }

    private static Brush ColorBrush(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }
}
