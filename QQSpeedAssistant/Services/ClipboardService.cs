using System.Runtime.InteropServices;
using System.Text;

namespace QQSpeedAssistant.Services;

/// <summary>
/// 剪贴板读写，读和写都直接走 Win32 的 CF_UNICODETEXT，不经过 WPF 的 OLE 层。
///
/// 为什么要绕：本机 .NET 10 在管理员进程里调 <c>System.Windows.Clipboard</c> 会抛
/// <c>FileNotFoundException 'System.Reflection.Metadata, Version=11.0.0.0'</c>
/// （新版实现改走 NRBF 反序列化，而这个版本的程序集在本机装载不到）。
/// 读会抛、写也一样，于是「粘不进去」和「复制不出来」是同一个病根。
/// </summary>
internal static class ClipboardService
{
    private const uint CfUnicodeText = 13;

    /// <summary>GlobalAlloc 的 GMEM_MOVEABLE，SetClipboardData 只接受可移动内存块。</summary>
    private const uint GmemMoveable = 0x0002;

    /// <summary>
    /// 读剪贴板文本。读不到返回 null，<paramref name="detail"/> 写原因（含剪贴板现有的格式号，
    /// 便于判断是「复制那步没进剪贴板」还是「被剪贴板管理器改成了私有格式」）。
    /// </summary>
    public static string? ReadText(out string detail)
    {
        detail = string.Empty;

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            if (!OpenClipboard(nint.Zero))
            {
                detail = $"OpenClipboard 失败（错误码 {Marshal.GetLastWin32Error()}）";
                Thread.Sleep(25);
                continue;
            }

            try
            {
                var formats = new List<string>();
                uint format = 0;
                while ((format = EnumClipboardFormats(format)) != 0)
                {
                    formats.Add(format.ToString());
                }

                var listing = formats.Count == 0 ? "空" : string.Join(",", formats);

                if (!IsClipboardFormatAvailable(CfUnicodeText))
                {
                    detail = $"剪贴板里没有 CF_UNICODETEXT（现有格式号=[{listing}]）";
                    return null;
                }

                var handle = GetClipboardData(CfUnicodeText);
                if (handle == nint.Zero)
                {
                    detail = $"GetClipboardData 返回 0（格式号=[{listing}]）";
                    return null;
                }

                var pointer = GlobalLock(handle);
                if (pointer == nint.Zero)
                {
                    detail = "GlobalLock 失败";
                    return null;
                }

                try
                {
                    var text = Marshal.PtrToStringUni(pointer) ?? string.Empty;
                    detail = $"Win32 读到 {text.Length} 字";
                    return text;
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
            finally
            {
                CloseClipboard();
            }
        }

        if (detail.Length == 0)
        {
            detail = "OpenClipboard 连续 5 次失败：剪贴板被别的程序独占";
        }

        return null;
    }

    /// <summary>
    /// 把纯文本写进剪贴板。别的程序正占着剪贴板是很常见的事（截图工具、输入法都会抢），
    /// 所以失败会重试几次；仍然失败就返回 false 并把 Win32 错误码写进 <paramref name="detail"/>。
    /// </summary>
    public static bool WriteText(string? text, out string detail)
    {
        detail = string.Empty;
        text ??= string.Empty;

        for (var attempt = 1; attempt <= 5; attempt++)
        {
            if (!OpenClipboard(nint.Zero))
            {
                detail = $"OpenClipboard 失败（错误码 {Marshal.GetLastWin32Error()}）";
                Thread.Sleep(25);
                continue;
            }

            nint handle = nint.Zero;
            var ownershipMoved = false;
            try
            {
                if (!EmptyClipboard())
                {
                    detail = $"EmptyClipboard 失败（错误码 {Marshal.GetLastWin32Error()}）";
                    Thread.Sleep(25);
                    continue;
                }

                // 结尾的 \0 得自己补：SetClipboardData 不会替我们加，读的一方是按 C 字符串取的。
                var bytes = Encoding.Unicode.GetBytes(text + "\0");
                handle = GlobalAlloc(GmemMoveable, (nuint)bytes.Length);
                if (handle == nint.Zero)
                {
                    detail = "GlobalAlloc 失败（内存不足？）";
                    return false;
                }

                var pointer = GlobalLock(handle);
                if (pointer == nint.Zero)
                {
                    detail = "GlobalLock 失败";
                    return false;
                }

                try
                {
                    Marshal.Copy(bytes, 0, pointer, bytes.Length);
                }
                finally
                {
                    GlobalUnlock(handle);
                }

                if (SetClipboardData(CfUnicodeText, handle) == nint.Zero)
                {
                    detail = $"SetClipboardData 失败（错误码 {Marshal.GetLastWin32Error()}）";
                    return false;
                }

                // 成功之后这块内存归系统所有，不能再 GlobalFree，否则剪贴板里就是野指针。
                ownershipMoved = true;
                detail = $"Win32 写入 {text.Length} 字";
                return true;
            }
            finally
            {
                if (!ownershipMoved && handle != nint.Zero)
                {
                    GlobalFree(handle);
                }

                CloseClipboard();
            }
        }

        if (detail.Length == 0)
        {
            detail = "OpenClipboard 连续 5 次失败：剪贴板被别的程序独占";
        }

        return false;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(nint owner);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint GetClipboardData(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetClipboardData(uint format, nint memory);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint EnumClipboardFormats(uint format);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalAlloc(uint flags, nuint bytes);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalLock(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalUnlock(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint GlobalFree(nint handle);
}
