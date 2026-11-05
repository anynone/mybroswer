(function () {
  'use strict';
  // 隐私指纹垫片（移植自 Polaris fp-shim）：语言 getter / 字体测量读取端补偿 /
  // Canvas 种子化扰动 / WebRTC IP 封锁 / Chrome 一致性补齐。
  // 白名单自检：本 frame 命中白名单（含子域）→ 全部直通（不伪装）。
  var C = __RIB_FP_CFG__;
  var ALLOW = __RIB_ALLOW__;
  (function () {
    try {
      if (ALLOW && ALLOW.length && typeof location === 'object' && location && location.hostname) {
        var h = location.hostname.toLowerCase(); var hit = false;
        for (var i = 0; i < ALLOW.length; i++) {
          var d = String(ALLOW[i]).toLowerCase();
          if (!d) continue;
          if (h === d || h.slice(-d.length - 1) === '.' + d) { hit = true; break; }
        }
        if (hit) C = { locale: '', langs: [], font: false, canvas: false, webrtc: false, seed: C.seed, uad: null };
      }
    } catch (e) { /* ignore */ }
  })();

  // CSS 全部通用字体族关键字（含 emoji —— 检测站以 font-family: emoji 探测系统 emoji 字体，
  // 缺它会触发读取端剥离，导致 emoji 渲染风格与真实 Windows 不一致）。
  // 注：不放行 fangsong —— stock en-US Windows 无 CJK 字体，剥离后与真人设一致。
  var GENERIC_FAMS = {
    'serif': 1, 'sans-serif': 1, 'monospace': 1, 'cursive': 1, 'fantasy': 1,
    'system-ui': 1, 'ui-serif': 1, 'ui-sans-serif': 1, 'ui-monospace': 1, 'ui-rounded': 1,
    'emoji': 1, 'math': 1,
    'inherit': 1, 'initial': 1, 'unset': 1, 'revert': 1, 'revert-layer': 1,
  };
  // 与 UA 人设（Windows Chrome）一致的标准字体集：探测时按"已安装"放行（真实渲染/度量）。
  // 中文字体（Microsoft YaHei / PingFang SC / SimSun / Noto CJK 等）与其它非标准字体
  // 不在集合内，一律按"未安装"处理 —— 既隐藏中文环境，又不产生"Windows 却无任何
  // Windows 字体（含 Segoe UI Emoji）"的自相矛盾信号。
  var WINDOWS_FAMS = {
    'segoe ui': 1, 'segoe ui emoji': 1, 'segoe ui symbol': 1, 'segoe ui historic': 1,
    'segoe ui black': 1, 'segoe ui light': 1, 'segoe ui semibold': 1, 'segoe mdl2 assets': 1,
    'segoe print': 1, 'segoe script': 1, 'arial': 1, 'arial black': 1, 'arial rounded mt bold': 1,
    'calibri': 1, 'calibri light': 1, 'cambria': 1, 'cambria math': 1, 'candara': 1,
    'comic sans ms': 1, 'consolas': 1, 'constantia': 1, 'corbel': 1, 'courier new': 1,
    'ebrima': 1, 'franklin gothic medium': 1, 'gabriola': 1, 'gadugi': 1, 'georgia': 1,
    'impact': 1, 'ink free': 1, 'javanese text': 1, 'leelawadee ui': 1, 'lucida console': 1,
    'lucida sans unicode': 1, 'malgun gothic': 0, 'marlett': 1, 'microsoft sans serif': 1,
    'mongolian baiti': 1, 'myanmar text': 1, 'nirmala ui': 1, 'palatino linotype': 1,
    'sylfaen': 1, 'symbol': 1, 'tahoma': 1, 'times new roman': 1, 'trebuchet ms': 1,
    'verdana': 1, 'webdings': 1, 'wingdings': 1, 'wingdings 2': 1, 'wingdings 3': 1,
    // CJK 字体（Malgun Gothic / MS Gothic / Meiryo / Yu Gothic / DengXian /
    // Microsoft YaHei / SimSun / Noto CJK 等）一律不放行
  };

  // Worker 内执行的字体剥离垫片源码（Blob 构造时前插）
  function buildWorkerShimSrc() {
    var fams = {};
    var ks = Object.keys(GENERIC_FAMS);
    for (var i = 0; i < ks.length; i++) { if (GENERIC_FAMS[ks[i]]) fams[ks[i].replace(/[^a-z0-9]/g, '')] = 1; }
    ks = Object.keys(WINDOWS_FAMS);
    for (var j = 0; j < ks.length; j++) { if (WINDOWS_FAMS[ks[j]]) fams[ks[j].replace(/[^a-z0-9]/g, '')] = 1; }
    return [
      '(function(){',
      'var FAMS=' + JSON.stringify(fams) + ';',
      // 归一化时把引号/空格/连字符一并剥掉（FAMS 键已同样归一化），payload 内无需出现引号字符
      'function isFam(t){var n=String(t).toLowerCase().replace(/[^a-z0-9]/g,"");return !!FAMS[n]}',
      'var RE=/^.*?[0-9.]+(?:px|pt|em|rem|%)(?:\\s*\\/\\s*\\S+)?\\s+(.+)$/',
      'function famPart(f){var m=String(f).match(RE);return m?m[1]:null}',
      'try{',
      ' var P=self.OffscreenCanvasRenderingContext2D&&self.OffscreenCanvasRenderingContext2D.prototype;',
      ' if(P){["measureText","fillText","strokeText"].forEach(function(k){',
      '  var real=P[k];if(typeof real!=="function")return;',
      '  P[k]=function(){var slf=this,raw="";',
      '   try{raw=slf.font||""}catch(e){}',
      '   var f=famPart(raw);',
      '   if(f){var toks=f.split(","),keep=[],bad=false;',
      '    for(var i=0;i<toks.length;i++){var t=toks[i];if(!t)continue;',
      '     if(isFam(t))keep.push(t);else bad=true}',
      '    if(bad){if(!keep.length)keep=["sans-serif"];',
      '     var head=raw.slice(0,raw.length-f.length);',
      '     try{slf.font=head+keep.join(", ")}catch(e){}',
      '     var r=real.apply(slf,arguments);',
      '     try{slf.font=raw}catch(e){}',
      '     return r}}',
      '   return real.apply(slf,arguments)};',
      ' })}',
      '}catch(e){}',
      '})();',
    ].join('\n');
  }

  var TAG = '__riFp__';
  var G = typeof window !== 'undefined' ? window : self;
  if (!G) return;
  if (G[TAG] && typeof G[TAG].update === 'function') { G[TAG].update(C); return; }

  // ---- 伪装基建 ----
  // prevFTS 委托链：若其它垫片已替换 Function.prototype.toString，继续委托而非直连原生，
  // 避免相互覆盖导致先装垫片的伪装失效
  var prevFTS = Function.prototype.toString;
  var fakeSrc = new WeakMap();
  var FTS = function toString() {
    var s = fakeSrc.get(this);
    return s !== undefined ? s : prevFTS.call(this);
  };
  function fake(fn, name) { fakeSrc.set(fn, 'function ' + name + '() { [native code] }'); return fn; }
  function swap(obj, key, desc) {
    try { Object.defineProperty(obj, key, desc); return true; } catch (e) { return false; }
  }

  // Navigator 原型（提前解析：0b/0d/1 多处使用）
  var NAVP = null;
  try {
    if (typeof Navigator === 'function' && Navigator.prototype) NAVP = Navigator.prototype;
    else if (typeof navigator === 'object') NAVP = Object.getPrototypeOf(navigator);
  } catch (e) { /* ignore */ }

  // ---- 0) window.chrome.runtime 垫片：真 Chrome 在普通网页上也有（不可枚举）的 runtime。
  // 语义对齐真 Chrome 非扩展页：存在、不可枚举、id 为 undefined、send/connect 抛错。
  try {
    if (typeof window !== 'undefined') {
      if (!window.chrome) window.chrome = {};
      if (!window.chrome.runtime) {
        var shimRuntime = {
          id: undefined,
          OnInstalledReason: { CHROME_UPDATE: 'chrome_update', INSTALL: 'install', UPDATE: 'update' },
          OnRestartRequiredReason: { APP_UPDATE: 'app_update', OS_UPDATE: 'os_update', PERIODIC: 'periodic' },
          PlatformArch: { ARM: 'arm', ARM64: 'arm64', X86_32: 'x86-32', X86_64: 'x86-64' },
          PlatformOs: { ANDROID: 'android', CROS: 'cros', LINUX: 'linux', MAC: 'mac', OPENBSD: 'openbsd', WIN: 'win' },
          sendNativeMessage: function () { throw new Error('Invalid extension id'); },
        };
        Object.defineProperty(window.chrome, 'runtime', {
          configurable: false, enumerable: false, writable: false, value: shimRuntime,
        });
      }
    }
  } catch (e) { /* ignore */ }

  // ---- 0b) navigator.userAgentData 垫片（自门控） ----
  // CDP 的 userAgentMetadata 生效时 brands 已含 Google Chrome → 跳过；
  // 无 CDP 覆盖时补齐低熵字段与 getHighEntropyValues。
  try {
    var uad = C.uad;
    var exUAD = navigator && navigator.userAgentData;
    var branded = exUAD && Array.isArray(exUAD.brands) && exUAD.brands.some(function (b) { return b && b.brand === 'Google Chrome'; });
    if (uad && !branded && NAVP) {
      var gcFull = '';
      for (var i = 0; i < (uad.fullVersionList || []).length; i++) {
        if (uad.fullVersionList[i].brand === 'Google Chrome') gcFull = uad.fullVersionList[i].version;
      }
      var uadObj = {
        brands: uad.brands,
        mobile: false,
        platform: uad.platform,
      };
      var gh = function getHighEntropyValues(hints) {
        var want = {};
        for (var i = 0; i < (hints || []).length; i++) want[String(hints[i])] = 1;
        var out = {};
        if (want.brands) out.brands = uad.brands;
        if (want.mobile) out.mobile = false;
        if (want.platform) out.platform = uad.platform;
        if (want.platformVersion) out.platformVersion = uad.platformVersion;
        if (want.architecture) out.architecture = uad.architecture;
        if (want.bitness) out.bitness = uad.bitness;
        if (want.model) out.model = '';
        if (want.wow64) out.wow64 = false;
        if (want.fullVersionList) out.fullVersionList = uad.fullVersionList;
        if (want.fullVersion || want.uaFullVersion) out.fullVersion = gcFull;
        return Promise.resolve(out);
      };
      fake(gh, 'getHighEntropyValues');
      uadObj.getHighEntropyValues = gh;
      var uadGet = function userAgentData() { return uadObj; };
      fake(uadGet, 'get userAgentData');
      swap(NAVP, 'userAgentData', { get: uadGet, configurable: true });
    }
  } catch (e) { /* ignore */ }

  // ---- 0c) EME/Widevine 能力垫片：未捆绑 Widevine CDM 时 requestMediaKeySystemAccess
  // ('com.widevine.alpha') 必失败——真 Chrome 均可用。折衷：能力探测通过（返回伪 access），
  // 真正播放 DRM 时才失败。
  try {
    var FAKE_SYSTEMS = { 'com.widevine.alpha': 1, 'com.microsoft.playready': 1, 'org.w3.clearkey': 1 };
    var realRmska = navigator.requestMediaKeySystemAccess && navigator.requestMediaKeySystemAccess.bind(navigator);
    if (realRmska && NAVP) {
      var fakeRmska = function requestMediaKeySystemAccess(keySystem, configs) {
        if (FAKE_SYSTEMS[keySystem]) {
          var cfg0 = (configs && configs[0]) || {
            videoCapabilities: [{ contentType: 'video/mp4; codecs="avc1.42E01E"' }],
            audioCapabilities: [{ contentType: 'audio/mp4; codecs="mp4a.40.2"' }],
          };
          var mkSession = function createSession() {
            var s = {
              addEventListener: function () {}, removeEventListener: function () {},
              generateRequest: function () { return Promise.resolve(); },
              load: function () { return Promise.resolve(false); },
              close: function () { return Promise.resolve(); },
              remove: function () { return Promise.resolve(); },
            };
            fake(s.generateRequest, 'generateRequest'); fake(s.close, 'close'); fake(s.remove, 'remove');
            return s;
          };
          var mk = { createSession: mkSession, serverProtocol: '' };
          fake(mkSession, 'createSession');
          var access = {
            keySystem: keySystem,
            getConfiguration: function getConfiguration() { return cfg0; },
            createMediaKeys: function createMediaKeys() { return Promise.resolve(mk); },
          };
          fake(access.getConfiguration, 'getConfiguration');
          fake(access.createMediaKeys, 'createMediaKeys');
          return Promise.resolve(access);
        }
        return realRmska(keySystem, configs);
      };
      fake(fakeRmska, 'requestMediaKeySystemAccess');
      swap(NAVP, 'requestMediaKeySystemAccess', { value: fakeRmska, writable: true, configurable: true });
    }
    // mediaCapabilities.decodingInfo 带 keySystemConfiguration 的探测同样放行
    if (navigator.mediaCapabilities && navigator.mediaCapabilities.decodingInfo) {
      var realDI = navigator.mediaCapabilities.decodingInfo.bind(navigator.mediaCapabilities);
      var fakeDI = function decodingInfo(cfg) {
        var ksc = cfg && cfg.keySystemConfiguration;
        if (ksc && FAKE_SYSTEMS[ksc.keySystem]) {
          return realDI(cfg).then(function (r) {
            r.supported = true; r.smooth = r.smooth !== false; r.powerEfficient = r.powerEfficient !== false;
            return r;
          }).catch(function () { return { supported: true, smooth: true, powerEfficient: true }; });
        }
        return realDI(cfg);
      };
      fake(fakeDI, 'decodingInfo');
      var MCP = Object.getPrototypeOf(navigator.mediaCapabilities);
      swap(MCP, 'decodingInfo', { value: fakeDI, writable: true, configurable: true });
    }
  } catch (e) { /* ignore */ }

  // ---- 0d) navigator.deviceMemory 钳制：真 Chrome 按规范上报上限 8GB
  try {
    var dm = navigator.deviceMemory;
    if (typeof dm === 'number' && dm > 8) {
      var dmGet = function deviceMemory() { return 8; };
      fake(dmGet, 'get deviceMemory');
      swap(NAVP, 'deviceMemory', { get: dmGet, configurable: true });
    }
  } catch (e) { /* ignore */ }

  // ---- 0e) window.chrome.app 垫片：真 Chrome 普通页面上存在
  // （isInstalled:false + 三个查询方法）
  try {
    if (typeof window !== 'undefined' && window.chrome && !window.chrome.app) {
      var shimApp = {
        isInstalled: false,
        InstallState: { DISABLED: 'disabled', INSTALLED: 'installed', NOT_INSTALLED: 'not_installed' },
        RunningState: { CANNOT_RUN: 'cannot_run', READY_TO_RUN: 'ready_to_run', RUNNING: 'running' },
        getDetails: function getDetails() { return null; },
        getIsInstalled: function getIsInstalled() { return false; },
        installState: function installState() { return shimApp.InstallState.NOT_INSTALLED; },
        runningState: function runningState() { return shimApp.RunningState.CANNOT_RUN; },
      };
      fake(shimApp.getDetails, 'getDetails');
      fake(shimApp.getIsInstalled, 'getIsInstalled');
      fake(shimApp.installState, 'installState');
      fake(shimApp.runningState, 'runningState');
      Object.defineProperty(window.chrome, 'app', {
        configurable: false, enumerable: false, writable: false, value: shimApp,
      });
    }
  } catch (e) { /* ignore */ }

  // ---- 1) 语言：navigator.language / navigator.languages ----
  // 保留原生 getter：未启用覆盖时完全透传，启用时返回目标语言
  var odLang = NAVP ? Object.getOwnPropertyDescriptor(NAVP, 'language') : null;
  var odLangs = NAVP ? Object.getOwnPropertyDescriptor(NAVP, 'languages') : null;
  var natLangGet = odLang && odLang.get;
  var natLangsGet = odLangs && odLangs.get;
  function realLang() {
    try { return natLangGet ? natLangGet.call(navigator) : String(navigator.language); }
    catch (e) { return ''; }
  }
  function realLangs() {
    try { return natLangsGet ? natLangsGet.call(navigator) : navigator.languages; }
    catch (e) { return []; }
  }
  if (NAVP && odLang && odLangs) {
    var gl = fake(function language() { return C.locale || realLang(); }, 'language');
    var gs = fake(function languages() {
      if (!C.locale) return realLangs();
      return C.langs && C.langs.length ? C.langs : [C.locale];
    }, 'languages');
    swap(NAVP, 'language', { get: gl, set: undefined, enumerable: true, configurable: true });
    swap(NAVP, 'languages', { get: gs, set: undefined, enumerable: true, configurable: true });
  }

  // ---- 2) 字体指纹防护：读取端补偿 ----
  // 宽度探测（大字号下比对目标字体与回退字体的宽度差）无法靠量化消除；
  // 且内联 style.fontFamily 赋值走 Blink 命名属性拦截器，无 JS accessor 可替换。
  // 故采用"读取端补偿"：在 offsetWidth/offsetHeight/client*/getBoundingClientRect/measureText
  // 读取时，若指定字体族含本地字体名（非通用族、非页面 webfont、非标准 Windows 字体），
  // 临时替换为剥离值 → 读取布局/度量 → 立即恢复原值。
  // 页面渲染与样式读回完全不变（无篡改痕迹），探测读到的恒等于"该字体未安装"。
  var CTX2D = null;
  try { if (typeof CanvasRenderingContext2D === 'function') CTX2D = CanvasRenderingContext2D.prototype; } catch (e) { /* ignore */ }
  var fontInstalled = false;

  var WF = Object.create(null);
  var wfAt = 0;
  function refreshWebfonts() {
    // 页面注册的 @font-face 字体名缓存（500ms 刷新），这些字体名允许保留
    try {
      var now = Date.now();
      if (now - wfAt < 500) return;
      wfAt = now;
      var s = Object.create(null);
      if (G.document && G.document.fonts) {
        G.document.fonts.forEach(function (f) {
          if (f && f.family) s[String(f.family).replace(/^['"]|['"]$/g, '').trim().toLowerCase()] = 1;
        });
      }
      WF = s;
    } catch (e) { /* ignore */ }
  }
  function splitFamilies(list) {
    var out = [], cur = '', q = '';
    var s = String(list);
    for (var i = 0; i < s.length; i++) {
      var ch = s.charAt(i);
      if (q) { cur += ch; if (ch === q) q = ''; continue; }
      if (ch === '"' || ch === "'") { q = ch; cur += ch; continue; }
      if (ch === ',') { out.push(cur); cur = ''; continue; }
      cur += ch;
    }
    out.push(cur);
    return out;
  }
  function famName(t) { return t.replace(/^['"]|['"]$/g, '').trim(); }
  function keepFam(tok) {
    var n = famName(tok).toLowerCase();
    if (!n) return false;
    return !!GENERIC_FAMS[n] || !!WF[n] || !!WINDOWS_FAMS[n];
  }
  function sanitizeFamilyList(list) {
    refreshWebfonts();
    var s = String(list);
    var imp = '';
    var im = /\s*!\s*important\s*$/i.exec(s);
    if (im) { imp = im[0]; s = s.slice(0, s.length - im[0].length); }
    var toks = splitFamilies(s);
    var kept = [];
    for (var i = 0; i < toks.length; i++) {
      var t = toks[i].trim();
      if (t && keepFam(t)) kept.push(t);
    }
    return (kept.length ? kept.join(', ') : 'sans-serif') + imp;
  }
  // font 简写："前缀(样式/字重/字号/行高) 家族"——只重写家族部分，解析失败则原样放行
  var FONT_SHORT_LH = /^(.*?\d+(?:\.\d+)?(?:px|pt|pc|in|cm|mm|q|em|rem|ex|ch|vw|vh|%)\s*\/\s*\S+\s+)(.+)$/;
  var FONT_SHORT = /^(.*?\d+(?:\.\d+)?(?:px|pt|pc|in|cm|mm|q|em|rem|ex|ch|vw|vh|%)\s+)(.+)$/;
  function sanitizeFontShorthand(v) {
    var s = String(v);
    var m = FONT_SHORT_LH.exec(s) || FONT_SHORT.exec(s);
    if (!m) return s;
    return m[1] + sanitizeFamilyList(m[2]);
  }

  // 内联 fontFamily（或 ctx.font）中是否含待剥离的本地字体名
  function needsSanitizeList(raw) {
    refreshWebfonts();
    var toks = splitFamilies(raw);
    for (var i = 0; i < toks.length; i++) {
      var t = toks[i].trim();
      if (t && !keepFam(t)) return true;
    }
    return false;
  }
  function needsSanitizeShorthand(v) {
    var m = FONT_SHORT_LH.exec(String(v)) || FONT_SHORT.exec(String(v));
    return !!m && needsSanitizeList(m[2]);
  }
  // 读取端补偿：临时把元素/上下文的字体剥离为回退字体 → 读取布局/度量 → 恢复原值。
  function withStrippedFont(getRaw, setRaw, restoreRaw, fn) {
    var raw = '';
    try { raw = getRaw(); } catch (e) { return fn(); }
    if (!raw || !needsSanitizeList(raw)) return fn();
    try {
      setRaw(sanitizeFamilyList(raw));
      var v = fn();
      restoreRaw(raw);
      return v;
    } catch (e) {
      try { restoreRaw(raw); } catch (e2) { /* ignore */ }
      return fn();
    }
  }

  function installFont() {
    if (fontInstalled) return;
    var okAny = false;

    // 2a. measureText：读取时临时剥离本地字体名（宽度探测防护），度量保持原生小数。
    // 同时覆盖 CanvasRenderingContext2D 与 OffscreenCanvasRenderingContext2D
    var ctx2dProtos = [];
    if (CTX2D) ctx2dProtos.push(CTX2D);
    try {
      if (typeof OffscreenCanvasRenderingContext2D === 'function') {
        ctx2dProtos.push(OffscreenCanvasRenderingContext2D.prototype);
      }
    } catch (e) { /* ignore */ }
    ctx2dProtos.forEach(function (P) {
      try {
        if (!P || !P.measureText) return;
        var realMT = P.measureText;
        var measureText = fake(function measureText(text) {
          var self = this;
          var run = function () { return realMT.call(self, text); };
          if (!C.font) return run();
          try {
            var raw = self.font || '';
            if (raw && needsSanitizeShorthand(raw)) {
              self.font = sanitizeFontShorthand(raw);
              var tm = run();
              self.font = raw;
              return tm;
            }
          } catch (e) { /* ignore */ }
          return run();
        }, 'measureText');
        if (swap(P, 'measureText', { value: measureText, writable: true, enumerable: false, configurable: true })) okAny = true;
      } catch (e) { /* ignore */ }
    });

    // 2a2. fillText/strokeText 渲染层剥离：像素级探测防护。
    // 检测站可绕过一切测量 API：用不同字体分别 fillText 再比对画布像素。
    // 渲染前临时剥离本地字体名 → 像素恒为回退字体 → 与"未安装"一致；
    // 白名单字体（Segoe UI Emoji 等）正常渲染，emoji 像素探测不受影响。
    ctx2dProtos.forEach(function (P) {
      try {
        if (!P) return;
        ['fillText', 'strokeText'].forEach(function (k) {
          var real = P[k];
          if (typeof real !== 'function') return;
          var wrapped = fake(function () {
            var self = this;
            if (!C.font) return real.apply(self, arguments);
            var raw = '';
            try { raw = self.font || ''; } catch (e) { /* ignore */ }
            if (raw && needsSanitizeShorthand(raw)) {
              try {
                self.font = sanitizeFontShorthand(raw);
                var r = real.apply(self, arguments);
                self.font = raw;
                return r;
              } catch (e) { try { self.font = raw; } catch (e2) { /* ignore */ } }
            }
            return real.apply(self, arguments);
          }, k);
          swap(P, k, { value: wrapped, writable: true, enumerable: false, configurable: true });
          okAny = true;
        });
      } catch (e) { /* ignore */ }
    });

    // 2b. 布局度量读取端：offsetWidth/offsetHeight/clientWidth/clientHeight
    try {
      var HEP = G.HTMLElement && G.HTMLElement.prototype;
      if (HEP) {
        ['offsetWidth', 'offsetHeight', 'clientWidth', 'clientHeight'].forEach(function (k) {
          try {
            var d = Object.getOwnPropertyDescriptor(HEP, k);
            if (!d || !d.get || d.configurable === false) return;
            var g = fake(function () {
              var el = this;
              if (!C.font) return d.get.call(el);
              return withStrippedFont(
                function () { return el.style ? el.style.fontFamily : ''; },
                function (v) { el.style.fontFamily = v; },
                function (v) { el.style.fontFamily = v; },
                function () { return d.get.call(el); }
              );
            }, 'get ' + k);
            swap(HEP, k, { get: g, set: d.set, enumerable: d.enumerable, configurable: true });
            okAny = true;
          } catch (e) { /* ignore */ }
        });
      }
    } catch (e) { /* ignore */ }

    // 2c. getBoundingClientRect 同理（部分探测用矩形宽度比对）
    try {
      var EP = G.Element && G.Element.prototype;
      if (EP) {
        var dR = Object.getOwnPropertyDescriptor(EP, 'getBoundingClientRect');
        if (dR && dR.value && dR.configurable !== false) {
          var gbcr = fake(function getBoundingClientRect() {
            var el = this;
            if (!C.font || !el.style) return dR.value.call(el);
            return withStrippedFont(
              function () { return el.style ? el.style.fontFamily : ''; },
              function (v) { el.style.fontFamily = v; },
              function (v) { el.style.fontFamily = v; },
              function () { return dR.value.call(el); }
            );
          }, 'getBoundingClientRect');
          swap(EP, 'getBoundingClientRect', { value: gbcr, writable: true, enumerable: false, configurable: true });
          okAny = true;
        }
      }
    } catch (e) { /* ignore */ }

    // 2c2. SVG 文本测量（getComputedTextLength 等）：与 offsetWidth 同样的读取端补偿
    try {
      var SVGTEXT = G.SVGTextContentElement && G.SVGTextContentElement.prototype;
      if (SVGTEXT) {
        ['getComputedTextLength', 'getSubStringLength', 'getExtentOfChar'].forEach(function (k) {
          try {
            var real = SVGTEXT[k];
            if (typeof real !== 'function') return;
            var wrapped = fake(function () {
              var el = this;
              if (!C.font || !el.style) return real.apply(el, arguments);
              return withStrippedFont(
                function () { return el.style.fontFamily; },
                function (v) { el.style.fontFamily = v; },
                function (v) { el.style.fontFamily = v; },
                function () { return real.apply(el, arguments); }
              );
            }, k);
            swap(SVGTEXT, k, { value: wrapped, writable: true, enumerable: false, configurable: true });
            okAny = true;
          } catch (e) { /* ignore */ }
        });
      }
    } catch (e) { /* ignore */ }

    // 2c3. Worker 内 OffscreenCanvas 字体探测防护：Worker 是独立 JS 环境，页面垫片
    // 无法进入。指纹库创建 Worker 必先 new Blob([code], {type:'text/javascript'})——
    // 在 Blob 构造时同步把 Worker 版剥离垫片前插进代码。
    try {
      var OrigBlob = G.Blob;
      var workerShimSrc = buildWorkerShimSrc();
      function PatchedBlob(parts, options) {
        try {
          if (C.font && parts && parts.length) {
            var type = ((options && options.type) || '');
            if (/javascript/i.test(String(type))) {
              var arr = Array.prototype.slice.call(parts);
              arr.unshift(workerShimSrc);
              return new OrigBlob(arr, options);
            }
          }
        } catch (e) { /* ignore */ }
        return Reflect.construct(OrigBlob, [parts, options]);
      }
      PatchedBlob.prototype = OrigBlob.prototype;
      try { Object.setPrototypeOf(PatchedBlob, OrigBlob); } catch (e) { /* ignore */ }
      swap(G, 'Blob', { value: PatchedBlob, writable: true, enumerable: false, configurable: true });
      okAny = true;
    } catch (e) { /* ignore */ }

    // 2d. document.fonts.check：非通用族/非 webfont/非标准字体一律返回 false（与"未安装"一致）
    try {
      var FFS = G.FontFaceSet && G.FontFaceSet.prototype;
      if (FFS && FFS.check) {
        var realChk = FFS.check;
        var check = fake(function check(font, text) {
          if (!C.font) return realChk.apply(this, arguments);
          try {
            var m = FONT_SHORT_LH.exec(String(font)) || FONT_SHORT.exec(String(font));
            if (!m) return realChk.apply(this, arguments);
            refreshWebfonts();
            var toks = splitFamilies(m[2]);
            var kept = [];
            for (var i = 0; i < toks.length; i++) {
              var t = toks[i].trim();
              if (t && keepFam(t)) kept.push(t);
            }
            if (!kept.length) return false;
            return realChk.call(this, m[1] + kept.join(', '), text);
          } catch (e) { return realChk.apply(this, arguments); }
        }, 'check');
        swap(FFS, 'check', { value: check, writable: true, enumerable: false, configurable: true });
        okAny = true;
      }
    } catch (e) { /* ignore */ }

    fontInstalled = okAny || !!CTX2D;
  }

  // ---- 3) Canvas 指纹确定性扰动 ----
  var CANP = null;
  try { if (typeof HTMLCanvasElement === 'function') CANP = HTMLCanvasElement.prototype; } catch (e) { /* ignore */ }
  var canvasInstalled = false;
  function installCanvas() {
    if (canvasInstalled || !CANP || !CTX2D) return;
    var realGetCtx = CANP.getContext;
    var realGID = CTX2D.getImageData;
    var realPID = CTX2D.putImageData;
    var realTUD = CANP.toDataURL;
    var realTB = CANP.toBlob;
    var ctx2d = new WeakMap();   // canvas -> 2d context
    var noised = new WeakSet();  // 已扰动过的 canvas（每画布一次性，保证同画布指纹稳定）
    var cvIdx = new WeakMap();
    var cvSeq = 1;
    function rnd(seed) {
      var s = seed >>> 0 || 1;
      return function () { s = (Math.imul(s, 1664525) + 1013904223) >>> 0; return s; };
    }
    function noiseOnce(cv) {
      if (!C.canvas || !cv || noised.has(cv)) return;
      var ctx = ctx2d.get(cv);
      if (!ctx) return;
      var w = cv.width, h = cv.height;
      if (!w || !h) return;
      noised.add(cv);
      var idx = cvIdx.get(cv);
      if (idx === undefined) { idx = cvSeq++; cvIdx.set(cv, idx); }
      try {
        var d = realGID.call(ctx, 0, 0, w, h);
        var a = d.data;
        var px = w * h;
        var r = rnd((C.seed ^ Math.imul(idx, 2654435761)) >>> 0);
        // 只翻转不透明像素的 R 通道最低位：canvas 内部按预乘 alpha 存储，
        // 透明像素的 RGB 在往返转换中会被清零，视觉上 LSB 亦不可见
        var flipped = 0;
        for (var p = 0; p < px; p++) {
          var o = p * 4;
          if (a[o + 3] === 0) continue;
          if ((r() & 7) === 0) { a[o] ^= 1; flipped++; }
        }
        if (!flipped) {
          // 保底：整个画布都没有命中时，按种子选定一个非透明像素
          var r2 = rnd((C.seed ^ Math.imul(idx, 0x9e3779b1)) >>> 0);
          var k = 1 + (r2() % 107);
          var seen = 0;
          for (var p2 = 0; p2 < px; p2++) {
            var o2 = p2 * 4;
            if (a[o2 + 3] === 0) continue;
            seen++;
            if (seen % k === 0) { a[o2] ^= 1; break; }
          }
        }
        realPID.call(ctx, d, 0, 0);
      } catch (e) { /* ignore */ }
    }
    var getContext = fake(function getContext(type) {
      var ctx = realGetCtx.apply(this, arguments);
      if (ctx && type === '2d') { try { ctx2d.set(this, ctx); } catch (e) { /* ignore */ } }
      return ctx;
    }, 'getContext');
    var getImageData = fake(function getImageData(sx, sy, sw, sh) {
      noiseOnce(this.canvas);
      return realGID.call(this, sx, sy, sw, sh);
    }, 'getImageData');
    var toDataURL = fake(function toDataURL() {
      noiseOnce(this);
      return realTUD.apply(this, arguments);
    }, 'toDataURL');
    var toBlob = fake(function toBlob() {
      noiseOnce(this);
      return realTB.apply(this, arguments);
    }, 'toBlob');
    var ok = swap(CANP, 'getContext', { value: getContext, writable: true, enumerable: false, configurable: true })
          && swap(CTX2D, 'getImageData', { value: getImageData, writable: true, enumerable: false, configurable: true })
          && swap(CANP, 'toDataURL', { value: toDataURL, writable: true, enumerable: false, configurable: true })
          && swap(CANP, 'toBlob', { value: toBlob, writable: true, enumerable: false, configurable: true });
    canvasInstalled = ok;
  }

  // ---- 4) WebRTC 防泄露：封锁页面可见的裸 IP ICE 候选 ----
  //   - onicecandidate / addEventListener('icecandidate') 交付的候选中，地址为 IP 字面量
  //     且类型非 relay 的（host=本机内网 IP，srflx=本机公网 IP）一律拦截不投递
  //   - localDescription / currentLocalDescription / pendingLocalDescription 的 SDP
  //     中对应 a=candidate 行移除
  //   - getStats 的 local-candidate 统计项清除 address/ip 字段
  // mDNS(.local) 候选与 relay(TURN) 候选不受影响。
  var webrtcInstalled = false;
  function installWebRTC() {
    if (webrtcInstalled) return;
    var RPC = G.RTCPeerConnection;
    if (!RPC || !RPC.prototype) return;
    var RP = RPC.prototype;
    var ET = G.EventTarget && G.EventTarget.prototype;

    function candInfo(line) {
      var m = String(line).split(' ');
      if (m.length < 8) return null;
      return { addr: m[4], typ: m[7] };
    }
    function isIpLiteral(a) {
      return /^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$/.test(a) || /^\[[0-9a-fA-F:]+\]$/.test(a);
    }
    function leaked(line) {
      var c = candInfo(line);
      return !!c && c.typ !== 'relay' && isIpLiteral(c.addr);
    }
    function isIpLoose(a) {
      return /^\d{1,3}\.\d{1,3}\.\d{1,3}\.\d{1,3}$/.test(a) || /^[0-9a-fA-F:]+:[0-9a-fA-F:]+$/.test(a);
    }
    function deliverable(cand) {
      return !!cand && (!cand.candidate || !leaked(cand.candidate));
    }
    function filterSdp(sdp) {
      if (!sdp) return sdp;
      return sdp.split('\r\n').map(function (l) {
        // c= 连接行若为本机 IP，替换为标准占位地址（ICE 实际走 a=candidate，不影响语义）
        if (l.indexOf('c=IN IP4 ') === 0) {
          var a4 = l.slice(9).trim();
          if (isIpLoose(a4) && a4 !== '0.0.0.0') return 'c=IN IP4 0.0.0.0';
        } else if (l.indexOf('c=IN IP6 ') === 0) {
          var a6 = l.slice(9).trim();
          if (isIpLoose(a6) && a6 !== '::') return 'c=IN IP6 ::';
        }
        return l;
      }).filter(function (l) {
        return !(l.indexOf('a=candidate:') === 0 && leaked(l.slice(2)));
      }).join('\r\n');
    }

    // 1) onicecandidate 属性回调
    var odIce = Object.getOwnPropertyDescriptor(RP, 'onicecandidate');
    if (odIce && odIce.get && odIce.set && odIce.configurable) {
      var iceUsers = new WeakMap();
      Object.defineProperty(RP, 'onicecandidate', {
        get: fake(function () {
          var u = iceUsers.get(this);
          return u ? u.user : (odIce.get ? odIce.get.call(this) : null);
        }, 'get onicecandidate'),
        set: (function () {
          var s = function (cb) {
            if (typeof cb !== 'function' || !C.webrtc) {
              iceUsers.delete(this);
              odIce.set.call(this, cb);
              return;
            }
            var self = this;
            iceUsers.set(this, { user: cb });
            odIce.set.call(this, function (e) {
              if (e && e.candidate && !deliverable(e.candidate)) return; // 拦截本机 IP 候选
              return cb.call(self, e);
            });
          };
          return fake(s, 'set onicecandidate');
        })(),
        enumerable: true, configurable: true,
      });
    }

    // 2) addEventListener('icecandidate')（记录包装以便 removeEventListener 对应）
    if (ET && typeof ET.addEventListener === 'function') {
      var realAEL = ET.addEventListener;
      var realREL = ET.removeEventListener;
      var iceWrappers = new WeakMap(); // instance -> Map<listener, wrapped>
      var addEventListener = fake(function addEventListener(type, listener, options) {
        if (C.webrtc && type === 'icecandidate' && this instanceof RPC && typeof listener === 'function') {
          var self = this;
          var wrapped = function (e) {
            if (e && e.candidate && !deliverable(e.candidate)) return;
            return listener.call(self, e);
          };
          var m = iceWrappers.get(self);
          if (!m) { m = new Map(); iceWrappers.set(self, m); }
          m.set(listener, wrapped);
          return realAEL.call(this, type, wrapped, options);
        }
        return realAEL.apply(this, arguments);
      }, 'addEventListener');
      var removeEventListener = fake(function removeEventListener(type, listener, options) {
        if (type === 'icecandidate' && this instanceof RPC && typeof listener === 'function') {
          var m = iceWrappers.get(this);
          if (m) {
            var wrapped = m.get(listener);
            if (wrapped) {
              m.delete(listener);
              return realREL.call(this, type, wrapped, options);
            }
          }
        }
        return realREL.apply(this, arguments);
      }, 'removeEventListener');
      if (swap(ET, 'addEventListener', { value: addEventListener, writable: true, enumerable: false, configurable: true })
        && swap(ET, 'removeEventListener', { value: removeEventListener, writable: true, enumerable: false, configurable: true })) {
        webrtcInstalled = true;
      }
    }

    // 3) localDescription 系列：SDP 中移除裸 IP 候选行
    ['localDescription', 'currentLocalDescription', 'pendingLocalDescription'].forEach(function (k) {
      var d = Object.getOwnPropertyDescriptor(RP, k);
      if (d && d.get && d.configurable) {
        var g = fake(function () {
          var desc = d.get.call(this);
          if (!desc || !C.webrtc) return desc;
          try { return { type: desc.type, sdp: filterSdp(desc.sdp) }; }
          catch (e) { return desc; }
        }, 'get ' + k);
        swap(RP, k, { get: g, set: undefined, enumerable: true, configurable: true });
      }
    });

    // 4) getStats：清除 local-candidate 统计项中的 address/ip
    if (RP.getStats) {
      var realGS = RP.getStats;
      var getStats = fake(function getStats(selector) {
        return realGS.call(this, selector).then(function (report) {
          if (!C.webrtc) return report;
          try {
            var entries = [];
            report.forEach(function (v) {
              if (v && v.type === 'local-candidate') {
                var addr = v.address || v.ip;
                if (addr && isIpLiteral(String(addr)) && v.candidateType !== 'relay') {
                  v = Object.assign({}, v, { address: null, ip: null, networkType: null, networkAdapterType: null });
                }
              }
              entries.push([v.id !== undefined ? v.id : String(entries.length), v]);
            });
            if (typeof G.RTCStatsReport === 'function') return new G.RTCStatsReport(new Map(entries));
            return new Map(entries);
          } catch (e) { return report; }
        });
      }, 'getStats');
      swap(RP, 'getStats', { value: getStats, writable: true, enumerable: false, configurable: true });
    }

    webrtcInstalled = true;
  }

  function update(cfg) {
    if (!cfg || typeof cfg !== 'object') return;
    C = cfg;
    if (C.font) { installFont(); }
    installCanvas(); // 钩子常驻；禁用时 noiseOnce 内直接透传
    installWebRTC(); // 钩子常驻；禁用时各拦截点直接透传
  }

  fake(FTS, 'toString');
  swap(Function.prototype, 'toString', { value: FTS, writable: true, enumerable: false, configurable: true });

  update(C);
  G[TAG] = { update: update };
})();
