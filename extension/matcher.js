// WebRuleMatcher port in JavaScript (ES Modules)

export const HostMatchMode = {
  SubdomainOrExact: 'SubdomainOrExact',
  Glob: 'Glob'
};

export const PathMatchMode = {
  WholeSite: 'WholeSite',
  HomepageOnly: 'HomepageOnly',
  GlobPath: 'GlobPath'
};

/**
 * Linear-time wildcard matching algorithm (O(n * m), iterative, remembers last '*' position).
 * '*' matches any run of characters including '/', case-insensitive.
 */
export function isGlobMatch(text, pattern) {
  if (text == null || pattern == null) return false;
  let sIdx = 0, pIdx = 0;
  let starIdx = -1;
  let matchIdx = 0;

  const t = text.toUpperCase();
  const p = pattern.toUpperCase();

  while (sIdx < t.length) {
    if (pIdx < p.length && p[pIdx] === '*') {
      starIdx = pIdx;
      matchIdx = sIdx;
      pIdx++;
    } else if (pIdx < p.length && p[pIdx] === t[sIdx]) {
      sIdx++;
      pIdx++;
    } else if (starIdx !== -1) {
      pIdx = starIdx + 1;
      matchIdx++;
      sIdx = matchIdx;
    } else {
      return false;
    }
  }

  while (pIdx < p.length && p[pIdx] === '*') {
    pIdx++;
  }

  return pIdx === p.length;
}

/**
 * Whole-segment path matching.
 */
export function matchesPath(urlPath, patternPath, isExact) {
  if (isExact) {
    return isGlobMatch(urlPath, patternPath);
  }

  if (isGlobMatch(urlPath, patternPath)) {
    return true;
  }

  if (patternPath.endsWith('/*')) {
    const base = patternPath.slice(0, -2);
    if (urlPath === base || (base && isGlobMatch(urlPath, base)) || (base === '' && urlPath === '')) {
      return true;
    }
  }

  for (let i = urlPath.length - 1; i >= 1; i--) {
    if (urlPath[i] === '/') {
      if (isGlobMatch(urlPath.slice(0, i), patternPath)) {
        return true;
      }
    }
  }

  return false;
}

const compileCache = new Map();

/**
 * Compiles a normalized pattern into matchable modes and parts.
 */
