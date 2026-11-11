namespace RiBrowser;

/// <summary>标签页搜索浮层（Chrome 同款）：
/// 顶部灰色圆角搜索域（内嵌放大镜 + 输入框），下方"打开的标签页 / 最近关闭的
/// 标签页"两个分区；每行 = 站点图标 + 标题 + 右侧域名，悬停整行圆角高亮，
/// 域名位置切换为关闭按钮；输入即时过滤，回车打开第一项，Esc 关闭。</summary>
internal sealed class TabSearchForm : Form
{
    private const int PanelWidth = 400;
    private const int RowHeight = 34;

    private readonly MainForm _form;
    private readonly SearchField _search;
    private readonly Panel _rows;

    private sealed record RowInfo(string Title, string Url, MainForm? Window, BrowserTab? Tab, bool Active);

    public TabSearchForm(MainForm form)
    {
        _form = form;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        KeyPreview = true;
        BackColor = Color.White;
        DoubleBuffered = true;
        Font = new Font("Segoe UI", 9f);

        // 顶部搜索域：灰底圆角 + 放大镜 + 无边框输入（Chrome 布局，左右各留 12px）
        _search = new SearchField
        {
            Dock = DockStyle.Top,
            Height = 52,
            Padding = new Padding(12, 10, 12, 10),
        };
        _search.Box.TextChanged += (s, e) => Rebuild();
        _search.Box.KeyDown += OnSearchKey;

        _rows = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.White,
            Padding = new Padding(0, 0, 2, 8),
        };

        Controls.Add(_rows);
        Controls.Add(_search);

        Deactivate += (s, e) => Close();
        KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

