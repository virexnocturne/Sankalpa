import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import {
  setTimeSource,
  resetTimeSource,
  getState,
  setState,
  onHostMessage,
  evaluateCurrentRules,
  isStale,
  FAIL_OPEN_MS
} from './background.js';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

function createTestProfile(domain = 'example.com') {
  return {
    name: 'Focus',
    blockAllWebsites: false,
    webRules: [{ pattern: domain, allow: false }]
  };
}

test('Fresh successful heartbeat updates lastResponseTime and maintains active blocking', () => {
  let virtualTime = 1_000_000;
  setTimeSource(() => virtualTime);

  const profile = createTestProfile('example.com');
  setState([profile], 1, virtualTime);

  assert.equal(isStale(), false);
  assert.equal(evaluateCurrentRules('http://example.com').blocked, true);
  assert.equal(getState().lastResponseTime, 1_000_000);

  // Advance time by 2 minutes
  virtualTime += 2 * 60 * 1000;

  // Receive fresh heartbeat (cached: false, serviceConnected: true)
  onHostMessage({
    ok: true,
    rulesVersion: 1,
    cached: false,
    serviceConnected: true,
    activeProfiles: [profile]
  });

  // lastResponseTime is updated to current virtual time
  assert.equal(getState().lastResponseTime, virtualTime);
  assert.equal(isStale(), false);
  assert.equal(evaluateCurrentRules('http://example.com').blocked, true);

  // Backward compatibility: reply without cached field (undefined) also updates lastResponseTime
  virtualTime += 1 * 60 * 1000;
  onHostMessage({
    ok: true,
    rulesVersion: 1,
    activeProfiles: [profile]
  });
  assert.equal(getState().lastResponseTime, virtualTime);
  assert.equal(isStale(), false);
  assert.equal(evaluateCurrentRules('http://example.com').blocked, true);

  resetTimeSource();
});

test('Service unavailable causes native host response to have cached: true', () => {
  // Simulate native host response logic when service pipe fails vs succeeds
  function simulateNativeHostResponse(pipeResult, lastCachedPayload) {
    const isCached = !(pipeResult.connected && pipeResult.response?.ok && pipeResult.response?.data);
    const basePayload = isCached ? lastCachedPayload : pipeResult.response.data;
    const parsed = JSON.parse(basePayload);
    parsed.cached = isCached;
    parsed.serviceConnected = !isCached;
    return parsed;
  }

  const lastCached = JSON.stringify({
    ok: true,
    rulesVersion: 1,
    activeProfiles: [createTestProfile('example.com')]
  });

  // When service pipe succeeds
  const freshRes = simulateNativeHostResponse({
    connected: true,
    response: { ok: true, data: lastCached }
  }, lastCached);
  assert.equal(freshRes.cached, false);
  assert.equal(freshRes.serviceConnected, true);

  // When service pipe is unavailable / disconnected
  const unavailableRes = simulateNativeHostResponse({
    connected: false,
    response: null
  }, lastCached);
  assert.equal(unavailableRes.cached, true);
  assert.equal(unavailableRes.serviceConnected, false);
});

test('Cached rules received while service is unavailable do NOT reset lastResponseTime', () => {
  let virtualTime = 1_000_000;
  setTimeSource(() => virtualTime);

  const profile = createTestProfile('example.com');
  setState([profile], 1, virtualTime);

  assert.equal(getState().lastResponseTime, 1_000_000);

  // Advance time by 4 minutes
  virtualTime += 4 * 60 * 1000;

  // Service unavailable: native host delivers cached payload
  onHostMessage({
    ok: true,
    rulesVersion: 1,
    cached: true,
    serviceConnected: false,
    activeProfiles: [profile]
  });

  // lastResponseTime MUST NOT be updated
  assert.equal(getState().lastResponseTime, 1_000_000);
  assert.equal(isStale(), false);
  assert.equal(evaluateCurrentRules('http://example.com').blocked, true);

  resetTimeSource();
});

