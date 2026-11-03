import test from 'node:test';
import assert from 'node:assert/strict';
import { LruCache, DomainDecisionCache, hasPathSpecificRules } from './matcher.js';
import { evaluateCurrentRules, setState, getState, domainCache } from './background.js';

test('LruCache basic operations and LRU eviction', () => {
  const cache = new LruCache(3);
  cache.set('a', 1);
  cache.set('b', 2);
  cache.set('c', 3);

  assert.equal(cache.get('a'), 1);
  assert.equal(cache.get('b'), 2);
  assert.equal(cache.size, 3);

  // Inserting 'd' should evict 'c' because 'a' and 'b' were accessed more recently
  cache.set('d', 4);
  assert.equal(cache.size, 3);
  assert.equal(cache.get('c'), undefined, "'c' should have been evicted as least recently used");
  assert.equal(cache.get('a'), 1);
  assert.equal(cache.get('b'), 2);
  assert.equal(cache.get('d'), 4);
});

test('DomainDecisionCache resolves subresources in O(1) time without re-evaluating rules', () => {
  const profiles = [
    {
      name: 'DistractionBlock',
      webRules: [
        { pattern: 'reddit.com', allow: false },
        { pattern: 'twitter.com', allow: false }
      ]
    }
  ];

  const cache = new DomainDecisionCache(500);

  // Request 1: Main document (Cache miss -> full rule evaluation)
  const docDecision = cache.getDecision(profiles, 'https://reddit.com/r/technology');
  assert.equal(docDecision.blocked, true);
  assert.equal(cache.misses, 1);
  assert.equal(cache.hits, 0);

  // Requests 2-200: Subresources (images, scripts, styles, APIs)
  // All should hit O(1) domain cache with 0 misses
  for (let i = 0; i < 199; i++) {
    const subresource = `https://reddit.com/static/asset_${i}.js?cachebust=${i}`;
    const subDecision = cache.getDecision(profiles, subresource);
    assert.equal(subDecision.blocked, true);
  }

  assert.equal(cache.misses, 1, 'Only the first request should have been a cache miss');
  assert.equal(cache.hits, 199, 'All 199 subresource requests must hit the O(1) LRU cache');
});

test('DomainDecisionCache accurately differentiates path-specific rules', () => {
  const profiles = [
    {
      name: 'WorkProfile',
      webRules: [
        { pattern: 'youtube.com/shorts', allow: false },
        { pattern: 'youtube.com/watch', allow: true }
      ]
    }
  ];

  assert.equal(hasPathSpecificRules(profiles), true);

  const cache = new DomainDecisionCache(500);

  // shorts is blocked
  const res1 = cache.getDecision(profiles, 'https://youtube.com/shorts/abc1234');
  assert.equal(res1.blocked, true);

  // watch is allowed
  const res2 = cache.getDecision(profiles, 'https://youtube.com/watch?v=xyz9876');
  assert.equal(res2.blocked, false);

  // Query variations on watch still hit cache without re-evaluating
  const res3 = cache.getDecision(profiles, 'https://youtube.com/watch?v=another&t=50s');
  assert.equal(res3.blocked, false);
  assert.equal(cache.hits, 1);
});

test('Cache invalidation on rule update in background.js', () => {
  const initialProfiles = [
    {
      name: 'BlockedSites',
      webRules: [{ pattern: 'news.ycombinator.com', allow: false }]
    }
  ];

  setState(initialProfiles, 1, Date.now());

  // First evaluation: blocked
  const res1 = evaluateCurrentRules('https://news.ycombinator.com');
  assert.equal(res1.blocked, true);

  // Service updates rules: unblock site
  const updatedProfiles = [
    {
      name: 'BlockedSites',
      webRules: []
    }
  ];

  setState(updatedProfiles, 2, Date.now());

  // Cache was invalidated by setState; second evaluation must reflect new rules immediately
  const res2 = evaluateCurrentRules('https://news.ycombinator.com');
  assert.equal(res2.blocked, false);
});
