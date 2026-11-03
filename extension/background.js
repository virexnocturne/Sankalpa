import { evaluateMultiple, DomainDecisionCache } from './matcher.js';

export function normalizeTheme(theme) {
  if (!theme || typeof theme !== 'string') return 'Current';
  const t = theme.trim().toLowerCase();
  if (t === 'deepblack' || t === 'deep-black') return 'DeepBlack';
  if (t === 'minimalwhite' || t === 'minimal-white') return 'MinimalWhite';
  if (t === 'current' || t === 'slate') return 'Current';
  return 'Current';
}

let activeProfiles = [];
let allProfiles = [];
let rulesVersion = -1;
let currentTheme = 'Current';
let pendingAddWebsiteCallback = null;
let pendingCreateProfileCallback = null;
let getTime = () => Date.now();
let lastResponseTime = getTime();
let port = null;
let pollTimeout = null;
let backoffDelay = 1000;
const HOST_NAME = 'com.blocker.host';
export const FAIL_OPEN_MS = 10 * 60 * 1000; // 10 minutes

export const domainCache = new DomainDecisionCache(1000);

export function setTimeSource(fn) {
  getTime = fn;
}

export function resetTimeSource() {
  getTime = () => Date.now();
}

export function getState() {
  return {
    activeProfiles,
    allProfiles,
    rulesVersion,
    lastResponseTime,
    isStale: isStale(),
    extensionTheme: currentTheme
  };
}

export function setState(profiles, version, responseTime, theme, all) {
  activeProfiles = profiles;
  if (all !== undefined) allProfiles = all;
  rulesVersion = version;
  if (responseTime !== undefined) {
    lastResponseTime = responseTime;
  }
  if (theme !== undefined) {
    currentTheme = theme;
  }
  domainCache.invalidate();
}

// Ready promise: loads initial state from storage on worker start or restart
export const ready = (typeof chrome !== 'undefined' && chrome.storage?.local)
  ? new Promise((resolve) => {
      chrome.storage.local.get(['activeProfiles', 'allProfiles', 'rulesVersion', 'lastResponseTime', 'extensionTheme'], (data) => {
        if (data.activeProfiles) activeProfiles = data.activeProfiles;
        if (data.allProfiles) allProfiles = data.allProfiles;
        if (data.rulesVersion !== undefined) rulesVersion = data.rulesVersion;
        if (data.lastResponseTime) lastResponseTime = data.lastResponseTime;
        if (data.extensionTheme) currentTheme = data.extensionTheme;
        domainCache.invalidate();
        resolve();
      });
    })
  : Promise.resolve();

// Start connection once ready
ready.then(() => {
  if (typeof chrome !== 'undefined' && chrome.runtime?.connectNative) {
    connectNativeHost();
  }
});

// Setup alarms as backup keepalive (in extension environment)
if (typeof chrome !== 'undefined' && chrome.alarms?.create) {
  chrome.alarms.create('blocker-heartbeat-alarm', { periodInMinutes: 1 });
  chrome.alarms.onAlarm.addListener((alarm) => {
    if (alarm.name === 'blocker-heartbeat-alarm') {
      if (!port) {
        connectNativeHost();
      } else {
        sendPoll();
      }
    }
  });
}

export function isStale() {
  return (getTime() - lastResponseTime) > FAIL_OPEN_MS;
}

export function evaluateCurrentRules(targetUrl) {
  // Requirement 2: 10-minute fail-open (stop enforcing, do not redirect anything new)
  if (isStale()) {
    return { blocked: false, reason: 'fail_open_stale' };
  }

  const now = getTime();
  // Requirement 2: a profile whose activeUntil is in the past is ignored
  const currentActiveProfiles = activeProfiles.filter(p => {
    if (!p.activeUntil) return true;
    const untilMs = new Date(p.activeUntil).getTime();
    return isNaN(untilMs) || untilMs > now;
  });

  return domainCache.getDecision(currentActiveProfiles, targetUrl);
}

export function connectNativeHost() {
  if (port) return;
  if (typeof chrome === 'undefined' || !chrome.runtime?.connectNative) return;

  try {
    port = chrome.runtime.connectNative(HOST_NAME);
    port.onMessage.addListener(onHostMessage);
    port.onDisconnect.addListener(onHostDisconnect);
    backoffDelay = 1000;
    sendPoll();
  } catch (err) {
    console.error('Failed to connect to native host:', err);
    onHostDisconnect();
  }
}