test('Repeated cached responses allow time to advance towards FAIL_OPEN_MS', () => {
  let virtualTime = 1_000_000;
  setTimeSource(() => virtualTime);

  const profile = createTestProfile('example.com');
  setState([profile], 1, virtualTime);

  // Simulate polls every 5 seconds over 8 minutes (96 polls)
  for (let i = 1; i <= 96; i++) {
    virtualTime += 5000;
    onHostMessage({
      ok: true,
      rulesVersion: 1,
      cached: true,
      serviceConnected: false,
      activeProfiles: [profile]
    });

    // Each poll with cached: true MUST NOT reset lastResponseTime
    assert.equal(getState().lastResponseTime, 1_000_000);
  }

  // 8 minutes elapsed (480,000 ms), still within 10-minute FAIL_OPEN_MS
  assert.equal(virtualTime - getState().lastResponseTime, 480_000);
  assert.equal(isStale(), false);
  assert.equal(evaluateCurrentRules('http://example.com').blocked, true);

  resetTimeSource();
});

test('Heartbeat timeout expiring (after 10 minutes) causes evaluateCurrentRules to return { blocked: false, reason: \'fail_open_stale\' }', () => {
  let virtualTime = 1_000_000;
  setTimeSource(() => virtualTime);

  const profile = createTestProfile('example.com');
  setState([profile], 1, virtualTime);

  // Advance time to 9m 59s: not yet stale
  virtualTime += 9 * 60 * 1000 + 59 * 1000;
  onHostMessage({
    ok: true,
    rulesVersion: 1,
    cached: true,
    serviceConnected: false,
    activeProfiles: [profile]
  });
  assert.equal(isStale(), false);
  assert.equal(evaluateCurrentRules('http://example.com').blocked, true);

  // Advance past 10 minutes (10 minutes + 2 seconds total)
  virtualTime += 3000;
  onHostMessage({
    ok: true,
    rulesVersion: 1,
    cached: true,
    serviceConnected: false,
    activeProfiles: [profile]
  });

  // Stale threshold exceeded!
  assert.equal(isStale(), true);
  const decision = evaluateCurrentRules('http://example.com');
  assert.equal(decision.blocked, false);
  assert.equal(decision.reason, 'fail_open_stale');

  resetTimeSource();
});

test('Service reconnection with fresh response resets lastResponseTime and restores blocking', () => {
  let virtualTime = 1_000_000;
  setTimeSource(() => virtualTime);

  const profile = createTestProfile('example.com');
  setState([profile], 1, virtualTime);

  // Time advances to 15 minutes without fresh heartbeat (stale)
  virtualTime += 15 * 60 * 1000;
  assert.equal(isStale(), true);
  assert.equal(evaluateCurrentRules('http://example.com').blocked, false);
  assert.equal(evaluateCurrentRules('http://example.com').reason, 'fail_open_stale');

  // Service reconnects! Fresh response arrives
  onHostMessage({
    ok: true,
    rulesVersion: 1,
    cached: false,
    serviceConnected: true,
    activeProfiles: [profile]
  });

  // lastResponseTime reset to current virtual time and blocking immediately resumes
  assert.equal(getState().lastResponseTime, virtualTime);
  assert.equal(isStale(), false);
  const decision = evaluateCurrentRules('http://example.com');
  assert.equal(decision.blocked, true);

  resetTimeSource();
});

