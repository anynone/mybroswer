namespace RiBrowser;

/// <summary>地址栏建议浮层（Chrome omnibox 下拉）：
/// 历史网址行两行式（标题 + 灰色 URL），末尾一行"搜索 xxx"；键盘选中行淡蓝、
/// 鼠标悬停行浅灰；WS_EX_NOACTIVATE 保证点击浮层时焦点仍在输入框。</summary>
internal sealed class OmniboxPopup : Form
{
    // 基准 96 DPI 下的布局，实际按 DeviceDpi 等比缩放（高 DPI 下字体像素更高，
    // 写死的行高/文本框会裁掉字形）
    private int RowH => (int)Math.Round(38 * DeviceDpi / 96d);
    private int PadV => (int)Math.Round(6 * DeviceDpi / 96d);
    private int IconW => (int)Math.Round(36 * DeviceDpi / 96d);

    private readonly Control _anchor;
    private List<HistoryRow> _rows = new();
    private int _selected = -1;
    private int _hover = -1;

    /// <summary>用户敲回车/点击行时触发，参数是可直接加载的 URL。</summary>
    public event Action<string>? Navigate;

    public OmniboxPopup(Control anchor)
    {
        _anchor = anchor;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.White;
        DoubleBuffered = true;
        Font = new Font("Segoe UI", 9f);
        Size = new Size(Math.Max(anchor.Width, 420), 10);
        Enabled = true; // NOACTIVATE 窗口仍要接收鼠标

        // 主窗口移动/缩放时浮层立即收起（位置已失效）
        if (anchor.FindForm() is { } owner)
        {
            owner.LocationChanged += (s, e) => Hide();
            owner.Resize += (s, e) => Hide();
        }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ClassStyle |= 0x20000;          // CS_DROPSHADOW：原生投影
            cp.ExStyle |= 0x0800_0000          // WS_EX_NOACTIVATE：不抢焦点
                        | 0x0000_0080;         // WS_EX_TOOLWINDOW：不进任务栏/Alt+Tab
            return cp;
        }
    }

    public bool Open => Visible && _rows.Count > 0;

    /// <summary>键盘选中的行（Chrome 默认选中第一条；无行返回 null = 按输入原文走）。</summary>
    public string? SelectedTarget => Open && _selected >= 0 ? _rows[_selected].Target : null;

    /// <summary>更新建议内容；空列表直接收起。</summary>
    public void Update(List<HistoryRow> rows)
    {
        if (rows.Count == 0) { Hide(); return; }
        if (!IsHandleCreated) _ = Handle; // 先建句柄拿到真实 DeviceDpi，行高缩放才正确
        _rows = rows;
        _selected = 0;
        _hover = -1;
        Width = Math.Max(_anchor.Width, 420);
        Height = rows.Count * RowH + PadV * 2;
        Location = _anchor.PointToScreen(new Point(0, _anchor.Height + 6));
        Invalidate();
        if (!Visible) Show(_anchor.FindForm());
    }

    private int RowIndexAt(int y)
    {
        var i = (y - PadV) / RowH;
        return i >= 0 && i < _rows.Count && y >= PadV + i * RowH ? i : -1;
    }

    /// <summary>键盘上下移动选中行（循环）。</summary>
    public void Move(int delta)
    {
        if (_rows.Count == 0) return;
        _selected = ((_selected + delta) % _rows.Count + _rows.Count) % _rows.Count;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var i = RowIndexAt(e.Y);
        if (i != _hover) { _hover = i; Invalidate(); }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hover = -1;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        var i = RowIndexAt(e.Y);
        if (i >= 0) Navigate?.Invoke(_rows[i].Target);
        base.OnMouseClick(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Color.White);

        using var border = new Pen(Color.FromArgb(0xDA, 0xDC, 0xE0));
        g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        using var urlFont = new Font(Font, FontStyle.Regular);
        using var titleFont = new Font(Font, FontStyle.Bold);
        using var iconFont = MainForm.IconFontOf(10f);

        var pad = (int)Math.Round(4 * DeviceDpi / 96d);
        for (var i = 0; i < _rows.Count; i++)
        {
            var row = _rows[i];
            var rect = new Rectangle(1, PadV + i * RowH, Width - 2, RowH);

            if (i == _selected)
                using (var b = new SolidBrush(Color.FromArgb(0xE8, 0xF0, 0xFE))) g.FillRectangle(b, rect);
            else if (i == _hover)
                using (var b = new SolidBrush(Color.FromArgb(0xF1, 0xF3, 0xF4))) g.FillRectangle(b, rect);

            // 图标：搜索行放大镜 / 书签星 / 历史地球
            var glyph = row.Search ? "\uE721" : row.Bookmark ? "\uE7A8" : "\uE77B";
            var glyphColor = row.Search
                ? UiColors.TextMuted
                : row.Bookmark ? Color.FromArgb(0xF9, 0xAB, 0x00) : Color.FromArgb(0x5F, 0x63, 0x68);
            TextRenderer.DrawText(g, glyph, iconFont, new Rectangle(pad * 2, rect.Y, IconW, RowH), glyphColor,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

            var textLeft = pad * 2 + IconW;
            var textW = Width - textLeft - pad * 3;
            if (row.Search)
            {
                // 搜索行：单行 "搜索“xxx”"+ 右侧灰色引擎名
                var engineW = (int)Math.Round(100 * DeviceDpi / 96d);
                TextRenderer.DrawText(g, row.Title, titleFont, new Rectangle(textLeft, rect.Y, textW - engineW, RowH),
                    UiColors.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, row.Url, urlFont, new Rectangle(Width - engineW - pad, rect.Y, engineW, RowH),
                    UiColors.TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.Right | TextFormatFlags.EndEllipsis);
            }
            else
            {
                // 两行式：上半标题（粗）、下半 URL（灰）。行高分两半，给足字形高度
                var half = RowH / 2 - pad / 2;
                TextRenderer.DrawText(g, row.Title, titleFont, new Rectangle(textLeft, rect.Y + pad, textW, half),
                    UiColors.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, row.Url, urlFont, new Rectangle(textLeft, rect.Y + RowH - half - pad / 2, textW, half),
                    UiColors.TextMuted, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis);
            }
        }
    }
}
