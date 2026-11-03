document.addEventListener('DOMContentLoaded', () => {
  const params = new URLSearchParams(window.location.search);
  const reason = params.get('reason') || '';
  const targetUrl = params.get('url') || '';
  const matchedRule = params.get('rule') || '';
  const profileName = params.get('profile') || '';
  const until = params.get('until') || '';

  const shieldEl = document.getElementById('shield-icon');
  const titleEl = document.getElementById('page-title');
  const standardDetails = document.getElementById('standard-details');
  const incognitoDetails = document.getElementById('incognito-details');
  const countdownEl = document.getElementById('countdown-val');
  const openExtBtn = document.getElementById('open-extensions-btn');
  const tryAgainBtn = document.getElementById('try-again-btn');

  const urlEl = document.getElementById('blocked-url');
  const ruleEl = document.getElementById('matched-rule');
  const profileEl = document.getElementById('profile-name');
  const untilRow = document.getElementById('until-row');
  const untilEl = document.getElementById('active-until');

  if (reason === 'incognito_required') {
    if (shieldEl) shieldEl.textContent = '⏳';
    if (titleEl) titleEl.textContent = 'Extension Setup Required';
    if (standardDetails) standardDetails.style.display = 'none';
    if (incognitoDetails) incognitoDetails.style.display = 'flex';
    if (openExtBtn) openExtBtn.style.display = 'inline-block';
    if (tryAgainBtn) tryAgainBtn.textContent = 'I enabled it';

    let remainingSeconds = parseInt(params.get('timer') || '30', 10);
    if (isNaN(remainingSeconds) || remainingSeconds <= 0) remainingSeconds = 30;

    const updateTimer = () => {
      if (countdownEl) {
        countdownEl.textContent = `${remainingSeconds} second${remainingSeconds === 1 ? '' : 's'} remaining`;
      }
    };
    updateTimer();

    const checkIncognito = () => {
      if (typeof chrome !== 'undefined' && chrome.runtime?.sendMessage) {
        chrome.runtime.sendMessage({ type: 'check_incognito' }, (resp) => {
          if (resp && resp.incognitoAllowed) {
            if (countdownEl) {
              countdownEl.textContent = '✅ InPrivate Enabled! Redirecting...';
              countdownEl.style.color = '#10B981';
            }
            clearInterval(timerInterval);
            setTimeout(() => {
              if (targetUrl) {
                window.location.href = targetUrl;
              } else {
                window.close();
              }
            }, 1000);
          }
        });
      }
    };

    const timerInterval = setInterval(() => {
      remainingSeconds--;
      if (remainingSeconds <= 0) {
        clearInterval(timerInterval);
        if (countdownEl) {
          countdownEl.textContent = 'Grace period expired.';
        }
      } else {
        updateTimer();
        checkIncognito();
      }
    }, 1000);

    if (openExtBtn) {
      openExtBtn.addEventListener('click', () => {
        const ua = navigator.userAgent;
        let extUrl = 'chrome://extensions';
        if (ua.includes('Edg')) {
          extUrl = 'edge://extensions';
        } else if (ua.includes('Firefox')) {
          extUrl = 'about:addons';
        }
        if (typeof chrome !== 'undefined' && chrome.tabs?.create) {
          chrome.tabs.create({ url: extUrl });
        } else {
          window.open(extUrl, '_blank');
        }
      });
    }

    if (tryAgainBtn) {
      tryAgainBtn.addEventListener('click', () => {
        checkIncognito();
      });
    }
  } else {
    // Normal site block
    if (urlEl) urlEl.textContent = targetUrl;
    if (ruleEl) ruleEl.textContent = matchedRule;
    if (profileEl) profileEl.textContent = profileName || 'Active Policy';
    if (until && untilRow && untilEl) {
      untilEl.textContent = until;
      untilRow.style.display = 'flex';
    }

    if (tryAgainBtn) {
      tryAgainBtn.addEventListener('click', () => {
        if (!targetUrl) return;
        try {
          const parsed = new URL(targetUrl);
          if (parsed.protocol === 'http:' || parsed.protocol === 'https:') {
            window.location.href = targetUrl;
          }
        } catch { }
      });
    }
  }
});
