namespace RiBrowser;

/// <summary>高 DPI 辅助。PerMonitorV2 下框架会按 DPI 放大字体、并把窗体构造期的
/// ClientSize 赋值自动折算（实测 .NET 9：560→840 @150%），但代码手工布局的子控件
/// Bounds 不会被缩放——AutoScaleMode.Dpi 也只缩窗体自身。因此各设置窗体在 OnLoad
/// 按 DeviceDpi/96 手动递归放大子控件；布局坐标一律按 96 DPI 设计。</summary>
internal static class DpiUtil
{
    /// <summary>递归缩放 root 的全部子控件 Bounds（含 MaximumSize/MinimumSize）。
    /// factor≈1 时直接返回；可在 OnLoad 安全调用。</summary>
    public static void ScaleTree(Control root, float factor)
    {
        if (Math.Abs(factor - 1f) < 0.001f) return;
        ScaleChildren(root, factor);
    }

    private static void ScaleChildren(Control parent, float f)
    {
        foreach (Control c in parent.Controls)
        {
            c.SetBounds((int)Math.Round(c.Left * f), (int)Math.Round(c.Top * f),
                (int)Math.Round(c.Width * f), (int)Math.Round(c.Height * f));
            if (c.MaximumSize != Size.Empty) c.MaximumSize = Scale(c.MaximumSize, f);
            if (c.MinimumSize != Size.Empty) c.MinimumSize = Scale(c.MinimumSize, f);
            ScaleChildren(c, f);
        }
    }

    private static Size Scale(Size s, float f) =>
        new((int)Math.Round(s.Width * f), (int)Math.Round(s.Height * f));
}
