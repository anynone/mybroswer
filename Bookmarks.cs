using System.Text.Json;

namespace RiBrowser;

/// <summary>一条收藏。Group 非空时归属分组（书签栏上的文件夹按钮）。</summary>
public sealed class Bookmark
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
    public string Group { get; set; } = "";
}

/// <summary>收藏夹：JSON 持久化在 %LocalAppData%\RiBrowser\bookmarks.json。</summary>
public static class Bookmarks
{
    public static event Action? Changed;

    public static List<Bookmark> Items { get; private set; } = new();

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RiBrowser");
    private static string FilePath => Path.Combine(Dir, "bookmarks.json");

    static Bookmarks()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<List<Bookmark>>(File.ReadAllText(FilePath));
                if (loaded != null) Items = loaded;
            }
        }
        catch { /* 损坏时回退空列表 */ }
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Items, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* 磁盘异常不影响浏览 */ }
        Changed?.Invoke();
    }

    public static bool Contains(string? url) =>
        !string.IsNullOrEmpty(url) && Items.Any(b => b.Url == url);

    /// <summary>切换收藏状态：没有则加，已有则删。</summary>
    public static void Toggle(string url, string title)
    {
        if (string.IsNullOrEmpty(url)) return;
        var existing = Items.FirstOrDefault(b => b.Url == url);
        if (existing != null)
        {
            Items.Remove(existing);
        }
        else
        {
            Items.Add(new Bookmark { Title = string.IsNullOrWhiteSpace(title) ? url : title, Url = url });
        }
        Save();
    }

    public static void Remove(string url)
    {
        if (Items.RemoveAll(b => b.Url == url) > 0) Save();
    }

    /// <summary>把书签移入分组（空串 = 移出分组）。</summary>
    public static void MoveToGroup(string url, string group)
    {
        var b = Items.FirstOrDefault(x => x.Url == url);
        if (b == null || b.Group == group) return;
        b.Group = group;
        Save();
    }
}

/// <summary>书签栏：工具栏下方 45px 的收藏条（Chrome Ctrl+Shift+B 同款）。
/// 分组书签显示为文件夹按钮（点击弹出组内书签），未分组书签直接平铺。</summary>
internal sealed class BookmarkBar : Panel
{
    public event Action<string>? OpenInCurrentTab;
    public event Action<string>? OpenInNewTab;

    private readonly FlowLayoutPanel _flow;

    public BookmarkBar()
    {
        Dock = DockStyle.Top;
        Height = 45;
        BackColor = Color.White;
        Visible = false;

        _flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            AutoScroll = false,
            Margin = new Padding(0),
            Padding = new Padding(6, 5, 6, 4),
        };
        Controls.Add(_flow);

        Bookmarks.Changed += Reload;
        Reload();
    }

    public void Reload()
    {
        _flow.SuspendLayout();
        _flow.Controls.Clear();
        // 分组在前（文件夹按钮），未分组书签平铺在后
        foreach (var g in Bookmarks.Items.Where(b => !string.IsNullOrWhiteSpace(b.Group))
                     .Select(b => b.Group).Distinct())
        {
            _flow.Controls.Add(new BookmarkGroupButton(g, this));
        }
        foreach (var b in Bookmarks.Items.Where(b => string.IsNullOrWhiteSpace(b.Group)))
        {
            _flow.Controls.Add(new BookmarkButton(b, this));
        }
        _flow.ResumeLayout();
    }

    internal void RaiseOpen(string url, bool newTab)
    {
        if (newTab) OpenInNewTab?.Invoke(url);
        else OpenInCurrentTab?.Invoke(url);
    }
}

/// <summary>分组文件夹按钮：点击弹出组内书签菜单（Chrome 书签文件夹同款）。</summary>
internal sealed class BookmarkGroupButton : Control
{
    private readonly string _group;
    private readonly BookmarkBar _bar;
    private bool _hover;

