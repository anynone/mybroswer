using System.ComponentModel;

namespace RiBrowser;

/// <summary>
/// 标签条上的单个标签（Chrome 新版风格）：圆角矩形悬浮在标签条内，
/// 上下各留 5px 边距；选中态白色、悬停浅灰、其余与条同色并画分隔线；
/// 自绘关闭按钮，支持中键关闭。
/// </summary>
internal sealed class TabItem : Control
{
    private const int CloseBoxSize = 16;
    private const int LeftPad = 10;
    private const int CornerRadius = 8;
    private const int FaviconSize = 12;

    /// <summary>DPI 缩放（96 基准），由主窗体在句柄创建/DPI 变化时下发。</summary>
    private double _scale = 1;
    private int S(int v) => (int)Math.Round(v * _scale);

    /// <summary>应用缩放并重算标签高度（宽度由 UpdateTabWidths 统一分配）。
    /// 高度 = 标签条 - 上下边距（各 5px 基准）。</summary>
    public void SetChromeScale(double scale)
    {
        if (Math.Abs(_scale - scale) < 0.001) return;
        _scale = scale;
        Height = S(35);
        Invalidate();
    }

    private static readonly Color StripColor = Color.FromArgb(0xDE, 0xE1, 0xE6);
    private static readonly Color ActiveText = Color.FromArgb(0x20, 0x21, 0x24);
    private static readonly Color MutedText = Color.FromArgb(0x5F, 0x63, 0x68);
    private static readonly Color HoverBack = Color.FromArgb(0xE8, 0xEA, 0xED);
    private static readonly Color CloseHoverBack = Color.FromArgb(0xD3, 0xD7, 0xDB);
    private static readonly Color Separator = Color.FromArgb(0xB3, 0xB9, 0xC0);
    private static readonly Color LoadingBlue = Color.FromArgb(0x1A, 0x73, 0xE8);
    private static readonly Color FaviconGray = Color.FromArgb(0x9A, 0xA0, 0xA6);
    private static readonly Color RoutedProxy = Color.FromArgb(0x1E, 0x8E, 0x3E);

    public event Action? CloseRequested;
    public event Action? SelectRequested;

    private string _title = "新标签页";
    private bool _selected;
    private bool _hover;
    private bool _hoverClose;
    private bool _isLoading;

    public TabItem()
    {
        Size = new Size(190, 35);
        Margin = new Padding(0);
        Cursor = Cursors.Default;
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw
                 | ControlStyles.UserPaint
                 | ControlStyles.UserMouse, true);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string Title
    {
        get => _title;
        set { if (_title != value) { _title = value; Invalidate(); } }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Selected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; Invalidate(); } }
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsLoading
    {
        get => _isLoading;
        set { if (_isLoading != value) { _isLoading = value; Invalidate(); } }
    }

    /// <summary>通道徽标：null=无数据不显示；true=流量走代理（绿点）；false=直连（灰点）。
    /// 由主窗体按 mihomo /connections 的域名对账刷新。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool? RoutedProxied
    {
        get => _routed;
        set { if (_routed != value) { _routed = value; Invalidate(); } }
    }
    private bool? _routed;

    private Rectangle GetCloseRect()
        => new(Width - S(CloseBoxSize) - S(8), (Height - S(CloseBoxSize)) / 2, S(CloseBoxSize), S(CloseBoxSize));

    protected override void OnMouseEnter(EventArgs e)
    {
        _hover = true;
        Invalidate();
        base.OnMouseEnter(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = false;
        _hoverClose = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var overClose = GetCloseRect().Contains(e.X, e.Y);
        if (overClose != _hoverClose)
        {
            _hoverClose = overClose;
            Cursor = overClose ? Cursors.Hand : Cursors.Default;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Middle)
        {
            CloseRequested?.Invoke();
            return;
        }
        if (e.Button == MouseButtons.Left)
        {
            if (GetCloseRect().Contains(e.X, e.Y)) CloseRequested?.Invoke();
            else SelectRequested?.Invoke();
        }
        base.OnMouseDown(e);
    }

    /// <summary>窄标签模式：宽度不足以显示文字，只画居中图标（Chrome 同款收缩行为）。</summary>
    private bool Narrow => Width < S(60);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // 背景：圆角矩形悬浮卡（选中=白色，悬停=浅灰），上下 5px 边距由布局留出
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        if (_selected || _hover)
        {
            using var path = UiPaint.RoundedRect(rect, S(CornerRadius));
            using var back = new SolidBrush(_selected ? Color.White : HoverBack);
            g.FillPath(back, path);
        }
        else
        {
            using var back = new SolidBrush(StripColor);
            g.FillRectangle(back, rect);
            // 未选中且未悬停：右缘分隔线（上下内缩，圆角卡之间）
            using var pen = new Pen(Separator);
            g.DrawLine(pen, Width - 1, S(12), Width - 1, Height - S(12));
        }

        if (Narrow)
        {
            // 迷你模式：仅居中圆点（加载中蓝色）
            var dot = new Rectangle((Width - S(FaviconSize)) / 2, (Height - S(FaviconSize)) / 2, S(FaviconSize), S(FaviconSize));
            using (var brush = new SolidBrush(_isLoading ? LoadingBlue : FaviconGray))
            {
                g.FillEllipse(brush, dot);
            }
            return;
        }

        // 左侧：加载中蓝色圆点，否则灰色占位
        var icon = new Rectangle(S(LeftPad), (Height - S(FaviconSize)) / 2, S(FaviconSize), S(FaviconSize));
        using (var dot = new SolidBrush(_isLoading ? LoadingBlue : FaviconGray))
        {
            g.FillEllipse(dot, icon);
        }

        // 通道徽标：favicon 右下角小圆点（绿=代理，灰=直连），带白描边与底色区分
        if (_routed is { } proxied)
        {
            const int Badge = 6;
            var b = new Rectangle(icon.Right - S(Badge) + S(2), icon.Bottom - S(Badge) + S(1), S(Badge), S(Badge));
            using var ring = new Pen(Color.White, 1.4f);
            g.FillEllipse(new SolidBrush(proxied ? RoutedProxy : Color.FromArgb(0x5F, 0x63, 0x68)), b);
            g.DrawEllipse(ring, b);
        }

        var close = GetCloseRect();
        var textRect = new Rectangle(icon.Right + S(6), 0, Math.Max(0, close.Left - icon.Right - S(10)), Height);
        TextRenderer.DrawText(g, _title, Font, textRect, _selected ? ActiveText : MutedText,
            TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter
            | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

        if (_hoverClose)
        {
            using (var cb = new SolidBrush(CloseHoverBack))
            {
                g.FillEllipse(cb, close);
            }
        }
        DrawCloseGlyph(g, close, _hoverClose || _selected ? ActiveText : MutedText);
    }

    private static void DrawCloseGlyph(Graphics g, Rectangle r, Color color)
    {
        int inset = r.Width <= 14 ? 4 : 5;
        using var pen = new Pen(color, 1.4f);
        g.DrawLine(pen, r.X + inset, r.Y + inset, r.Right - inset, r.Bottom - inset);
        g.DrawLine(pen, r.Right - inset, r.Y + inset, r.X + inset, r.Bottom - inset);
    }
}
