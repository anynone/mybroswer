using System.ComponentModel;

namespace RiBrowser;

/// <summary>
/// 标签条上的单个标签（Chrome 风格）：选中态白色圆角顶并与工具栏融为一体，
/// 未选中态与标签条同色仅显示分隔线；自绘关闭按钮，支持中键关闭。
/// </summary>
internal sealed class TabItem : Control
{
    private const int CloseBoxSize = 16;
    private const int LeftPad = 10;
    private const int CornerRadius = 8;

    private static readonly Color StripColor = Color.FromArgb(0xDE, 0xE1, 0xE6);
    private static readonly Color ActiveText = Color.FromArgb(0x20, 0x21, 0x24);
    private static readonly Color MutedText = Color.FromArgb(0x5F, 0x63, 0x68);
    private static readonly Color HoverBack = Color.FromArgb(0xE8, 0xEA, 0xED);
    private static readonly Color CloseHoverBack = Color.FromArgb(0xD3, 0xD7, 0xDB);
    private static readonly Color Separator = Color.FromArgb(0xB3, 0xB9, 0xC0);
    private static readonly Color LoadingBlue = Color.FromArgb(0x1A, 0x73, 0xE8);
    private static readonly Color FaviconGray = Color.FromArgb(0x9A, 0xA0, 0xA6);

    public event Action? CloseRequested;
    public event Action? SelectRequested;

    private string _title = "新标签页";
    private bool _selected;
    private bool _hover;
    private bool _hoverClose;
    private bool _isLoading;

    public TabItem()
    {
        Size = new Size(190, 34);
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

    private Rectangle GetCloseRect()
        => new(Width - CloseBoxSize - 8, (Height - CloseBoxSize) / 2, CloseBoxSize, CloseBoxSize);

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
    private bool Narrow => Width < 60;

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        // 背景：选中=白色圆角顶（下缘与工具栏融合），悬停=浅灰圆角顶，其余与条同色
        var rect = new Rectangle(0, 0, Width, Height);
        if (_selected || _hover)
        {
            using var path = UiPaint.RoundedTop(rect, CornerRadius);
            using var back = new SolidBrush(_selected ? Color.White : HoverBack);
            g.FillPath(back, path);
        }
        else
        {
            using var back = new SolidBrush(StripColor);
            g.FillRectangle(back, rect);
            // 未选中且未悬停：右缘分隔线（与相邻标签的视觉分界）
            using var pen = new Pen(Separator);
            g.DrawLine(pen, Width - 1, 12, Width - 1, Height - 8);
        }

        if (Narrow)
        {
            // 迷你模式：仅居中圆点（加载中蓝色）
            var dot = new Rectangle((Width - 12) / 2, (Height - 12) / 2, 12, 12);
            using (var brush = new SolidBrush(_isLoading ? LoadingBlue : FaviconGray))
            {
                g.FillEllipse(brush, dot);
            }
            return;
        }

        // 左侧：加载中蓝色圆点，否则灰色占位
        var icon = new Rectangle(LeftPad, (Height - 12) / 2, 12, 12);
        using (var dot = new SolidBrush(_isLoading ? LoadingBlue : FaviconGray))
        {
            g.FillEllipse(dot, icon);
        }

        var close = GetCloseRect();
        var textRect = new Rectangle(icon.Right + 6, 0, Math.Max(0, close.Left - icon.Right - 10), Height);
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
