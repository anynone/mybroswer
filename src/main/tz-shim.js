'use strict';
// 时区垫片：解决 CDP Emulation.setTimezoneOverride 无法触达跨进程 iframe（OOPIF）的问题。
//
// 原理：Intl.DateTimeFormat 显式指定 timeZone 时使用 ICU 内置时区库，与系统时区无关。
// 垫片在目标 frame 的主世界里重写 Date.prototype 的本地时间语义（getter/setter/
// toString/toLocale*、Date 构造器与解析、Intl.DateTimeFormat 默认时区），使其与
// CDP 覆写后的主框架输出完全一致。
//
// 注入路径（双保险）：
//   1) preload（nodeIntegrationInSubFrames）在每个 frame 的 document_start 检查并注入 —— 主路径
//   2) 主进程在导航/设置变更时遍历 frame 树 executeJavaScript —— 兜底与实时更新
// 垫片自带去重与自愈：CDP 已覆盖的 frame（原生时区 == 目标时区）不安装补丁。

function buildShim(tz) {
  const REQ = JSON.stringify(String(tz || ''));
  return `(function () {
  'use strict';
  var REQ = ${REQ};
  var TAG = '__polarisTz__';
  var G = typeof window !== 'undefined' ? window : self;
  if (!G) return;
  if (G[TAG] && typeof G[TAG].update === 'function') { G[TAG].update(REQ); return; }

  var Dp = Date.prototype;
  var RealDate = Date;
  var RealIDTF = Intl.DateTimeFormat;
  var nativeParse = RealDate.parse;
  var nativeNow = RealDate.now;
  var N = {};
  ['getTime','getTimezoneOffset','toString','toDateString','toTimeString',
   'toLocaleString','toLocaleDateString','toLocaleTimeString',
   'getHours','getMinutes','getSeconds','getMilliseconds',
   'getDay','getDate','getMonth','getFullYear',
   'setMilliseconds','setSeconds','setMinutes','setHours',
   'setDate','setMonth','setFullYear','setTime'
  ].forEach(function (k) { N[k] = Dp[k]; });

  function nativeTz() {
    try { return new RealIDTF().resolvedOptions().timeZone; } catch (e) { return null; }
  }

  var curTZ = '';
  var installed = false;
  var partsFmt, nameFmt;
  var offCache = Object.create(null), offCount = 0;

  function rebuild() {
    partsFmt = new RealIDTF('en-US', { timeZone: curTZ, hourCycle: 'h23',
      year: 'numeric', month: '2-digit', day: '2-digit',
      hour: '2-digit', minute: '2-digit', second: '2-digit' });
    // 时区名跟随页面默认 locale（undefined），与 CDP 原生 toString 的本地化行为一致
    nameFmt = new RealIDTF(undefined, { timeZone: curTZ, timeZoneName: 'long' });
    offCache = Object.create(null); offCount = 0;
  }

  function pad2(n) { return (n < 10 ? '0' : '') + n; }

  function wallOf(ms) {
    var p = partsFmt.formatToParts(new RealDate(ms));
    var o = {};
    for (var i = 0; i < p.length; i++) o[p[i].type] = p[i].value;
    var h = o.hour === '24' ? 0 : +o.hour;
    return { y: +o.year, mo: +o.month, d: +o.day, h: h, mi: +o.minute, s: +o.second,
             ms: ((ms % 1000) + 1000) % 1000 };
  }
  // UTC 与目标时区的偏移（毫秒，东正西负）；15 分钟粒度缓存（DST 切换均落在该网格上）
  function offsetMsAt(ms) {
    var b = Math.floor(ms / 900000);
    var v = offCache[b];
    if (v !== undefined) return v;
    var w = wallOf(ms);
    var wallMs = RealDate.UTC(w.y, w.mo - 1, w.d, w.h, w.mi, w.s, w.ms);
    v = wallMs - (ms - (((ms % 1000) + 1000) % 1000));
    offCache[b] = v;
    if (++offCount > 8192) { offCache = Object.create(null); offCount = 0; }
    return v;
  }
  function epochFromWall(w) {
    var guess = RealDate.UTC(w.y, w.mo - 1, w.d, w.h, w.mi, w.s, w.ms);
    var off = offsetMsAt(guess);
    var t = guess - off;
    var off2 = offsetMsAt(t);
    if (off2 !== off) t = guess - off2; // DST 边界修正
    return t;
  }
  function tOf(self) { var t = N.getTime.call(self); return typeof t === 'number' && isFinite(t) ? t : NaN; }
  function partOf(fmt, t, key) {
    var p = fmt.formatToParts(new RealDate(t));
    for (var i = 0; i < p.length; i++) if (p[i].type === key) return p[i].value;
    return '';
  }
  var DAYS = ['Sun','Mon','Tue','Wed','Thu','Fri','Sat'];
  var MONS = ['Jan','Feb','Mar','Apr','May','Jun','Jul','Aug','Sep','Oct','Nov','Dec'];
  function gmtLabel(offMs) {
    var m = Math.round(offMs / 60000);
    var sign = m < 0 ? '-' : '+';
    m = Math.abs(m);
    return 'GMT' + sign + pad2(Math.floor(m / 60)) + pad2(m % 60);
  }

  // ---- 补丁安装/还原基础设施（伪装为 native 代码） ----
  // prevFTS 委托链：若其它垫片（fp-shim）已替换 Function.prototype.toString，
  // 继续委托而非直连原生，避免相互覆盖导致先装垫片的伪装失效
  var prevFTS = Function.prototype.toString;
  var fakeSrc = new WeakMap();
  var FTS = function toString() {
    var s = fakeSrc.get(this);
    return s !== undefined ? s : prevFTS.call(this);
  };
  var patches = [];
  function def(obj, key, fn) {
    try {
      fakeSrc.set(fn, 'function ' + key + '() { [native code] }');
      patches.push([obj, key, obj[key]]);
      Object.defineProperty(obj, key, { value: fn, writable: true, enumerable: false, configurable: true });
    } catch (e) { /* ignore */ }
  }

  function install() {
    def(Dp, 'getTimezoneOffset', function () {
      var t = tOf(this); if (isNaN(t)) return NaN;
      return Math.round(-offsetMsAt(t) / 60000);
    });
    function getter(name) {
      return function () {
        var t = tOf(this); if (isNaN(t)) return NaN;
        return new RealDate(t + offsetMsAt(t))['getUTC' + name]();
      };
    }
    ['Hours','Minutes','Seconds','Milliseconds','Day','Date','Month','FullYear']
      .forEach(function (p) { def(Dp, 'get' + p, getter(p)); });

    function setter(mut) {
      return function () {
        var t = tOf(this); if (isNaN(t)) return NaN;
        var w = wallOf(t);
        mut(w, arguments);
        var nt = epochFromWall(w);
        N.setTime.call(this, nt);
        return nt;
      };
    }
    def(Dp, 'setMilliseconds', setter(function (w, a) { w.ms = +a[0]; }));
    def(Dp, 'setSeconds', setter(function (w, a) { w.s = +a[0]; if (a.length > 1) w.ms = +a[1]; }));
    def(Dp, 'setMinutes', setter(function (w, a) { w.mi = +a[0]; if (a.length > 1) w.s = +a[1]; if (a.length > 2) w.ms = +a[2]; }));
    def(Dp, 'setHours', setter(function (w, a) { w.h = +a[0]; if (a.length > 1) w.mi = +a[1]; if (a.length > 2) w.s = +a[2]; if (a.length > 3) w.ms = +a[3]; }));
    def(Dp, 'setDate', setter(function (w, a) { w.d = +a[0]; }));
    def(Dp, 'setMonth', setter(function (w, a) { w.mo = +a[0] + 1; if (a.length > 1) w.d = +a[1]; }));
    def(Dp, 'setFullYear', setter(function (w, a) { w.y = +a[0]; if (a.length > 1) w.mo = +a[1] + 1; if (a.length > 2) w.d = +a[2]; }));

    def(Dp, 'toString', function () {
      var t = tOf(this); if (isNaN(t)) return N.toString.call(this);
      var w = wallOf(t);
      var wd = new RealDate(t + offsetMsAt(t)).getUTCDay();
      return DAYS[wd] + ' ' + MONS[w.mo - 1] + ' ' + pad2(w.d) + ' ' + w.y + ' ' +
             pad2(w.h) + ':' + pad2(w.mi) + ':' + pad2(w.s) + ' ' +
             gmtLabel(offsetMsAt(t)) + ' (' + partOf(nameFmt, t, 'timeZoneName') + ')';
    });
    def(Dp, 'toDateString', function () {
      var t = tOf(this); if (isNaN(t)) return N.toDateString.call(this);
      var w = wallOf(t);
      var wd = new RealDate(t + offsetMsAt(t)).getUTCDay();
      return DAYS[wd] + ' ' + MONS[w.mo - 1] + ' ' + pad2(w.d) + ' ' + w.y;
    });
    def(Dp, 'toTimeString', function () {
      var t = tOf(this); if (isNaN(t)) return N.toTimeString.call(this);
      var w = wallOf(t);
      return pad2(w.h) + ':' + pad2(w.mi) + ':' + pad2(w.s) + ' ' +
             gmtLabel(offsetMsAt(t)) + ' (' + partOf(nameFmt, t, 'timeZoneName') + ')';
    });
    function loc(key) {
      return function (locales, options) {
        if (options && options.timeZone !== undefined) return N[key].call(this, locales, options);
        var o = options ? Object.assign({}, options) : {};
        o.timeZone = curTZ;
        return N[key].call(this, locales, o);
      };
    }
    ['toLocaleString','toLocaleDateString','toLocaleTimeString'].forEach(function (k) { def(Dp, k, loc(k)); });

    // Date 构造器：本地墙钟语义换算为目标时区墙钟
    var ISO_LOCAL = /^(\\d{4})-(\\d{2})-(\\d{2})(?:[T ](\\d{2}):(\\d{2})(?::(\\d{2})(?:\\.(\\d{1,3}))?)?)?$/;
    var TOSTR_FMT = /^(?:Sun|Mon|Tue|Wed|Thu|Fri|Sat)\\s+(?:Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\\s+(\\d{1,2})\\s+(\\d{4})\\s+(\\d{2}):(\\d{2}):(\\d{2})\\s+GMT[+-]\\d{4}/;
    function parseShim(s) {
      if (typeof s === 'string') {
        var str = s.trim();
        var m = ISO_LOCAL.exec(str);
        if (m) return epochFromWall({ y: +m[1], mo: +m[2], d: +m[3], h: +(m[4] || 0), mi: +(m[5] || 0), s: +(m[6] || 0), ms: +(m[7] || 0) });
        var m2 = TOSTR_FMT.exec(str);
        if (m2) return epochFromWall({ y: +m2[2], mo: MONS.indexOf(str.substr(0, 24).split(' ')[1]) + 1, d: +m2[1], h: +m2[3], mi: +m2[4], s: +m2[5], ms: 0 });
      }
      return nativeParse(s);
    }
    function PolarisDate(a, b, c, d, e, f, g) {
      if (!(this instanceof PolarisDate)) return Dp.toString.call(new RealDate());
      if (arguments.length === 0) return new RealDate(nativeNow());
      if (arguments.length === 1) {
        if (a instanceof RealDate) return new RealDate(N.getTime.call(a));
        if (typeof a === 'string') return new RealDate(parseShim(a));
        return new RealDate(a);
      }
      var y = +a; if (y >= 0 && y <= 99) y += 1900;
      return new RealDate(epochFromWall({ y: y, mo: +b + 1, d: +c, h: +(d || 0), mi: +(e || 0), s: +(f || 0), ms: +(g || 0) }));
    }
    PolarisDate.prototype = Dp;
    PolarisDate.now = function () { return nativeNow(); };
    PolarisDate.parse = parseShim;
    PolarisDate.UTC = RealDate.UTC;
    def(G, 'Date', PolarisDate);

    // Intl.DateTimeFormat：未显式指定 timeZone 时注入目标时区，其余行为全部走原生实现
    function PolarisIDTF(locales, options) {
      if (options && options.timeZone !== undefined) return new RealIDTF(locales, options);
      var o = options ? Object.assign({}, options) : {};
      o.timeZone = curTZ;
      return new RealIDTF(locales, o);
    }
    PolarisIDTF.prototype = RealIDTF.prototype;
    PolarisIDTF.supportedLocalesOf = function () { return RealIDTF.supportedLocalesOf.apply(RealIDTF, arguments); };
    def(Intl, 'DateTimeFormat', PolarisIDTF);

    fakeSrc.set(FTS, 'function toString() { [native code] }');
    fakeSrc.set(PolarisDate.now, 'function now() { [native code] }');
    fakeSrc.set(parseShim, 'function parse() { [native code] }');
    patches.push([Function.prototype, 'toString', origFTS]);
    Object.defineProperty(Function.prototype, 'toString', { value: FTS, writable: true, enumerable: false, configurable: true });
  }

  function restore() {
    patches.splice(0).forEach(function (p) {
      try { Object.defineProperty(p[0], p[1], { value: p[2], writable: true, enumerable: false, configurable: true }); } catch (e) { /* ignore */ }
    });
    installed = false;
  }

  function update(tz) {
    if (typeof tz !== 'string') return;
    if (tz && nativeTz() === tz) {
      // CDP 已覆盖本 frame（主框架/同进程 iframe）：原生即为目标时区，无需补丁
      if (installed) restore();
      curTZ = tz;
      G[TAG] = { active: false, native: true, update: update, tz: tz };
      return;
    }
    if (tz === curTZ && installed) return;
    curTZ = tz;
    if (!tz) {
      if (installed) restore();
      G[TAG] = { active: false, native: false, update: update, tz: '' };
      return;
    }
    rebuild();
    if (!installed) { install(); installed = true; }
    G[TAG] = { active: true, native: false, update: update, tz: curTZ };
  }

  update(REQ);

  // 自愈：若 2 秒后 CDP 覆盖追上了本 frame（无需补丁），自动还原，保持原生行为
  setTimeout(function () {
    try {
      if (installed && curTZ && nativeTz() === curTZ) {
        restore();
        G[TAG] = { active: false, native: true, update: update, tz: curTZ };
      }
    } catch (e) { /* ignore */ }
  }, 2000);
})();
`;
}

