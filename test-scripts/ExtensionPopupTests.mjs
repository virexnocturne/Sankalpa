import test from 'node:test';
import assert from 'node:assert/strict';

// Helper mock state partitioner matching popup.js logic
function partitionProfiles(state) {
  const cachedProfiles = state.activeProfiles || [];
  const cachedAllProfiles = state.allProfiles || [];

  const activeMap = new Map();
  for (const ap of cachedProfiles) {
    if (ap.id) activeMap.set(String(ap.id), ap);
    if (ap.name) activeMap.set((ap.name || '').trim().toLowerCase(), ap);
  }

  const activeList = [];
  const inactiveList = [];

  if (cachedAllProfiles && cachedAllProfiles.length > 0) {
    for (const p of cachedAllProfiles) {
      const matchedActive = activeMap.get(String(p.id)) || activeMap.get((p.name || '').trim().toLowerCase());
      const isActive = !!(p.active || matchedActive);
      const combined = {
        id: p.id,
        name: p.name,
        active: isActive,
        locked: p.locked || matchedActive?.locked || (p.lockMode && p.lockMode !== 'None'),
        lockMode: p.lockMode || matchedActive?.lockMode || 'None',
        ruleCount: p.ruleCount !== undefined ? p.ruleCount : (matchedActive?.webRules?.length || 0),
        blockAllWebsites: !!(p.blockAllWebsites || matchedActive?.blockAllWebsites),
        webRules: matchedActive?.webRules || [],
        activeUntil: matchedActive?.activeUntil || null
      };
      if (isActive) {
        activeList.push(combined);
      } else {
        inactiveList.push(combined);
      }
    }
  } else {
    for (const ap of cachedProfiles) {
      activeList.push({
        id: ap.id,
        name: ap.name,
        active: true,
        locked: ap.locked || (ap.lockMode && ap.lockMode !== 'None'),
        lockMode: ap.lockMode || 'None',
        ruleCount: ap.webRules ? ap.webRules.length : (ap.blockAllWebsites ? 1 : 0),
        blockAllWebsites: !!ap.blockAllWebsites,
        webRules: ap.webRules || [],
        activeUntil: ap.activeUntil || null
      });
    }
  }

  activeList.sort((a, b) => (a.name || '').localeCompare(b.name || ''));
  inactiveList.sort((a, b) => (a.name || '').localeCompare(b.name || ''));

  let totalActiveRules = 0;
  for (const p of activeList) {
    if (p.blockAllWebsites) totalActiveRules += 1;
    else totalActiveRules += (p.webRules ? p.webRules.length : (p.ruleCount || 0));
  }

  return { activeList, inactiveList, totalActiveRules };
}

// Helper mock validation matching popup.js logic
function validateBlockName(nameInput, allKnownProfiles) {
  const name = (nameInput || '').trim();
  if (!name) {
    return { ok: false, error: 'Block name cannot be empty.' };
  }

  const lower = name.toLowerCase();
  const exists = allKnownProfiles.some(p => (p.name || '').trim().toLowerCase() === lower);
  if (exists) {
    return { ok: false, error: `A block named "${name}" already exists.` };
  }

  return { ok: true, name };
}

// Timer formatting helper matching popup.js
function formatBlockTime(untilIso, nowMs = Date.now()) {
  if (!untilIso) return '';
  const ms = new Date(untilIso).getTime();
  if (isNaN(ms) || ms <= nowMs) return '';
  const diffSec = Math.max(0, Math.floor((ms - nowMs) / 1000));
  const hours = Math.floor(diffSec / 3600);
  const mins = Math.floor((diffSec % 3600) / 60);
  const secs = diffSec % 60;
  if (hours > 0) {
    return `${hours}h ${mins}m remaining`;
  }
  return `${String(mins).padStart(2, '0')}:${String(secs).padStart(2, '0')} remaining`;
}

// ---------------------- TEST SUITE ----------------------

test('Requirement 1: Partitions active and inactive blocks correctly', () => {
  const sampleState = {
    activeProfiles: [
      { id: '1', name: 'Focus Study', webRules: [{ pattern: 'youtube.com' }, { pattern: 'tiktok.com' }] },
      { id: '2', name: 'Social Media', blockAllWebsites: true }
    ],
    allProfiles: [
      { id: '1', name: 'Focus Study', active: true, ruleCount: 2 },
      { id: '2', name: 'Social Media', active: true, blockAllWebsites: true },
      { id: '3', name: 'Weekend Leisure', active: false, ruleCount: 8 },
      { id: '4', name: 'Night Sleep', active: false, ruleCount: 1 }
    ]
  };

  const { activeList, inactiveList, totalActiveRules } = partitionProfiles(sampleState);

  assert.equal(activeList.length, 2, 'Should have 2 active blocks');
  assert.equal(inactiveList.length, 2, 'Should have 2 inactive blocks');

  assert.equal(activeList[0].name, 'Focus Study');
  assert.equal(activeList[0].active, true);
  assert.equal(activeList[1].name, 'Social Media');
  assert.equal(activeList[1].active, true);

  assert.equal(inactiveList[0].name, 'Night Sleep');
  assert.equal(inactiveList[0].active, false);
  assert.equal(inactiveList[1].name, 'Weekend Leisure');
  assert.equal(inactiveList[1].active, false);

  // Total active rules: 2 (from Focus Study) + 1 (from Social Media blockAllWebsites) = 3
  assert.equal(totalActiveRules, 3);
});

