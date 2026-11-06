namespace RiBrowser;

/// <summary>
/// 站点专用补丁（复刻 uBlock 的 scriptlet 注入能力，v1：json-prune 等价实现）。
/// YouTube 的广告与视频内容同域（googlevideo.com），URL 过滤无法区分，
/// 必须在播放器 API 响应里剥除广告字段（adPlacements/ssapConfig），
/// 播放器读不到广告位即按“无广告”处理。
/// </summary>
internal static class SitePatches
{
    public static string Build(string? host)
    {
        if (string.IsNullOrEmpty(host) || !AdBlocker.Enabled) return "";
        if (Environment.GetEnvironmentVariable("RI_NO_SITE_PATCH") == "1") return ""; // 对照/逃生开关
        if (host.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("youtube-nocookie.com", StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith("youtu.be", StringComparison.OrdinalIgnoreCase))
            return YoutubePatch;
        return "";
    }

    private const string YoutubePatch = """
        (function(){
        if (window.__riYtPatch) return; window.__riYtPatch = 1;
        // 等价 uBlock: youtube.com##+js(json-prune, adPlacements adSlots playerConfig.ssapConfig playerConfig.webPlayerConfig.ssapConfig)
        var KEYS = ['adPlacements', 'adSlots', 'playerAds'];
        function prune(o, depth){
          if (!o || typeof o !== 'object' || depth > 14) return o;
          if (Array.isArray(o)) { for (var i = 0; i < o.length; i++) prune(o[i], depth + 1); return o; }
          for (var i = 0; i < KEYS.length; i++) { try { delete o[KEYS[i]]; } catch (e) {} }
          try { delete o.playerConfig.ssapConfig; } catch (e) {}
          try { delete o.playerConfig.webPlayerConfig.ssapConfig; } catch (e) {}
          for (var k in o) { try { if (o[k] && typeof o[k] === 'object') prune(o[k], depth + 1); } catch (e) {} }
          return o;
        }
        // 1) youtubei API 响应裁剪（播放器的 player/next 请求走 fetch）
        // 注意：new Response() 的 url/redirected 是空值，用 Proxy 透传原始值（播放器会读 res.url）
        var of = window.fetch;
        if (of) window.fetch = function(){
          var a = arguments, url = '';
          try { url = typeof a[0] === 'string' ? a[0] : (a[0] && a[0].url) || ''; } catch (e) {}
          var p = of.apply(this, a);
          if (!/youtubei\/v1\//.test(url)) return p;
          return p.then(function(res){
            try {
              var ct = (res.headers && res.headers.get && res.headers.get('content-type')) || '';
              if (ct.indexOf('json') < 0) return res;
              return res.clone().text().then(function(t){
                try {
                  var nr = new Response(JSON.stringify(prune(JSON.parse(t), 0)),
                    { status: res.status, statusText: res.statusText, headers: res.headers });
                  return new Proxy(nr, {
                    get: function(t2, k){
                      if (k === 'url') return res.url;
                      if (k === 'redirected') return res.redirected;
                      var v = t2[k];
                      return typeof v === 'function' ? v.bind(t2) : v;
                    }
                  });
                } catch (e) { return res; }
              }, function(){ return res; });
            } catch (e) { return res; }
          });
        };
        // 2) 页面内联全局裁剪：var ytInitialPlayerResponse = {...} 赋值时截获
        function guard(name){
          var v;
          try {
            Object.defineProperty(window, name, {
              configurable: true,
              get: function(){ return v; },
              set: function(nv){ v = (nv && typeof nv === 'object') ? prune(nv, 0) : nv; }
            });
          } catch (e) {}
        }
        guard('ytInitialPlayerResponse');
        guard('ytInitialData');
        })();
        """;
}
