## 🚀 Summary of Changes / Описание изменений

This Pull Request brings a major set of performance optimizations, interactive device diagnostics, modern Windows 11 Fluent UI backdrops, and an extensive UI/UX polish across the entire application:

---

### 1. ⚡ Interactive Device Unlock Test (UnlockTestService)
- **mTLS Host Listener:** Temporary lightweight Kestrel listener on a dynamic loopback/LAN port with mDNS advertising (`_portal-test._tcp.local.`, `test=true`).
- **Bidirectional Test:** Supports both WebSocket (/ws) and REST (/api/unlock) endpoints with client certificate thumbprint/hash verification.
- **Accurate RTT Latency:** Measures Round-Trip Time latency in milliseconds and reports it in the completion dialog and activity journal.
- **Zero-Latency Shutdown:** Resolved Kestrel socket-draining freeze by immediately aborting active sockets on first approval and setting a 500 ms shutdown timeout.
- **Robust Message Parsing:** Case-insensitive JSON deserialization supporting both PascalCase and camelCase payloads from mobile devices.

### 2. 🚀 LogonUI Performance Optimization (ReadyToRun & Tiered Compilation)
- **ReadyToRun (AOT):** Added <PublishReadyToRun>true</PublishReadyToRun> in Portal.CredentialProvider.csproj to eliminate JIT compilation pauses when Windows loads logonui.exe.
- **Tiered Compilation:** Enabled CLR <TieredCompilation> and <TieredCompilationQuickJit> for near-instant first-frame rendering on the lock screen.
- **COM Compatibility:** Maintained non-composite DLL output without trimming, ensuring 100% reliable COM registration of Portal.CredentialProvider.comhost.dll.

### 3. 🪟 Windows 11 Fluent UI (Mica & Acrylic Backdrops)
- Added native Windows 11 DWM backdrop effects (Mica Alt, Acrylic) with an automatic dark-mode fallback on Windows 10.
- Modern visual depth and polished window chrome.

### 4. 📱 Full Device Card Redesign & SVG Vector Polish
- **SVG Vector Graphics:** Replaced Segoe UI emoji glyphs (which rendered as a retro calculator/pager 🖩 and monochrome stairs 📶) with crisp, resolution-independent vector paths (smartphone outline, Wi-Fi waves, Bluetooth geometry, user, calendar, key).
- **Full-Width Metadata Container:** Placed Client ID GUID in a dedicated full-width container so it never gets clipped or truncated.
- **Proper Label Gutters:** Added clean margins (`Margin="0,0,6,0"`) between labels and values to prevent text concatenation (`Аккаунт: rinshima`).
- **Integrated Power Switch:** Added a dedicated [ ⏻ Включено ] / [ ⏻ Отключено ] toggle switch with emerald glow and responsive tooltip.
- **Prominent Test Button:** Styled golden [ ⚡ Проверить связь ] button for immediate access to connection diagnostics.

### 5. 📐 Adaptive Root Dashboard (ViewDashboard)
- **ScrollViewer:** Wrapped the entire dashboard in an adaptive ScrollViewer to eliminate vertical overflow on low-resolution or high-DPI scaled displays.
- **Balanced Proportions:** Rescaled the main logo from 176×176 to 100×100 px and tightened subtitle margins.
- **De-cluttered Navigation:** Removed redundant Download Mobile Client button from the root screen (already accessible in About and Settings).
- **Clean Localized Status:** Replaced raw internal COM registration paths under the START button with clean localized status labels (⚠ Требуется настройка службы) and hid duplicate error strings when the error card is displayed.

### 6. 📊 Responsive System Health Layout
- Rebuilt component list using independent micro-cards, preventing text collisions between component titles, status indicators, and action buttons.

### 7. 🌐 Full Russian & English Localization
- Added comprehensive dictionary translations for all new buttons, diagnostics, setup issues, hints, and tooltips.

---

## 🔍 Verification & Testing
- [x] **Debug Build:** dotnet build src/Portal.Host/Portal.Host.csproj -c Debug -> 0 errors.
- [x] **Release Build & Publish:** dotnet publish src/Portal.Host/Portal.Host.csproj -c Release -o publish/ -> 0 errors.
- [x] **Digital Signing:** All binaries in publish/, publish/CredentialProvider/, and publish/Updater/ signed with authentic certificates and Sectigo RFC 3161 timestamps.
- [x] **Release Packaging:** Created PortalWin-1.5.4-win-x64.zip (31 MB) containing all signed components and runtimes.
- [x] **Device Connection Test:** Tested against real mobile client over Wi-Fi, RTT verified, instant completion without UI hangs.

---

## 🔄 Compatibility
- **Backward Compatible:** 100% compatible with existing Portal Android and Wear OS apps.
- **Breaking Changes:** None.
