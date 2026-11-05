using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace RiBrowser;

/// <summary>Chrome 风格菜单渲染表：白底、细边框（项内高亮由渲染器接管）。</summary>
internal sealed class ChromeColorTable : ProfessionalColorTable
{
    private static readonly Color Border = Color.FromArgb(0xDA, 0xDC, 0xE0);
    private static readonly Color Hover = Color.FromArgb(0xE8, 0xEA, 0xED);
    private static readonly Color Sep = Color.FromArgb(0xE4, 0xE6, 0xE9);

    public override Color ToolStripDropDownBackground => Color.White;
    public override Color ToolStripBorder => Border;
    public override Color MenuBorder => Border;
    public override Color ImageMarginGradientBegin => Color.White;
    public override Color ImageMarginGradientMiddle => Color.White;
    public override Color ImageMarginGradientEnd => Color.White;
    public override Color MenuItemSelected => Hover;
    public override Color MenuItemSelectedGradientBegin => Hover;
    public override Color MenuItemSelectedGradientEnd => Hover;
    public override Color MenuItemBorder => Color.Transparent;
    public override Color SeparatorDark => Sep;
    public override Color SeparatorLight => Sep;
}

/// <summary>Chrome 菜单渲染器：接管项背景（圆角悬停块）、项文字（含右对齐灰色快捷键列、
/// 子菜单箭头）与分隔线（内缩细线）。颜色取自 <see cref="UiColors"/>，与浏览器主题一致。</summary>
internal sealed class ChromeRenderer : ToolStripProfessionalRenderer
{
    public ChromeRenderer() : base(new ChromeColorTable()) { RoundedEdges = true; }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var g = e.Graphics;
        var r = e.Item.ContentRectangle;
        using var pen = new Pen(UiColors.Separator);
        var y = r.Top + r.Height / 2;
        g.DrawLine(pen, 14, y, r.Right - 10, y);
    }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (e.Item is not ToolStripMenuItem item || !item.Enabled) return;
        if (item.Selected || item.Pressed)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            var hover = new Rectangle(3, 1, item.Width - 6, item.Height - 2);
            using var path = UiPaint.RoundedRect(hover, 5);
            using var bg = new SolidBrush(UiColors.Hover);
            g.FillPath(bg, path);
        }
        // 不调 base：默认渐变高亮全部替换
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        if (e.Item is not ToolStripMenuItem mi) { base.OnRenderItemText(e); return; }
        var g = e.Graphics;
        var font = e.TextFont ?? mi.Font;
        var rect = new Rectangle(e.TextRectangle.X, 0, e.TextRectangle.Width, mi.Height);

        TextRenderer.DrawText(g, mi.Text, font, rect, UiColors.Text,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter
            | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);

        // 快捷键 / 子菜单箭头按项自身宽度右对齐（EqualizeWidths 保证各项等宽 → 成列）
        if (mi.Tag is string key)
        {
            TextRenderer.DrawText(g, key, font,
                new Rectangle(mi.Width - 78, 0, 70, mi.Height), UiColors.TextMuted,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        }
        if (mi.HasDropDownItems)
        {
            using var arrow = MainForm.IconFontOf(8f);
            TextRenderer.DrawText(g, "\uE76C", arrow,
                new Rectangle(mi.Width - 30, 0, 22, mi.Height), UiColors.TextMuted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        }
    }
}

/// <summary>
/// 接管子菜单位置的菜单项：WinForms 默认定位依赖布局完成时机（非确定性），
/// 且右缘翻转在高 DPI 下不可靠。这里用 PointToScreen（DPI 安全）显式计算：
/// 默认贴父项右缘，放不下翻转到左侧，并夹取在屏幕工作区内。
/// </summary>
internal sealed class PositionedMenuItem : ToolStripMenuItem
{
    public PositionedMenuItem(string text) : base(text) { }

