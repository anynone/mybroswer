using System.Runtime.InteropServices;
using System.Text;
using CefSharp;
using CefSharp.WinForms;
using RiBrowser.Privacy;

namespace RiBrowser;

internal sealed class MainForm : Form
{
    // 启动页地址：来自设置（默认 Google），为空或非法时回退
    private static string HomeUrl()
    {
        var s = (PrivacySettings.Current.HomePage ?? "").Trim();
        if (s.Length == 0) return "https://www.google.com";
        if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            s = s.Contains('.') ? "https://" + s : "";
        }
        return Uri.TryCreate(s, UriKind.Absolute, out var u) &&
               (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps) ? u.AbsoluteUri : "https://www.google.com";
    }

    // 支持的搜索引擎：地址栏输入非 URL 时按当前配置的模板跳转（⋮ 菜单可切换）
    private static readonly (string Id, string Name, string Template)[] SearchEngines =
    {
        ("google", "Google", "https://www.google.com/search?q={0}"),
        ("baidu", "百度", "https://www.baidu.com/s?wd={0}"),
    };

    private static string SearchTemplate()
    {
        foreach (var e in SearchEngines)
            if (e.Id == PrivacySettings.Current.SearchEngine) return e.Template;
        return SearchEngines[0].Template;
    }

    private static string SearchEngineName()
    {
        foreach (var e in SearchEngines)
            if (e.Id == PrivacySettings.Current.SearchEngine) return e.Name;
        return SearchEngines[0].Name;
    }

    /// <summary>地址栏建议的"搜索 xxx"行（目标为拼好的搜索 URL）。</summary>
    internal static HistoryRow SearchRow(string q) =>
        new("搜索“" + q + "”", SearchEngineName(),
            string.Format(SearchTemplate(), Uri.EscapeDataString(q)), Bookmark: false, Search: true);

    /// <summary>输入是否长得像网址（与 NormalizeAddress 的判定同口径）：
    /// 像网址且无历史命中时不弹搜索行，回车直接按网址导航。</summary>
    internal static bool LooksLikeAddress(string input)
    {
        var s = input.Trim();
        if (s.Length == 0 || s.Contains(' ')) return false;
        if (Uri.TryCreate(s, UriKind.Absolute, out var u) && IsNavigableScheme(u))
            return true;
        return s.Contains('.') || s.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || s.Contains(':');
    }

    /// <summary>地址栏可直达的 scheme（clash:// = 内置 Clash 控制台）。</summary>
    private static bool IsNavigableScheme(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps
        || uri.Scheme == Uri.UriSchemeFile || uri.Scheme == "clash";

    private static readonly Color StripColor = Color.FromArgb(0xDE, 0xE1, 0xE6);
    private static readonly Color ToolbarColor = Color.White;
    private const int NavBarLeft = 158;

    private static Font? _iconFont;
    internal static Font IconFont => _iconFont ??= CreateIconFont(11f);

    /// <summary>按指定字号创建图标字体（Segoe Fluent Icons / MDL2）。</summary>
    internal static Font IconFontOf(float size)
    {
        var f = IconFont;
        return new Font(f.FontFamily, size, f.Style, GraphicsUnit.Point);
    }

    private readonly ToolTip _tips = new();
    private readonly PrivacyEngine _privacy;

    private readonly Panel _content;
    private readonly Panel _toolbar;
    private readonly Panel _tabStrip;
    private readonly FlowLayoutPanel _tabFlow;
    private readonly Button _plusButton;
    private readonly IconButton _backButton;
    private readonly IconButton _forwardButton;
    private readonly IconButton _reloadButton;
    private readonly IconButton _homeButton;
    private readonly IconButton _menuButton;
    private readonly IconButton _tabSearchButton;
    private readonly IconButton _clashButton;
    private readonly AddressBar _addressBar;
    private readonly WindowButton _minButton;
    private readonly Panel _windowButtons;
    private readonly Panel _tabSearchHost;
    private readonly WindowButton _maxButton;
    private readonly WindowButton _closeButton;
    private readonly BookmarkBar _bookmarkBar;
    private bool _showBookmarkBar;

    private readonly List<BrowserTab> _tabs = new();
    // _tabs 会被 CEF 线程（DoClose→IsOurTab）并发读取：增删必须加锁，
    // 否则 List 边遍历边改抛异常，CEF 原生回调未处理 → 进程崩溃（0xe0434352）
    private readonly object _tabsLock = new();
    // _tabs 会被 CEF 线程（DoClose→IsOurTab）并发读取：增删必须加锁，
    private BrowserTab? _activeTab;

    // 启动时待恢复的窗口边界（物理像素，句柄创建后应用，避开构造器 DPI 缩放）
    private readonly Rectangle? _restoreBounds;
    private readonly bool _restoreMaximized;

    public MainForm(string? initialUrl = null, List<string>? restoreUrls = null, int restoreActive = 0, bool netLock = false)
    {
        Text = "Ri 浏览器";
        MinimumSize = new Size(640, 420);
        _netState = netLock ? NetState.Locked : NetState.ClashChain; // 门禁「仍要进入」：锁定态打开
        _proxySig = ProxySignature(PrivacySettings.Current); // 启动时已应用，作基线

        // 窗口初始尺寸与 Chrome 一致：启动窗口记忆上次的位置/大小/最大化状态
        // （跨正常退出持续记忆）。标题栏够不到任何屏幕（换显示器/改分辨率）时回退
        // 默认。首次运行默认取主屏工作区 3/4 居中；普通新窗口保持原默认。
        var remembered = initialUrl == null ? SessionStore.PeekWindow() : null;
        var wa0 = remembered is { } wb0
            ? Screen.AllScreens.FirstOrDefault(s => s.Bounds.IntersectsWith(
                  new Rectangle(wb0.X, wb0.Y, wb0.Width, Math.Min(wb0.Height, 120))))?.WorkingArea
            : null;
        if (remembered is { } wb && wa0 is { } wa)
        {
            var w = Math.Min(wb.Width, wa.Width);
            var h = Math.Min(wb.Height, wa.Height);
            StartPosition = FormStartPosition.Manual;
            // 构造器里设 Bounds 会被 WinForms 按 DPI 二次缩放（保存的是物理像素），
            // 留到句柄创建后再恢复（见 OnHandleCreated）
            _restoreBounds = new Rectangle(
                Math.Clamp(wb.X, wa.Left, wa.Right - w),
                Math.Clamp(wb.Y, wa.Top, wa.Bottom - h), w, h);
            _restoreMaximized = wb.Maximized;
        }
        else if (initialUrl == null)
        {
            var waDef = (Screen.PrimaryScreen ?? Screen.AllScreens[0]).WorkingArea;
            ClientSize = new Size(waDef.Width * 3 / 4, waDef.Height * 3 / 4);
            StartPosition = FormStartPosition.CenterScreen;
        }
        else
        {
            ClientSize = new Size(1200, 800);
            StartPosition = FormStartPosition.CenterScreen;
        }
        Font = new Font("Segoe UI", 9f);
        BackColor = StripColor;
        KeyPreview = true;
        _privacy = new PrivacyEngine(this);

        // ---- 内容区（承载当前标签的 Chromium 控件）----
        _content = new Panel { Dock = DockStyle.Fill, BackColor = Color.White };

        // ---- 工具栏（后退/前进/刷新/主页/地址栏/菜单）----
        _toolbar = new Panel { Dock = DockStyle.Top, Height = 48, BackColor = ToolbarColor };

        _backButton = new IconButton { Glyph = "\uE72B", AccessibleName = "后退" };
        _backButton.Click += (s, e) => UiBack();
        _tips.SetToolTip(_backButton, "后退 (Alt+←)");

        _forwardButton = new IconButton { Glyph = "\uE72A", AccessibleName = "前进" };
        _forwardButton.Click += (s, e) => UiForward();
        _tips.SetToolTip(_forwardButton, "前进 (Alt+→)");

        _reloadButton = new IconButton { Glyph = "\uE72C", AccessibleName = "刷新" };
        _reloadButton.Click += (s, e) =>
        {
            if (_activeTab is not { } tab) return;
            if (tab.IsLoading) tab.Browser.Stop();
            else tab.Browser.Reload();
        };
        _tips.SetToolTip(_reloadButton, "刷新 / 停止 (F5)");

        _homeButton = new IconButton { Glyph = "\uE80F", AccessibleName = "主页" };
        _homeButton.Click += (s, e) => _activeTab?.Browser.Load(HomeUrl());
        _tips.SetToolTip(_homeButton, "主页");

        _menuButton = new IconButton { Glyph = "\uE712", AccessibleName = "菜单" };
        _menuButton.Click += (s, e) => ShowMainMenu();
        _tips.SetToolTip(_menuButton, "菜单");

        // Clash 状态盾牌：地址栏右侧、菜单按钮左侧（点击弹节点/模式控制）
        _clashButton = new IconButton { Glyph = "\uE72E", GlyphSize = 11f, AccessibleName = "Clash 代理状态" };
        _clashButton.Click += (s, e) => ShowClashMenu();

        _addressBar = new AddressBar();
        _addressBar.Box.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                NavigateFromAddressBox();
            }
        };
        // 建议浮层的回车/点击导航（目标已是完整 URL）
        _addressBar.NavigateRequested += t => Ui(() =>
        {
            _addressBar.Box.Text = t;
            NavigateFromAddressBox();
        });
        _addressBar.StarClicked += ToggleBookmarkForCurrent;

        // ---- 书签栏（存在书签且开关打开时显示，Chrome 同款）----
        _showBookmarkBar = PrivacySettings.Current.ShowBookmarkBar;
        _bookmarkBar = new BookmarkBar();
        _bookmarkBar.OpenInCurrentTab += url => _activeTab?.Browser.Load(url);
        _bookmarkBar.OpenInNewTab += url => AddTab(url);
        Bookmarks.Changed += () => Ui(() =>
        {
            _bookmarkBar.Reload();
            _bookmarkBar.Visible = _showBookmarkBar && Bookmarks.Items.Count > 0;
        });
        _bookmarkBar.Visible = _showBookmarkBar && Bookmarks.Items.Count > 0;

        _toolbar.Controls.AddRange(new Control[]
        {
            _backButton, _forwardButton, _reloadButton, _homeButton, _addressBar, _clashButton, _menuButton,
        });
        _toolbar.Resize += (s, e) => LayoutToolbar();
        LayoutToolbar();
        InitClashPolling();

        // ---- 标签条（左：标签 + 新建；右：窗口控制按钮；空白处可拖动窗口）----
        _tabStrip = new CaptionStripPanel { Dock = DockStyle.Top, Height = 45, BackColor = StripColor };
        _tabFlow = new CaptionFlowPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            AutoScroll = false,
            Margin = new Padding(0),
            Padding = new Padding(6, 5, 6, 0),
        };
        _tabFlow.Resize += (s, e) => UpdateTabWidths();

        _plusButton = new Button
        {
            Text = "\uE710",
            Font = IconFont,
            FlatStyle = FlatStyle.Flat,
            Size = new Size(30, 34),
            Margin = new Padding(3, 6, 0, 0),
            ForeColor = Color.FromArgb(0x3C, 0x40, 0x43),
            BackColor = StripColor,
            TabStop = false,
            UseVisualStyleBackColor = false,
        };
        _plusButton.FlatAppearance.BorderSize = 0;
        _plusButton.FlatAppearance.MouseOverBackColor = Color.FromArgb(0xE8, 0xEA, 0xED);
        _plusButton.Click += (s, e) => AddTab();
        _tips.SetToolTip(_plusButton, "新建标签页 (Ctrl+T)");
        _tabFlow.Controls.Add(_plusButton);

        _minButton = new WindowButton("\uE921", "最小化", isClose: false);
        _minButton.Click += (s, e) => WindowState = FormWindowState.Minimized;
        _maxButton = new WindowButton("\uE922", "最大化", isClose: false);
        _maxButton.Click += (s, e) => ToggleMaximize();
        _closeButton = new WindowButton("\uE8BB", "关闭", isClose: true);
        _closeButton.Click += (s, e) => Close();

        _windowButtons = new Panel { Dock = DockStyle.Right, Width = 46 * 3, BackColor = StripColor };
        _windowButtons.Controls.Add(_minButton);
        _windowButtons.Controls.Add(_maxButton);
        _windowButtons.Controls.Add(_closeButton);
        _minButton.Location = new Point(0, 0);
        _maxButton.Location = new Point(46, 0);
        _closeButton.Location = new Point(92, 0);

        _tabStrip.Controls.Add(_tabFlow);
        _tabStrip.Controls.Add(_windowButtons);

        // 标签条最左侧：标签搜索箭头（Clash 盾牌在工具栏地址栏右侧）
        _tabSearchHost = new Panel { Dock = DockStyle.Left, Width = 42, BackColor = StripColor };
        _tabSearchButton = new IconButton { Glyph = "\uE70D", GlyphSize = 9f, AccessibleName = "搜索标签页" };
        _tabSearchButton.SetBounds(6, 7, 30, 30);
        _tabSearchButton.Click += (s, e) => ShowTabSearchMenu();
        _tips.SetToolTip(_tabSearchButton, "搜索标签页  Ctrl+Shift+A");
        _tabSearchHost.Controls.Add(_tabSearchButton);
        _tabStrip.Controls.Add(_tabSearchHost);

        // Dock 顺序：内容区先加，标签条最后加 → 标签条最上方，其下工具栏、书签栏
        Controls.Add(_content);
        Controls.Add(_bookmarkBar);
        Controls.Add(_toolbar);
        Controls.Add(_tabStrip);

        // 焦点在地址栏等 WinForms 控件时的快捷键（CEF 内部的由 KeyboardHandler 处理）
        KeyDown += OnFormKeyDown;
        Resize += (s, e) => UpdateMaximizeGlyph();

        // 新窗口（右键"在新窗口中打开链接"）首标签直接加载目标页；
        // 会话恢复：上次异常终止 → 一次性恢复全部页面并选中原选中项
        if (restoreUrls is { Count: > 0 })
        {
            for (var i = 0; i < restoreUrls.Count; i++)
                AddTab(restoreUrls[i], select: i == Math.Clamp(restoreActive, 0, restoreUrls.Count - 1));
        }
        else
        {
            AddTab(initialUrl ?? HomeUrl());
        }

        if (Environment.GetCommandLineArgs().Contains("--selftest"))
            RunSelfTest();
    }

    // ==================== 界面 DPI 缩放（chrome 部件按 96 基准等比放大） ====================

    /// <summary>当前界面缩放系数（主窗体 DPI / 96）。标签条/工具栏/书签栏/标签页等
    /// 自绘部件的像素尺寸都以此为基准放大，高分屏下与系统缩放一致。</summary>
    internal double UiScale = 1;

    internal int S(int v) => (int)Math.Round(v * UiScale);

    /// <summary>句柄创建/DPI 变化后统一应用：此时 DeviceDpi 已是显示器真实值。</summary>
    private void ApplyChromeDpi()
    {
        if (UiScale <= 0) return;

        _toolbar.Height = S(48);
        _tabStrip.Height = S(45);
        _windowButtons.Width = S(46 * 3);
        _minButton.Location = new Point(0, 0);
        _maxButton.Location = new Point(S(46), 0);
        _closeButton.Location = new Point(S(92), 0);
        foreach (var b in new[] { _minButton, _maxButton, _closeButton }) b.Size = new Size(S(46), S(38));

        _tabSearchHost.Width = S(42);
        _tabSearchButton.SetBounds(S(6), S(7), S(30), S(30));
        _tabFlow.Padding = new Padding(S(6), S(5), S(6), 0);
        // ＋按钮与标签卡同高同基线（标签高 35，流顶部内边距已留 5px）
        _plusButton.Size = new Size(S(30), S(35));
        _plusButton.Margin = new Padding(S(3), 0, 0, 0);

        _bookmarkBar.SetChromeScale(UiScale);
        foreach (var t in _tabs) t.TabItem.SetChromeScale(UiScale);

        UpdateTabWidths();
        LayoutToolbar();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        UiScale = DeviceDpi / 96d; // 事件后 Control.DeviceDpi 已是新值
        ApplyChromeDpi();
    }

    // ==================== 无边框窗口（保留 DWM 阴影 / 贴边分屏 / 边缘缩放） ====================

    private const int WM_NCCALCSIZE = 0x83;
    private const int WM_NCHITTEST = 0x84;
    private const int WM_DESTROY = 0x02;
    private const int WM_GETMINMAXINFO = 0x24;
    private const int WM_WINDOWPOSCHANGING = 0x46;
    private const int WM_SIZE = 0x05;
    private const long SIZE_MAXIMIZED = 2;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const int HTCLIENT = 1, HTCAPTION = 2;
    private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13, HTTOPRIGHT = 14,
                      HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
        public Point ToScreenPoint(IntPtr hwndFrom)
        {
            var p = this;
            ClientToScreen(hwndFrom, ref p);
            return new Point(p.X, p.Y);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    private static Rectangle GetWindowRectP(IntPtr hwnd)
    {
        GetWindowRect(hwnd, out var r);
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }

    private static POINT GetCursorPosP() { GetCursorPos(out var p); return p; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved, ptMaxSize, ptMaxPosition, ptMinTrackSize, ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd, hwndInsertAfter;
        public int x, y, cx, cy;
        public uint flags;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // 记忆窗口边界在句柄创建后应用：此时 DPI 上下文已确定，Bounds 即物理像素，
        // 不会被 WinForms 的构造期 DPI 缩放折算（见构造器注释）
        if (_restoreBounds is { } b)
        {
            Bounds = b;
            if (_restoreMaximized) WindowState = FormWindowState.Maximized;
        }
        // chrome 部件按真实 DPI 缩放（构造期 DeviceDpi 未知，不能提前算）
        UiScale = DeviceDpi / 96d;
        ApplyChromeDpi();
    }

    protected override void WndProc(ref Message m)
    {
        // 去掉系统标题栏/边框（客户区铺满窗口），保留阴影、贴边与任务栏动画
        if (m.Msg == WM_NCCALCSIZE && m.WParam != IntPtr.Zero)
        {
            m.Result = IntPtr.Zero;
            return;
        }
        if (m.Msg == WM_GETMINMAXINFO)
        {
            // 无边框窗口最大化：给系统精确的最大化位置/尺寸。
            // ptMaxPosition 需用监视器相对坐标（系统会再加监视器原点）
            var screen = Screen.FromControl(this);
            var wa = screen.WorkingArea;
            var origin = screen.Bounds.Location;
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(m.LParam);
            mmi.ptMaxSize = new POINT { X = wa.Width, Y = wa.Height };
            mmi.ptMaxPosition = new POINT { X = wa.X - origin.X, Y = wa.Y - origin.Y };
            Marshal.StructureToPtr(mmi, m.LParam, false);
            m.Result = IntPtr.Zero;
            return; // 不再交给 DefWindowProc（它会用默认值覆盖）
        }
        if (m.Msg == WM_WINDOWPOSCHANGING)
        {
            // 无需处理：最大化时系统会带一个框架宽的外扩（与原生带边框窗口一致），
            // 由 WM_SIZE 里设置的窗体内边距补偿，内容精确落在工作区
        }
        else if (m.Msg == WM_SIZE)
        {
            // 无边框窗口最大化时窗口会外扩一个框架宽（内容被推出屏幕 8px）：
            // 用等量 Padding 把内容收回工作区；还原时清零
            if (m.WParam.ToInt64() == SIZE_MAXIMIZED)
            {
                var wa = Screen.FromControl(this).WorkingArea;
                var pad = Math.Max(0, Math.Max((Width - wa.Width) / 2, (Height - wa.Height) / 2));
                if (Padding.All != pad) Padding = new Padding(pad);
            }
            else if (Padding.All != 0)
            {
                Padding = new Padding(0);
            }
        }
        if (m.Msg == WM_NCHITTEST)
        {
            base.WndProc(ref m);
            if ((int)m.Result == HTCLIENT)
            {
                var lp = m.LParam.ToInt64();
                var screen = new Point((int)(short)(lp & 0xFFFF), (int)(short)((lp >> 16) & 0xFFFF));
                m.Result = (IntPtr)HitTestNca(screen);
            }
            return;
        }
        base.WndProc(ref m);
    }

    private int HitTestNca(Point screen)
    {
        var pt = PointToClient(screen);
        var edge = Math.Max(5, (int)Math.Round(6 * DeviceDpi / 96d));

        if (WindowState != FormWindowState.Maximized)
        {
            var left = pt.X <= edge;
            var right = pt.X >= ClientSize.Width - edge - 1;
            var top = pt.Y <= edge;
            var bottom = pt.Y >= ClientSize.Height - edge - 1;
            if (left && top) return HTTOPLEFT;
            if (right && top) return HTTOPRIGHT;
            if (left && bottom) return HTBOTTOMLEFT;
            if (right && bottom) return HTBOTTOMRIGHT;
            if (left) return HTLEFT;
            if (right) return HTRIGHT;
            if (top) return HTTOP;
            if (bottom) return HTBOTTOM;
        }

        // 标签条空白区域可拖动窗口（标题栏语义，双击最大化、拖到边缘分屏均可用）
        if (_tabStrip.Bounds.Contains(pt) && !InteractiveAt(screen))
            return HTCAPTION;

        return HTCLIENT;
    }

    private static Control? DeepHit(Control parent, Point screenPt)
    {
        var child = parent.GetChildAtPoint(parent.PointToClient(screenPt),
            GetChildAtPointSkip.Invisible | GetChildAtPointSkip.Disabled | GetChildAtPointSkip.Transparent);
        if (child == null) return null;
        return DeepHit(child, screenPt) ?? child;
    }

    private bool InteractiveAt(Point screen)
    {
        var hit = DeepHit(this, screen);
        return hit != null && hit != _tabStrip && hit != _tabFlow;
    }

    // ---------- CEF 渲染窗口边缘缩放：光标与拖拽手势转发 ----------

    /// <summary>Chromium 渲染子窗口（Chrome_RenderWidgetHostHWND）铺满客户区，鼠标
    /// 在窗口边缘时命中的是它而非主窗体（且它跑在 CEF UI 线程，HTTRANSPARENT 的
    /// 同线程传递规则不适用），主窗体的无边框命中测试收不到——边缘缩放因此失效。
    /// 解法：子类化渲染窗口，在边缘区内自己设置缩放光标，左键按下时把
    /// WM_NCLBUTTONDOWN(缩放码) 投递给主窗体，由系统原生接管拖拽 resize。
    /// 全部走 Win32（跨线程安全），不触碰 WinForms 成员。</summary>
    private sealed class RenderWndHook : NativeWindow
    {
        private const int WM_MOUSEMOVE = 0x200, WM_SETCURSOR = 0x20, WM_LBUTTONDOWN = 0x201, WM_NCLBUTTONDOWN = 0xA1;
        private const int HTLEFTZ = 10, HTRIGHTZ = 11, HTTOPZ = 12, HTTOPLEFTZ = 13,
            HTTOPRIGHTZ = 14, HTBOTTOMZ = 15, HTBOTTOMLEFTZ = 16, HTBOTTOMRIGHTZ = 17;

        private readonly IntPtr _main;
        private readonly Action _onReleased;

        public RenderWndHook(IntPtr main, IntPtr hwnd, Action onReleased)
        { _main = main; _onReleased = onReleased; AssignHandle(hwnd); }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DESTROY)
            {
                base.WndProc(ref m);
                ReleaseHandle(); // 标签关闭/浏览器销毁时自动摘钩；HWND 地址可复用，需从已钩集合移除
                _onReleased();
                return;
            }
            // 光标：CEF 的父窗口会自行处理 WM_SETCURSOR（页面手型/文本光标）不再转发，
            // 所以主要靠 WM_MOUSEMOVE（必然到达渲染窗口）实时设置缩放光标并吞掉消息，
            // 不给 CEF 机会改回页面光标
            if (m.Msg is WM_MOUSEMOVE or WM_SETCURSOR or WM_LBUTTONDOWN)
            {
                Point pt;
                if (m.Msg == WM_SETCURSOR)
                {
                    var cp = GetCursorPosP();
                    pt = new Point(cp.X, cp.Y);
                }
                else
                {
                    // lParam 是渲染窗口客户坐标，转屏幕
                    var lp = m.LParam.ToInt64();
                    var local = new POINT { X = (short)(lp & 0xFFFF), Y = (short)((lp >> 16) & 0xFFFF) };
                    var sp = local.ToScreenPoint(Handle);
                    pt = new Point(sp.X, sp.Y);
                }

                var ht = EdgeHitTest(_main, pt);
                if (ht != 0)
                {
                    if (m.Msg == WM_LBUTTONDOWN)
                    {
                        PostMessage(_main, WM_NCLBUTTONDOWN, (IntPtr)ht,
                            (IntPtr)(((pt.Y & 0xFFFF) << 16) | (pt.X & 0xFFFF)));
                        return; // 不下传给页面：这是窗口缩放手势
                    }
                    SetCursor(LoadCursor(IntPtr.Zero, CursorFor(ht)));
                    if (m.Msg == WM_SETCURSOR) m.Result = (IntPtr)1;
                    return;
                }
            }
            base.WndProc(ref m);
        }

        /// <summary>主窗体边缘 8 向命中（最大化时 0）。基于 GetWindowRect 物理像素，
        /// 与 HitTestNca 的边缘判定同口径，但不依赖任何 WinForms 成员。</summary>
        internal static int EdgeHitTest(IntPtr main, Point screen)
        {
            if (IsZoomed(main)) return 0;
            var r = GetWindowRectP(main);
            var edge = Math.Max(5, 6 * GetDpiForWindow(main) / 96);
            var left = screen.X <= r.X + edge;
            var right = screen.X >= r.X + r.Width - edge - 1;
            var top = screen.Y <= r.Y + edge;
            var bottom = screen.Y >= r.Y + r.Height - edge - 1;
            if (left && top) return HTTOPLEFTZ;
            if (right && top) return HTTOPRIGHTZ;
            if (left && bottom) return HTBOTTOMLEFTZ;
            if (right && bottom) return HTBOTTOMRIGHTZ;
            if (left) return HTLEFTZ;
            if (right) return HTRIGHTZ;
            if (top) return HTTOPZ;
            if (bottom) return HTBOTTOMZ;
            return 0;
        }

        private static IntPtr CursorFor(int ht) => ht switch
        {
            HTLEFTZ or HTRIGHTZ => (IntPtr)32644,      // IDC_SIZEWE
            HTTOPZ or HTBOTTOMZ => (IntPtr)32645,      // IDC_SIZENS
            HTTOPLEFTZ or HTBOTTOMRIGHTZ => (IntPtr)32642, // IDC_SIZENWSE
            _ => (IntPtr)32643,                        // IDC_SIZENESW
        };
    }

    private readonly List<RenderWndHook> _renderHooks = new();

    /// <summary>挂接 CEF 窗口的缩放转发：渲染窗口（Chrome_RenderWidgetHostHWND，
    /// 页面可有多个——跨进程 iframe 各一个）负责转发左键拖拽；CEF 容器窗口
    /// （Chrome_WidgetWin_0）负责光标——它才是 WM_SETCURSOR 的处理者，会在每次
    /// 鼠标移动后把光标改回页面样式。新的 HWND 随导航出现，持续轮询补钩。</summary>
    private void HookRenderWindow(ChromiumWebBrowser browser)
    {
        var hooked = new HashSet<IntPtr>();
        var timer = new System.Windows.Forms.Timer { Interval = 250 };
        var tries = 0;
        timer.Tick += (s, e) =>
        {
            tries++;
            // 标签已关/窗体正在关：浏览器控件随时可能销毁，停止轮询防止 ObjectDisposed
            if (browser.IsDisposed || IsDisposed)
            {
                timer.Stop(); timer.Dispose();
                return;
            }
            try
            {
                foreach (var hwnd in FindCefHosts(browser.Handle).Where(h => !hooked.Contains(h)))
                {
                    hooked.Add(hwnd);
                    _renderHooks.Add(new RenderWndHook(Handle, hwnd, () => hooked.Remove(hwnd)));
                }
            }
            catch { /* 控件销毁竞态：下个 tick 的 IsDisposed 检查会收尾 */ }
            if (tries >= 80) { timer.Stop(); timer.Dispose(); }
        };
        timer.Start();
    }

    /// <summary>枚举 root 下所有 CEF 容器/渲染窗口（光标与拖拽的拦截点）。</summary>
    private static List<IntPtr> FindCefHosts(IntPtr root)
    {
        var found = new List<IntPtr>();
        EnumChildWindows(root, (h, _) =>
        {
            var sb = new StringBuilder(64);
            GetClassNameW(h, sb, 64);
            var cls = sb.ToString();
            if ((cls == "Chrome_RenderWidgetHostHWND" || cls == "Chrome_WidgetWin_0") && !found.Contains(h))
                found.Add(h);
            return true; // EnumChildWindows 自带深度遍历全部后代
        }, IntPtr.Zero);
        return found;
    }

    private delegate bool EnumChildProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hwnd, EnumChildProc proc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder sb, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT p);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT r);

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern int GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr cursor);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp);

    private void ToggleMaximize()
    {
        WindowState = WindowState == FormWindowState.Maximized
            ? FormWindowState.Normal
            : FormWindowState.Maximized;
    }

    private void UpdateMaximizeGlyph()
    {
        var glyph = WindowState == FormWindowState.Maximized ? "\uE923" : "\uE922";
        if (_maxButton.Glyph != glyph)
        {
            _maxButton.Glyph = glyph;
            _maxButton.Invalidate();
        }
    }

    // ==================== 自检 ====================

    /// <summary>隐私防护自检：在页面里执行与指纹检测站同类的探测，结果打到 stdout。</summary>
    private async void RunSelfTest()
    {
        var tab = _tabs.FirstOrDefault();
        if (tab == null) return;
        for (var i = 0; i < 100 && !tab.Browser.CanExecuteJavascriptInMainFrame; i++)
            await Task.Delay(200);
        await Task.Delay(1500); // 等 CDP 覆盖与垫片全部就绪

        // 首页文档可能早于垫片注册，导航到新文档再探测（垫片在 document_start 生效）
        tab.Browser.Load("https://www.nuget.org");
        await Task.Delay(6000);
        await Task.Delay(1000);

        const string probe =
            "(function(){var r={};r.fpTag=typeof window.__riFp__;r.tzTag=typeof window.__riTz__;" +
            "r.us=typeof window.__riUserScriptTest!=='undefined'?window.__riUserScriptTest:'none';" +
            "r.lang=navigator.language;r.langs=(navigator.languages||[]).join(',');" +
            "r.dm=navigator.deviceMemory;" +
            "r.uadPlat=(navigator.userAgentData&&navigator.userAgentData.platform)||null;" +
            "r.tz=Intl.DateTimeFormat().resolvedOptions().timeZone;" +
            "r.dateStr=new Date(2026,0,15,12,0,0).toString();r.offset=new Date(2026,0).getTimezoneOffset();" +
            "function w(fam){var c=document.createElement('canvas');var x=c.getContext('2d');" +
            "x.font='72px \\\"'+fam+'\\\", monospace';return x.measureText('mmmmmmmmmm').width;}" +
            "var base=w('__no_such_font_xyz__');" +
            "r.yaheiEq=Math.abs(w('Microsoft YaHei')-base)<0.01;" +
            "r.pingfangEq=Math.abs(w('PingFang SC')-base)<0.01;" +
            "r.simsunEq=Math.abs(w('SimSun')-base)<0.01;" +
            "r.segoeDiff=Math.abs(w('Segoe UI')-base)>0.01;" +
            // Canvas 指纹稳定性：噪声由内容决定（同内容任意画布/重画后读取必须一致）
            "function fpDraw(cv){var x=cv.getContext('2d');x.clearRect(0,0,100,40);x.textBaseline='top';" +
            "x.font='14px Arial';x.fillStyle='#f60';x.fillRect(0,0,60,20);x.fillStyle='#069';" +
            "x.fillText('RiBrowser 指纹稳定性',2,2);return cv.toDataURL();}" +
            "var A=document.createElement('canvas');A.width=100;A.height=40;" +
            "var B=document.createElement('canvas');B.width=100;B.height=40;" +
            "r.cvSame=fpDraw(A)===fpDraw(A);" +   // 同画布重画两次
            "r.cvCross=fpDraw(A)===fpDraw(B);return JSON.stringify(r);})()"; // 两个画布同内容

        // Worker 一致性探针：Worker 上下文不经过脚本垫片，读到的必须是浏览器原生值，
        // 页面（CDP/垫片）与 Worker（CefSettings.AcceptLanguageList）三端需一致
        const string workerStart =
            "(function(){window.__riWk='pending';try{" +
            "var src='postMessage({langs:(navigator.languages||[]).join(\\\",\\\"),dm:navigator.deviceMemory," +
            "offset:new Date(2026,0).getTimezoneOffset(),ua:navigator.userAgent});';" +
            "var w=new Worker(URL.createObjectURL(new Blob([src],{type:'application/javascript'})));" +
            "w.onmessage=function(e){window.__riWk=JSON.stringify(e.data);};" +
            "w.onerror=function(){window.__riWk='error'};}catch(e){window.__riWk='ex:'+e.message;}" +
            "return 'started';})()";

        try
        {
            var resp = await tab.Browser.EvaluateScriptAsync(probe);
            Console.WriteLine("SELFTEST " + (resp.Success ? resp.Result?.ToString() : "EVAL_FAIL:" + resp.Message));

            await tab.Browser.EvaluateScriptAsync(workerStart);
            await Task.Delay(2500);
            var wk = await tab.Browser.EvaluateScriptAsync("window.__riWk");
            Console.WriteLine("SELFTEST WORKER " + (wk.Success ? wk.Result?.ToString() : "EVAL_FAIL:" + wk.Message));

            // 对比页面与 Worker 的关键值
            try
            {
                var page = System.Text.Json.JsonDocument.Parse(resp.Result!.ToString()!).RootElement;
                var work = System.Text.Json.JsonDocument.Parse(wk.Result!.ToString()!).RootElement;
                var pageLangs = page.GetProperty("langs").ToString();
                var workLangs = work.GetProperty("langs").GetString();
                var pageDm = page.GetProperty("dm").GetDouble();
                var workDm = work.GetProperty("dm").GetDouble();
                Console.WriteLine($"SELFTEST WORKER-CHECK langs一致={pageLangs == workLangs} (page={pageLangs} worker={workLangs}) " +
                    $"deviceMemory一致={Math.Abs(pageDm - workDm) < 0.01} (page={pageDm} worker={workDm})");
            }
            catch (Exception ex) { Console.WriteLine("SELFTEST WORKER-COMPARE FAIL " + ex.Message); }
        }
        catch (Exception ex)
        {
            Console.WriteLine("SELFTEST EXCEPTION " + ex.Message);
        }
        Close();
    }

    // ==================== 标签管理 ====================

    internal BrowserTab AddTab(string? url = null, bool select = true)
    {
        url ??= HomeUrl();

        var browser = new ChromiumWebBrowser(url)
        {
            Dock = DockStyle.Fill,
            LifeSpanHandler = new LifeSpanHandler(this),
            KeyboardHandler = new KeyboardHandler(this),
            DownloadHandler = new DownloadHandler(this),
            MenuHandler = new ContextMenuHandler(this),
            RequestHandler = new GlobalRequestHandler(),
            PermissionHandler = new GeolocationPermissionHandler(),
        };
        // 反机器人检测：CefSharp 默认向每个页面注入 window.CefSharp / window.cefSharp
        // 全局对象（BindObjectAsync 等），browserscan 等 bot 检测以此判定"机器人"。
        // v156 该开关在 JS 对象仓库上（不在 CefSettings）：关闭后对象不再注入
        browser.JavascriptObjectRepository.Settings.JavascriptBindingApiEnabled = false;

        var item = new TabItem();
        // 新建标签立即按当前 DPI 缩放高度；ApplyChromeDpi 只覆盖启动时已存在的标签
        item.SetChromeScale(UiScale);
        var tab = new BrowserTab { Browser = browser, TabItem = item };

        // Clash 断线/锁定内页：代理类加载错误换成中文引导页。
        // 锁定态（无可用代理且无内核）：显示锁定页、禁止访问（无自动重试），
        // 记住来源地址，解锁后由轮询统一恢复；非锁定态：通道瞬断，5s 自动重试
        browser.LoadError += (s, e) => Ui(() =>
        {
            if (PrivacySettings.Current.ProxyMode != "clash" || !e.Frame.IsMain)
                return;
            var onLockPage = e.FailedUrl.Contains(LockUrl, StringComparison.OrdinalIgnoreCase)
                && e.ErrorCode == CefErrorCode.NameNotResolved;
            if (e.ErrorCode is not (CefErrorCode.ProxyConnectionFailed or CefErrorCode.TunnelConnectionFailed)
                && !onLockPage)
                return;
            if (_netState == NetState.Locked)
            {
                if (!onLockPage) _lockOrigin[browser] = e.FailedUrl;
                browser.LoadHtml(NetLockPageHtml(ClashPanel.Url + "/config"), LockUrl);
            }
            else
            {
                browser.LoadHtml(ProxyOfflinePageHtml(e.ErrorText), e.FailedUrl);
            }
        });

        browser.TitleChanged += (s, e) => Ui(() =>
        {
            if (!string.IsNullOrWhiteSpace(e.Title))
            {
                item.Title = e.Title;
                History.UpdateTitle(tab.Browser.Address, e.Title); // 历史条目标题回填
            }
        });
        browser.AddressChanged += (s, e) => Ui(() =>
        {
            if (_activeTab == tab) _addressBar.Text = e.Address;
            _addressBar.IsStarred = _activeTab == tab && Bookmarks.Contains(e.Address);
            History.Record(e.Address); // 地址栏建议：导航即记录
            // 导航提交后重判白名单并重下发 CDP 覆盖（已注册垫片自动对新文档生效）
            _privacy.ApplyOnNavigation(tab);
            SessionStore.Save(); // 会话恢复：地址变化立即落盘
        });
        browser.LoadingStateChanged += (s, e) => Ui(() =>
        {
            tab.IsLoading = e.IsLoading;
            item.IsLoading = e.IsLoading;
            if (_activeTab == tab) UpdateNavState(tab);
        });

        item.CloseRequested += () => CloseTab(tab);
        item.SelectRequested += () => SelectTab(tab);

        // 隐私防护：浏览器就绪后下发 CDP 覆盖 + 上下文创建时注入垫片
        _privacy.Attach(tab);
        // 边缘缩放：渲染子窗口盖满客户区导致主窗体收不到边缘命中测试，钩上透传
        HookRenderWindow(browser);

        lock (_tabsLock) _tabs.Add(tab);
        _tabFlow.Controls.Add(item);
        _tabFlow.Controls.SetChildIndex(_plusButton, _tabFlow.Controls.Count - 1); // 保持 ＋ 在末尾
        _content.Controls.Add(browser);

        UpdateTabWidths();
        if (select) SelectTab(tab);
        else
        {
            // 后台标签（右键"在新标签页中打开"/会话恢复）：不切换当前页。
            // 窗体构造期创建的不可见控件不会被 WinForms 创建句柄，而 CEF 浏览器
            // 是在 HandleCreated 时才初始化的——必须先强制创建句柄再隐藏，
            // 否则该标签的浏览器永远不启动（标题停在"新标签页"、无 DevTools 目标）。
            _ = browser.Handle;
            browser.Visible = false;
        }
        SessionStore.Save();
        return tab;
    }

    /// <summary>会话恢复用：当前窗口全部标签的 (地址, 是否选中) 快照。</summary>
    internal IEnumerable<(string Url, bool Active)> TabSnapshot()
        => _tabs.Select(t => (t.Browser.Address ?? string.Empty, ReferenceEquals(t, _activeTab)));

    internal void CloseTab(BrowserTab tab)
    {
        var index = _tabs.IndexOf(tab);
        if (index < 0) return;
        RiLog.Info($"[tabs] CloseTab 剩余={_tabs.Count - 1} url={tab.Browser.Address}");

        // 记入"最近关闭"，供标签条下拉箭头重新打开
        ClosedTabs.Push(tab.TabItem.Title, tab.Browser.Address);
        _privacy.Detach(tab);
        lock (_tabsLock) _tabs.RemoveAt(index);
        _tabFlow.Controls.Remove(tab.TabItem);
        _content.Controls.Remove(tab.Browser);
        try { tab.Browser.Dispose(); }
        catch { /* 关闭中的 CEF 控件可能已在释放 */ }
        tab.TabItem.Dispose();

        if (_tabs.Count == 0)
        {
            RiLog.Info("[win] 最后一个标签已关 → 关闭窗口退出");
            Close(); // 最后一个标签关闭时退出浏览器
            return;
        }

        UpdateTabWidths();
        SelectTab(_tabs[Math.Min(index, _tabs.Count - 1)]);
        SessionStore.Save(); // 关标签后落盘（最后标签走 Close() 由窗口注销处理）
    }

    private void SelectTab(BrowserTab tab)
    {
        _activeTab = tab;
        foreach (var t in _tabs)
        {
            var selected = ReferenceEquals(t, tab);
            t.TabItem.Selected = selected;
            t.Browser.Visible = selected;
        }
        _addressBar.Text = tab.Browser.Address ?? string.Empty;
        _addressBar.IsStarred = Bookmarks.Contains(tab.Browser.Address);
        UpdateNavState(tab);
        ActiveControl = tab.Browser;
        tab.Browser.Focus();
        SessionStore.Save(); // 切换标签改变恢复时的选中项
    }

    private void CycleTab(int delta)
    {
        if (_tabs.Count < 2 || _activeTab == null) return;
        var i = _tabs.IndexOf(_activeTab);
        SelectTab(_tabs[(i + delta + _tabs.Count) % _tabs.Count]);
    }

    private void UpdateTabWidths()
    {
        if (_tabs.Count == 0) return;
        // 可用宽度 = 标签条宽 - 内边距(12) - ＋按钮(30+边距3)，全部按 DPI 缩放
        // 标签 Margin 为 0，全部标签平分剩余空间；无论多少标签，＋ 按钮始终在可视区内
        var available = _tabFlow.ClientSize.Width - S(45);
        var width = Math.Clamp(available / _tabs.Count, S(32), S(190));
        foreach (var t in _tabs) t.TabItem.Width = width;
    }

    // ==================== 导航 ====================

    private void NavigateFromAddressBox()
    {
        var target = NormalizeAddress(_addressBar.Box.Text);
        if (target == null) return;
        _addressBar.Box.Text = target;
        _activeTab?.Browser.Load(target);
    }

    internal static string? NormalizeAddress(string input)
    {
        var s = input.Trim();
        if (s.Length == 0) return null;

        if (Uri.TryCreate(s, UriKind.Absolute, out var uri) && IsNavigableScheme(uri))
        {
            return uri.AbsoluteUri;
        }

        var looksLikeHost = !s.Contains(' ') &&
            (s.Contains('.') || s.StartsWith("localhost", StringComparison.OrdinalIgnoreCase) || s.Contains(':'));
        if (looksLikeHost)
        {
            return s.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
                ? "http://" + s
                : "https://" + s;
        }
        return string.Format(SearchTemplate(), Uri.EscapeDataString(s));
    }

    private void UpdateNavState(BrowserTab tab)
    {
        _backButton.Enabled = tab.Browser.CanGoBack;
        _forwardButton.Enabled = tab.Browser.CanGoForward;
        _reloadButton.Glyph = tab.IsLoading ? "\uE711" : "\uE72C";
        _reloadButton.Invalidate();
    }

    // ==================== 供 CEF 处理器跨线程调用 ====================

    /// <summary>本窗口全部标签（标签下拉列表用）。</summary>
    internal IReadOnlyList<BrowserTab> Tabs => _tabs;

    internal BrowserTab? ActiveTab => _activeTab;

    internal void UiSelectTab(BrowserTab tab) => Ui(() => SelectTab(tab));

    internal void UiActivate() => Ui(() => { Show(); Activate(); });

    internal void UiShowTabSearchMenu() => Ui(ShowTabSearchMenu);

    /// <summary>标签条左侧下拉箭头：Chrome 风格标签搜索浮层（过滤/切换/重开/关闭）。</summary>
    private void ShowTabSearchMenu() => TabSearchForm.ShowBelow(_tabSearchButton, this);

    internal void UiAddTab(string? url, bool select = true) => Ui(() => AddTab(url, select));

    /// <summary>该浏览器控件是否为本窗口托管的标签（CEF 原生弹窗不是）。</summary>
    internal bool IsOurTab(IWebBrowser browser)
    {
        lock (_tabsLock) return _tabs.Any(t => ReferenceEquals(t.Browser, browser));
    }

    /// <summary>页面 window.close() 的接管：按浏览器控件找到所属标签走正常关闭路径。</summary>
    internal void UiCloseBrowserTab(IWebBrowser browser) => Ui(() =>
    {
        var tab = _tabs.FirstOrDefault(t => ReferenceEquals(t.Browser, browser));
        if (tab == null) return; // 已在关闭流程中（窗口退出/重复回调）
        CloseTab(tab);
    });
    internal void UiCloseActiveTab() => Ui(() => { if (_activeTab != null) CloseTab(_activeTab); });
    internal void UiOpenPrivacySettings() => Ui(ShowPrivacySettings);
    internal void UiToggleBookmark() => Ui(ToggleBookmarkForCurrent);
    internal void UiToggleBookmarkBar() => Ui(ToggleBookmarkBar);
    internal void UiShowDevToolsActive() => Ui(ShowDevToolsForActiveTab);
    internal void UiShowDevTools(IWebBrowser browser, int x, int y)
        => Ui(() => browser.ShowDevTools(null, x, y));
    internal void UiFocusAddress() => Ui(() => { _addressBar.Box.Focus(); _addressBar.Box.SelectAll(); });
    internal void UiReload() => Ui(() => _activeTab?.Browser.Reload());
    internal void UiBack() => Ui(() => _activeTab?.Browser.Back());
    internal void UiForward() => Ui(() => _activeTab?.Browser.Forward());
    internal void UiCycleTab(int delta) => Ui(() => CycleTab(delta));

    internal void ShowMessage(string text) => Ui(() =>
        MessageBox.Show(this, text, "Ri 浏览器", MessageBoxButtons.OK, MessageBoxIcon.Information));

    internal void Ui(Action action)
    {
        if (IsDisposed) return;
        try
        {
            if (IsHandleCreated && InvokeRequired) BeginInvoke(action);
            else action();
        }
        catch (ObjectDisposedException) { }
    }

    // ==================== 收藏夹 ====================

    private void ToggleBookmarkForCurrent()
    {
        if (_activeTab is not { } tab) return;
        var url = tab.Browser.Address;
        if (string.IsNullOrEmpty(url)) return;
        Bookmarks.Toggle(url, tab.TabItem.Title);
        _addressBar.IsStarred = Bookmarks.Contains(url);
    }

    private void ToggleBookmarkBar()
    {
        _showBookmarkBar = !_showBookmarkBar;
        _bookmarkBar.Visible = _showBookmarkBar && Bookmarks.Items.Count > 0;
        PrivacySettings.Current.ShowBookmarkBar = _showBookmarkBar;
        PrivacySettings.Current.Save();
    }

    // ==================== 菜单 / 设置 ====================

    private void ShowMainMenu(bool expandMore = false)
    {
        var menu = MenuStyle.Create();
        menu.Items.Add(MenuStyle.Item("新建标签页", "\uE710", (s, e) => AddTab(), "Ctrl+T"));
        menu.Items.Add(new ToolStripSeparator());

        var more = MenuStyle.Item("更多工具", null, null);
        more.DropDownItems.Add(MenuStyle.Item("开发者工具", "\uEC7A", (s, e) => ShowDevToolsForActiveTab(), "F12"));
        more.DropDownItems.Add(MenuStyle.Item("用户脚本", "\uEC7A", (s, e) => ShowScriptsForm()));
        MenuStyle.HookSubmenuRounding(more);
        menu.Items.Add(more);

        menu.Items.Add(MenuStyle.Item("隐私防护", "\uE713", (s, e) => ShowPrivacySettings(), "Ctrl+,"));
        // 代理配置已并入「隐私防护」(Ctrl+,) 的代理分组；菜单这里给控制台直达入口
        menu.Items.Add(MenuStyle.Item("Clash 控制台", "\uE895", async (s, e) => await OpenClashPanelAsync()));
        menu.Items.Add(MenuStyle.Item("书签栏", "\uE735", (s, e) => ToggleBookmarkBar(), "Ctrl+Shift+B"));
        menu.Items.Add(MenuStyle.Item(
            AdBlocker.Enabled ? "广告拦截 ✓（已拦截 " + AdBlocker.BlockedCount + " 次）" : "广告拦截 ✗（已关闭）",
            "\uE72E", (s, e) => AdBlocker.Toggle()));

        // 搜索引擎切换（当前项打勾，即时生效并持久化）
        var engineMenu = MenuStyle.Item("搜索引擎", "\uE721", null);
        foreach (var se in SearchEngines)
        {
            var id = se.Id;
            engineMenu.DropDownItems.Add(MenuStyle.Item(
                PrivacySettings.Current.SearchEngine == id ? se.Name + "  ✓" : se.Name, null,
                (s, e) =>
                {
                    if (PrivacySettings.Current.SearchEngine == id) return;
                    PrivacySettings.Current.SearchEngine = id;
                    PrivacySettings.Current.Save();
                }));
        }
        MenuStyle.HookSubmenuRounding(engineMenu);
        menu.Items.Add(engineMenu);

        // 启动页配置：预设 / 当前页 / 自定义输入，当前项打勾
        var homeMenu = MenuStyle.Item("启动页", "\uE80F", null);
        var homeNow = HomeUrl();
        foreach (var (name, url) in new[] { ("Google", "https://www.google.com"), ("百度", "https://www.baidu.com") })
        {
            var u = url;
            homeMenu.DropDownItems.Add(MenuStyle.Item(
                homeNow == u ? name + "  ✓" : name, null,
                (s, e) => SetHomePage(u)));
        }
        if (_activeTab is { } cur && !string.IsNullOrEmpty(cur.Browser.Address) && cur.Browser.Address.StartsWith("http"))
            homeMenu.DropDownItems.Add(MenuStyle.Item("使用当前页", null, (s, e) => SetHomePage(cur.Browser.Address)));
        homeMenu.DropDownItems.Add(MenuStyle.Item("自定义…", null, (s, e) => PromptCustomHomePage()));
        MenuStyle.HookSubmenuRounding(homeMenu);
        menu.Items.Add(homeMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(MenuStyle.Item("关于 Ri 浏览器", "\uE946", (s, e) => ShowAbout()));
        menu.Items.Add(MenuStyle.Item("退出", "\uE7E8", (s, e) => Close()));
        MenuStyle.EqualizeWidths(menu);
        menu.Show(_menuButton, new Point(0, _menuButton.Height));
        if (expandMore) BeginInvoke(() => more.ShowDropDown()); // 等主菜单布局完成再展开，避免旧坐标
    }

    // ---- 代理即时生效 ----

    private string _proxySig = "";              // 上次已应用的代理字段签名（非代理修改不触发）
    private CancellationTokenSource? _proxyCts; // 防抖：连续输入（逐字符改地址）只应用最后一次

    /// <summary>代理字段指纹：onChanged 对所有设置触发，仅代理相关字段变化才需要切换链路。</summary>
    private static string ProxySignature(PrivacySettings s) => string.Join("|",
        s.ProxyMode, s.ProxyScheme, s.ProxyHost, s.ProxyPort.ToString(),
        s.ClashApiBase, s.ClashApiSecret, s.ClashMixedPort.ToString(),
        s.ClashSubUrl, s.ClashManualYaml, s.ClashSelfManage.ToString(),
        s.SbProto, s.SbServer, s.SbPort.ToString(), s.SbPassword, s.SbUuid, s.SbMethod,
        s.SbLocalPort.ToString(), s.SbTls, s.SbSni, s.SbFingerprint, s.SbAlpn, s.SbInsecure,
        s.SbRealityKey, s.SbRealitySid, s.SbTransport, s.SbWsPath, s.SbWsHost, s.SbFlow,
        s.SbTun, s.SbDnsProtect, s.SbDnsServer);

    /// <summary>设置页保存回调：代理即时生效——sing-box 按需重启（重写 config.json），
    /// 再把 CEF 'proxy' preference 切到新链路，无需重启浏览器。0.8s 防抖吸收逐字符输入。</summary>
    private async void ApplyProxyNow()
    {
        var s = PrivacySettings.Current;
        var sig = ProxySignature(s);
        if (sig == _proxySig) return;
        _proxySig = sig;

        _proxyCts?.Cancel();
        var cts = new CancellationTokenSource();
        _proxyCts = cts;
        try
        {
            await Task.Delay(800, cts.Token);
            if (ProxyManager.NeedsCore(s))
                await Task.Run(() => ProxyManager.StartCore(s)); // 旧 sing-box 模式（已下线，保留迁移）
            else
            {
                if (ProxyManager.CoreRunning) await Task.Run(ProxyManager.StopCore);
                // clash 模式一律内置内核（Start 重启以应用新配置；零节点常驻直连占位）
                if (s.ProxyMode == "clash")
                    await Task.Run(() => ClashCoreManager.Start(s));
                else if (ClashCoreManager.CoreRunning)
                    await Task.Run(ClashCoreManager.Stop);
            }
            await ProxyManager.ApplyCefProxyAsync(s);
            _ = CheckClashAsync(); // 模式切换后立刻刷新盾牌状态
        }
        catch (OperationCanceledException) { /* 被后续修改取代 */ }
        catch (Exception ex)
        {
            RiLog.Error("ApplyProxy", ex);
            MessageBox.Show(this, "代理设置未能生效：" + ex.Message, "Ri 浏览器",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ---- Clash 状态盾牌 + 控制弹层 ----

    private System.Windows.Forms.Timer? _clashTimer;
    private bool _clashBusy; // 轮询/刷新重入保护

    // ---- 网络状态机：Clash 链（正常走本机/自管内核）· 外部通道（其他代理软件已打通
    //// 内网，浏览器直连放行 + 30s 心跳监督）· 锁定（无任何可用通道，禁止全部网页访问）----
    private enum NetState { ClashChain, ExternalPassage, Locked }
    private NetState _netState = NetState.ClashChain;
    private readonly Dictionary<ChromiumWebBrowser, string> _lockOrigin = new(); // 被锁页 → 原地址
    private System.Windows.Forms.Timer? _passageTimer; // 外部通道 30s 心跳
    private const string LockUrl = "http://ri.browser.locked/";

    private void SetNetState(NetState st)
    {
        if (_netState == st) return;
        var wasLocked = _netState == NetState.Locked;
        _netState = st;
        _passageTimer?.Stop();
        if (st == NetState.Locked)
        {
            RiLog.Info("[gate] 网络锁定：无可用通道，禁止全部网页访问");
            // 探测不通 → 自动打开控制台（内置内核常驻，面板可连；用户在
            // 控制台右下角「手动节点配置」加公司代理，保存即解锁）
            _ = OpenClashPanelIfNeededAsync();
            return;
        }
        if (wasLocked)
        {
            RiLog.Info(st == NetState.ExternalPassage
                ? "[gate] 外部通道确认（探测地址直连可达），解除锁定：直连放行 + 30s 心跳"
                : "[gate] 通道就绪，解除网络锁定");
            RestoreLockedTabs();
        }
    }

    /// <summary>锁定时自动开控制台：已有面板页则不重复开（防心跳反复锁定时刷屏）。</summary>
    private async Task OpenClashPanelIfNeededAsync()
    {
        if (!await ClashPanel.EnsureFilesAsync(PrivacySettings.Current)) return;
        var url = ClashPanel.Url;
        if (_tabs.Any(t => !t.Browser.IsDisposed
                && t.Browser.Address.StartsWith(url, StringComparison.OrdinalIgnoreCase)))
            return;
        UiAddTab(url, true);
    }

    private void RestoreLockedTabs()
    {
        foreach (var (b, url) in _lockOrigin)
            if (!b.IsDisposed && url.Length > 0) b.Load(url);
        _lockOrigin.Clear();
    }

    /// <summary>直接探测内网地址：走系统默认网络路径（系统代理 / TUN 透明接管 / 直连
    /// 都覆盖）——任何 HTTP 应答（含 4xx/5xx）都算"地址可达"。</summary>
    private static async Task<bool> ProbeDirectAsync(PrivacySettings s)
    {
        var url = s.ClashProbeUrl.Trim();
        if (url.Length == 0) return false;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            return true;
        }
        catch { return false; }
    }

    /// <summary>进入外部通道态：其他代理软件已把内网打通——浏览器不启动自己的代理，
    /// 直连放行，30s 心跳盯探测地址。</summary>
    private async Task EnterExternalPassageAsync()
    {
        await ProxyManager.ApplyCefProxyDirectAsync();
        SetNetState(NetState.ExternalPassage);
        _passageTimer ??= new System.Windows.Forms.Timer { Interval = 30000 };
        _passageTimer.Tick += OnPassageHeartbeat;
        _passageTimer.Start();
    }

    private async void OnPassageHeartbeat(object? sender, EventArgs e)
    {
        if (_netState != NetState.ExternalPassage) return;
        if (await ProbeDirectAsync(PrivacySettings.Current)) return;

        // 外部通道断了：弹窗 + 立即重锁（流量指回 clash 目标——死端口 → 全部失败 → 锁定页）
        _passageTimer!.Stop();
        RiLog.Info("[gate] 外部通道心跳失败，重新锁定网络访问");
        _netState = NetState.Locked;
        _ = ProxyManager.ApplyCefProxyAsync(PrivacySettings.Current);
        MessageBox.Show(this,
            $"内网通道已断开：探测地址不再可达。\n浏览器已重新锁定全部网络访问，通道恢复后自动解锁。",
            "内网通道断开", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>锁定内页（不自动重试：解锁由状态轮询统一触发，恢复原地址）。</summary>
    private static string NetLockPageHtml(string? configUrl)
    {
        var entry = configUrl != null
            ? "<a href=\"" + configUrl + "\" style=\"display:inline-block;margin-top:16px;padding:11px 30px;" +
              "border-radius:22px;background:#1a73e8;color:#fff;font-size:14px;text-decoration:none\">" +
              "去添加节点配置（控制台）</a>"
            : "<p>入口：地址栏右侧盾牌 → 「手动添加节点配置…」</p>";
        return """
            <!DOCTYPE html><html><head><meta charset="utf-8"><title>网络访问已锁定</title>
            <style>
              body{margin:0;font-family:"Segoe UI","Microsoft YaHei",sans-serif;background:#f8f9fa;
                   display:flex;align-items:center;justify-content:center;height:100vh;color:#202124}
              .card{background:#fff;border:1px solid #dadce0;border-radius:12px;padding:40px 48px;
                    max-width:500px;box-shadow:0 1px 3px rgba(60,64,67,.2);text-align:center}
              .lock{font-size:44px;line-height:1}
              h1{font-size:20px;margin:14px 0 14px}
              p{font-size:14px;line-height:1.8;color:#5f6368;margin:0 0 8px;text-align:left}
            </style></head><body><div class="card">
            <div class="lock">&#128274;</div>
            <h1>网络访问已锁定</h1>
            <p>内网探测地址不可达：没有可用的代理通道，所有网页访问已被禁止——不会发生任何直连泄漏。</p>
            <p>在节点配置里添加公司代理并保存后，浏览器会<b>自动解锁</b>并恢复本页：</p>
            @ENTRY@
            </div></body></html>
            """.Replace("@ENTRY@", entry);
    }

    private static string ClashModeText(string mode) => mode switch
    {
        "global" => "全局", "direct" => "直连", _ => "规则",
    };

    private void InitClashPolling()
    {
        _tips.SetToolTip(_clashButton, "Clash 代理状态（检查中…）");
        // 8s：状态刷新 + 通道徽标对账（/connections 按域名匹配各标签页）
        _clashTimer = new System.Windows.Forms.Timer { Interval = 8000 };
        _clashTimer.Tick += async (_, _) => await CheckClashAsync();
        _clashTimer.Start();
        _ = CheckClashAsync();
        if (PrivacySettings.Current.ProxyMode == "clash")
            _ = ClashPanel.EnsureFilesAsync(PrivacySettings.Current); // 预热控制台（含首次下载）
    }

    /// <summary>Clash 通道离线时的替代错误页（以原 URL 为虚拟地址加载，
    /// 每 5s 自动重试，通道恢复后下一次 reload 即正常打开）。</summary>
    private static string ProxyOfflinePageHtml(string rawError)
    {
        return """
            <!DOCTYPE html><html><head><meta charset="utf-8"><title>代理通道离线</title>
            <style>
              body{margin:0;font-family:"Segoe UI","Microsoft YaHei",sans-serif;background:#f8f9fa;
                   display:flex;align-items:center;justify-content:center;height:100vh;color:#202124}
              .card{background:#fff;border:1px solid #dadce0;border-radius:12px;padding:40px 48px;
                    max-width:480px;box-shadow:0 1px 3px rgba(60,64,67,.2);text-align:center}
              h1{font-size:20px;margin:0 0 14px}
              p{font-size:14px;line-height:1.7;color:#5f6368;margin:0 0 8px}
              button{margin-top:18px;padding:9px 26px;border:none;border-radius:18px;background:#1a73e8;
                     color:#fff;font-size:14px;cursor:pointer}
              .err{font-size:12px;color:#9aa0a6;margin-top:14px}
            </style></head><body><div class="card">
            <h1>代理通道离线</h1>
            <p>本浏览器的流量必须经 Clash 通道转发，当前通道不可达，页面暂时无法打开。</p>
            <p>通道恢复后本页会自动重试；也可以点标签栏左侧盾牌查看状态或启动 Clash Verge。</p>
            <button onclick="location.reload()">立即重试</button>
            <p class="err">@ERR@</p>
            </div>
            <script>setTimeout(function(){location.reload()},5000);</script>
            </body></html>
            """.Replace("@ERR@", rawError);
    }

    private void ClearTabBadges()
    {
        foreach (var t in _tabs)
            if (!t.TabItem.IsDisposed) t.TabItem.RoutedProxied = null;
    }

    /// <summary>通道徽标对账：把 mihomo /connections 按域名匹配到各标签页
    /// （精确相等或互为父子域，www.x.com ↔ x.com），设置绿/灰点与悬停明细。</summary>
    private void UpdateTabBadges(List<ClashConnection> conns)
    {
        foreach (var t in _tabs)
        {
            if (t.TabItem.IsDisposed) continue;
            var host = "";
            try
            {
                if (Uri.TryCreate(t.Browser.Address, UriKind.Absolute, out var u)) host = u.Host;
            }
            catch { /* 地址尚未就绪 */ }
            var matches = host.Length == 0
                ? Array.Empty<ClashConnection>()
                : conns.Where(c =>
                      string.Equals(c.Host, host, StringComparison.OrdinalIgnoreCase)
                      || c.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase)
                      || host.EndsWith("." + c.Host, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 0)
            {
                t.TabItem.RoutedProxied = null;
                continue;
            }
            var proxied = matches.Any(m => m.Chains.Count > 0
                && !m.Chains.Any(c => c is "DIRECT" or "REJECT" or "REJECT-DROP"));
            t.TabItem.RoutedProxied = proxied;
            var chain = string.Join(" → ", matches[0].Chains.AsEnumerable().Reverse());
            _tips.SetToolTip(t.TabItem, proxied
                ? $"通道：代理 · {chain}\n规则：{matches[0].Rule}"
                : $"通道：直连\n规则：{matches[0].Rule}");
        }
    }

    /// <summary>打开浏览器内 Clash 控制台（metacubexd 面板，clash://console）。</summary>
    private async Task OpenClashPanelAsync()
    {
        if (!await ClashPanel.EnsureFilesAsync(PrivacySettings.Current))
        {
            MessageBox.Show(this, "控制台面板下载失败：需要能访问 GitHub（直连或经当前 Clash）。",
                "Ri 浏览器", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        UiAddTab(ClashPanel.Url, true);
    }

    /// <summary>重载已打开的控制台标签页（语言切换等配置变更后立即生效）。</summary>
    private void ReloadPanelTabs()
    {
        var b = ClashPanel.Url;
        foreach (var t in _tabs)
            if (!t.Browser.IsDisposed
                && t.Browser.Address.StartsWith(b, StringComparison.OrdinalIgnoreCase))
                t.Browser.Reload();
    }

    /// <summary>按 CEF 浏览器标识找 IBrowser（clash://console 的 WS 桥向真实
    /// 标签页推送帧用——scheme 处理器里拿到的包装是短命对象，只能按标识回查）。</summary>
    internal static CefSharp.IBrowser? FindBrowserById(int id)
    {
        foreach (var w in Program.Windows)
            foreach (var t in w._tabs)
            {
                if (t.Browser.IsDisposed) continue;
                if (t.Browser.TryGetBrowserCoreById(id, out var ib)) return ib;
            }
        return null;
    }

    /// <summary>状态轮询：非 clash 模式不着色；绿=端口+控制器就绪（附出口节点/延迟），
    /// 黄=端口在但控制器不可达，红=混合端口未监听（流量全部失败，须处理）。</summary>
    private async Task CheckClashAsync()
    {
        if (_clashBusy || IsDisposed) return;
        _clashBusy = true;
        try
        {
            var s = PrivacySettings.Current;
            if (s.ProxyMode != "clash")
            {
                _clashButton.GlyphTint = null;
                _tips.SetToolTip(_clashButton, "代理：未启用 Clash 接管");
                SetNetState(NetState.ClashChain); // 其他模式代理由 ApplyProxyNow 管
                ClearTabBadges();
                return;
            }
            if (!await Task.Run(() => ProxyManager.TcpProbe("127.0.0.1", s.ClashMixedPort, 1200)))
            {
                // 端口死且内置内核不在（正在启动则等下一轮）：先试"外部通道"
                // （其他代理软件可能已把探测地址打通），不通则锁定并自动开控制台
                if (!ClashCoreManager.CoreRunning)
                {
                    if (await ProbeDirectAsync(s)) await EnterExternalPassageAsync();
                    else SetNetState(NetState.Locked);
                }
                _clashButton.GlyphTint = UiColors.CloseRed;
                _tips.SetToolTip(_clashButton, _netState switch
                {
                    NetState.Locked => $"网络已锁定（探测地址不可达）——在控制台「手动节点配置」里添加代理后自动解锁",
                    NetState.ExternalPassage => "外部通道：直连放行，30s 心跳监督中",
                    _ => $"内置内核端口 {s.ClashMixedPort} 未监听——启动中或异常",
                });
                ClearTabBadges();
                return;
            }
            if (_netState == NetState.ExternalPassage)
            {
                // 本机 Clash 通道恢复：从外部通道切回（心跳随之停止）
                RiLog.Info("[gate] 本机 Clash 通道恢复，从外部通道切回");
                _passageTimer?.Stop();
                await ProxyManager.ApplyCefProxyAsync(s);
            }
            SetNetState(NetState.ClashChain); // 端口在 = 通道存在（即使控制器异常，浏览照常）
            var cfg = await ClashApi.GetConfigsAsync(s);
            if (cfg == null)
            {
                _clashButton.GlyphTint = Color.FromArgb(0xF9, 0xAB, 0x00);
                _tips.SetToolTip(_clashButton, "Clash 端口在监听，控制器不可达（浏览可用，浏览器内换节点暂不可用）");
                ClearTabBadges();
                return;
            }
            var proxies = await ClashApi.GetProxiesAsync(s) ?? new List<ClashProxy>();
            var main = ClashApi.MainProxy(proxies, cfg.Mode) ?? "—";
            var delay = proxies.FirstOrDefault(p => p.Name == main)?.DelayMs ?? 0;
            var delayTxt = delay > 0 ? $" · {delay}ms" : "";
            _clashButton.GlyphTint = Color.FromArgb(0x1E, 0x8E, 0x3E);
            _tips.SetToolTip(_clashButton, $"Clash · {ClashModeText(cfg.Mode)} · 出口 {main}{delayTxt}");
            var conns = await ClashApi.GetConnectionsAsync(s);
            if (conns != null) UpdateTabBadges(conns);
        }
        catch { /* 轮询失败静默，下一轮再试 */ }
        finally { _clashBusy = false; }
    }

    /// <summary>盾牌点击弹层：状态行 + 模式切换 + 主分组成员列表（点击换节点）+
    /// 整组测速 + 打开 Clash Verge。全部走 mihomo 控制器 API，不写配置文件。</summary>
    private async void ShowClashMenu()
    {
        var s = PrivacySettings.Current;
        var menu = MenuStyle.Create();

        if (s.ProxyMode != "clash")
        {
            menu.Items.Add(MenuStyle.Item("Clash 未接管：在 设置 → 代理 里启用", "\uE72E", null));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(MenuStyle.Item("打开设置", "\uE713", (s2, e2) => Ui(ShowPrivacySettings)));
            menu.Show(_clashButton, new Point(0, _clashButton.Height));
            return;
        }

        // 拉取状态（控制器不在时 <=5s 超时返回 null，仍给出可操作菜单）
        var cfg = await ClashApi.GetConfigsAsync(s);
        var proxies = await ClashApi.GetProxiesAsync(s) ?? new List<ClashProxy>();

        var head = cfg == null
            ? "控制器不可达（检查地址/密钥）"
            : $"mihomo · {ClashModeText(cfg.Mode)}模式 · 出口 {ClashApi.MainProxy(proxies, cfg.Mode) ?? "—"}";
        var headItem = MenuStyle.Item(head, "\uE72E", null);
        headItem.Enabled = false;
        menu.Items.Add(headItem);

        if (cfg != null)
        {
            menu.Items.Add(new ToolStripSeparator());
            foreach (var (mode, text) in new[] { ("rule", "规则模式"), ("global", "全局模式"), ("direct", "直连模式") })
            {
                var m = mode;
                var item = MenuStyle.Item(text, cfg.Mode == m ? "\uE73E" : null,
                    async (_, _) => { await Task.Run(() => ClashApi.SetModeAsync(s, m)); await CheckClashAsync(); });
                item.Checked = cfg.Mode == m; // P1 双保险：勾态 + 图标
            }
        }

        // 主分组（第一个真 Selector；GLOBAL 也是 Selector 类型，须排除）：成员即"换节点"入口
        var group = proxies.FirstOrDefault(p => p.Name != "GLOBAL" && p.Type == "Selector" && p.All.Count > 0);
        if (group != null)
        {
            menu.Items.Add(new ToolStripSeparator());
            foreach (var node in group.All.Take(50))
            {
                var n = node;
                var d = proxies.FirstOrDefault(p => p.Name == n)?.DelayMs ?? 0;
                var text = d > 0 ? $"{n}  —  {d} ms" : n;
                var item = MenuStyle.Item(text, null,
                    async (_, _) =>
                    {
                        await Task.Run(() => ClashApi.SelectAsync(s, group.Name, n));
                        await CheckClashAsync();
                    });
                item.Checked = n == group.Now;
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(MenuStyle.Item("测速当前分组", "\uE895", async (_, _) => await TestClashGroupAsync(s, group)));
        }

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(MenuStyle.Item("Clash 控制台（浏览器内）", "\uE895", async (_, _) => await OpenClashPanelAsync()));
        menu.Items.Add(MenuStyle.Item("手动添加节点配置…", "\uE70F", (_, _) =>
        {
            using var f = new ClashNodesForm(s, ApplyProxyNow);
            f.ShowDialog(this);
        }));

        // 控制台语言：面板按 navigator.language 选语言，防指纹的 en-US 伪装会带偏成英文
        var langMenu = MenuStyle.Item("控制台语言", "\uE713", null);
        foreach (var (code, label) in new[] { ("zh-CN", "中文"), ("en-US", "English"), ("", "跟随浏览器") })
        {
            var c = code;
            var item = MenuStyle.Item(label, null, (_, _) =>
            {
                s.ClashPanelLang = c;
                s.Save();
                ReloadPanelTabs(); // config.js 由 scheme 处理器按最新设置动态生成
            });
            item.Checked = s.ClashPanelLang.Trim() == c;
            langMenu.DropDownItems.Add(item);
        }
        MenuStyle.HookSubmenuRounding(langMenu);
        menu.Items.Add(langMenu);

        MenuStyle.EqualizeWidths(menu);
        menu.Show(_clashButton, new Point(0, _clashButton.Height));
    }

    /// <summary>整组并发测速（小并发防压爆），完成后刷新状态并重开菜单显示延迟。</summary>
    private async Task TestClashGroupAsync(PrivacySettings s, ClashProxy group)
    {
        var url = string.IsNullOrWhiteSpace(s.ClashProbeUrl) ? "https://www.gstatic.com/generate_204" : s.ClashProbeUrl.Trim();
        await Task.Run(async () =>
        {
            var sem = new SemaphoreSlim(6);
            var tasks = group.All.Take(60).Select(async n =>
            {
                await sem.WaitAsync();
                try { await ClashApi.DelayAsync(s, n, url, 4000); }
                finally { sem.Release(); }
            });
            await Task.WhenAll(tasks);
        });
        await CheckClashAsync();
        ShowClashMenu(); // 重开：带上新鲜延迟
    }

    internal void ShowPrivacySettings()
    {
        // onChanged：设置即时生效——对全部标签页重注册垫片（runImmediately）并重下发 CDP 覆盖；
        // 代理相关修改经 ApplyProxyNow 即时切换（仅代理字段变化时触发，带防抖）
        using var form = new SettingsForm(PrivacySettings.Current,
            onChanged: _ => { _privacy.SettingsChanged(_tabs.ToList()); ApplyProxyNow(); });
        form.ShowDialog(this);
    }

    // 旧独立代理设置页（ProxyForm）已下线：代理配置并入「隐私防护」的代理分组，
    // ProxyForm 仅剩设置页里旧 singbox 模式的迁移入口可达

    private void SetHomePage(string url)
    {
        PrivacySettings.Current.HomePage = url;
        PrivacySettings.Current.Save();
    }

    /// <summary>自定义启动页输入框：空输入/非法地址保持原值不变。</summary>
    private void PromptCustomHomePage()
    {
        using var form = new Form
        {
            Text = "自定义启动页",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(420, 118),
            Font = new Font("Segoe UI", 9F),
        };
        var label = new Label { Text = "启动页地址（新标签页与主页按钮）", AutoSize = true, Location = new Point(12, 15) };
        var input = new TextBox
        {
            Text = HomeUrl(),
            Location = new Point(12, 38),
            Width = 396,
        };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Location = new Point(248, 76), Width = 75 };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Location = new Point(333, 76), Width = 75 };
        form.Controls.AddRange(new Control[] { label, input, ok, cancel });
        form.AcceptButton = ok;
        form.CancelButton = cancel;

        if (form.ShowDialog(this) == DialogResult.OK)
        {
            var s = input.Text.Trim();
            if (s.Length == 0) return;
            if (!s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !s.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                s = "https://" + s;
            if (Uri.TryCreate(s, UriKind.Absolute, out var u) &&
                (u.Scheme == Uri.UriSchemeHttp || u.Scheme == Uri.UriSchemeHttps))
            {
                SetHomePage(u.AbsoluteUri);
            }
            else
            {
                ShowMessage("地址无效：" + input.Text);
            }
        }
    }

    private void ShowScriptsForm()
    {
        using var form = new ScriptsForm();
        form.ShowDialog(this);
    }

    private void ShowAbout()
    {
        var kernel = $"Chromium {Cef.ChromiumVersion}";
        try { kernel += $"\nCEF {Cef.CefVersion} / CefSharp {Cef.CefSharpVersion}"; }
        catch { /* 版本信息仅展示用 */ }
        MessageBox.Show(this, $"Ri 浏览器 0.1.0\n内核：{kernel}", "关于",
            MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    private void ShowDevToolsForActiveTab() => _activeTab?.Browser.ShowDevTools();

    // ==================== 界面细节 ====================

    private void LayoutToolbar()
    {
        _backButton.SetBounds(S(8), S(7), S(34), S(34));
        _forwardButton.SetBounds(S(46), S(7), S(34), S(34));
        _reloadButton.SetBounds(S(84), S(7), S(34), S(34));
        _homeButton.SetBounds(S(122), S(7), S(34), S(34));
        var pillWidth = Math.Max(S(80), _toolbar.Width - S(NavBarLeft) - S(90));
        _addressBar.SetBounds(S(NavBarLeft), S(6), pillWidth, S(35)); // 地址/搜索框
        _clashButton.SetBounds(_toolbar.Width - S(82), S(7), S(34), S(34)); // 地址栏右端、菜单左邻
        _menuButton.SetBounds(_toolbar.Width - S(44), S(7), S(34), S(34));
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        RiLog.Info($"[win] FormClosing reason={e.CloseReason} tabs={_tabs.Count}");
        base.OnFormClosing(e);
    }

    private void OnFormKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.KeyData)
        {
            case Keys.Control | Keys.T:
                AddTab(); e.Handled = true; break;
            case Keys.Control | Keys.W:
                if (_activeTab != null) CloseTab(_activeTab);
                e.Handled = true; break;
            case Keys.Control | Keys.Oemcomma:
                ShowPrivacySettings(); e.Handled = true; break;
            case Keys.Control | Keys.D:
                ToggleBookmarkForCurrent(); e.Handled = true; break;
            case Keys.Control | Keys.Shift | Keys.B:
                ToggleBookmarkBar(); e.Handled = true; break;
            case Keys.Control | Keys.Shift | Keys.A:
                ShowTabSearchMenu(); e.Handled = true; break;
            case Keys.F12:
            case Keys.Control | Keys.Shift | Keys.I:
                ShowDevToolsForActiveTab(); e.Handled = true; break;
            case Keys.Control | Keys.L:
            case Keys.F6:
                _addressBar.Box.Focus(); _addressBar.Box.SelectAll(); e.Handled = true; break;
            case Keys.Control | Keys.R:
            case Keys.F5:
                _activeTab?.Browser.Reload(); e.Handled = true; break;
            case Keys.Control | Keys.Tab:
                CycleTab(1); e.Handled = true; break;
            case Keys.Control | Keys.Shift | Keys.Tab:
                CycleTab(-1); e.Handled = true; break;
            case Keys.Alt | Keys.Left:
                _activeTab?.Browser.Back(); e.Handled = true; break;
            case Keys.Alt | Keys.Right:
                _activeTab?.Browser.Forward(); e.Handled = true; break;
        }
        if (e.Handled) e.SuppressKeyPress = true;
    }

    private static Font CreateIconFont(float size)
    {
        foreach (var name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
        {
            try
            {
                var family = new FontFamily(name);
                if (family.IsStyleAvailable(FontStyle.Regular))
                    return new Font(family, size, FontStyle.Regular, GraphicsUnit.Point);
            }
            catch { /* 该字体不存在，尝试下一个 */ }
        }
        return new Font(FontFamily.GenericSansSerif, size);
    }
}

/// <summary>
/// 标签流面板：空白区域（无子控件处）把 WM_NCHITTEST 透传给下层窗口，
/// 使主窗体的标题栏判定（拖动/双击最大化）能在标签条空白处生效。
/// </summary>
internal sealed class CaptionFlowPanel : FlowLayoutPanel
{
    private const int WM_NCHITTEST = 0x84;
    private const int HTTRANSPARENT = -1;

    /// <summary>屏幕坐标处是否为空白（无子控件命中）。</summary>
    public bool EmptyAt(Point screen)
    {
        try
        {
            return GetChildAtPoint(PointToClient(screen),
                GetChildAtPointSkip.Invisible | GetChildAtPointSkip.Disabled | GetChildAtPointSkip.Transparent) == null;
        }
        catch { return false; }
    }

    private static Point ScreenPoint(Message m)
    {
        var lp = m.LParam.ToInt64();
        return new Point((int)(short)(lp & 0xFFFF), (int)(short)((lp >> 16) & 0xFFFF));
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST && EmptyAt(ScreenPoint(m)))
        {
            m.Result = (IntPtr)HTTRANSPARENT;
            return;
        }
        base.WndProc(ref m);
    }
}

/// <summary>
/// 标签条容器：命中标签流面板的空白区域时同样透传，
/// 让命中测试继续下沉到主窗体（HTTRANSPARENT 只向 z 序下一层传递）。
/// </summary>
internal sealed class CaptionStripPanel : Panel
{
    private const int WM_NCHITTEST = 0x84;
    private const int HTTRANSPARENT = -1;

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_NCHITTEST)
        {
            try
            {
                var lp = m.LParam.ToInt64();
                var screen = new Point((int)(short)(lp & 0xFFFF), (int)(short)((lp >> 16) & 0xFFFF));
                var child = GetChildAtPoint(PointToClient(screen),
                    GetChildAtPointSkip.Invisible | GetChildAtPointSkip.Disabled | GetChildAtPointSkip.Transparent);
                // 唯一的非交互子面板是标签流：其空白处透传给主窗体
                if (child is CaptionFlowPanel flow && flow.EmptyAt(screen))
                {
                    m.Result = (IntPtr)HTTRANSPARENT;
                    return;
                }
            }
            catch { /* 常规处理 */ }
        }
        base.WndProc(ref m);
    }
}
