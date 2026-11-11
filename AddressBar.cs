using System.ComponentModel;

namespace RiBrowser;

/// <summary>胶囊地址栏：圆角背景 + 左侧搜索图标 + 右侧收藏星标 + 无边框输入框，聚焦时白底蓝描边。</summary>
internal sealed class AddressBar : Control
{
    private readonly TextBox _box = new()
    {
        BorderStyle = BorderStyle.None,
        Font = new Font("Segoe UI", 10f),
    };

    public event Action? StarClicked;

    private bool _starred;
    private bool _hoverStar;

    public AddressBar()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
                 | ControlStyles.OptimizedDoubleBuffer
                 | ControlStyles.ResizeRedraw
                 | ControlStyles.UserPaint, true);
        Cursor = Cursors.IBeam;
        Height = 35;

        _box.Enter += (s, e) => { _box.BackColor = Color.White; Invalidate(); };
        _box.Leave += (s, e) => { _box.BackColor = BackColorOfPill; Invalidate(); };
        _box.BackColor = BackColorOfPill;
        Controls.Add(_box);
    }

    private static Color BackColorOfPill => Color.FromArgb(0xF1, 0xF3, 0xF4);

    /// <summary>内嵌输入框（回车导航、取值/赋值用）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public TextBox Box => _box;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public new string Text
    {
        get => _box.Text;
        set => _box.Text = value;
    }

    /// <summary>当前页是否已收藏（星标实心/空心）。</summary>
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool IsStarred
    {
        get => _starred;
        set { if (_starred != value) { _starred = value; Invalidate(); } }
    }

    private Rectangle StarRect => new(Width - 30, 0, 26, Height);

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        // 左侧搜索图标 38px、右侧星标 30px
        _box.SetBounds(38, (Height - _box.PreferredHeight) / 2, Math.Max(10, Width - 76), _box.PreferredHeight);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var overStar = StarRect.Contains(e.X, e.Y);
        if (overStar != _hoverStar)
        {
            _hoverStar = overStar;
            Cursor = overStar ? Cursors.Hand : Cursors.IBeam;
            Invalidate();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        _hoverStar = false;
        Invalidate();
        base.OnMouseLeave(e);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (StarRect.Contains(e.X, e.Y))
        {
            StarClicked?.Invoke();
            return;
        }
        _box.Focus();
        _box.Select(_box.TextLength, 0); // 点胶囊把光标移到末尾
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

        var focused = _box.Focused;
        var rect = new Rectangle(0, 0, Width - 1, Height - 1);
        using var path = UiPaint.RoundedRect(rect, Height / 2 - 1);

        using (var fill = new SolidBrush(focused ? Color.White : BackColorOfPill))
        {
            g.FillPath(fill, path);
        }
        if (focused)
        {
            using var pen = new Pen(Color.FromArgb(0x1A, 0x73, 0xE8), 1.6f);
            g.DrawPath(pen, path);
        }

        using var font = MainForm.IconFontOf(10f);
        // 左侧搜索图标
        var iconRect = new Rectangle(10, 0, 22, Height);
        TextRenderer.DrawText(g, "\uE721", font, iconRect, UiColors.TextMuted,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);

        // 右侧收藏星标：未收藏空心 E735，已收藏实心（金黄）E7A8
        TextRenderer.DrawText(g, _starred ? "\uE7A8" : "\uE735", font, StarRect,
            _starred ? Color.FromArgb(0xF9, 0xAB, 0x00) : (_hoverStar ? UiColors.Text : UiColors.TextMuted),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
    }
}