        Rebuild();
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000; // CS_DROPSHADOW：原生投影
            return cp;
        }
    }

    /// <summary>在锚点按钮正下方弹出。</summary>
    public static void ShowBelow(Control anchor, MainForm form)
    {
        var location = anchor.PointToScreen(new Point(0, anchor.Height + 4));
        var popup = new TabSearchForm(form) { Location = location };
        popup.Show(anchor.FindForm());
        popup._search.Box.Focus();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        using var path = UiPaint.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 12);
        Region = new Region(path);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var pen = new Pen(UiColors.Border);
        using var path = UiPaint.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 12);
        e.Graphics.DrawPath(pen, path);
    }

    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Enter or Keys.Down)
        {
            if (_rows.Controls.OfType<TabRow>().FirstOrDefault(r => r.Visible) is { } first)
                first.InvokeDefault();
            else if (e.KeyCode == Keys.Enter) Close();
        }
    }

    private void Rebuild()
    {
        var opens = new List<RowInfo>();
        foreach (var w in Program.Windows.ToList())
            foreach (var t in w.Tabs.ToList())
                opens.Add(new RowInfo(
                    string.IsNullOrWhiteSpace(t.TabItem.Title) ? (t.Browser.Address ?? "新标签页") : t.TabItem.Title,
                    t.Browser.Address ?? "", w, t,
                    ReferenceEquals(w, _form) && ReferenceEquals(t, _form.ActiveTab)));

        var closeds = new List<RowInfo>();
        foreach (var (title, url) in ClosedTabs.Items.ToList())
            closeds.Add(new RowInfo(title, url, null, null, false));

        var filter = _search.Box.Text.Trim();
        bool Match(RowInfo r) => filter.Length == 0 ||
            r.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            r.Url.Contains(filter, StringComparison.OrdinalIgnoreCase);

        _rows.SuspendLayout();
        foreach (var c in _rows.Controls.Cast<Control>().ToList()) c.Dispose();
        _rows.Controls.Clear();

        var matchedOpen = opens.Where(Match).ToList();
        var matchedClosed = closeds.Where(Match).ToList();

        if (matchedOpen.Count > 0) AddSection("打开的标签页");
        foreach (var r in matchedOpen) AddRow(r);
        if (matchedClosed.Count > 0) AddSection("最近关闭的标签页");
        foreach (var r in matchedClosed) AddRow(r);
        if (matchedOpen.Count == 0 && matchedClosed.Count == 0)
            AddEmpty(filter.Length == 0 ? "没有标签页" : "无匹配的标签页");

        _rows.ResumeLayout();

        // 高度自适应：搜索域 56 + 内容，超高滚动；贴近屏幕底时向上展开
        var content = _rows.Controls.Cast<Control>().Sum(c => c.Height + c.Margin.Vertical);
        var height = Math.Min(560, 56 + content + 6);
        var screen = Screen.FromControl(this).WorkingArea;
        if (Location.Y + height > screen.Bottom)
            Location = new Point(Location.X, Math.Max(screen.Top, screen.Bottom - height));
        SetBounds(Location.X, Location.Y, PanelWidth, height);
    }

    private void AddSection(string text)
    {
        var label = new Label
        {
            Text = text,
            Dock = DockStyle.Top,
            Height = 30,
            Padding = new Padding(14, 8, 8, 2),
            ForeColor = UiColors.TextMuted,
            Font = new Font("Segoe UI Semibold", 9f),
            BackColor = Color.White,
        };
        _rows.Controls.Add(label);
        _rows.Controls.SetChildIndex(label, 0);
    }

    private void AddEmpty(string text)
    {
        var label = new Label
        {
            Text = text,
            Dock = DockStyle.Top,
            Height = 44,
            TextAlign = ContentAlignment.MiddleCenter,
            ForeColor = UiColors.TextFaint,
            BackColor = Color.White,
        };
        _rows.Controls.Add(label);
        _rows.Controls.SetChildIndex(label, 0);
    }

    private void AddRow(RowInfo r)
    {
        var row = new TabRow(r, this);
        _rows.Controls.Add(row);
        _rows.Controls.SetChildIndex(row, 0);
    }

    /// <summary>关闭标签后的列表刷新（等主窗口的关闭动作落地再重建）。</summary>
    internal void RefreshAfterClose()
    {
        var timer = new System.Windows.Forms.Timer { Interval = 100 };
        timer.Tick += (s, e) => { timer.Stop(); timer.Dispose(); Rebuild(); };
        timer.Start();
    }

    /// <summary>灰底圆角搜索域：放大镜图标 + 无边框输入框（EM_SETCUEBANNER 原生占位符）。</summary>
    private sealed class SearchField : Panel
    {
        public readonly TextBox Box = new()
        {
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 9.5f),
            ForeColor = UiColors.Text,
            BackColor = UiColors.Pill,
        };

        public SearchField()
        {
            BackColor = Color.White;
            DoubleBuffered = true;

            var field = new Panel { Height = 32, Dock = DockStyle.Fill };
            field.Paint += (s, e) =>
            {
                var g = e.Graphics;
                g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
                var rect = new Rectangle(0, 0, field.Width - 1, field.Height - 1);
                using (var path = UiPaint.RoundedRect(rect, 16))
                using (var bg = new SolidBrush(UiColors.Pill))
                {
                    g.FillPath(bg, path);
                }
                using var font = MainForm.IconFontOf(9f);
                TextRenderer.DrawText(g, "\uE721", font, new Rectangle(9, 0, 22, field.Height), UiColors.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            };
            Box.SetBounds(36, 7, 200, 20);
            // 原生占位符：聚焦时也显示浅灰提示（与 Chrome 一致），有输入才消失
            Box.HandleCreated += (s, e) =>
                Native.SendMessage(Box.Handle, 0x1501, (IntPtr)1, "搜索标签页");
            field.Controls.Add(Box);
            field.Resize += (s, e) => Box.Width = Math.Max(20, field.Width - 46);
            Controls.Add(field);
        }
    }

    /// <summary>单行：站点图标（Chrome 无 favicon 时的地球占位）+ 标题（当前加粗）+ 右侧域名；悬停域名变关闭按钮。</summary>
    private sealed class TabRow : Control
    {
        private readonly RowInfo _info;
        private readonly TabSearchForm _owner;
        private bool _hover;
        private bool _hoverClose;

        public TabRow(RowInfo info, TabSearchForm owner)
        {
            _info = info;
            _owner = owner;
            Dock = DockStyle.Top;
            Height = RowHeight;
            Margin = new Padding(8, 0, 10, 0);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.UserMouse | ControlStyles.ResizeRedraw, true);
        }

        private Rectangle IconRect => new(12, (Height - 16) / 2, 16, 16);
        private Rectangle CloseRect => new(Width - 36, (Height - 18) / 2, 22, 18);
        private static string Host(string url)
        {
            try { return new Uri(url).Host; }
            catch { return url; }
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; _hoverClose = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var overClose = _hover && _info.Tab != null && CloseRect.Contains(e.X, e.Y);
            if (overClose != _hoverClose) { _hoverClose = overClose; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                if (_hoverClose && _info.Tab != null)
                {
                    var (w, t) = (_info.Window!, _info.Tab!);
                    w.Ui(() =>
                    {
                        var title = t.TabItem.Title;
                        var url = t.Browser.Address ?? "";
                        w.CloseTab(t);
                        ClosedTabs.Push(title, url);
                    });
                    BeginInvoke(() => _owner.RefreshAfterClose());
                    return;
                }
                InvokeDefault();
            }
            base.OnMouseUp(e);
        }

        public void InvokeDefault()
        {
            if (_info.Tab != null)
            {
                var (w, t) = (_info.Window!, _info.Tab!);
                _owner.Close();
                w.UiSelectTab(t);
                if (!ReferenceEquals(w, _owner._form)) w.UiActivate();
            }
            else
            {
                var url = _info.Url;
                _owner.Close();
                ClosedTabs.Take(url);
                _owner._form.Ui(() => _owner._form.AddTab(url));
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            if (_hover)
            {
                using var path = UiPaint.RoundedRect(new Rectangle(0, 0, Width - 2, Height - 1), 8);
                using var bg = new SolidBrush(UiColors.Pill);
                g.FillPath(bg, path);
            }

            // 站点图标：地球占位（Chrome 无 favicon 的默认样式）
            using (var iconFont = MainForm.IconFontOf(10f))
            {
                TextRenderer.DrawText(g, "\uE909", iconFont, IconRect, UiColors.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }

            var host = Host(_info.Url);
            var showClose = _hover && _info.Tab != null;
            var titleRight = showClose ? Width - 44 : Width - 14 - TextRenderer.MeasureText(host, new Font("Segoe UI", 8.75f)).Width;
            var titleRect = new Rectangle(36, 0, Math.Max(24, titleRight - 40), Height);
            TextRenderer.DrawText(g, _info.Title,
                _info.Active ? new Font(Font, FontStyle.Bold) : Font, titleRect, UiColors.Text,
                TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            if (showClose)
            {
                using var iconFont = MainForm.IconFontOf(8f);
                TextRenderer.DrawText(g, "\uE711", iconFont, CloseRect,
                    _hoverClose ? UiColors.Text : UiColors.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
            else
            {
                var domainRect = new Rectangle(Width - 14 - 180, 0, 180, Height);
                TextRenderer.DrawText(g, host, new Font("Segoe UI", 8.75f), domainRect, UiColors.TextFaint,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.Right);
            }
        }
    }
}

/// <summary>Win32 杂项。</summary>
internal static class Native
{
    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, string lParam);
}