export function onHostDisconnect() {
  port = null;
  if (pendingAddWebsiteCallback) {
    const cb = pendingAddWebsiteCallback;
    pendingAddWebsiteCallback = null;
    cb({ ok: false, error: 'Connection to native host was lost.' });
  }
  if (pendingCreateProfileCallback) {
    const cb = pendingCreateProfileCallback;
    pendingCreateProfileCallback = null;
    cb({ ok: false, error: 'Connection to native host was lost.' });
  }

  const lastErr = (typeof chrome !== 'undefined' && chrome.runtime?.lastError) ? chrome.runtime.lastError : null;
  if (lastErr) {
    console.warn('Native host disconnected:', lastErr.message);
  }

  if (pollTimeout) clearTimeout(pollTimeout);
  if (typeof chrome !== 'undefined' && chrome.runtime?.connectNative && typeof setTimeout !== 'undefined') {
    pollTimeout = setTimeout(() => {
      connectNativeHost();
    }, backoffDelay);
  }

  backoffDelay = Math.min(backoffDelay * 2, 10000);
}

let cachedIncognito = null;
let lastIncognitoCheck = 0;
let incognitoWarningTabOpened = false;

export function ensureIncognitoWarningTab() {
  if (incognitoWarningTabOpened || cachedIncognito !== false || !activeProfiles || activeProfiles.length === 0) {
    return;
  }
  if (typeof chrome === 'undefined' || !chrome.tabs?.create) return;

  const blockedPageUrl = chrome.runtime.getURL('blocked.html');
  chrome.tabs.query({}, (tabs) => {
    if (!tabs) return;
    const alreadyOpen = tabs.some(t => t.url && t.url.startsWith(blockedPageUrl) && t.url.includes('reason=incognito_required'));
    if (!alreadyOpen) {
      incognitoWarningTabOpened = true;
      chrome.tabs.create({ url: `${blockedPageUrl}?reason=incognito_required&timer=30` });
    }
  });
}

if (typeof chrome !== 'undefined' && chrome.runtime?.onMessage) {
  chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
    if (msg.type === 'check_incognito') {
      const extApi = (typeof browser !== 'undefined' && browser.extension)
        ? browser.extension
        : (typeof chrome !== 'undefined' ? chrome.extension : null);
      if (extApi?.isAllowedIncognitoAccess) {
        extApi.isAllowedIncognitoAccess((isAllowed) => {
          cachedIncognito = !!isAllowed;
          lastIncognitoCheck = getTime();
          sendResponse({ incognitoAllowed: cachedIncognito });
          if (cachedIncognito) {
            incognitoWarningTabOpened = false;
            recheckAllTabs();
          }
        });
        return true;
      } else {
        sendResponse({ incognitoAllowed: true });
      }
    }
  });
}

export function sendPoll() {
  if (!port) {
    connectNativeHost();
    return;
  }

  const manifest = (typeof chrome !== 'undefined' && chrome.runtime?.getManifest)
    ? chrome.runtime.getManifest()
    : { version: '1.0.0' };

  const postHeartbeat = (isAllowed) => {
    try {
      if (isAllowed) {
        incognitoWarningTabOpened = false;
      } else if (activeProfiles && activeProfiles.length > 0) {
        ensureIncognitoWarningTab();
      }
      if (port) {
        port.postMessage({
          type: 'heartbeat',
          incognitoAllowed: !!isAllowed,
          extensionVersion: manifest.version || '1.0.0'
        });
      }
    } catch (err) {
      console.warn('Error posting to native host:', err);
      onHostDisconnect();
    }
  };

  const now = getTime();
  if (cachedIncognito !== null && (now - lastIncognitoCheck) < 15000) {
    postHeartbeat(cachedIncognito);
    return;
  }

  try {
    const extApi = (typeof browser !== 'undefined' && browser.extension)
      ? browser.extension
      : (typeof chrome !== 'undefined' ? chrome.extension : null);

    if (extApi?.isAllowedIncognitoAccess) {
      const res = extApi.isAllowedIncognitoAccess((isAllowed) => {
        cachedIncognito = !!isAllowed;
        lastIncognitoCheck = getTime();
        postHeartbeat(cachedIncognito);
      });
      if (res && typeof res.then === 'function') {
        res.then((isAllowed) => {
          cachedIncognito = !!isAllowed;
          lastIncognitoCheck = getTime();
          postHeartbeat(cachedIncognito);
        }).catch(() => {
          postHeartbeat(cachedIncognito ?? false);
        });
      }
      return;
    }
  } catch (err) {
    console.warn('Error checking incognito access:', err);
  }

  postHeartbeat(cachedIncognito ?? false);
}

