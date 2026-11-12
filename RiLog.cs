namespace RiBrowser;

/// <summary>轻量文件日志：%LocalAppData%\RiBrowser\rilog.txt。
/// 用于排查"浏览器突然退出"类问题：记录所有关闭路径（页面自关/弹窗/关标签/关窗口/退出）。
/// 双击启动没有控制台输出，必须落盘。超过 2MB 时截断保留后半。</summary>
internal static class RiLog
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RiBrowser", "rilog.txt");
    private static readonly object Lock = new();

    public static void Info(string msg)
    {
        try
        {
            lock (Lock)
            {
                if (!File.Exists(LogPath)) Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
                var size = new FileInfo(LogPath).Length;
                if (size > 2_000_000)
                {
                    // 截断：保留后 1MB
                    var lines = File.ReadAllLines(LogPath);
                    File.WriteAllLines(LogPath, lines[^5000..]);
                }
                File.AppendAllText(LogPath, $"{DateTime.Now:MM-dd HH:mm:ss.fff} {msg}\r\n");
            }
        }
        catch { /* 日志失败绝不影响主流程 */ }
    }
}
