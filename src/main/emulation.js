'use strict';

// 通过 CDP（Chrome DevTools Protocol）为每个 webContents 注入仿真环境：
//   - Emulation.setUserAgentOverride : UA + Client Hints(userAgentMetadata)，与真实 Chrome 完全一致
//   - Emulation.setTimezoneOverride   : 时区覆盖（ICU 级，Date/Intl 全部生效），完全独立于系统时区
//   - Emulation.setLocaleOverride     : Intl 默认 locale（自定义浏览器语言时）
// 同一 webContents 只附加一次调试器，之后每次导航重复下发命令即可。
// 隐私白名单（env.allowlist 命中的站点）：时区/语言下发"清除"恢复系统，UA 伪装保留。

const attached = new WeakSet();
const { isAllowed } = require('./allowlist');
const ua = require('./ua');

async function ensureAttached(wc) {
  if (attached.has(wc)) return true;
  try {
    wc.debugger.attach('1.3');
  } catch {
    // DevTools 或其它客户端占用调试通道时暂时放弃，下次导航会自动重试
    return false;
  }
  attached.add(wc);
  try {
    wc.debugger.on('detach', () => attached.delete(wc));
    wc.once('destroyed', () => attached.delete(wc));
  } catch { /* ignore */ }
  return true;
}

function currentUrl(wc, urlArg) {
  if (typeof urlArg === 'string' && urlArg) return urlArg;
  try { return wc.getURL(); } catch { return ''; }
}

async function apply(wc, env, urlArg) {
  if (!wc || wc.isDestroyed() || !env) return;
  if (!(await ensureAttached(wc))) return;

  const allow = isAllowed(currentUrl(wc, urlArg), env.allowlist);

  if (!env.uaCustom && env.metadata) {
    try {
      // 语言：白名单站点使用系统 Accept-Language，其余使用配置语言；UA/Client Hints 始终保留
      const accept = allow ? ua.chromeAcceptLanguages() : env.acceptLanguage;
      await wc.debugger.sendCommand('Emulation.setUserAgentOverride', {
        userAgent: env.userAgent,
        acceptLanguage: accept,
        userAgentMetadata: env.metadata,
      });
    } catch (err) {
      console.warn('[emulation] setUserAgentOverride:', err.message);
    }
  }

  try {
    // timezoneId 为空字符串即清除覆盖、恢复宿主系统时区（新版 CDP 已移除 clearTimezoneOverride）
    const tz = allow ? '' : (env.timezone || '');
    await wc.debugger.sendCommand('Emulation.setTimezoneOverride', { timezoneId: tz });
  } catch (err) {
    console.warn('[emulation] timezone:', err.message);
  }

  // 浏览器语言：setUserAgentOverride 的 acceptLanguage 覆盖 navigator.language(s)，
  // setLocaleOverride 覆盖 Intl 默认 locale；白名单站点下发空参数恢复系统。
  try {
    if (!allow && env.locale) {
      await wc.debugger.sendCommand('Emulation.setLocaleOverride', { locale: env.locale });
    } else {
      await wc.debugger.sendCommand('Emulation.setLocaleOverride', {});
    }
  } catch (err) {
    console.warn('[emulation] locale:', err.message);
  }
}

function applyAll(env, webContents) {
  for (const wc of webContents.getAllWebContents()) {
    apply(wc, env);
  }
}

module.exports = { apply, applyAll };