export function onHostMessage(msg) {
  backoffDelay = 1000;

  if (msg?.type === 'add_website_response' || (pendingAddWebsiteCallback && (msg?.rulesVersion === undefined))) {
    if (pendingAddWebsiteCallback) {
      const cb = pendingAddWebsiteCallback;
      pendingAddWebsiteCallback = null;
      cb(msg);
      setTimeout(sendPoll, 50);
      return;
    }
  }

  if (msg?.type === 'create_profile_response' || (pendingCreateProfileCallback && (msg?.rulesVersion === undefined))) {
    if (pendingCreateProfileCallback) {
      const cb = pendingCreateProfileCallback;
      pendingCreateProfileCallback = null;
      cb(msg);
      setTimeout(sendPoll, 50);
      return;
    }
  }

  // Requirement 2: Only an ok:true reply that carries rules may refresh lastResponseTime
  if (msg && msg.ok === true && msg.rulesVersion !== undefined) {
    lastResponseTime = getTime();

    const newVersion = msg.rulesVersion;
    const newProfiles = msg.activeProfiles || [];
    const newAllProfiles = msg.allProfiles || [];

    allProfiles = newAllProfiles;

    if (newVersion !== rulesVersion) {
      rulesVersion = newVersion;
      activeProfiles = newProfiles;
      domainCache.invalidate();

      if (typeof chrome !== 'undefined' && chrome.storage?.local) {
        chrome.storage.local.set({
          activeProfiles,
          allProfiles,
          rulesVersion,
          lastResponseTime
        });
      }

      // Re-check all open tabs in all windows
      recheckAllTabs();
    } else if (typeof chrome !== 'undefined' && chrome.storage?.local) {
      chrome.storage.local.set({ allProfiles, lastResponseTime });
    }

    if (typeof chrome !== 'undefined' && chrome.action?.setBadgeText) {
      let ruleCount = 0;
      for (const p of activeProfiles) {
        if (p.blockAllWebsites) ruleCount += 1;
        else if (p.webRules) ruleCount += p.webRules.length;
      }
      chrome.action.setBadgeText({ text: ruleCount > 0 ? String(ruleCount) : '' });
      chrome.action.setBadgeBackgroundColor({ color: '#2563EB' });
    }

    if (msg.extensionTheme) {
      const validTheme = normalizeTheme(msg.extensionTheme);
      if (validTheme !== currentTheme) {
        currentTheme = validTheme;
        if (typeof chrome !== 'undefined' && chrome.storage?.local) {
          chrome.storage.local.set({ extensionTheme: currentTheme });
        }
      }
    }
  }

  if (pollTimeout) clearTimeout(pollTimeout);
  if (typeof chrome !== 'undefined' && chrome.runtime?.connectNative && typeof setTimeout !== 'undefined') {
    pollTimeout = setTimeout(sendPoll, 5000);
  }
}

const checkedTabUrls = new Map();

export async function checkAndRedirectTab(tabId, targetUrl) {
  // Requirement 3: Every navigation handler must await ready promise
  await ready;

  if (!targetUrl || typeof targetUrl !== 'string') return;

  // Zero-overhead fast path: If no active block profiles exist, return immediately
  if (!activeProfiles || activeProfiles.length === 0) {
    return;
  }

  // Deduplicate redundant checks for same tab & URL
  if (checkedTabUrls.get(tabId) === targetUrl) {
    return;
  }
  checkedTabUrls.set(tabId, targetUrl);
  if (checkedTabUrls.size > 200) {
    const oldestKey = checkedTabUrls.keys().next().value;
    checkedTabUrls.delete(oldestKey);
  }

  // Ignore extension pages and non-http/https protocols
  const blockedPageUrl = (typeof chrome !== 'undefined' && chrome.runtime?.getURL)
    ? chrome.runtime.getURL('blocked.html')
    : 'blocked.html';

  if (targetUrl.startsWith(blockedPageUrl) ||
      targetUrl.startsWith('chrome://') ||
      targetUrl.startsWith('edge://') ||
      targetUrl.startsWith('chrome-extension://') ||
      targetUrl.startsWith('moz-extension://') ||
      targetUrl.startsWith('about:') ||
      targetUrl.startsWith('devtools://')) {
    return;
  }

  if (!targetUrl.startsWith('http://') && !targetUrl.startsWith('https://')) {
    return;
  }

  // If InPrivate is not allowed and active block profiles exist, block all web traffic until enabled
  if (cachedIncognito === false) {
    const redirectUrl = `${blockedPageUrl}?reason=incognito_required&timer=30&url=${encodeURIComponent(targetUrl)}`;
    if (typeof chrome !== 'undefined' && chrome.tabs?.update) {
      chrome.tabs.update(tabId, { url: redirectUrl });
    }
    return;
  }

  const match = evaluateCurrentRules(targetUrl);
  if (match.blocked) {
    const until = match.profile?.activeUntil || '';
    const redirectUrl = `${blockedPageUrl}?url=${encodeURIComponent(targetUrl)}&rule=${encodeURIComponent(match.matchedBlockPattern || '')}&profile=${encodeURIComponent(match.profileName || '')}&until=${encodeURIComponent(until)}`;
    if (typeof chrome !== 'undefined' && chrome.tabs?.update) {
      chrome.tabs.update(tabId, { url: redirectUrl });
    }
  }
}

