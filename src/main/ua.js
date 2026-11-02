'use strict';
const os = require('os');
const { app } = require('electron');

function chromeFullVersion() {
  return process.versions.chrome || '';
}

function chromeMajor() {
  return chromeFullVersion().split('.')[0] || '0';
}

// 按真实 Chrome 的官方格式手工构造 UA。
// 不依赖 app.userAgentFallback：Electron 45 会把应用名（如 Polaris/0.1.0）注入默认 UA，
// 手工构造可保证与同内核版本的真实 Chrome 逐字符一致。
function chromeUA() {
  const v = chromeFullVersion();
  let platform;
  if (process.platform === 'win32') {
    platform = process.arch === 'ia32' ? 'Windows NT 10.0' : 'Windows NT 10.0; Win64; x64';
  } else if (process.platform === 'darwin') {
    // Chrome 在 Apple Silicon 上同样上报 Intel 形式的平台串
    platform = 'Macintosh; Intel Mac OS X 10_15_7';
  } else {
    platform = 'X11; Linux x86_64';
  }
  return `Mozilla/5.0 (${platform}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/${v} Safari/537.36`;
}

// Chrome 的 Accept-Language 形如 zh-CN,zh;q=0.9 / en-US,en;q=0.9
function chromeAcceptLanguages() {
  const loc = app.getLocale() || 'en-US';
  const primary = loc.split('-')[0];
  return `${loc},${primary};q=0.9`;
}

// 指定 BCP-47 语言的 Accept-Language（与 Chrome 的派生规则一致：主语言 + 语系回退）
function acceptLanguagesFor(locale) {
  const loc = String(locale || 'en-US').trim();
  const primary = loc.split('-')[0];
  return `${loc},${primary};q=0.9`;
}

// Chrome 官方的 GREASE 品牌轮换集合（Sec-CH-UA 中的 Not-A-Brand 变体），按 major 版本轮换
const GREASE = [
  { brand: 'Not A(Brand', version: '99' },
  { brand: 'Not;A=Brand', version: '99' },
  { brand: 'Not_A Brand', version: '24' },
  { brand: 'Not.A/Brand', version: '8' },
  { brand: 'Not)A;Brand', version: '99' },
];

function grease(major) {
  const n = Number(major) || 0;
  return GREASE[n % GREASE.length];
}

function platformInfo() {
  const p = os.platform();
  const arch = os.arch();
  const architecture = arch === 'arm64' ? 'arm' : 'x86';
  const bitness = arch === 'ia32' ? '32' : '64';
  if (p === 'win32') {
    const build = Number(os.release().split('.')[2] || 0);
    // 与 Chromium 的 UA-CH 映射一致: Win11 -> 15.x, Win10 2004+ -> 13.x, 更老 -> 10.x 以下
    const platformVersion = build >= 22000 ? '15.0.0'
      : build >= 19041 ? '13.0.0'
        : build >= 18362 ? '10.0.0' : '0.1.0';
    return { platform: 'Windows', platformVersion, architecture, bitness };
  }
  if (p === 'darwin') {
    const kernel = Number(os.release().split('.')[0] || 0);
    const map = { 20: 11, 21: 12, 22: 13, 23: 14, 24: 15, 25: 16, 26: 17, 27: 18 };
    const ver = map[kernel] || Math.max(10, kernel - 9);
    return { platform: 'macOS', platformVersion: `${ver}.0.0`, architecture, bitness };
  }
  return { platform: 'Linux', platformVersion: '', architecture, bitness };
}

// 构造与真实 Chrome 完全一致的 userAgentMetadata（Client Hints）
// 同时驱动 navigator.userAgentData 与 Sec-CH-UA* 请求头
function userAgentMetadata() {
  const full = chromeFullVersion();
  const major = chromeMajor();
  const g = grease(major);
  const pi = platformInfo();
  return {
    brands: [
      { brand: 'Chromium', version: major },
      { brand: 'Google Chrome', version: major },
      { brand: g.brand, version: g.version },
    ],
    fullVersionList: [
      { brand: 'Chromium', version: full },
      { brand: 'Google Chrome', version: full },
      { brand: g.brand, version: g.version },
    ],
    fullVersion: full,
    platform: pi.platform,
    platformVersion: pi.platformVersion,
    architecture: pi.architecture,
    model: '',
    mobile: false,
    bitness: pi.bitness,
    wow64: false,
  };
}

function secChUaHeader(md) {
  return md.brands.map((b) => `"${b.brand}";v="${b.version}"`).join(', ');
}

function secChUaFullListHeader(md) {
  return md.fullVersionList.map((b) => `"${b.brand}";v="${b.version}"`).join(', ');
}

module.exports = {
  chromeFullVersion,
  chromeMajor,
  chromeUA,
  chromeAcceptLanguages,
  acceptLanguagesFor,
  userAgentMetadata,
  secChUaHeader,
  secChUaFullListHeader,
  platformInfo,
};