test('Browser or native host restart resumes correctly with stored time', () => {
  let virtualTime = 1_000_000;
  setTimeSource(() => virtualTime);

  const profile = createTestProfile('example.com');

  // 1. Extension had last heard from service at T = 1,000,000
  const storedState = {
    activeProfiles: [profile],
    rulesVersion: 1,
    lastResponseTime: virtualTime
  };

  // 2. Browser / worker restart happens 7 minutes later (virtualTime = 1,420,000)
  virtualTime += 7 * 60 * 1000;

  // On restart, state is restored from storage
  setState(storedState.activeProfiles, storedState.rulesVersion, storedState.lastResponseTime);
  assert.equal(getState().lastResponseTime, 1_000_000);
  assert.equal(isStale(), false); // 7 min < 10 min, still within grace period

  // Native host connects, but service is still offline, so it sends cached response
  onHostMessage({
    ok: true,
    rulesVersion: 1,
    cached: true,
    serviceConnected: false,
    activeProfiles: [profile]
  });

  // lastResponseTime MUST NOT be overwritten with current time on restart!
  assert.equal(getState().lastResponseTime, 1_000_000);
  assert.equal(isStale(), false);

  // 3. Another 3 minutes and 1 second pass (total 10m 1s since last fresh response at 1,000,000)
  virtualTime += 3 * 60 * 1000 + 1000;
  onHostMessage({
    ok: true,
    rulesVersion: 1,
    cached: true,
    serviceConnected: false,
    activeProfiles: [profile]
  });

  // Expired fail-open triggers properly across the restart
  assert.equal(isStale(), true);
  assert.equal(evaluateCurrentRules('http://example.com').blocked, false);
  assert.equal(evaluateCurrentRules('http://example.com').reason, 'fail_open_stale');

  resetTimeSource();
});

test('Staleness and Fail-Open: ok:false replies do not refresh lastResponseTime', () => {
  let virtualTime = 1_000_000;
  setTimeSource(() => virtualTime);

  const profile = createTestProfile('example.com');
  setState([profile], 1, virtualTime);

  assert.equal(isStale(), false);
  assert.equal(evaluateCurrentRules('http://example.com').blocked, true);

  virtualTime += 5 * 60 * 1000;
  onHostMessage({ ok: false, error: 'Service unavailable' });

  assert.equal(getState().lastResponseTime, 1_000_000);
  resetTimeSource();
});

test('Profile activeUntil in the past is ignored during evaluation', () => {
  let virtualTime = 1_000_000;
  setTimeSource(() => virtualTime);

  const activeUntilDate = new Date(virtualTime + 60 * 1000).toISOString();
  const profileWithExpiry = {
    name: 'TimedSession',
    blockAllWebsites: false,
    webRules: [{ pattern: 'example.com', allow: false }],
    activeUntil: activeUntilDate
  };

  setState([profileWithExpiry], 1, virtualTime);

  virtualTime += 30 * 1000;
  assert.equal(evaluateCurrentRules('http://example.com').blocked, true);

  virtualTime += 40 * 1000;
  const expiredResult = evaluateCurrentRules('http://example.com');
  assert.equal(expiredResult.blocked, false);

  resetTimeSource();
});

test('Shared sample host reply format uses identical keys between C# and JS', () => {
  const possiblePaths = [
    path.resolve(__dirname, '..', 'shared', 'sample-host-reply.json'),
    path.resolve(__dirname, '..', '..', 'shared', 'sample-host-reply.json'),
    path.resolve(__dirname, 'shared', 'sample-host-reply.json')
  ];
  const samplePath = possiblePaths.find(p => fs.existsSync(p));
  assert.ok(samplePath, `Sample file missing in candidate paths: ${possiblePaths.join(', ')}`);

  const raw = fs.readFileSync(samplePath, 'utf-8');
  const msg = JSON.parse(raw);

  assert.equal(msg.ok, true);
  assert.equal(typeof msg.rulesVersion, 'number');
  assert.ok(Array.isArray(msg.activeProfiles));

  const prof = msg.activeProfiles[0];
  assert.ok(prof);
  assert.equal(typeof prof.name, 'string');
  assert.equal(typeof prof.blockAllWebsites, 'boolean');
  assert.ok(Array.isArray(prof.webRules));
  assert.equal(typeof prof.activeUntil, 'string');

  const rule = prof.webRules[0];
  assert.equal(typeof rule.pattern, 'string');
  assert.equal(typeof rule.allow, 'boolean');

  let virtualTime = 5_000_000;
  setTimeSource(() => virtualTime);
  onHostMessage(msg);

  assert.equal(getState().rulesVersion, msg.rulesVersion);
  assert.equal(getState().activeProfiles.length, 1);
  resetTimeSource();
});
