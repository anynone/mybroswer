using System.ComponentModel;

namespace RiBrowser;

/// <summary>自绘图标按钮：Segoe 图标字形 + 圆角悬停底色，无边框、无 WinForms 默认样式。</summary>
internal sealed class IconButton : Control
{
    private bool _hover;

    public IconButton()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw
                 | ControlStyles.UserPaint
                 | ControlStyles.UserMouse
                 | ControlStyles.SupportsTransparentBackColor, true);
        Size = new Size(34, 34);
        Cursor = Cursors.Hand;
        TabStop = false;
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Glyph { get; set; }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public float GlyphSize { get; set; } = 11f;

    protected Color GlyphColor => Enabled
        ? Color.FromArgb(0x3C, 0x40, 0x43)
        : Color.FromArgb(0xBD, 0xC1, 0xC6);

    protected Color HoverColor => Color.FromArgb(0xE8, 0xEA, 0xED);

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        if (_hover && Enabled)
        {
            var r = new Rectangle(2, 2, Width - 5, Height - 5);
            using var path = UiPaint.RoundedRect(r, 7);
            using var bg = new SolidBrush(HoverColor);
            g.FillPath(bg, path);
        }

        if (Glyph != null)
        {
            using var font = MainForm.IconFontOf(GlyphSize);
            TextRenderer.DrawText(g, Glyph, font, ClientRectangle, GlyphColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }
}

/// <summary>窗口控制按钮（最小化/最大化/关闭）：贴条满高、悬停平铺，关闭键悬停变红。</summary>
internal sealed class WindowButton : Control
{
    private readonly bool _isClose;
    private bool _hover;

    public WindowButton(string glyph, string tip, bool isClose)
    {
        _isClose = isClose;
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.UserPaint
                 | ControlStyles.UserMouse
                 | ControlStyles.ResizeRedraw, true);
        Glyph = glyph;
        Size = new Size(46, 38);
        Cursor = Cursors.Hand;
        TabStop = false;
        AccessibleName = tip;
        AccessibleRole = AccessibleRole.PushButton;
        var tips = new ToolTip();
        tips.SetToolTip(this, tip);
    }

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string? Glyph { get; set; }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        if (_hover)
        {
            using var bg = new SolidBrush(_isClose ? Color.FromArgb(0xE8, 0x11, 0x23) : Color.FromArgb(0xE8, 0xEA, 0xED));
            g.FillRectangle(bg, ClientRectangle);
        }
        if (Glyph != null)
        {
            using var font = MainForm.IconFontOf(9f);
            TextRenderer.DrawText(g, Glyph, font, ClientRectangle,
                _hover && _isClose ? Color.White : Color.FromArgb(0x3C, 0x40, 0x43),
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
    }
}

/// <summary>界面配色唯一来源：标签条/工具栏/地址栏/菜单共用同一组颜色。</summary>
internal static class UiColors
{
    public static readonly Color Strip = Color.FromArgb(0xDE, 0xE1, 0xE6);
    public static readonly Color Hover = Color.FromArgb(0xE8, 0xEA, 0xED);
    public static readonly Color Border = Color.FromArgb(0xDA, 0xDC, 0xE0);
    public static readonly Color Pill = Color.FromArgb(0xF1, 0xF3, 0xF4);
    public static readonly Color Text = Color.FromArgb(0x20, 0x21, 0x24);
    public static readonly Color TextMuted = Color.FromArgb(0x5F, 0x63, 0x68);
    public static readonly Color TextFaint = Color.FromArgb(0x9A, 0xA0, 0xA6);
    public static readonly Color Separator = Color.FromArgb(0xE4, 0xE6, 0xE9);
    public static readonly Color Accent = Color.FromArgb(0x1A, 0x73, 0xE8);
    public static readonly Color CloseRed = Color.FromArgb(0xE8, 0x11, 0x23);
    public static readonly Color Disabled = Color.FromArgb(0xBD, 0xC1, 0xC6);
}

/// <summary>绘图辅助。</summary>
internal static class UiPaint
{
    public static System.Drawing.Drawing2D.GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>只有顶部两个圆角的路径（标签页形状）。</summary>
    public static System.Drawing.Drawing2D.GraphicsPath RoundedTop(Rectangle r, int radius)
    {
        var path = new System.Drawing.Drawing2D.GraphicsPath();
        int d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddLine(r.Right, r.Y + radius, r.Right, r.Bottom);
        path.AddLine(r.Right, r.Bottom, r.X, r.Bottom);
        path.AddLine(r.X, r.Bottom, r.X, r.Y + radius);
        path.CloseFigure();
        return path;
    }
}
