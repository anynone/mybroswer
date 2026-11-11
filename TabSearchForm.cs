namespace RiBrowser;

/// <summary>标签页搜索浮层（Chrome 同款）：圆角阴影面板、顶部搜索框、
/// "打开的标签页 / 最近关闭" 两个分区，行内站点图标 + 标题 + 域名，
/// 悬停显示关闭按钮；支持输入即时过滤，回车打开第一项，Esc 关闭。</summary>
internal sealed class TabSearchForm : Form
{
    private const int RowHeight = 36;
    private const int PanelWidth = 380;

    private readonly MainForm _form;
    private readonly TextBox _search;
    private readonly Panel _rows;
    private readonly List<RowInfo> _open = new();
    private readonly List<RowInfo> _closed = new();

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

        // 顶部：搜索框（占位文本即面板标题）
        _search = new TextBox
        {
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 10f),
            ForeColor = UiColors.Text,
            Height = 30,
            Dock = DockStyle.Top,
            Padding = new Padding(0),
            Cursor = Cursors.IBeam,
        };
        // TextBox 无原生 Placeholder，用提示标签覆盖（聚焦/有字时隐藏）
        var hint = new Label
        {
            Text = "搜索标签页",
            ForeColor = UiColors.TextFaint,
            AutoSize = false,
            Height = 30,
            Dock = DockStyle.Top,
            Cursor = Cursors.IBeam,
            BackColor = Color.White,
        };
        var header = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(16, 8, 16, 6), BackColor = Color.White };
        hint.Dock = DockStyle.Fill;
        _search.TextChanged += (s, e) => { hint.Visible = _search.Text.Length == 0; Rebuild(); };
        _search.KeyDown += OnSearchKey;
        hint.Click += (s, e) => { _search.Focus(); hint.Visible = false; };
        header.Controls.Add(_search);
        header.Controls.Add(hint);
        // 搜索框下沿细分隔线
        var divider = new Panel { Dock = DockStyle.Top, Height = 1, BackColor = UiColors.Separator };

        _rows = new Panel
        {
            Dock = DockStyle.Fill,
            AutoScroll = true,
            BackColor = Color.White,
            Padding = new Padding(0, 0, 0, 6),
        };

        Controls.Add(_rows);
        Controls.Add(divider);
        Controls.Add(header);
        // Dock 顺序自下而上填：rows Fill、divider、header Top

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
        var popup = new TabSearchForm(form)
        {
            Location = location,
        };
        popup.Show(anchor.FindForm());
        popup._search.Focus();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // 圆角裁剪（配合原生阴影）
        using var path = UiPaint.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 10);
        Region = new Region(path);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        using var pen = new Pen(UiColors.Border);
        using var path = UiPaint.RoundedRect(new Rectangle(0, 0, Width - 1, Height - 1), 10);
        e.Graphics.DrawPath(pen, path);
    }

    private void OnSearchKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter)
        {
            // 回车打开第一行
            if (_rows.Controls.OfType<TabRow>().FirstOrDefault(r => r.Visible) is { } first)
            {
                first.InvokeDefault();
                return;
            }
            Close();
        }
        else if (e.KeyCode == Keys.Down)
        {
            if (_rows.Controls.OfType<TabRow>().FirstOrDefault(r => r.Visible) is { } first)
                first.InvokeDefault();
        }
    }

    private void Rebuild()
    {
        _open.Clear();
        foreach (var w in Program.Windows.ToList())
            foreach (var t in w.Tabs.ToList())
                _open.Add(new RowInfo(
                    string.IsNullOrWhiteSpace(t.TabItem.Title) ? (t.Browser.Address ?? "新标签页") : t.TabItem.Title,
                    t.Browser.Address ?? "", w, t,
                    ReferenceEquals(w, _form) && ReferenceEquals(t, _form.ActiveTab)));

        _closed.Clear();
        foreach (var (title, url) in ClosedTabs.Items.ToList())
            _closed.Add(new RowInfo(title, url, null, null, false));

        var filter = _search.Text.Trim();
        bool Match(RowInfo r) => filter.Length == 0 ||
            r.Title.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            r.Url.Contains(filter, StringComparison.OrdinalIgnoreCase);

        _rows.SuspendLayout();
        foreach (var c in _rows.Controls.Cast<Control>().ToList()) c.Dispose();
        _rows.Controls.Clear();

        var opens = _open.Where(Match).ToList();
        var closeds = _closed.Where(Match).ToList();

        if (opens.Count > 0) AddSection("打开的标签页");
        foreach (var r in opens) AddRow(r);
        if (closeds.Count > 0) AddSection("最近关闭");
        foreach (var r in closeds) AddRow(r);
        if (opens.Count == 0 && closeds.Count == 0)
            AddSection(filter.Length == 0 ? "没有标签页" : "无匹配的标签页");

        // 高度自适应：头部 44 + 内容，超高滚动
        var content = _rows.Controls.Cast<Control>().Sum(c => c.Height + c.Margin.Vertical);
        var height = Math.Min(520, 44 + 1 + content + 10);
        var screen = Screen.FromControl(this).WorkingArea;
        if (Location.Y + height > screen.Bottom) // 贴近屏幕底时改为向上展开
            Location = new Point(Location.X, Math.Max(screen.Top, screen.Bottom - height));
        SetBounds(Location.X, Location.Y, PanelWidth, height);
        _rows.ResumeLayout();
    }

    private void AddSection(string text)
    {
        var label = new Label
        {
            Text = text,
            Dock = DockStyle.Top,
            Height = 32,
            Padding = new Padding(16, 10, 8, 4),
            ForeColor = UiColors.TextMuted,
            Font = new Font("Segoe UI Semibold", 9f),
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

    // 关闭某个标签后由行回调：刷新列表内容
    internal void OnTabClosed(BrowserTab tab)
    {
        ClosedTabs.Push(tab.TabItem.Title, tab.Browser.Address);
        _closed.Clear();
        foreach (var (title, url) in ClosedTabs.Items.ToList())
            _closed.Add(new RowInfo(title, url, null, null, false));
        var keep = _search.Text;
        _search.Text = ""; // 触发 Rebuild
        _search.Text = keep;
    }

    /// <summary>单行：站点图标（域名首字母圆角块）+ 标题 + 域名；悬停域名变关闭按钮（打开的标签）。</summary>
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
            Margin = new Padding(8, 0, 8, 0);
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                     | ControlStyles.UserPaint | ControlStyles.UserMouse | ControlStyles.ResizeRedraw, true);
        }

        private Rectangle IconRect => new(10, (Height - 20) / 2, 20, 20);
        private Rectangle CloseRect => new(Width - 34, (Height - 16) / 2, 20, 16);
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
                    // 关闭该标签（留在浮层里继续选）
                    var (w, t) = (_info.Window!, _info.Tab!);
                    w.Ui(() =>
                    {
                        var snapshot = t; // 关闭后控件即释放，先取标题
                        var title = snapshot.TabItem.Title;
                        var url = snapshot.Browser.Address ?? "";
                        w.CloseTab(snapshot);
                        ClosedTabs.Push(title, url);
                    });
                    BeginInvoke(() => _owner.OnTabClosed(_info.Tab));
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
                using var path = UiPaint.RoundedRect(new Rectangle(0, 1, Width - 1, Height - 2), 7);
                using var bg = new SolidBrush(UiColors.Hover);
                g.FillPath(bg, path);
            }

            // 站点图标：域名首字母圆角块（Chrome 无 favicon 时的占位样式）
            var host = Host(_info.Url);
            using (var iconPath = UiPaint.RoundedRect(IconRect, 5))
            using (var iconBg = new SolidBrush(UiColors.Pill))
            {
                g.FillPath(iconBg, iconPath);
            }
            using (var letterFont = new Font("Segoe UI Semibold", 8.5f))
            {
                TextRenderer.DrawText(g, host.Length > 0 ? host[0].ToString().ToUpperInvariant() : "?", letterFont,
                    IconRect, UiColors.TextMuted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }

            // 标题（当前标签加粗），右侧域名（悬停时换成关闭按钮）
            var showClose = _hover && _info.Tab != null;
            var titleRight = showClose ? Width - 44 : Width - 10 - Math.Min(160, TextRenderer.MeasureText(host, Font).Width);
            var titleRect = new Rectangle(38, 0, Math.Max(20, titleRight - 42), Height);
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
                var domainRect = new Rectangle(Width - 10 - 170, 0, 170, Height);
                TextRenderer.DrawText(g, host, new Font("Segoe UI", 8.25f), domainRect, UiColors.TextFaint,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.Right);
            }
        }
    }
}

/// <summary>标签条最左侧的圆形下拉箭头按钮（Chrome Tab Search 同款）。</summary>
internal sealed class TabSearchButton : Control
{
    private bool _hover;

    public TabSearchButton()
    {
        Size = new Size(32, 32);
        Cursor = Cursors.Hand;
        TabStop = false;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.UserMouse | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        var r = new Rectangle(3, 3, Width - 7, Height - 7);
        // 圆形描边（Chrome 样式），悬停加深
        using var pen = new Pen(_hover ? UiColors.Text : UiColors.TextMuted, 1.4f);
        g.DrawEllipse(pen, r);
        // 圆内向下箭头
        using var font = MainForm.IconFontOf(7.5f);
        TextRenderer.DrawText(g, "\uE70D", font, new Rectangle(0, 0, Width, Height + 1),
            _hover ? UiColors.Text : UiColors.TextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}
