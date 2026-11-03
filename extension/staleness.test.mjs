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

test('Staleness and Fail-Open: ok:false replies do not refresh lastResponseTime', () => {
  let virtualTime = 1000000;
  setTimeSource(() => virtualTime);

  // Setup initial active profile
  const profile = {
    name: 'Work',
    blockAllWebsites: false,
    webRules: [{ pattern: 'example.com', allow: false }]
  };
  setState([profile], 1, virtualTime);

  assert.equal(isStale(), false);
  assert.equal(evaluateCurrentRules('http://example.com').blocked, true);

  // Advance time by 5 minutes
  virtualTime += 5 * 60 * 1000;

  // Receive ok: false message (e.g. service error)
  onHostMessage({ ok: false, error: 'Service unavailable' });

  // lastResponseTime should NOT be updated, so it remains 1000000
  const state = getState();
  assert.equal(state.lastResponseTime, 1000000);

  resetTimeSource();
});

test('Staleness and Fail-Open: stale after 10 minutes stops enforcement (not blocked)', () => {
  let virtualTime = 1000000;
  setTimeSource(() => virtualTime);

  const profile = {
    name: 'Work',
    blockAllWebsites: false,
    webRules: [{ pattern: 'example.com', allow: false }]
  };
  setState([profile], 1, virtualTime);

  // Still active at 9m59s
  virtualTime += 9 * 60 * 1000 + 59 * 1000;
  assert.equal(isStale(), false);
  assert.equal(evaluateCurrentRules('http://example.com').blocked, true);

  // Stale after 10 minutes + 1s
  virtualTime += 2000;
  assert.equal(isStale(), true);
  // Evaluation returns not blocked
  const result = evaluateCurrentRules('http://example.com');
  assert.equal(result.blocked, false);
  assert.equal(result.reason, 'fail_open_stale');

  resetTimeSource();
});

test('Staleness and Fail-Open: a fresh ok reply restores enforcement', () => {
  let virtualTime = 1000000;
  setTimeSource(() => virtualTime);

  const profile = {
    name: 'Work',
    blockAllWebsites: false,
    webRules: [{ pattern: 'example.com', allow: false }]
  };
  setState([profile], 1, virtualTime);

  // Stale after 15 minutes
  virtualTime += 15 * 60 * 1000;
  assert.equal(isStale(), true);
  assert.equal(evaluateCurrentRules('http://example.com').blocked, false);

  // Fresh ok: true message arrives
  onHostMessage({
    ok: true,
    rulesVersion: 2,
    activeProfiles: [profile]
  });

  // Enforcement restored!
  assert.equal(isStale(), false);
  assert.equal(getState().lastResponseTime, virtualTime);
  assert.equal(evaluateCurrentRules('http://example.com').blocked, true);

  resetTimeSource();
});

test('Profile activeUntil in the past is ignored during evaluation', () => {
  let virtualTime = 1000000;
  setTimeSource(() => virtualTime);

  const activeUntilDate = new Date(virtualTime + 60 * 1000).toISOString(); // 1 minute in the future
  const profileWithExpiry = {
    name: 'TimedSession',
    blockAllWebsites: false,
    webRules: [{ pattern: 'example.com', allow: false }],
    activeUntil: activeUntilDate
  };

  setState([profileWithExpiry], 1, virtualTime);

  // 30 seconds later: still active
  virtualTime += 30 * 1000;
  assert.equal(evaluateCurrentRules('http://example.com').blocked, true);

  // 70 seconds later (past activeUntil): expired and ignored!
  virtualTime += 40 * 1000;
  const expiredResult = evaluateCurrentRules('http://example.com');
  assert.equal(expiredResult.blocked, false);

  resetTimeSource();
});

test('Shared sample host reply format uses identical keys between C# and JS', () => {
  const samplePath = path.resolve(__dirname, '..', 'shared', 'sample-host-reply.json');
  assert.ok(fs.existsSync(samplePath), `Sample file missing at ${samplePath}`);

  const raw = fs.readFileSync(samplePath, 'utf-8');
  const msg = JSON.parse(raw);

  // Check top-level contract keys
  assert.equal(msg.ok, true);
  assert.equal(typeof msg.rulesVersion, 'number');
  assert.ok(Array.isArray(msg.activeProfiles));

  // Check profile contract keys
  const prof = msg.activeProfiles[0];
  assert.ok(prof);
  assert.equal(typeof prof.name, 'string');
  assert.equal(typeof prof.blockAllWebsites, 'boolean');
  assert.ok(Array.isArray(prof.webRules));
  assert.equal(typeof prof.activeUntil, 'string');

  // Check rule contract keys
  const rule = prof.webRules[0];
  assert.equal(typeof rule.pattern, 'string');
  assert.equal(typeof rule.allow, 'boolean');

  // Feed into onHostMessage
  let virtualTime = 5000000;
  setTimeSource(() => virtualTime);
  onHostMessage(msg);

  assert.equal(getState().rulesVersion, msg.rulesVersion);
  assert.equal(getState().activeProfiles.length, 1);
  resetTimeSource();
});
