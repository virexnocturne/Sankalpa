## Sankalpa Update — Audio UX Improvements, Playlist Import, Catalog Synchronization & HUD Z-Order/Watchdog Regression Fixes

This release packages major UX, architectural, and security upgrades for Sankalpa's audio management and window tracking:
1. **Redesigned Soundscape Settings Layout**: Clean Section 5 hierarchy in `MeditationEditDialog.xaml` separating built-in presets from custom tracks, dynamic track card with top toolbar, inline rename/delete, and empty states.
2. **Left-Side Soundscape Pill Audio Management Flyout**: Expanded 280px flyout in `SoundscapePillControl.xaml` with quick actions for multi-file audio import, M3U/M3U8 playlist import, track count badges, scrollable list with delete buttons, volume dock, and Escape key dismissal.
3. **Custom Audio Catalog Synchronization**: Cross-component `CatalogChanged` event bus, canonical ID persistence (`custom:<guid>`), elimination of `"Custom"` enum clobbering, and atomic `.tmp.<guid>` settings persistence.
4. **Secure Multi-File and Playlist (.M3U / .M3U8) Import**: Fast local playlist parsing with Anti-SMB relay defenses (rejecting UNC paths), remote URL blocking, nested playlist rejection, directory traversal checks, 50MB file size validation, non-blocking decodability probe with atomic rollback, and active session lockdown.
5. **HUD Z-Order & Watchdog Regression Fixes**:
   - Maintained allowed applications at `HWND_NOTOPMOST` and re-asserted Mini-HUD topmost status via `EnsureTopmost()` (`SWP_NOACTIVATE`), keeping the HUD visible over fullscreen/borderless applications without stealing focus.
   - Updated fallback window scan with `allowIconic: false` so minimizing Notepad does not cause the watchdog to continually force restore it, smoothly respecting the 750ms debounce before fail-closed Zen Shield restoration.

---

### 🧪 Verification & Automated Testing
- **Total Automated Test Suite**: **1,099 / 1,099 Tests Passed (0 failed, 0 skipped)**
  - `Sankalpa.Security.Tests.dll`: 123 / 123 Passed
  - `Sankalpa.Tests.dll`: 976 / 976 Passed
- **New Unit Tests**:
  - `CustomSongsAndSoundscapeTests.cs`: 14 new tests covering audio import, 50MB limit, 0-byte rejection, SHA-256 duplicate detection, decodability rollback, track rename/delete lifecycle, standard M3U, UTF-8 BOM M3U8, remote URL rejection, UNC share rejection, nested playlist rejection, traversal rejection, partial playlist parsing, and `CatalogChanged` event.
  - `HudLifecycleTests.cs`: 6 new tests covering fullscreen allowed apps, borderless fullscreen apps, watchdog non-restoration of minimized apps, debounce cancellation on restore within 750ms, unauthorized breach suppression during debounce, and stale HWND handling.
