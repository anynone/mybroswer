'use strict';
// 隐私防护白名单：命中站点（含子域名）不启用任何伪装——
// 直接使用系统时区/系统语言、字体测量不做干预、Canvas 不扰动、WebRTC 不拦截。
// User-Agent 伪装保留（与 Chrome 一致的基础，非隐私伪装）。

function normalizeDomain(d) {
  return String(d || '').trim().toLowerCase()
    .replace(/^\*\./, '')
    .replace(/\/.*$/, '')
    .replace(/:\d+$/, '');
}

function isAllowed(url, list) {
  if (!Array.isArray(list) || !list.length || !url) return false;
  let host;
  try {
    host = new URL(url).hostname.toLowerCase();
  } catch {
    return false;
  }
  return list.some((item) => {
    const d = normalizeDomain(item);
    if (!d) return false;
    return host === d || host.endsWith('.' + d);
  });
}

function normalizeList(list) {
  const seen = new Set();
  const out = [];
  for (const item of list) {
    const d = normalizeDomain(item);
    if (!d) continue;
    // 合法域名或 IPv4（不支持端口级区分，按主机匹配）
    if (!/^([a-z0-9]([a-z0-9-]*[a-z0-9])?\.)+[a-z]{2,}$/.test(d)
      && !/^[a-z0-9]([a-z0-9-]*[a-z0-9])?$/.test(d)
      && !/^(\d{1,3}\.){3}\d{1,3}$/.test(d)) continue;
    if (seen.has(d)) continue;
    seen.add(d);
    out.push(d);
  }
  return out.slice(0, 200);
}

module.exports = { isAllowed, normalizeDomain, normalizeList };
