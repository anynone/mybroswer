using System.Runtime.InteropServices;
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
    private readonly AddressBar _addressBar;
    private readonly WindowButton _minButton;
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

    public MainForm(string? initialUrl = null, List<string>? restoreUrls = null, int restoreActive = 0)
    {
        Text = "Ri 浏览器";
        ClientSize = new Size(1200, 800);
        MinimumSize = new Size(640, 420);
        StartPosition = FormStartPosition.CenterScreen;
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

        _addressBar = new AddressBar();
        _addressBar.Box.KeyDown += (s, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                NavigateFromAddressBox();
            }
        };
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
            _backButton, _forwardButton, _reloadButton, _homeButton, _addressBar, _menuButton,
        });
        _toolbar.Resize += (s, e) => LayoutToolbar();
        LayoutToolbar();

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

        var windowButtons = new Panel { Dock = DockStyle.Right, Width = 46 * 3, BackColor = StripColor };
        windowButtons.Controls.Add(_minButton);
        windowButtons.Controls.Add(_maxButton);
        windowButtons.Controls.Add(_closeButton);
        _minButton.Location = new Point(0, 0);
        _maxButton.Location = new Point(46, 0);
        _closeButton.Location = new Point(92, 0);

        _tabStrip.Controls.Add(_tabFlow);
        _tabStrip.Controls.Add(windowButtons);

        // 标签条最左侧的下拉箭头：弹出标签搜索浮层（与工具栏按钮同款样式）
        var tabSearchHost = new Panel { Dock = DockStyle.Left, Width = 42, BackColor = StripColor };
        _tabSearchButton = new IconButton { Glyph = "\uE70D", GlyphSize = 9f, AccessibleName = "搜索标签页" };
        _tabSearchButton.SetBounds(6, 7, 30, 30);
        _tabSearchButton.Click += (s, e) => ShowTabSearchMenu();
        _tips.SetToolTip(_tabSearchButton, "搜索标签页  Ctrl+Shift+A");
        tabSearchHost.Controls.Add(_tabSearchButton);
        _tabStrip.Controls.Add(tabSearchHost);

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

    // ==================== 无边框窗口（保留 DWM 阴影 / 贴边分屏 / 边缘缩放） ====================

    private const int WM_NCCALCSIZE = 0x83;
    private const int WM_NCHITTEST = 0x84;
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
    private struct POINT { public int X, Y; }

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
        var tab = new BrowserTab { Browser = browser, TabItem = item };

        browser.TitleChanged += (s, e) => Ui(() =>
        {
            if (!string.IsNullOrWhiteSpace(e.Title)) item.Title = e.Title;
        });
        browser.AddressChanged += (s, e) => Ui(() =>
        {
            if (_activeTab == tab) _addressBar.Text = e.Address;
            _addressBar.IsStarred = _activeTab == tab && Bookmarks.Contains(e.Address);
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
        // 可用宽度 = 标签条宽 - 内边距(12) - ＋按钮(30+边距3)
        // 标签 Margin 为 0，全部标签平分剩余空间；无论多少标签，＋ 按钮始终在可视区内
        var available = _tabFlow.ClientSize.Width - 45;
        var width = Math.Clamp(available / _tabs.Count, 32, 190);
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

        if (Uri.TryCreate(s, UriKind.Absolute, out var uri) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFile))
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
        menu.Items.Add(MenuStyle.Item("代理设置", "\uE71E", (s, e) => ShowProxySettings()));
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

    internal void ShowPrivacySettings()
    {
        // onChanged：设置即时生效——对全部标签页重注册垫片（runImmediately）并重下发 CDP 覆盖
        using var form = new SettingsForm(PrivacySettings.Current,
            onChanged: _ => _privacy.SettingsChanged(_tabs.ToList()));
        form.ShowDialog(this);
    }

    /// <summary>独立代理设置页（sing-box 完整参数 + 手动代理）。</summary>
    internal void ShowProxySettings()
    {
        using var form = new ProxyForm(PrivacySettings.Current,
            onChanged: _ => _privacy.SettingsChanged(_tabs.ToList()));
        form.ShowDialog(this);
    }

    internal void UiOpenProxySettings() => Ui(ShowProxySettings);

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
        _backButton.SetBounds(8, 7, 34, 34);
        _forwardButton.SetBounds(46, 7, 34, 34);
        _reloadButton.SetBounds(84, 7, 34, 34);
        _homeButton.SetBounds(122, 7, 34, 34);
        var pillWidth = Math.Max(80, _toolbar.Width - NavBarLeft - 52);
        _addressBar.SetBounds(NavBarLeft, 6, pillWidth, 35); // 地址/搜索框 35px
        _menuButton.SetBounds(_toolbar.Width - 44, 7, 34, 34);
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