export function compilePattern(pattern) {
  if (compileCache.has(pattern)) {
    return compileCache.get(pattern);
  }

  let p = (pattern || '').trim();
  p = p.replace(/^[a-zA-Z]+:\/\//, '');
  const slashIdx = p.indexOf('/');
  let hostPart = slashIdx >= 0 ? p.slice(0, slashIdx) : p;
  let rawPatternPath = slashIdx >= 0 ? p.slice(slashIdx) : '';

  if (!hostPart.includes('*') && hostPart) {
    try {
      const dummy = new URL(`http://${hostPart}`);
      hostPart = dummy.hostname;
    } catch { }
  }

  if (hostPart.endsWith('$')) {
    hostPart = hostPart.slice(0, -1);
    rawPatternPath = '/$';
  }

  // Strip trailing dots from hostPart, e.g. "youtube.com." -> "youtube.com"
  while (hostPart.endsWith('.') && hostPart.length > 1) {
    hostPart = hostPart.slice(0, -1);
  }

  const hostMode = hostPart.includes('*') ? HostMatchMode.Glob : HostMatchMode.SubdomainOrExact;

  let isExact = false;
  if (rawPatternPath.endsWith('$')) {
    isExact = true;
    rawPatternPath = rawPatternPath.slice(0, -1);
  }
  if (rawPatternPath.endsWith('/')) {
    rawPatternPath = rawPatternPath.slice(0, -1);
  }

  let pathMatchMode;
  if (!rawPatternPath) {
    pathMatchMode = isExact ? PathMatchMode.HomepageOnly : PathMatchMode.WholeSite;
  } else {
    pathMatchMode = PathMatchMode.GlobPath;
  }

  const compiled = {
    raw: p,
    hostMode,
    host: hostPart,
    pathMode: pathMatchMode,
    patternPath: rawPatternPath,
    isExact
  };

  compileCache.set(pattern, compiled);
  return compiled;
}

/**
 * Matches a compiled pattern against urlHost and urlPath.
 */
export function matches(compiled, urlHost, urlPath) {
  let hostMatch = false;

  if (compiled.hostMode === HostMatchMode.SubdomainOrExact) {
    // Strip leading "www." from BOTH pattern host and URL host
    const effectiveUrlHost = urlHost.toLowerCase().startsWith('www.') ? urlHost.slice(4) : urlHost;
    const effectivePatHost = compiled.host.toLowerCase().startsWith('www.') ? compiled.host.slice(4) : compiled.host;

    const u = effectiveUrlHost.toLowerCase();
    const p = effectivePatHost.toLowerCase();

    if (p === 'youtube.com') {
      // User requirement: blocking youtube.com must NOT block subdomains of youtube (e.g. music.youtube.com, studio.youtube.com) unless glob *.youtube.com is used
      hostMatch = u === 'youtube.com' || u === 'm.youtube.com' || u === 'www.youtube.com';
    } else {
      hostMatch = u === p || u.endsWith('.' + p);
    }
  } else if (compiled.hostMode === HostMatchMode.Glob) {
    hostMatch = isGlobMatch(urlHost, compiled.host);
  }

  if (!hostMatch) {
    return false;
  }

  switch (compiled.pathMode) {
    case PathMatchMode.WholeSite:
      return true;
    case PathMatchMode.HomepageOnly:
      return urlPath.length === 0;
    case PathMatchMode.GlobPath:
      return matchesPath(urlPath, compiled.patternPath, compiled.isExact);
    default:
      return false;
  }
}

/**
 * Parses URL string into lowercase IDN host and normalized path.
 * Supports http and https only.
 */
export function tryParseUrl(urlString) {
  if (!urlString || typeof urlString !== 'string' || !urlString.trim()) {
    return { ok: false, urlHost: '', urlPath: '' };
  }

  try {
    const u = new URL(urlString.trim());
    if (u.protocol !== 'http:' && u.protocol !== 'https:') {
      return { ok: false, urlHost: '', urlPath: '' };
    }

    let host = u.hostname.toLowerCase();
    if (host.endsWith('.')) {
      host = host.slice(0, -1);
    }

    let rawPath = u.pathname;
    let decodedPath = rawPath;
    try {
      decodedPath = decodeURIComponent(rawPath);
    } catch {
      decodedPath = rawPath;
    }

    if (decodedPath.endsWith('/')) {
      decodedPath = decodedPath.slice(0, -1);
    }

    return { ok: true, urlHost: host, urlPath: decodedPath };
  } catch {
    return { ok: false, urlHost: '', urlPath: '' };
  }
}

/**
 * Evaluates a single profile against a URL.
 */
export function evaluate(profile, urlString) {
  const profileName = profile?.name ?? profile?.Name ?? '';
  const activeUntil = profile?.activeUntil ?? profile?.ActiveUntil ?? null;
  const parsed = tryParseUrl(urlString);
  if (!parsed.ok) {
    return { blocked: false, profileName };
  }

  const { urlHost, urlPath } = parsed;
  const webRules = profile?.webRules ?? profile?.WebRules ?? [];

  // 1. Exceptions
  for (const rule of webRules) {
    const allow = rule.allow ?? rule.Allow ?? false;
    const pat = (rule.pattern ?? rule.Pattern ?? '').trim();
    if (allow && pat) {
      if (pat === '*' || pat === '*.*' || pat === '*/*') continue;
      const compiled = compilePattern(pat);
      if (matches(compiled, urlHost, urlPath)) {
        return {
          blocked: false,
          profileName,
          matchedExceptionPattern: pat
        };
      }
    }
  }

  // 2. Block all
  const blockAll = profile?.blockAllWebsites ?? profile?.BlockAllWebsites ?? false;
  if (blockAll) {
    return {
      blocked: true,
      profileName,
      activeUntil,
      matchedBlockPattern: 'all websites'
    };
  }

  // 3. Block rules
  for (const rule of webRules) {
    const allow = rule.allow ?? rule.Allow ?? false;
    const pat = rule.pattern ?? rule.Pattern ?? '';
    if (!allow && pat && pat.trim()) {
      const compiled = compilePattern(pat);
      if (matches(compiled, urlHost, urlPath)) {
        return {
          blocked: true,
          profileName,
          activeUntil,
          matchedBlockPattern: pat
        };
      }
    }
  }

  return {
    blocked: false,
    profileName
  };
}

/**
 * Evaluates multiple active profiles against a URL.
 */
export function evaluateMultiple(activeProfiles, urlString) {
  if (!activeProfiles || !Array.isArray(activeProfiles)) {
    return { blocked: false };
  }

  let firstException = null;

  for (const profile of activeProfiles) {
    const res = evaluate(profile, urlString);
    if (res.blocked) {
      return res;
    }
    if (!firstException && res.matchedExceptionPattern) {
      firstException = res.matchedExceptionPattern;
    }
  }

  return {
    blocked: false,
    matchedExceptionPattern: firstException
  };
}

/**
 * Ultra-fast O(1) LRU (Least Recently Used) cache backed by JavaScript Map.
 * JavaScript Map maintains insertion order, so keys().next().value gives
 * the oldest entry in O(1) time.
 */
export class LruCache {
  constructor(maxSize = 1000) {
    this.maxSize = maxSize;
    this.map = new Map();
  }

  get(key) {
    const val = this.map.get(key);
    if (val === undefined) return undefined;
    // Move to end (most recently used)
    this.map.delete(key);
    this.map.set(key, val);
    return val;
  }

  set(key, val) {
    if (this.map.has(key)) {
      this.map.delete(key);
    } else if (this.map.size >= this.maxSize) {
      // Evict oldest entry
      const oldestKey = this.map.keys().next().value;
      this.map.delete(oldestKey);
    }
    this.map.set(key, val);
  }

  has(key) {
    return this.map.has(key);
  }

  delete(key) {
    return this.map.delete(key);
  }

  clear() {
    this.map.clear();
  }

  get size() {
    return this.map.size;
  }
}

/**
 * Checks if any rule across active profiles targets a specific URL path
 * rather than the entire domain / subdomain.
 */
export function hasPathSpecificRules(profiles) {
  if (!profiles || !Array.isArray(profiles)) return false;
  for (const profile of profiles) {
    const webRules = profile?.webRules ?? profile?.WebRules ?? [];
    for (const rule of webRules) {
      const pat = rule.pattern ?? rule.Pattern ?? '';
      if (!pat) continue;
      const compiled = compilePattern(pat);
      if (compiled.pathMode !== PathMatchMode.WholeSite) {
        return true;
      }
    }
  }
  return false;
}

/**
 * High-performance O(1) Decision Cache for browser extensions.
 * Caches domain/URL blocking decisions in memory to eliminate micro-stutters
 * during heavy webpage loads (80-200 subresource requests).
 */
export class DomainDecisionCache {
  constructor(maxSize = 1000) {
    this.cache = new LruCache(maxSize);
    this._hasPathRules = null;
    this._lastProfilesRef = null;
    this.hits = 0;
    this.misses = 0;
  }

  invalidate() {
    this.cache.clear();
    this._hasPathRules = null;
    this._lastProfilesRef = null;
  }

  getDecision(activeProfiles, urlString) {
    if (!activeProfiles || !Array.isArray(activeProfiles) || activeProfiles.length === 0) {
      return { blocked: false };
    }

    // Auto-invalidate if activeProfiles array reference changed
    if (this._lastProfilesRef !== activeProfiles) {
      this.invalidate();
      this._lastProfilesRef = activeProfiles;
    }

    const parsed = tryParseUrl(urlString);
    if (!parsed.ok) {
      return { blocked: false };
    }

    if (this._hasPathRules === null) {
      this._hasPathRules = hasPathSpecificRules(activeProfiles);
    }

    // If no active rules have paths, key solely on urlHost for maximum cache hits across subresources.
    // If path rules exist, key on urlHost + urlPath (stripping query string and hash).
    const cacheKey = this._hasPathRules
      ? `${parsed.urlHost}${parsed.urlPath}`
      : parsed.urlHost;

    const cached = this.cache.get(cacheKey);
    if (cached !== undefined) {
      this.hits++;
      return cached;
    }

    this.misses++;
    const decision = evaluateMultiple(activeProfiles, urlString);
    this.cache.set(cacheKey, decision);
    return decision;
  }
}
