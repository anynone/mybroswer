'use strict';
const fs = require('fs');
const path = require('path');
const { app } = require('electron');

const DEFAULTS = {
  // '' 表示跟随系统时区；否则为 IANA 时区 ID（如 America/New_York），页面将使用该时区
  timezone: '',
  // 'chrome' = 自动生成与真实 Chrome 完全一致的 UA（按内核版本）；'custom' = 使用 customUserAgent
  userAgentMode: 'chrome',
  customUserAgent: '',
  // 地址栏搜索引擎: bing | baidu | google
  searchEngine: 'bing',
  homepage: 'browser://newtab/',
  // 显示书签栏（Ctrl+Shift+B / 菜单切换）
  bookmarkBar: false,
  // 隐私防护白名单：命中域名（含子域）不启用时区/语言/字体/Canvas/WebRTC 伪装
  privacyAllowlist: [],
  // 浏览器语言：'system' = 跟随系统；否则为 BCP-47（如 en-US）。
  // 同步覆盖 navigator.language(s) / Intl 默认 locale / Accept-Language 请求头，不暴露系统语言
  locale: 'system',
  // WebRTC 防泄露：页面层拦截 ICE 候选/SDP/统计中的本机 IP（内核级开关已在 Chromium 156 移除）
  webrtcGuard: true,
  // 字体指纹防护：Canvas 文本测量量化 + 移除本地字体枚举 API（queryLocalFonts）
  fontGuard: true,
  // Canvas 指纹确定性扰动：默认关闭——扰动会使画布哈希不匹配任何真实系统样本，
  // 触发检测站"OS 供应商不确定"回退；原生渲染与真 Chrome 完全一致。需要防关联可手动开启。
  canvasNoise: false,
  // Canvas 扰动种子（首次启动自动生成）
  fpSeed: 0,
};

function settingsPath() {
  return path.join(app.getPath('userData'), 'settings.json');
}

function load() {
  let s;
  try {
    const raw = fs.readFileSync(settingsPath(), 'utf8');
    s = { ...DEFAULTS, ...JSON.parse(raw) };
  } catch {
    s = { ...DEFAULTS };
  }
  if (!Number.isInteger(s.fpSeed) || s.fpSeed <= 0) {
    s.fpSeed = 1 + Math.floor(Math.random() * 2147483646);
  }
  return s;
}

function save(settings) {
  const file = settingsPath();
  fs.mkdirSync(path.dirname(file), { recursive: true });
  fs.writeFileSync(file, JSON.stringify(settings, null, 2), 'utf8');
}

module.exports = { DEFAULTS, load, save };
