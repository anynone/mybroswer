using System.Text.Json;

namespace RiBrowser;

public sealed class HistoryEntry
{
    public string Url { get; set; } = "";
    public string Title { get; set; } = "";
    public int Visits { get; set; } = 1;
    /// <summary>最近一次访问（Unix 毫秒）</summary>
    public long LastVisitUtcMs { get; set; }
}

/// <summary>一行地址栏建议。Search 行的 Target 是拼好的搜索 URL。</summary>
public sealed record HistoryRow(string Title, string Url, string Target, bool Bookmark, bool Search);

/// <summary>浏览历史：JSON 持久化在 %LocalAppData%\RiBrowser\history.json，按 URL
/// 聚合（访问次数 + 最近访问）。导航时记录，为地址栏提供"频次 × 新近度"排序的
/// 建议（Chrome omnibox 的排序口径）。</summary>
public static class History
{
    private const int MaxEntries = 3000;

    public static List<HistoryEntry> Items { get; private set; } = new();

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RiBrowser");
    private static string FilePath => Path.Combine(Dir, "history.json");

    static History()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var loaded = JsonSerializer.Deserialize<List<HistoryEntry>>(File.ReadAllText(FilePath));
                if (loaded != null) Items = loaded;
            }
        }
        catch { /* 损坏时回退空列表 */ }
    }

    /// <summary>记录一次访问（按 URL 聚合，标题由 TitleChanged 事后补全）。</summary>
    public static void Record(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (url == "about:blank" || url.StartsWith("chrome-error://") || url.StartsWith("devtools://")) return;

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var e = Items.FirstOrDefault(x => x.Url == url);
        if (e == null)
        {
            Items.Insert(0, new HistoryEntry { Url = url, Visits = 1, LastVisitUtcMs = now });
            if (Items.Count > MaxEntries) Items.RemoveRange(MaxEntries, Items.Count - MaxEntries);
        }
        else
        {
            e.Visits++;
            e.LastVisitUtcMs = now;
            Items.Remove(e);
            Items.Insert(0, e);
        }
        Save();
    }

    /// <summary>页面标题出来后回填对应条目。</summary>
    public static void UpdateTitle(string? url, string? title)
    {
        if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title)) return;
        var e = Items.FirstOrDefault(x => x.Url == url);
        if (e == null || e.Title == title) return;
        e.Title = title;
        Save();
    }

    public static void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Items));
            File.Move(tmp, FilePath, overwrite: true); // 原子替换
        }
        catch { /* 磁盘异常不影响浏览 */ }
    }

    // ---------------- 建议（地址栏下拉） ----------------

    /// <summary>生成地址栏建议。input 为空 = 聚焦空框（Chrome zero-suggest：常去的站点）；
    /// likelyUrl = 输入长得像网址（无历史命中时不弹搜索行，回车直接按网址走）。</summary>
    public static List<HistoryRow> Suggest(string input, int maxRows, bool likelyUrl, Func<string, HistoryRow> searchRowOf)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 历史条目 + 未进历史的书签（书签标记 ★）
        var pool = new List<(HistoryEntry E, bool Bm)>();
        foreach (var e in Items) pool.Add((e, false));
        foreach (var b in Bookmarks.Items)
            if (!Items.Any(h => h.Url == b.Url))
                pool.Add((new HistoryEntry { Url = b.Url, Title = b.Title, Visits = 1 }, true));

        var q = (input ?? "").Trim();
        if (q.Length == 0)
            return pool.OrderByDescending(p => p.E.Visits * 3 + Recency(p.E.LastVisitUtcMs, now))
                .Take(maxRows)
                .Select(p => ToRow(p.E, p.Bm || Bookmarks.Contains(p.E.Url)))
                .ToList();

        var ql = q.ToLowerInvariant();
        var scored = new List<(HistoryRow Row, int Score)>();
        foreach (var (e, bm) in pool)
        {
            var s = MatchScore(e, ql);
            if (s < 0) continue;
            if (bm) s += 15; // 书签加权（Chrome 同口径）
            s += Math.Min(e.Visits, 20) * 2 + Recency(e.LastVisitUtcMs, now);
            scored.Add((ToRow(e, bm || Bookmarks.Contains(e.Url)), s));
        }

        var rows = scored.OrderByDescending(x => x.Score).Take(maxRows).Select(x => x.Row).ToList();
        if (rows.Count == 0 && likelyUrl) return rows; // 像网址且无历史：不弹（回车直接导航）
        rows.Add(searchRowOf(q));                     // 其余情况兜底一行"搜索 xxx"
        return rows;
    }

    private static HistoryRow ToRow(HistoryEntry e, bool bookmark)
    {
        var title = string.IsNullOrWhiteSpace(e.Title) ? HostOf(e.Url) : e.Title;
        return new HistoryRow(title, e.Url, e.Url, bookmark, false);
    }

    /// <summary>匹配分：域名前缀 &gt; URL 包含 &gt; 标题前缀 &gt; 标题包含；不匹配 -1。</summary>
    private static int MatchScore(HistoryEntry e, string ql)
    {
        var url = e.Url.ToLowerInvariant();
        if (HostOf(url).StartsWith(ql)) return 100;
        if (url.Contains(ql)) return 60;
        var title = (e.Title ?? "").ToLowerInvariant();
        if (title.StartsWith(ql)) return 50;
        return title.Contains(ql) ? 30 : -1;
    }

    private static int Recency(long lastMs, long now)
    {
        var days = (now - lastMs) / 86_400_000.0;
        return days >= 7 ? 0 : (int)(25 * (1 - days / 7));
    }

    private static string HostOf(string url)
    {
        int s = url.IndexOf("://", StringComparison.Ordinal);
        var rest = s >= 0 ? url[(s + 3)..] : url;
        int cut = rest.IndexOfAny(new[] { '/', '?', '#' });
        return cut >= 0 ? rest[..cut] : rest;
    }
}