export function recheckAllTabs() {
  if (typeof chrome === 'undefined' || !chrome.tabs?.query) return;

  chrome.tabs.query({}, (tabs) => {
    if (!tabs) return;
    for (const tab of tabs) {
      if (tab.id && tab.url) {
        checkedTabUrls.delete(tab.id);
        checkAndRedirectTab(tab.id, tab.url);
      }
    }
  });
}

// Synchronous top-level registration of navigation listeners (Requirement 3)
if (typeof chrome !== 'undefined' && chrome.webNavigation?.onBeforeNavigate) {
  chrome.webNavigation.onBeforeNavigate.addListener(async (details) => {
    if (details.frameId === 0) {
      checkedTabUrls.delete(details.tabId);
      await checkAndRedirectTab(details.tabId, details.url);
    }
  });

  chrome.webNavigation.onHistoryStateUpdated.addListener(async (details) => {
    if (details.frameId === 0) {
      checkedTabUrls.delete(details.tabId);
      await checkAndRedirectTab(details.tabId, details.url);
    }
  });

  chrome.tabs.onUpdated.addListener(async (tabId, changeInfo, tab) => {
    const targetUrl = changeInfo.url || (changeInfo.status === 'loading' ? tab?.url : null);
    if (targetUrl) {
      checkedTabUrls.delete(tabId);
      await checkAndRedirectTab(tabId, targetUrl);
    }
  });

  chrome.tabs.onRemoved?.addListener((tabId) => {
    checkedTabUrls.delete(tabId);
  });
}

// Message handler for extension popup
if (typeof chrome !== 'undefined' && chrome.runtime?.onMessage) {
  chrome.runtime.onMessage.addListener((request, sender, sendResponse) => {
    if (request?.type === 'get_status') {
      sendResponse({
        ok: true,
        rulesVersion,
        activeProfiles,
        allProfiles,
        lastResponseTime,
        isStale: isStale(),
        connected: !!port && !isStale(),
        extensionTheme: currentTheme
      });
      return false;
    }

    if (request?.type === 'open_app') {
      if (!port) {
        connectNativeHost();
      }
      if (port) {
        port.postMessage({ type: 'open_app' });
        sendResponse({ ok: true });
      } else {
        sendResponse({ ok: false, error: 'Not connected to service.' });
      }
      return false;
    }

    if (request?.type === 'add_website') {
      if (!port) {
        connectNativeHost();
      }
      if (!port) {
        sendResponse({ ok: false, error: 'Not connected to Sankalpa service.' });
        return false;
      }

      const addTimeout = setTimeout(() => {
        if (pendingAddWebsiteCallback) {
          const cb = pendingAddWebsiteCallback;
          pendingAddWebsiteCallback = null;
          cb({ ok: false, error: 'Operation timed out waiting for Sankalpa service.' });
        }
      }, 4000);

      pendingAddWebsiteCallback = (res) => {
        clearTimeout(addTimeout);
        sendResponse(res);
      };

      port.postMessage({
        type: 'add_website',
        domain: request.domain,
        profileId: request.profileId,
        profileName: request.profileName
      });
      return true;
    }

    if (request?.type === 'create_profile') {
      if (!port) {
        connectNativeHost();
      }
      if (!port) {
        sendResponse({ ok: false, error: 'Not connected to Sankalpa service.' });
        return false;
      }

      const createTimeout = setTimeout(() => {
        if (pendingCreateProfileCallback) {
          const cb = pendingCreateProfileCallback;
          pendingCreateProfileCallback = null;
          cb({ ok: false, error: 'Operation timed out waiting for Sankalpa service.' });
        }
      }, 4000);

      pendingCreateProfileCallback = (res) => {
        clearTimeout(createTimeout);
        sendResponse(res);
      };

      port.postMessage({
        type: 'create_profile',
        profileName: request.profileName
      });
      return true;
    }
  });
}