    public BookmarkGroupButton(string group, BookmarkBar bar)
    {
        _group = group;
        _bar = bar;
        Size = new Size(Math.Min(180, Math.Max(64, TextRenderer.MeasureText(group, new Font("Segoe UI", 9f)).Width + 44)), 34);
        Margin = new Padding(1, 1, 1, 1);
        Cursor = Cursors.Hand;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.UserMouse | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button is MouseButtons.Left or MouseButtons.Right) ShowGroupMenu();
        base.OnMouseUp(e);
    }

    private void ShowGroupMenu()
    {
        var menu = new ContextMenuStrip { Font = new Font("Segoe UI", 9f), Renderer = new ChromeRenderer(), ShowImageMargin = false };
        foreach (var b in Bookmarks.Items.Where(x => x.Group == _group))
        {
            var item = MenuStyle.Item(string.IsNullOrWhiteSpace(b.Title) ? b.Url : b.Title, "\uE8A7",
                (s, _) => _bar.RaiseOpen(b.Url, newTab: false));
            // 悬停子菜单：新标签打开 / 移出分组 / 删除
            item.DropDownItems.Add(MenuStyle.Item("在新标签页打开", "\uE8A7", (s, _) => _bar.RaiseOpen(b.Url, newTab: true)));
            item.DropDownItems.Add(MenuStyle.Item("移出分组", "\uE8A8", (s, _) => Bookmarks.MoveToGroup(b.Url, "")));
            item.DropDownItems.Add(MenuStyle.Item("删除", "\uE74D", (s, _) => Bookmarks.Remove(b.Url)));
            MenuStyle.HookSubmenuRounding(item);
            menu.Items.Add(item);
        }
        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(MenuStyle.Item("在新标签页打开全部", "\uE8A7", (s, _) =>
            {
                foreach (var b in Bookmarks.Items.Where(x => x.Group == _group).ToList())
                    _bar.RaiseOpen(b.Url, newTab: true);
            }));
            menu.Items.Add(MenuStyle.Item("整组移出分组", "\uE8A8", (s, _) =>
            {
                foreach (var b in Bookmarks.Items.Where(x => x.Group == _group).ToList())
                    Bookmarks.MoveToGroup(b.Url, "");
            }));
        }
        else
        {
            menu.Items.Add(MenuStyle.Item("（空分组）", null, (s, _) => { }));
        }
        MenuStyle.EqualizeWidths(menu);
        menu.Show(this, new Point(0, Height));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if (_hover)
        {
            using var path = UiPaint.RoundedRect(new Rectangle(1, 1, Width - 3, Height - 3), 6);
            using var bg = new SolidBrush(UiColors.Hover);
            g.FillPath(bg, path);
        }
        using var font = MainForm.IconFontOf(10f);
        // 文件夹图标
        TextRenderer.DrawText(g, "\uE8B7", font, new Rectangle(8, 0, 20, Height), Color.FromArgb(0x5F, 0x63, 0x68),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        var textRect = new Rectangle(30, 0, Width - 34, Height);
        TextRenderer.DrawText(g, _group, Font, textRect, UiColors.Text,
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }
}

/// <summary>书签栏上的单个按钮：小圆点 + 标题，左键当前页打开，中键/右键菜单新标签打开、移动分组、删除。</summary>
internal sealed class BookmarkButton : Control
{
    private readonly Bookmark _bookmark;
    private readonly BookmarkBar _bar;
    private bool _hover;

