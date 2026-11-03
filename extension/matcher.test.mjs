import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { evaluate, evaluateMultiple, isGlobMatch, matchesPath, compilePattern } from './matcher.js';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);

test('WebRuleMatcher JS port passes all shared url-rule-vectors.json', () => {
  const vectorFilePath = path.resolve(__dirname, '..', 'shared', 'url-rule-vectors.json');
  assert.ok(fs.existsSync(vectorFilePath), `Vector file missing at ${vectorFilePath}`);

  const content = fs.readFileSync(vectorFilePath, 'utf-8');
  const data = JSON.parse(content);
  assert.ok(Array.isArray(data.vectors));
  assert.ok(data.vectors.length >= 60, `Expected >= 60 vectors, found ${data.vectors.length}`);

  let index = 0;
  for (const v of data.vectors) {
    index++;
    if (v.profiles && v.profiles.length > 0) {
      const profiles = v.profiles.map(p => ({
        name: p.name,
        blockAllWebsites: !!p.blockAll,
        webRules: [
          ...(p.patterns || []).map(pat => ({ pattern: pat, allow: false })),
          ...(p.exceptions || []).map(ex => ({ pattern: ex, allow: true }))
        ]
      }));

      const res = evaluateMultiple(profiles, v.url);
      assert.equal(
        res.blocked,
        v.expectedBlocked,
        `Vector #${index} failed: ${v.note}. URL: '${v.url}'. Expected: ${v.expectedBlocked}, Actual: ${res.blocked}`
      );
    } else {
      const profile = {
        name: 'TestProfile',
        blockAllWebsites: !!v.blockAll,
        webRules: [
          ...(v.patterns || []).map(pat => ({ pattern: pat, allow: false })),
          ...(v.exceptions || []).map(ex => ({ pattern: ex, allow: true }))
        ]
      };

      const res = evaluate(profile, v.url);
      assert.equal(
        res.blocked,
        v.expectedBlocked,
        `Vector #${index} failed: ${v.note}. URL: '${v.url}'. Expected: ${v.expectedBlocked}, Actual: ${res.blocked}`
      );
    }
  }
});

test('Linear glob stress test', () => {
  const text = 'a'.repeat(5000);
  const pattern = '*a*a*a*a*b';
  const start = Date.now();
  const matched = isGlobMatch(text, pattern);
  const elapsed = Date.now() - start;
  assert.equal(matched, false);
  assert.ok(elapsed < 100, `Glob stress test took ${elapsed}ms, expected < 100ms`);
});

test('Glob matching with literal asterisk in text', () => {
  // 1. pattern "*" vs text "*x"
  assert.equal(isGlobMatch('*x', '*'), true);

  // 2. pattern "a*" vs text "a*b"
  assert.equal(isGlobMatch('a*b', 'a*'), true);

  // 3. wildcard path pattern vs URL path containing literal "*"
  const profile = {
    name: 'TestProfile',
    blockAllWebsites: false,
    webRules: [
      { pattern: 'example.com/*', allow: false },
      { pattern: 'example.com/a*', allow: false }
    ]
  };
  assert.equal(evaluate(profile, 'http://example.com/*x').blocked, true);
  assert.equal(evaluate(profile, 'http://example.com/a*b').blocked, true);
  assert.equal(evaluate(profile, 'http://example.com/item/*wild*').blocked, true);
});