    protected override Point DropDownLocation
    {
        get
        {
            try
            {
                if (Owner is not ToolStripDropDown owner || DropDown == null)
                    return base.DropDownLocation;

                var dd = DropDown;
                // 高 DPI 下 WinForms 布局坐标（逻辑）与渲染位置（物理）可能不一致：
                // X 用主菜单的物理矩形（真实渲染位置），Y 实测 PointToScreen 正确
                var ownerRect = owner.RectangleToScreen(owner.ClientRectangle);
                var y = owner.PointToScreen(new Point(0, Bounds.Top)).Y - 4;
                var work = Screen.FromRectangle(ownerRect).WorkingArea;

                int x;
                if (ownerRect.Right + dd.Width <= work.Right)
                    x = ownerRect.Right - 2;                        // 贴主菜单右缘
                else
                    x = ownerRect.Left - dd.Width + 2;              // 放不下翻转到左缘

                if (y < work.Y) y = work.Y;
                if (y + dd.Height > work.Bottom) y = work.Bottom - dd.Height;
                return new Point(x, y);
            }
            catch { return base.DropDownLocation; }
        }
    }
}

internal static class MenuStyle
{
    private const int ShortcutReserve = 78; // 右侧快捷键列预留宽度

    // Win11 DWM 圆角 + 浅灰描边
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWCP_ROUND = 2;

    /// <summary>把 Segoe 图标字形渲染成 16px 位图（Chrome 式灰色图标）。</summary>
    public static Image Glyph(string glyph, float sizePt = 9.5f)
    {
        var bmp = new Bitmap(16, 16);
        using var g = Graphics.FromImage(bmp);
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        using var font = MainForm.IconFontOf(sizePt);
        TextRenderer.DrawText(g, glyph, font, new Rectangle(0, 0, 16, 16), UiColors.TextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
        return bmp;
    }

    /// <summary>创建 Chrome 风格下拉菜单（Win11 圆角 + 自定义渲染器）。</summary>
    public static ContextMenuStrip Create()
    {
        var menu = new ContextMenuStrip
        {
            Font = new Font("Segoe UI", 9f),
            ShowImageMargin = true,
            Padding = new Padding(1, 4, 1, 4),
            Renderer = new ChromeRenderer(),
        };
        menu.Opened += (s, e) => RoundCorners(((ContextMenuStrip)s).Handle);
        return menu;
    }

    /// <summary>
    /// 各菜单项保持 AutoSize（子菜单默认定位因此天然贴邻、DPI 安全），
    /// 通过动态右内边距把所有项补齐到同一宽度，实现 Chrome 式等宽列与快捷键对列。
    /// </summary>
    public static void EqualizeWidths(ToolStripDropDown menu)
    {
        var font = menu.Font;
        var maxText = 0;
        foreach (var item in menu.Items.OfType<ToolStripMenuItem>())
        {
            var w = TextRenderer.MeasureText(item.Text, font).Width;
            if (w > maxText) maxText = w;
        }
        foreach (var item in menu.Items.OfType<ToolStripMenuItem>())
        {
            var textW = TextRenderer.MeasureText(item.Text, font).Width;
            item.Padding = new Padding(4, 6, ShortcutReserve + (maxText - textW), 6);
        }
    }

    /// <summary>子菜单弹出时加 Win11 圆角（定位交给默认逻辑，无需手工干预）。</summary>
    public static void HookSubmenuRounding(ToolStripMenuItem parent)
    {
        parent.DropDown.Opened += (s, e) =>
        {
            try { RoundCorners(((ToolStripDropDown)s).Handle); }
            catch { /* 不支持时保持默认 */ }
        };
    }

    private static void RoundCorners(IntPtr handle)
    {
        try
        {
            var pref = DWMWCP_ROUND;
            DwmSetWindowAttribute(handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            var border = 0x00E0DCDA; // COLORREF (BGR) = #DADCE0
            DwmSetWindowAttribute(handle, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        }
        catch { /* Win10 等不支持时保持默认 */ }
    }

    /// <summary>菜单项：AutoSize 常开，快捷键存 Tag 由渲染器绘制；子菜单位置由 PositionedMenuItem 接管。</summary>
    public static ToolStripMenuItem Item(string text, string? glyph, EventHandler? onClick, string? shortcut = null)
    {
        var item = new PositionedMenuItem(text)
        {
            ForeColor = UiColors.Text,
            ImageScaling = ToolStripItemImageScaling.SizeToFit,
            Tag = shortcut,
            Padding = new Padding(4, 6, ShortcutReserve, 6),
        };
        if (glyph != null) item.Image = Glyph(glyph);
        if (onClick != null) item.Click += onClick;
        return item;
    }
}
