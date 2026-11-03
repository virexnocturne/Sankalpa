document.addEventListener('DOMContentLoaded', async () => {
  const serviceStatusPill = document.getElementById('service-status');
  const serviceStatusText = document.getElementById('service-status-text');
  const btnOpenApp = document.getElementById('btn-open-app');
  const btnThemeCycle = document.getElementById('btn-theme-cycle');

  const currentSiteBar = document.getElementById('current-site-bar');
  const currentDomainText = document.getElementById('current-domain-text');
  const btnBlockCurrent = document.getElementById('btn-block-current');

  const quickAddInput = document.getElementById('quick-add-input');
  const btnQuickAdd = document.getElementById('btn-quick-add');
  const quickAddStatus = document.getElementById('quick-add-status');

  const blockPickerDialog = document.getElementById('block-picker-dialog');
  const pickerDomainPreview = document.getElementById('picker-domain-preview');
  const pickerBlocksList = document.getElementById('picker-blocks-list');
  const btnPickerClose = document.getElementById('btn-picker-close');

  const createBlockDialog = document.getElementById('create-block-dialog');
  const createBlockTitle = document.getElementById('create-block-title');
  const createBlockDomainHint = document.getElementById('create-block-domain-hint');
  const createBlockDomainPreview = document.getElementById('create-block-domain-preview');
  const createBlockNameInput = document.getElementById('create-block-name-input');
  const createBlockError = document.getElementById('create-block-error');
  const btnCreateModalClose = document.getElementById('btn-create-modal-close');
  const btnCreateCancel = document.getElementById('btn-create-cancel');
  const btnCreateSubmit = document.getElementById('btn-create-submit');

  const btnCreateBlock = document.getElementById('btn-create-block');
  const activeProfilesContainer = document.getElementById('active-profiles-list');
  const inactiveProfilesContainer = document.getElementById('inactive-profiles-list');
  const activeCountBadge = document.getElementById('active-count-badge');
  const inactiveCountBadge = document.getElementById('inactive-count-badge');
  const profilesContainer = document.getElementById('profiles-list');
  const ruleCountBadge = document.getElementById('rule-count-badge');
  const lastSyncLabel = document.getElementById('last-sync-label');

  let countdownTimer = null;
  let activeTabDomain = '';
  let cachedProfiles = [];
  let cachedAllProfiles = [];
  let isConnected = false;
  let pendingModalDomain = null;
  let quickStatusTimer = null;

  // 1. Theme application and cycling logic
  function normalizeTheme(theme) {
    if (!theme || typeof theme !== 'string') return 'Current';
    const t = theme.trim().toLowerCase();
    if (t === 'deepblack' || t === 'deep-black') return 'DeepBlack';
    if (t === 'minimalwhite' || t === 'minimal-white') return 'MinimalWhite';
    return 'Current';
  }

  function applyTheme(theme) {
    const norm = normalizeTheme(theme);
    let slug = 'current';
    if (norm === 'DeepBlack') slug = 'deep-black';
    else if (norm === 'MinimalWhite') slug = 'minimal-white';

    document.body.setAttribute('data-theme', slug);
    document.body.classList.remove('theme-current', 'theme-deep-black', 'theme-minimal-white');
    document.body.classList.add(`theme-${slug}`);
  }

  // Initial theme load (checks local manual override first, then app theme)
  if (typeof chrome !== 'undefined' && chrome.storage?.local) {
    chrome.storage.local.get(['extensionThemeOverride', 'extensionTheme'], (data) => {
      const selected = data.extensionThemeOverride || data.extensionTheme || 'Current';
      applyTheme(selected);
    });

    // React live to external theme storage updates
    chrome.storage.onChanged?.addListener((changes, area) => {
      if (area === 'local') {
        if (changes.extensionThemeOverride) {
          applyTheme(changes.extensionThemeOverride.newValue);
        } else if (changes.extensionTheme) {
          chrome.storage.local.get(['extensionThemeOverride'], (d) => {
            if (!d.extensionThemeOverride) {
              applyTheme(changes.extensionTheme.newValue);
            }
          });
        }
      }
    });
  }

  // Theme cycle button click (Current -> Deep Black -> Minimal White -> Current)
  btnThemeCycle?.addEventListener('click', () => {
    const currentSlug = document.body.getAttribute('data-theme') || 'current';
    let nextTheme = 'Current';
    if (currentSlug === 'current') {
      nextTheme = 'DeepBlack';
    } else if (currentSlug === 'deep-black') {
      nextTheme = 'MinimalWhite';
    } else {
      nextTheme = 'Current';
    }

    applyTheme(nextTheme);
    if (typeof chrome !== 'undefined' && chrome.storage?.local) {
      chrome.storage.local.set({ extensionThemeOverride: nextTheme });
    }
  });

  // 2. Open App button in popup header
  btnOpenApp?.addEventListener('click', () => {
    try {
      if (typeof chrome !== 'undefined' && chrome.runtime?.sendMessage) {
        chrome.runtime.sendMessage({ type: 'open_app' });
      }
    } catch { }
  });

  // 3. Detect active tab domain
  try {
    if (typeof chrome !== 'undefined' && chrome.tabs?.query) {
      chrome.tabs.query({ active: true, currentWindow: true }, (tabs) => {
        if (tabs && tabs.length > 0 && tabs[0].url) {
          try {
            const parsed = new URL(tabs[0].url);
            if (parsed.protocol === 'http:' || parsed.protocol === 'https:') {
              let domain = parsed.hostname.toLowerCase();
              if (domain.startsWith('www.')) domain = domain.substring(4);
              if (domain && domain !== 'localhost' && !domain.includes('127.0.0.1')) {
                activeTabDomain = domain;
                currentDomainText.textContent = domain;
                currentSiteBar.style.display = 'flex';
              }
            }
          } catch { }
        }
      });
    }
  } catch { }

  // 4. Block Picker popover handling
  function openBlockPicker(domain) {
    if (!domain) return;
    pickerDomainPreview.textContent = domain;
    pickerBlocksList.innerHTML = '';

    // Choose full profiles list if available, otherwise active profiles
    const sourceProfiles = (cachedAllProfiles && cachedAllProfiles.length > 0)
      ? cachedAllProfiles
      : (cachedProfiles || []);

    // List existing blocks (active blocks first, then inactive)
    if (sourceProfiles && sourceProfiles.length > 0) {
      const sorted = [...sourceProfiles].sort((a, b) => {
        if (!!a.active !== !!b.active) return a.active ? -1 : 1;
        return (a.name || '').localeCompare(b.name || '');
      });

      for (const p of sorted) {
        const item = document.createElement('div');
        item.className = 'picker-block-item';
        const isLocked = p.locked || (p.lockMode && p.lockMode !== 'None');
        const isActive = !!p.active;
        item.innerHTML = `
          <span>${escapeHtml(p.name || 'Block')}</span>
          <div style="display: flex; gap: 4px; align-items: center;">
            <span class="picker-block-tag ${isActive ? 'is-active' : 'is-inactive'}">${isActive ? 'Active' : 'Inactive'}</span>
            ${isLocked ? '<span class="picker-block-tag is-locked">🔒 Locked</span>' : ''}
          </div>
        `;
        item.addEventListener('click', () => submitAdd(domain, p.id, p.name));
        pickerBlocksList.appendChild(item);
      }
    }

    // Always offer "+ New block" option - opens name input modal with domain context
    const newItem = document.createElement('div');
    newItem.className = 'picker-block-item is-new';
    newItem.innerHTML = `<span>+ New block</span>`;
    newItem.addEventListener('click', () => {
      closeBlockPicker();
      openCreateBlockModal(domain);
    });
    pickerBlocksList.appendChild(newItem);

    blockPickerDialog.style.display = 'flex';
  }

  function closeBlockPicker() {
    blockPickerDialog.style.display = 'none';
  }

  btnPickerClose?.addEventListener('click', closeBlockPicker);

  // 5. Create Named Block Modal Handling
  function openCreateBlockModal(domain = null) {
    pendingModalDomain = domain;
    hideCreateBlockError();
    createBlockNameInput.value = '';
    btnCreateSubmit.disabled = false;
    btnCreateSubmit.textContent = 'Create';

    if (domain) {
      createBlockTitle.textContent = 'Create Block & Add Site';
      createBlockDomainPreview.textContent = domain;
      createBlockDomainHint.style.display = 'flex';
    } else {
      createBlockTitle.textContent = 'Create New Block';
      createBlockDomainHint.style.display = 'none';
    }

    createBlockDialog.style.display = 'flex';
    setTimeout(() => createBlockNameInput.focus(), 50);
  }

  function closeCreateBlockModal() {
    createBlockDialog.style.display = 'none';
    hideCreateBlockError();
    pendingModalDomain = null;
  }

  function showCreateBlockError(msg) {
    if (!createBlockError) return;
    createBlockError.textContent = msg;
    createBlockError.style.display = 'block';
  }

  function hideCreateBlockError() {
    if (!createBlockError) return;
    createBlockError.textContent = '';
    createBlockError.style.display = 'none';
  }

  function handleCreateBlockSubmit() {
    const rawVal = createBlockNameInput.value;
    const name = (rawVal || '').trim();

    // Validation: non-empty
    if (!name) {
      showCreateBlockError('Block name cannot be empty.');
      createBlockNameInput.focus();
      return;
    }

    // Validation: not duplicate (case-insensitive)
    const lower = name.toLowerCase();
    const allKnown = [...(cachedAllProfiles || []), ...(cachedProfiles || [])];
    const isDup = allKnown.some(p => (p.name || '').trim().toLowerCase() === lower);
    if (isDup) {
      showCreateBlockError(`A block named "${name}" already exists.`);
      createBlockNameInput.focus();
      return;
    }

    btnCreateSubmit.disabled = true;
    btnCreateSubmit.textContent = 'Creating...';

    if (pendingModalDomain) {
      const targetDomain = pendingModalDomain;
      closeCreateBlockModal();
      submitAdd(targetDomain, null, name);
    } else {
      showQuickStatus('Creating block...', false);
      if (typeof chrome !== 'undefined' && chrome.runtime?.sendMessage) {
        chrome.runtime.sendMessage({ type: 'create_profile', profileName: name }, (res) => {
          closeCreateBlockModal();
          if (chrome.runtime?.lastError) {
            showQuickStatus(chrome.runtime.lastError.message || 'Communication error.', true);
            return;
          }
          if (res && res.ok) {
            showQuickStatus(`Created block '${name}'.`, false);
            setTimeout(queryStatus, 300);
          } else {
            const err = res?.error || 'Failed to create block.';
            showQuickStatus(err, true);
          }
        });
      } else {
        closeCreateBlockModal();
        showQuickStatus('Service not available.', true);
      }
    }
  }

  btnCreateBlock?.addEventListener('click', () => {
    if (isConnected) {
      openCreateBlockModal(null);
    }
  });

  btnCreateSubmit?.addEventListener('click', handleCreateBlockSubmit);
  btnCreateCancel?.addEventListener('click', closeCreateBlockModal);
  btnCreateModalClose?.addEventListener('click', closeCreateBlockModal);

  createBlockNameInput?.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
      handleCreateBlockSubmit();
    } else if (e.key === 'Escape') {
      closeCreateBlockModal();
    }
  });

  // Global Escape key listener to close open modals
  document.addEventListener('keydown', (e) => {
    if (e.key === 'Escape') {
      if (createBlockDialog && createBlockDialog.style.display !== 'none') {
        closeCreateBlockModal();
      } else if (blockPickerDialog && blockPickerDialog.style.display !== 'none') {
        closeBlockPicker();
      }
    }
  });

  // 6. Site / Quick Add actions
  btnBlockCurrent?.addEventListener('click', () => {
    if (activeTabDomain && isConnected) {
      openBlockPicker(activeTabDomain);
    }
  });

  btnQuickAdd?.addEventListener('click', () => {
    const val = quickAddInput.value.trim();
    if (!val) return;
    openBlockPicker(val);
  });

  quickAddInput?.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
      const val = quickAddInput.value.trim();
      if (val) openBlockPicker(val);
    }
  });

  function showQuickStatus(text, isError) {
    if (!quickAddStatus) return;
    if (quickStatusTimer) clearTimeout(quickStatusTimer);
    quickAddStatus.textContent = text;
    quickAddStatus.className = isError ? 'status-inline-msg error' : 'status-inline-msg success';
    quickAddStatus.style.display = 'block';
    quickStatusTimer = setTimeout(() => {
      quickAddStatus.style.display = 'none';
      quickStatusTimer = null;
    }, 3500);
  }

  function submitAdd(domain, profileId, profileName) {
    if (typeof chrome !== 'undefined' && chrome.runtime?.sendMessage) {
      showQuickStatus('Adding...', false);
      chrome.runtime.sendMessage(
        { type: 'add_website', domain, profileId, profileName },
        (res) => {
          closeBlockPicker();
          if (chrome.runtime?.lastError) {
            showQuickStatus(chrome.runtime.lastError.message || 'Communication error.', true);
            return;
          }
          if (res && res.ok) {
            quickAddInput.value = '';
            let targetLabel = profileName || 'New block';
            try {
              if (res.data) {
                const parsed = typeof res.data === 'string' ? JSON.parse(res.data) : res.data;
                if (parsed.name) targetLabel = parsed.name;
              }
            } catch { }
            showQuickStatus(`Blocked '${domain}' in '${targetLabel}'.`, false);
            // Refresh popup state
            setTimeout(queryStatus, 300);
          } else {
            const err = res?.error || 'Failed to add website.';
            showQuickStatus(err, true);
          }
        }
      );
    }
  }

  // 7. Format remaining time for block chips
  function formatBlockTime(untilIso) {
    if (!untilIso) return '';
    const ms = new Date(untilIso).getTime();
    if (isNaN(ms) || ms <= Date.now()) return '';
    const diffSec = Math.max(0, Math.floor((ms - Date.now()) / 1000));
    const hours = Math.floor(diffSec / 3600);
    const mins = Math.floor((diffSec % 3600) / 60);
    const secs = diffSec % 60;
    if (hours > 0) {
      return `${hours}h ${mins}m remaining`;
    }
    return `${String(mins).padStart(2, '0')}:${String(secs).padStart(2, '0')} remaining`;
  }

  function updateBlockTimers() {
    const timeEls = document.querySelectorAll('.profile-chip-time');
    timeEls.forEach((el) => {
      const until = el.getAttribute('data-until');
      if (until) {
        const text = formatBlockTime(until);
        if (text) {
          el.textContent = text;
          el.style.display = 'inline-block';
        } else {
          el.textContent = 'Expiring...';
        }
      }
    });
  }

  // Helper to create a structured profile chip
  function createProfileChipElement(p, isActive) {
    const div = document.createElement('div');
    div.className = `profile-chip ${isActive ? 'is-active-chip' : 'is-inactive-chip'}`;

    const rulesCount = (p.webRules && p.webRules.length > 0) ? p.webRules.length : (p.ruleCount || 0);
    const rulesText = p.blockAllWebsites ? 'All Sites' : (rulesCount === 1 ? '1 rule' : `${rulesCount} rules`);
    const isLocked = p.locked || (p.lockMode && p.lockMode !== 'None');
    const timeStr = isActive ? formatBlockTime(p.activeUntil) : '';

    div.innerHTML = `
      <div class="profile-chip-header">
        <div class="profile-chip-title-row">
          <span class="profile-chip-name" title="${escapeHtml(p.name || 'Block')}">${escapeHtml(p.name || 'Block')}</span>
          <div class="profile-chip-badges">
            <span class="badge-status ${isActive ? 'is-active' : 'is-inactive'}">${isActive ? 'Active' : 'Inactive'}</span>
            ${isLocked ? '<span class="badge-locked" title="Locked block">🔒 Locked</span>' : ''}
          </div>
        </div>
        <span class="profile-chip-rules">${escapeHtml(rulesText)}</span>
      </div>
      ${isActive && p.activeUntil && timeStr ? `<div class="profile-chip-time" data-until="${escapeHtml(p.activeUntil)}">${escapeHtml(timeStr)}</div>` : ''}
    `;
    return div;
  }

  // 8. Render full popup state (Active and Inactive blocks)
  function renderState(state) {
    if (!state) return;

    isConnected = !!state.connected;

    // Apply theme from state if no local manual override exists
    if (typeof chrome !== 'undefined' && chrome.storage?.local) {
      chrome.storage.local.get(['extensionThemeOverride'], (d) => {
        if (!d.extensionThemeOverride && state.extensionTheme) {
          applyTheme(state.extensionTheme);
        }
      });
    }

    if (isConnected) {
      serviceStatusPill.className = 'status-pill status-connected';
      serviceStatusText.textContent = 'Guarding';
      btnBlockCurrent.disabled = false;
      quickAddInput.disabled = false;
      btnQuickAdd.disabled = false;
      if (btnCreateBlock) btnCreateBlock.disabled = false;
    } else {
      serviceStatusPill.className = 'status-pill status-disconnected';
      serviceStatusText.textContent = 'Not connected';
      btnBlockCurrent.disabled = true;
      quickAddInput.disabled = true;
      btnQuickAdd.disabled = true;
      if (btnCreateBlock) btnCreateBlock.disabled = true;
    }

    cachedProfiles = state.activeProfiles || [];
    cachedAllProfiles = state.allProfiles || [];

    // Map active profile details (rules, timers) by id and lowercase name
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
      // Fallback: only activeProfiles are known
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

    // Sort alphabetically
    activeList.sort((a, b) => (a.name || '').localeCompare(b.name || ''));
    inactiveList.sort((a, b) => (a.name || '').localeCompare(b.name || ''));

    // Calculate total active rules
    let totalActiveRules = 0;
    for (const p of activeList) {
      if (p.blockAllWebsites) totalActiveRules += 1;
      else totalActiveRules += (p.webRules ? p.webRules.length : (p.ruleCount || 0));
    }

    if (ruleCountBadge) ruleCountBadge.textContent = `${totalActiveRules} rules`;
    if (activeCountBadge) activeCountBadge.textContent = `${activeList.length} active`;
    if (inactiveCountBadge) inactiveCountBadge.textContent = `${inactiveList.length} inactive`;

    // Render Active Blocks
    if (activeProfilesContainer) {
      if (activeList.length === 0) {
        activeProfilesContainer.innerHTML = '<div class="empty-state">No website blocks currently active.</div>';
      } else {
        activeProfilesContainer.innerHTML = '';
        for (const p of activeList) {
          activeProfilesContainer.appendChild(createProfileChipElement(p, true));
        }
      }
    }

    // Render Inactive Blocks
    if (inactiveProfilesContainer) {
      if (inactiveList.length === 0) {
        inactiveProfilesContainer.innerHTML = '<div class="empty-state">No inactive website blocks.</div>';
      } else {
        inactiveProfilesContainer.innerHTML = '';
        for (const p of inactiveList) {
          inactiveProfilesContainer.appendChild(createProfileChipElement(p, false));
        }
      }
    }

    // Retain legacy profilesContainer populated for backward compatibility
    if (profilesContainer && activeProfilesContainer) {
      profilesContainer.innerHTML = activeProfilesContainer.innerHTML;
    }

    if (countdownTimer) clearInterval(countdownTimer);
    countdownTimer = setInterval(updateBlockTimers, 1000);

    if (state.lastResponseTime) {
      const agoSec = Math.round((Date.now() - state.lastResponseTime) / 1000);
      lastSyncLabel.textContent = agoSec < 5 ? 'Synced: Just now' : `Synced: ${agoSec}s ago`;
    }
  }

  function escapeHtml(str) {
    const p = document.createElement('p');
    p.textContent = str || '';
    return p.innerHTML;
  }

  function queryStatus() {
    try {
      if (typeof chrome !== 'undefined' && chrome.runtime?.sendMessage) {
        chrome.runtime.sendMessage({ type: 'get_status' }, (response) => {
          if (response && response.ok) {
            renderState(response);
          } else {
            fallbackFromStorage();
          }
        });
      } else {
        fallbackFromStorage();
      }
    } catch {
      fallbackFromStorage();
    }
  }

  function fallbackFromStorage() {
    if (typeof chrome !== 'undefined' && chrome.storage?.local) {
      chrome.storage.local.get(['activeProfiles', 'allProfiles', 'rulesVersion', 'lastResponseTime', 'extensionTheme'], (data) => {
        const isStale = (Date.now() - (data.lastResponseTime || 0)) > 600000;
        renderState({
          connected: !isStale && !!data.lastResponseTime,
          isStale,
          activeProfiles: data.activeProfiles || [],
          allProfiles: data.allProfiles || [],
          lastResponseTime: data.lastResponseTime,
          extensionTheme: data.extensionTheme
        });
      });
    }
  }

  queryStatus();
});
