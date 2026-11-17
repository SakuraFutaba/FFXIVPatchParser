using System.ComponentModel;
using System.Runtime.InteropServices;

namespace FFXIVPatchParser.Desktop;

internal static class ClipboardText
{
    private const int AccessDenied = 5;
    private const uint MoveableMemory = 0x0002;
    private const uint UnicodeText = 13;
    private const int OpenAttempts = 21;
    private const int RetryDelayMilliseconds = 75;

    public static async Task<bool> TryCopyAsync(nint owner, string text)
    {
        var buffer = new char[text.Length + 1];
        text.CopyTo(0, buffer, 0, text.Length);
        var memory = GlobalAlloc(MoveableMemory, (nuint)buffer.Length * sizeof(char));
        if (memory == 0) throw new Win32Exception();

        try
        {
            var address = GlobalLock(memory);
            if (address == 0) throw new Win32Exception();
            try { Marshal.Copy(buffer, 0, address, buffer.Length); }
            finally { GlobalUnlock(memory); }

            for (var attempt = 1; !OpenClipboard(owner); attempt++)
            {
                var error = Marshal.GetLastPInvokeError();
                if (error != AccessDenied) throw new Win32Exception(error);
                if (attempt == OpenAttempts) return false;
                await Task.Delay(RetryDelayMilliseconds);
            }

            try
            {
                if (!EmptyClipboard() || SetClipboardData(UnicodeText, memory) == 0) throw new Win32Exception();
                // 写入成功后，系统负责释放这块内存。
                memory = 0;
                return true;
            }
            finally { CloseClipboard(); }
        }
        finally
        {
            if (memory != 0) GlobalFree(memory);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint memory);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(nint memory);

    [DllImport("kernel32.dll")]
    private static extern nint GlobalFree(nint memory);
}