// 主进程侧：把（最新）时区垫片下发到一个 webContents 的全部 frame（含跨进程 iframe）。
// allowlist（隐私白名单）命中的 frame 下发空目标 —— 垫片自动还原为系统时区原生行为。
const lastSweptTz = new WeakMap();   // frame -> 已下发的目标时区（去重）

function framesOf(wc) {
  const out = [];
  try {
    (function walk(f) {
      out.push(f);
      const kids = f.frames || [];
      for (const k of kids) walk(k);
    })(wc.mainFrame);
  } catch { /* frame 树不可用时静默 */ }
  return out;
}

function sweepWc(wc, tz, allowlist) {
  if (!wc || wc.isDestroyed()) return;
  for (const f of framesOf(wc)) {
    let fUrl = '';
    try { fUrl = f.url; } catch { /* ignore */ }
    const allow = allowlist && allowlist.length
      ? require('./allowlist').isAllowed(fUrl, allowlist)
      : false;
    const target = allow ? '' : (tz || '');
    if (target === lastSweptTz.get(f)) continue; // 该 frame 已是目标状态，跳过重复注入
    lastSweptTz.set(f, target);
    try { f.executeJavaScript(buildShim(target), false).catch(() => {}); } catch { /* 个别 frame 拒绝执行时忽略 */ }
  }
}

function sweepAll(webContentsList, tz, allowlist) {
  for (const wc of webContentsList) sweepWc(wc, tz, allowlist);
}

module.exports = { buildShim, sweepWc, sweepAll };
