using System.Text.Json;

namespace RiBrowser;

/// <summary>
/// 会话持久化：持续记录所有窗口打开的标签页（每次变化立即原子落盘）。
/// 正常退出（用户关掉所有窗口）时写入 CleanExit 标记；下次启动若会话文件
/// 存在且没有 CleanExit 标记，说明上次是异常终止（崩溃/被杀/断电），
/// 此时恢复上次关闭前的所有页面。
/// </summary>
internal static class SessionStore
{
    private sealed class SessionData
    {
        public bool CleanExit { get; set; }
        public List<string> Urls { get; set; } = new();
        public int ActiveIndex { get; set; }
        public WindowBounds? Window { get; set; }
    }

    /// <summary>上次窗口状态（最大化时存还原尺寸，Chrome 同款记忆）。</summary>
    public sealed class WindowBounds
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public bool Maximized { get; set; }
    }

    private static readonly string SessionPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RiBrowser", "session.json");

    // 注册的窗口（按创建顺序），恢复时把所有窗口的标签平铺进一个窗口
    private static readonly List<MainForm> _windows = new();
    private static bool _running;

    // 最近一次已知的窗口状态（关闭中的窗口也先记下，最后一个窗口关闭后仍有值）
    private static WindowBounds? _lastWindow;

    /// <summary>启动时判断：上次是否异常终止，并取出待恢复的页面与选中下标。</summary>
    public static (bool Restore, List<string> Urls, int ActiveIndex) PeekStartup()
    {
        try
        {
            if (!File.Exists(SessionPath)) return (false, new List<string>(), 0);
            var data = JsonSerializer.Deserialize<SessionData>(File.ReadAllText(SessionPath));
            if (data is null || data.CleanExit) return (false, new List<string>(), 0);
            var urls = data.Urls.Where(u => !string.IsNullOrWhiteSpace(u)).ToList();
            return urls.Count == 0
                ? (false, new List<string>(), 0)
                : (true, urls, Math.Clamp(data.ActiveIndex, 0, urls.Count - 1));
        }
        catch
        {
            return (false, new List<string>(), 0); // 会话文件损坏按无会话处理
        }
    }

    /// <summary>启动判定完成后立即调用：写入"运行中"状态（clean=false），
    /// 之后任何时候崩溃，下次启动都能识别为异常终止。</summary>
    public static void BeginRun()
    {
        // 先把上次的窗口状态读进内存：MainForm 在 BeginRun 之后才构造，
        // 首次 Save() 若不带 Window 字段会把记忆抹掉
        try
        {
            if (File.Exists(SessionPath))
                _lastWindow = JsonSerializer.Deserialize<SessionData>(File.ReadAllText(SessionPath))?.Window;
        }
        catch { /* 文件损坏按无记忆处理 */ }
        _running = true;
        Save();
    }

    /// <summary>启动时取上次窗口状态。与标签恢复无关：正常退出也记忆窗口
    /// 位置/大小/最大化（Chrome 的 window_placement 行为）；无可信值返回 null。</summary>
    public static WindowBounds? PeekWindow()
    {
        try
        {
            if (!File.Exists(SessionPath)) return null;
            var data = JsonSerializer.Deserialize<SessionData>(File.ReadAllText(SessionPath));
            var w = data?.Window;
            if (w is null || w.Width < 400 || w.Height < 300) return null;
            return w;
        }
        catch { return null; }
    }

    /// <summary>窗口创建时注册；关闭时自动注销并重存会话。</summary>
    public static void Register(MainForm form)
    {
        _windows.Add(form);
        form.FormClosed += (s, e) => { _lastWindow = CaptureWindow(form); _windows.Remove(form); Save(); };
        Save();
    }

    /// <summary>任一窗口的标签增删/切换/导航后调用，立即落盘当前全部页面。</summary>
    public static void Save()
    {
        if (!_running) return; // 启动判定未完成前不写（避免覆盖待恢复数据）
        try
        {
            // 记忆最新窗口的状态（崩溃恢复时窗口尺寸也能还原）
            if (_windows.Count > 0) _lastWindow = CaptureWindow(_windows[^1]);
            var urls = new List<string>();
            var active = -1;
            foreach (var w in _windows.ToList()) // ToList：关闭中窗口的回调可能改动集合
            {
                foreach (var (url, isActive) in w.TabSnapshot())
                {
                    if (string.IsNullOrWhiteSpace(url) || url == "about:blank") continue;
                    urls.Add(url);
                    if (isActive && active < 0) active = urls.Count - 1;
                }
            }
            Write(new SessionData { CleanExit = false, Urls = urls, ActiveIndex = active < 0 ? 0 : active, Window = _lastWindow });
        }
        catch
        {
            /* 会话保存失败不影响浏览 */
        }
    }

    /// <summary>用户主动关闭最后一个窗口：打 clean 标记，下次正常启动（窗口状态保留记忆）。</summary>
    public static void MarkCleanExit()
    {
        try { Write(new SessionData { CleanExit = true, Window = _lastWindow }); }
        catch { /* 忽略 */ }
    }

    /// <summary>读取窗口当前状态：最大化/最小化时取 RestoreBounds（还原尺寸）。</summary>
    private static WindowBounds? CaptureWindow(MainForm form)
    {
        try
        {
            var state = form.WindowState;
            var b = state == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
            if (b.Width < 400 || b.Height < 300) return _lastWindow; // 异常尺寸不覆盖记忆
            return new WindowBounds { X = b.X, Y = b.Y, Width = b.Width, Height = b.Height, Maximized = state == FormWindowState.Maximized };
        }
        catch { return _lastWindow; }
    }

    // 临时文件 + 原子替换：写一半崩溃不会留下损坏的会话文件
    private static void Write(SessionData data)
    {
        var dir = Path.GetDirectoryName(SessionPath)!;
        Directory.CreateDirectory(dir);
        var tmp = SessionPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(data));
        File.Move(tmp, SessionPath, overwrite: true);
    }
}
