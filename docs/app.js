(() => {
  "use strict";

  const THEME_KEY = "color-scheme";
  const ACCENT_KEY = "sankalpa-accent";
  const VALID_ACCENTS = new Set(["emerald", "indigo", "violet", "amber", "rose", "slate"]);

  const metaScheme = document.querySelector('meta[name="color-scheme"]');
  const root = document.documentElement;
  const darkQuery = window.matchMedia("(prefers-color-scheme: dark)");

  function safeGet(key) {
    try {
      return localStorage.getItem(key);
    } catch {
      return null;
    }
  }

  function safeSet(key, value) {
    try {
      if (value === null) {
        localStorage.removeItem(key);
      } else {
        localStorage.setItem(key, value);
      }
    } catch {
      // Storage may be blocked in strict privacy contexts; non-fatal.
    }
  }

  function getStoredTheme() {
    const val = safeGet(THEME_KEY);
    return val === "light" || val === "dark" ? val : "system";
  }

  function getStoredAccent() {
    const val = safeGet(ACCENT_KEY);
    return val && VALID_ACCENTS.has(val) ? val : "emerald";
  }

  function syncThemeUI() {
    const theme = getStoredTheme();
    const accent = getStoredAccent();

    if (metaScheme) {
      metaScheme.content = theme === "system" ? "light dark" : theme;
    }
    root.dataset.accent = accent;

    const systemStateText = darkQuery.matches ? "Currently dark" : "Currently light";
    const systemMetaEl = document.getElementById("system-scheme-meta");
    if (systemMetaEl) {
      systemMetaEl.textContent = systemStateText;
    }

    document.querySelectorAll('input[name="site-theme"]').forEach((input) => {
      input.checked = input.value === theme;
    });

    document.querySelectorAll('input[name="site-accent"]').forEach((input) => {
      input.checked = input.value === accent;
    });
  }

  // Sync system label on OS color-scheme changes without erasing explicit pins.
  darkQuery.addEventListener("change", syncThemeUI);

  window.addEventListener("storage", (event) => {
    if (event.key === THEME_KEY || event.key === ACCENT_KEY || event.key === null) {
      syncThemeUI();
    }
  });

  document.querySelectorAll('input[name="site-theme"]').forEach((input) => {
    input.addEventListener("change", () => {
      if (!input.checked) return;
      safeSet(THEME_KEY, input.value === "system" ? null : input.value);
      syncThemeUI();
    });
  });

  document.querySelectorAll('input[name="site-accent"]').forEach((input) => {
    input.addEventListener("change", () => {
      if (!input.checked) return;
      if (VALID_ACCENTS.has(input.value)) {
        safeSet(ACCENT_KEY, input.value);
        syncThemeUI();
      }
    });
  });

  syncThemeUI();

  // Appearance Popover: return focus on Done & fallback if native Popover API is unavailable
  const appearanceTrigger = document.getElementById("appearance-trigger");
  const appearancePopover = document.getElementById("appearance-popover");
  const appearanceDone = document.getElementById("appearance-done");

  if (appearancePopover && appearanceTrigger) {
    const supportsPopover = Object.hasOwn(HTMLElement.prototype, "popover");

    if (!supportsPopover) {
      appearancePopover.hidden = true;
      appearanceTrigger.addEventListener("click", () => {
        const willOpen = appearancePopover.hidden;
        appearancePopover.hidden = !willOpen;
        appearanceTrigger.setAttribute("aria-expanded", String(willOpen));
        if (willOpen) {
          appearancePopover.querySelector("input, button")?.focus();
        }
      });

      document.addEventListener("keydown", (event) => {
        if (event.key === "Escape" && !appearancePopover.hidden) {
          appearancePopover.hidden = true;
          appearanceTrigger.setAttribute("aria-expanded", "false");
          appearanceTrigger.focus();
        }
      });

      document.addEventListener("click", (event) => {
        if (
          !appearancePopover.hidden &&
          !appearancePopover.contains(event.target) &&
          !appearanceTrigger.contains(event.target)
        ) {
          appearancePopover.hidden = true;
          appearanceTrigger.setAttribute("aria-expanded", "false");
        }
      });
    }

    if (appearanceDone) {
      appearanceDone.addEventListener("click", () => {
        if (supportsPopover && typeof appearancePopover.hidePopover === "function") {
          appearancePopover.hidePopover();
        } else {
          appearancePopover.hidden = true;
          appearanceTrigger.setAttribute("aria-expanded", "false");
        }
        appearanceTrigger.focus();
      });
    }
  }

  // Accessible Feature Tabs (selection follows focus, hidden="until-found", beforematch)
  const tablist = document.querySelector(".tablist");
  if (tablist) {
    const tabs = Array.from(tablist.querySelectorAll('[role="tab"]'));
    const panels = Array.from(document.querySelectorAll('.tab-panels [role="tabpanel"]'));
    const supportsFocusgroup = Object.hasOwn(HTMLElement.prototype, "focusGroup");

    function selectTab(targetTab, { moveFocus = false } = {}) {
      const targetId = targetTab.id;

      tabs.forEach((tab) => {
        const isSelected = tab === targetTab;
        tab.setAttribute("aria-selected", String(isSelected));
        tab.toggleAttribute("focusgroupstart", isSelected);
        if (!supportsFocusgroup) {
          tab.tabIndex = isSelected ? 0 : -1;
        }
      });

      panels.forEach((panel) => {
        const matches = panel.getAttribute("aria-labelledby") === targetId;
        if (matches) {
          panel.removeAttribute("hidden");
        } else {
          panel.setAttribute("hidden", "until-found");
        }
      });

      if (moveFocus) {
        targetTab.focus();
      }

      try {
        targetTab.scrollIntoView({ block: "nearest", inline: "nearest", container: "nearest" });
      } catch {
        targetTab.scrollIntoView({ block: "nearest", inline: "nearest" });
      }
    }

    if (!supportsFocusgroup) {
      tabs.forEach((tab) => {
        tab.tabIndex = tab.getAttribute("aria-selected") === "true" ? 0 : -1;
      });
    }

    tabs.forEach((tab, index) => {
      tab.addEventListener("click", () => selectTab(tab));

      // Selection follows focus (matches native focusgroup arrow navigation)
      tab.addEventListener("focus", () => {
        if (tab.getAttribute("aria-selected") !== "true") {
          selectTab(tab);
        }
      });

      if (!supportsFocusgroup) {
        tab.addEventListener("keydown", (event) => {
          let nextIndex = null;
          if (event.key === "ArrowRight") {
            nextIndex = (index + 1) % tabs.length;
          } else if (event.key === "ArrowLeft") {
            nextIndex = (index - 1 + tabs.length) % tabs.length;
          } else if (event.key === "Home") {
            nextIndex = 0;
          } else if (event.key === "End") {
            nextIndex = tabs.length - 1;
          }

          if (nextIndex !== null) {
            event.preventDefault();
            selectTab(tabs[nextIndex], { moveFocus: true });
          }
        });
      }
    });

    // Reveal matching tab when browser Find-in-Page matches hidden="until-found" content
    panels.forEach((panel) => {
      panel.addEventListener("beforematch", () => {
        const tabId = panel.getAttribute("aria-labelledby");
        const tab = tabs.find((item) => item.id === tabId);
        if (tab) {
          selectTab(tab);
        }
      });
    });
  }

  // Zen Breathing Ring Pause/Resume (WCAG 2.2.2)
  const zenWidget = document.querySelector(".zen-widget");
  const zenMotionBtn = document.getElementById("zen-motion-toggle");
  if (zenWidget && zenMotionBtn) {
    zenMotionBtn.addEventListener("click", () => {
      const currentlyPaused = zenWidget.dataset.paused === "true";
      const nextPaused = !currentlyPaused;
      zenWidget.dataset.paused = String(nextPaused);
      zenMotionBtn.textContent = nextPaused ? "Resume breathing animation" : "Pause breathing animation";
    });
  }

  // Native Lightbox Modal (<dialog closedby="any"> + Safari fallback)
  const lightboxDialog = document.getElementById("screenshot-dialog");
  const lightboxTitle = document.getElementById("lightbox-title");
  const lightboxImg = document.getElementById("lightbox-img");
  const lightboxCaption = document.getElementById("lightbox-caption");

  if (lightboxDialog && lightboxImg && lightboxTitle && lightboxCaption) {
    if (!("closedBy" in HTMLDialogElement.prototype)) {
      lightboxDialog.addEventListener("click", (event) => {
        if (event.target !== lightboxDialog) return;
        const rect = lightboxDialog.getBoundingClientRect();
        const isInside =
          rect.top <= event.clientY &&
          event.clientY <= rect.top + rect.height &&
          rect.left <= event.clientX &&
          event.clientX <= rect.left + rect.width;
        if (!isInside) {
          lightboxDialog.close();
        }
      });
    }

    document.querySelectorAll("[data-lightbox-src]").forEach((trigger) => {
      trigger.addEventListener("click", () => {
        const src = trigger.getAttribute("data-lightbox-src");
        const title = trigger.getAttribute("data-lightbox-title") || "Sankalpa Screenshot";
        const caption = trigger.getAttribute("data-lightbox-caption") || "";
        const w = trigger.getAttribute("data-lightbox-width") || "";
        const h = trigger.getAttribute("data-lightbox-height") || "";
        const innerImg = trigger.querySelector("img");

        lightboxTitle.textContent = title;
        lightboxCaption.textContent = caption;
        lightboxImg.src = src;
        lightboxImg.alt = innerImg ? innerImg.alt : title;
        if (w) lightboxImg.width = Number(w);
        if (h) lightboxImg.height = Number(h);

        lightboxDialog.showModal();
      });
    });

    lightboxDialog.addEventListener("close", () => {
      lightboxImg.removeAttribute("src");
    });
  }

  // CLI Filter Pills
  const cliFilterBtns = Array.from(document.querySelectorAll("[data-cli-filter]"));
  const cliRows = Array.from(document.querySelectorAll(".cli-row[data-cli-group]"));

  if (cliFilterBtns.length && cliRows.length) {
    cliFilterBtns.forEach((btn) => {
      btn.addEventListener("click", () => {
        const selected = btn.getAttribute("data-cli-filter");
        cliFilterBtns.forEach((item) => {
          item.setAttribute("aria-pressed", String(item === btn));
        });
        cliRows.forEach((row) => {
          const group = row.getAttribute("data-cli-group");
          row.hidden = selected !== "all" && group !== selected;
        });
      });
    });
  }

  // Copy-to-Clipboard with Centralized Live Region
  const liveAnnouncer = document.getElementById("copy-live-region");
  const toastEl = document.getElementById("copy-toast");
  let toastTimer = null;

  function announceCopy(message) {
    if (liveAnnouncer) {
      liveAnnouncer.textContent = message;
    }
    if (toastEl) {
      toastEl.textContent = message;
      toastEl.hidden = false;
      window.clearTimeout(toastTimer);
      toastTimer = window.setTimeout(() => {
        toastEl.hidden = true;
      }, 2400);
    }
  }

  document.querySelectorAll("[data-copy-text]").forEach((btn) => {
    btn.addEventListener("click", async () => {
      const value = btn.getAttribute("data-copy-text");
      const label = btn.getAttribute("data-copy-label") || "Text";
      if (!value) return;

      const originalText = btn.textContent;
      try {
        await navigator.clipboard.writeText(value);
        btn.textContent = "Copied";
        announceCopy(`${label} copied to clipboard.`);
      } catch {
        // Fallback when clipboard permission is unavailable (e.g., file:// context)
        const temp = document.createElement("textarea");
        temp.value = value;
        temp.setAttribute("readonly", "");
        temp.style.position = "fixed";
        temp.style.opacity = "0";
        document.body.appendChild(temp);
        temp.select();
        try {
          document.execCommand("copy");
          btn.textContent = "Copied";
          announceCopy(`${label} copied to clipboard.`);
        } catch {
          announceCopy(`Could not copy automatically. Value: ${value}`);
        }
        document.body.removeChild(temp);
      }

      window.setTimeout(() => {
        btn.textContent = originalText;
      }, 1800);
    });
  });
})();
