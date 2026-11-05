using System.Text.Json;
using System.Text.RegularExpressions;

namespace RiBrowser;

/// <summary>一个用户脚本（.user.js，Tampermonkey 格式头部）。</summary>
public sealed class UserScript
{
    public string Name { get; set; } = "";
    public string FileName { get; set; } = "";
    public string RunAt { get; set; } = "document-end"; // document-start | document-end
    public List<string> Patterns { get; set; } = new();  // 已转成正则源
    public string Body { get; set; } = "";
    public bool Enabled { get; set; } = true;

    public override string ToString() => Name + "  (" + RunAt + ")";
}

/// <summary>
/// 用户脚本引擎：CEF 官方构建不含 Chrome 扩展子系统，无法加载真扩展（CRX/商店）；
/// 这里以内置注入通道提供最常用的替代——Tampermonkey 格式用户脚本（@match/@include/@run-at + 常用 GM_* 桩）。
/// 脚本目录：%LocalAppData%\RiBrowser\scripts\*.user.js
/// </summary>
public static class UserScripts
{
    public static event Action? Changed;

    public static List<UserScript> Items { get; private set; } = new();

    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RiBrowser", "scripts");

    public static void Reload()
    {
        var list = new List<UserScript>();
        try
        {
            if (Directory.Exists(Dir))
            {
                foreach (var file in Directory.GetFiles(Dir, "*.user.js"))
                {
                    var script = Parse(file);
                    if (script != null) list.Add(script);
                }
            }
        }
        catch { /* 目录异常时给出空列表 */ }
        Items = list;
        _injectorCache = null;
        Changed?.Invoke();
    }

    /// <summary>把 .user.js 文件复制进脚本目录并加载。</summary>
    public static void Import(IEnumerable<string> paths)
    {
        Directory.CreateDirectory(Dir);
        foreach (var p in paths)
        {
            var dest = Path.Combine(Dir, Path.GetFileName(p));
            File.Copy(p, dest, overwrite: true);
        }
        Reload();
    }

    public static void Remove(string fileName)
    {
        try
        {
            var full = Path.Combine(Dir, fileName);
            if (File.Exists(full)) File.Delete(full);
        }
        catch { }
        Reload();
    }

    // ---------------- 解析 ----------------

    private static UserScript? Parse(string file)
    {
        try
        {
            var text = File.ReadAllText(file);
            var script = new UserScript { FileName = Path.GetFileName(file) };
            var m = Regex.Match(text, @"//\s*==UserScript==\s*(.*?)//\s*==/UserScript==", RegexOptions.Singleline);
            var body = text;
            if (m.Success)
            {
                var header = m.Groups[1].Value;
                body = text[(m.Index + m.Length)..];
                // 标准 UserScript 头部格式为 "// @key value"（// 与 @ 之间有空格）
                foreach (Match name in Regex.Matches(header, @"//\s*@(\w+)\s+(.+)"))
                {
                    var key = name.Groups[1].Value;
                    var val = name.Groups[2].Value.Trim();
                    if (key == "name") script.Name = val;
                    else if (key == "match" || key == "include") script.Patterns.Add(ToRegexSource(val));
                    else if (key == "run-at")
                        script.RunAt = val.Contains("start", StringComparison.OrdinalIgnoreCase) ? "document-start" : "document-end";
                }
            }
            script.Name = string.IsNullOrWhiteSpace(script.Name) ? Path.GetFileNameWithoutExtension(file) : script.Name;
            if (script.Patterns.Count == 0) script.Patterns.Add(ToRegexSource("*")); // 无匹配规则 = 全站
            script.Body = body;
            return script;
        }
        catch { return null; }
    }

    /// <summary>@match / @include 通配 → 正则源。</summary>
    private static string ToRegexSource(string pattern)
    {
        var p = pattern.Trim();
        // @match 形如 https://*.example.com/path*：宿主通配独立处理
        var escaped = Regex.Escape(p).Replace(@"\*", ".*");
        return "^" + escaped + "$";
    }

    // ---------------- 注入器 ----------------

    private static string? _injectorCache;

    /// <summary>生成注入脚本：按 URL 匹配规则在 document-start / document-end 运行。</summary>
    public static string BuildInjector()
    {
        if (_injectorCache != null) return _injectorCache;
        var rules = Items.Where(s => s.Enabled).Select(s => new
        {
            name = s.Name,
            runAt = s.RunAt,
            re = s.Patterns.Select(p => p).ToArray(),
            body = s.Body,
        });
        var json = JsonSerializer.Serialize(rules);
        _injectorCache = @"(function () {
  var S = " + json + @";
  if (!S.length) return;
  var GM_addStyle = function (css) { var s = document.createElement('style'); s.textContent = css; (document.head || document.documentElement).appendChild(s); return s; };
  var GM_getValue = function (k, d) { try { var v = localStorage.getItem('ri_gm_' + k); return v === null ? d : JSON.parse(v); } catch (e) { return d; } };
  var GM_setValue = function (k, v) { try { localStorage.setItem('ri_gm_' + k, JSON.stringify(v)); } catch (e) {} };
  var GM_deleteValue = function (k) { try { localStorage.removeItem('ri_gm_' + k); } catch (e) {} };
  var GM_listValues = function () { var out = []; try { for (var i = 0; i < localStorage.length; i++) { var k = localStorage.key(i); if (k.indexOf('ri_gm_') === 0) out.push(k.slice(6)); } } catch (e) {} return out; };
  var GM_info = { scriptHandler: 'Ri 浏览器' };
  var unsafeWindow = window;
  var GM = { getValue: GM_getValue, setValue: GM_setValue, deleteValue: GM_deleteValue, listValues: GM_listValues, addStyle: GM_addStyle, info: GM_info };
  var stubs = 'var GM_addStyle=' + GM_addStyle + ';var GM_getValue=' + GM_getValue + ';var GM_setValue=' + GM_setValue + ';var GM_deleteValue=' + GM_deleteValue + ';var GM_listValues=' + GM_listValues + ';var GM_info=' + JSON.stringify(GM_info) + ';var unsafeWindow=window;var GM={getValue:GM_getValue,setValue:GM_setValue,deleteValue:GM_deleteValue,listValues:GM_listValues,addStyle:GM_addStyle,info:GM_info};';
  function run(s) {
    try { (0, eval)(stubs + '\n;' + s.body); }
    catch (e) { try { console.warn('[用户脚本:' + s.name + ']', e); } catch (e2) {} }
  }
  var loc = location.href;
  var starts = [], ends = [];
  for (var i = 0; i < S.length; i++) {
    var s = S[i];
    var hit = false;
    for (var j = 0; j < s.re.length; j++) { try { if (new RegExp(s.re[j]).test(loc)) { hit = true; break; } } catch (e) {} }
    if (hit) (s.runAt === 'document-start' ? starts : ends).push(s);
  }
  starts.forEach(run);
  function runEnds() { ends.forEach(run); }
  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', runEnds);
  else runEnds();
})();";
        return _injectorCache;
    }
}