    public BookmarkButton(Bookmark bookmark, BookmarkBar bar)
    {
        _bookmark = bookmark;
        _bar = bar;
        Size = new Size(Math.Min(180, Math.Max(60, TextRenderer.MeasureText(DisplayTitle, new Font("Segoe UI", 9f)).Width + 40)), 34);
        Margin = new Padding(1, 1, 1, 1);
        Cursor = Cursors.Hand;
        var tips = new ToolTip();
        tips.SetToolTip(this, bookmark.Url);
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint | ControlStyles.UserMouse | ControlStyles.ResizeRedraw, true);
    }

    private string DisplayTitle => string.IsNullOrWhiteSpace(_bookmark.Title) ? _bookmark.Url : _bookmark.Title;

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left) _bar.RaiseOpen(_bookmark.Url, newTab: false);
        else if (e.Button == MouseButtons.Middle) _bar.RaiseOpen(_bookmark.Url, newTab: true);
        base.OnMouseUp(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
        {
            var menu = new ContextMenuStrip { Font = new Font("Segoe UI", 9f), Renderer = new ChromeRenderer() };
            menu.Items.Add(MenuStyle.Item("在新标签页打开", "\uE8A7", (s, _) => _bar.RaiseOpen(_bookmark.Url, newTab: true)));
            menu.Items.Add(MenuStyle.Item("在当前页打开", "\uE8A7", (s, _) => _bar.RaiseOpen(_bookmark.Url, newTab: false)));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(MenuStyle.Item("移动到分组…", "\uE8E5", (s, _) => PromptMoveToGroup()));
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(MenuStyle.Item("删除", "\uE74D", (s, _) => Bookmarks.Remove(_bookmark.Url)));
            menu.Show(this, e.Location);
        }
        base.OnMouseClick(e);
    }

    private void PromptMoveToGroup()
    {
        var groups = Bookmarks.Items.Where(b => !string.IsNullOrWhiteSpace(b.Group))
            .Select(b => b.Group).Distinct().ToList();
        var initial = string.IsNullOrWhiteSpace(_bookmark.Group) ? "" : _bookmark.Group;
        var group = BookmarkPrompt.Show(FindForm(), "移动到分组", "分组名称（留空 = 不分组）", initial, groups);
        if (group != null) Bookmarks.MoveToGroup(_bookmark.Url, group.Trim());
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        if (_hover)
        {
            using var path = UiPaint.RoundedRect(new Rectangle(1, 1, Width - 3, Height - 3), 6);
            using var bg = new SolidBrush(UiColors.Hover);
            g.FillPath(bg, path);
        }
        using (var dot = new SolidBrush(UiColors.TextFaint))
        {
            g.FillEllipse(dot, 8, Height / 2 - 3, 6, 6);
        }
        var textRect = new Rectangle(20, 0, Width - 24, Height);
        TextRenderer.DrawText(g, DisplayTitle, Font, textRect, UiColors.Text,
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }
}

/// <summary>单行输入对话框：返回输入文本，取消返回 null。可带候选分组快速选择。</summary>
internal static class BookmarkPrompt
{
    public static string? Show(IWin32Window? owner, string title, string label, string initial, List<string>? suggestions = null)
    {
        string? result = null;
        using var form = new Form
        {
            Text = title,
            FormBorderStyle = FormBorderStyle.FixedDialog,
            StartPosition = FormStartPosition.CenterParent,
            MaximizeBox = false,
            MinimizeBox = false,
            ClientSize = new Size(420, 150),
            Font = new Font("Segoe UI", 9F),
        };
        var lab = new Label { Text = label, AutoSize = true, Location = new Point(12, 14) };
        var input = new TextBox { Text = initial, Location = new Point(12, 38), Width = 394 };
        var ok = new Button { Text = "确定", DialogResult = DialogResult.OK, Size = new Size(88, 30), Location = new Point(230, 104) };
        var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel, Size = new Size(88, 30), Location = new Point(322, 104) };
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        ok.Click += (s, e) => result = input.Text;
        form.Controls.AddRange(new Control[] { lab, input, ok, cancel });

        // 已有分组作为下拉候选
        if (suggestions is { Count: > 0 })
        {
            var combo = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Location = new Point(12, 68),
                Width = 200,
            };
            combo.Items.AddRange(suggestions.Cast<object>().ToArray());
            combo.SelectedIndexChanged += (s, e) => input.Text = combo.SelectedItem?.ToString() ?? input.Text;
            form.Controls.Add(combo);
        }
        form.ShowDialog(owner);
        return result;
    }
}
