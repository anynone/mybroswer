namespace RiBrowser;

/// <summary>最近关闭的标签页（进程级，全部窗口共享，Chrome 同款）。
/// 供标签条左侧下拉箭头重新打开；最多记住 10 条，同地址只保留最近一次。</summary>
internal static class ClosedTabs
{
    private static readonly List<(string Title, string Url)> _items = new();

    public static IReadOnlyList<(string Title, string Url)> Items => _items;

    public static void Push(string? title, string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url == "about:blank") return;
        _items.RemoveAll(x => x.Url == url);
        _items.Insert(0, (string.IsNullOrWhiteSpace(title) ? url! : title, url!));
        if (_items.Count > 10) _items.RemoveRange(10, _items.Count - 10);
    }

    /// <summary>已重新打开（或被新开同地址标签取代）时移出列表。</summary>
    public static void Take(string url) => _items.RemoveAll(x => x.Url == url);
}