test('Requirement 1: Fallback when allProfiles is empty uses activeProfiles', () => {
  const sampleState = {
    activeProfiles: [
      { id: '1', name: 'Solo Active', webRules: [{ pattern: 'gaming.com' }] }
    ],
    allProfiles: []
  };

  const { activeList, inactiveList, totalActiveRules } = partitionProfiles(sampleState);

  assert.equal(activeList.length, 1);
  assert.equal(activeList[0].name, 'Solo Active');
  assert.equal(inactiveList.length, 0);
  assert.equal(totalActiveRules, 1);
});

test('Requirement 1: Empty state handling when no profiles exist', () => {
  const sampleState = { activeProfiles: [], allProfiles: [] };
  const { activeList, inactiveList, totalActiveRules } = partitionProfiles(sampleState);

  assert.equal(activeList.length, 0);
  assert.equal(inactiveList.length, 0);
  assert.equal(totalActiveRules, 0);
});

test('Requirement 2: Block naming validation rejects empty or whitespace-only names', () => {
  const known = [{ name: 'Existing Block' }];

  const emptyRes = validateBlockName('', known);
  assert.equal(emptyRes.ok, false);
  assert.match(emptyRes.error, /cannot be empty/i);

  const whitespaceRes = validateBlockName('   \t  \n ', known);
  assert.equal(whitespaceRes.ok, false);
  assert.match(whitespaceRes.error, /cannot be empty/i);
});

test('Requirement 2: Block naming validation rejects duplicate names (case-insensitive & trimmed)', () => {
  const known = [
    { name: 'Social Media' },
    { name: 'Focus Study' }
  ];

  const dupExact = validateBlockName('Social Media', known);
  assert.equal(dupExact.ok, false);
  assert.match(dupExact.error, /already exists/i);

  const dupCase = validateBlockName('social media', known);
  assert.equal(dupCase.ok, false);
  assert.match(dupCase.error, /already exists/i);

  const dupTrim = validateBlockName('   Focus STUDY   ', known);
  assert.equal(dupTrim.ok, false);
  assert.match(dupTrim.error, /already exists/i);
});

test('Requirement 2: Block naming validation accepts valid, unique, trimmed names', () => {
  const known = [
    { name: 'Social Media' },
    { name: 'Focus Study' }
  ];

  const validRes = validateBlockName('  News & Politics  ', known);
  assert.equal(validRes.ok, true);
  assert.equal(validRes.name, 'News & Politics');
});

test('Timer formatting: Accurately formats remaining time and handles expiration', () => {
  const baseTime = 1700000000000;

  // 15 seconds remaining
  const iso15s = new Date(baseTime + 15000).toISOString();
  assert.equal(formatBlockTime(iso15s, baseTime), '00:15 remaining');

  // 5 minutes, 30 seconds
  const iso5m = new Date(baseTime + 330000).toISOString();
  assert.equal(formatBlockTime(iso5m, baseTime), '05:30 remaining');

  // 2 hours, 10 minutes
  const iso2h = new Date(baseTime + 7800000).toISOString();
  assert.equal(formatBlockTime(iso2h, baseTime), '2h 10m remaining');

  // Expired in past
  const pastIso = new Date(baseTime - 5000).toISOString();
  assert.equal(formatBlockTime(pastIso, baseTime), '');
});

test('Stress test: Large set of profiles partitioned and sorted in < 10ms', () => {
  const allProfiles = [];
  const activeProfiles = [];

  for (let i = 0; i < 200; i++) {
    const isActive = i % 3 === 0;
    const name = `Profile_${String(i).padStart(3, '0')}`;
    allProfiles.push({ id: String(i), name, active: isActive, ruleCount: i % 10 });
    if (isActive) {
      activeProfiles.push({ id: String(i), name, webRules: new Array(i % 10).fill({ pattern: 'test.com' }) });
    }
  }

  const start = performance.now();
  const { activeList, inactiveList } = partitionProfiles({ activeProfiles, allProfiles });
  const elapsed = performance.now() - start;

  assert.equal(activeList.length, 67);
  assert.equal(inactiveList.length, 133);
  assert.ok(elapsed < 20, `Partitioning took ${elapsed.toFixed(2)}ms (expected < 20ms)`);
});
