using System.Text.Json;

namespace RiBrowser;

/// <summary>一条收藏。</summary>
public sealed class Bookmark
{
    public string Title { get; set; } = "";
    public string Url { get; set; } = "";
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
}

/// <summary>书签栏：工具栏下方的收藏条（Chrome Ctrl+Shift+B 同款）。</summary>
internal sealed class BookmarkBar : Panel
{
    public event Action<string>? OpenInCurrentTab;
    public event Action<string>? OpenInNewTab;

    private readonly FlowLayoutPanel _flow;

    public BookmarkBar()
    {
        Dock = DockStyle.Top;
        Height = 32;
        BackColor = Color.White;
        Visible = false;

        _flow = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            AutoScroll = false,
            Margin = new Padding(0),
            Padding = new Padding(6, 2, 6, 2),
        };
        Controls.Add(_flow);

        Bookmarks.Changed += Reload;
        Reload();
    }

    public void Reload()
    {
        _flow.SuspendLayout();
        _flow.Controls.Clear();
        foreach (var b in Bookmarks.Items) _flow.Controls.Add(new BookmarkButton(b, this));
        _flow.ResumeLayout();
    }

    internal void RaiseOpen(string url, bool newTab)
    {
        if (newTab) OpenInNewTab?.Invoke(url);
        else OpenInCurrentTab?.Invoke(url);
    }
}

/// <summary>书签栏上的单个按钮：小圆点 + 标题，左键当前页打开，中键/右键菜单新标签打开、删除。</summary>
internal sealed class BookmarkButton : Control
{
    private readonly Bookmark _bookmark;
    private readonly BookmarkBar _bar;
    private bool _hover;

    public BookmarkButton(Bookmark bookmark, BookmarkBar bar)
    {
        _bookmark = bookmark;
        _bar = bar;
        Size = new Size(Math.Min(180, Math.Max(60, TextRenderer.MeasureText(DisplayTitle, new Font("Segoe UI", 9f)).Width + 40)), 26);
        Margin = new Padding(1, 2, 1, 2);
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
            menu.Items.Add(MenuStyle.Item("删除", "\uE74D", (s, _) => Bookmarks.Remove(_bookmark.Url)));
            menu.Show(this, e.Location);
        }
        base.OnMouseClick(e);
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
