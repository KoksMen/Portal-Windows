## Portal-Windows 1.5.5 Herta 🪄✨

---

### 🇷🇺 Русский язык

#### 🌟 Новые возможности
- **⏳ Живой динамический прогресс-бар на экране блокировки (LogonUI Tile Progress Bar):**
  - Прямо в плитке пользователя на экране блокировки Windows теперь отображается стилизованный ASCII-прогресс-бар и таймер отсчёта.
  - **Идеальное нативное центрирование:** Статусный заголовок («Ожидание подтверждения», «Поиск устройства») и строка прогресса разнесены по независимым элементам управления LogonUI — текст всегда строго выровнен по центру контейнера плитки без смещений влево.
  - **Адаптивная калибровка шрифта Segoe UI:** Длина шкалы прогресса точно откалибрована под ширину текстового заголовка и жесткий лимит контейнера DirectUI (~305 px) — 20 блоков для «Ожидание подтверждения» (~289 px) и 14 блоков для «Поиск устройства» (~204 px). Полоса никогда не обрезается и гармонично дополняет текст.
  - **Защита от переноса строк (No-Wrap):** Вокруг бейджей времени и индикаторов используются неразрывные пробелы (`\u00A0`), что полностью предотвращает разрыв строки системой Windows DirectUI.
  - **Симметричная индикация бесконечного тайм-аута:** При отключенном тайм-ауте отображается плавная бегущая волна (marquee) и симметричные бейджи бесконечности с двух сторон от прошедшего времени: `[██░░░░  (∞) 0:15 (∞)  ░░░░██]`.
  - **Точный таймер обратного отсчёта:** В режиме с лимитом времени по центру отображается оставшееся время `[█████████  1:44  ██████▒░░░]` с динамическим заполнением и пульсирующим маркером активности.
  - **Настройка в приложении:** В параметрах приложения добавлен переключатель отображения прогресс-бара и таймера на плитке входа.
- **📦 Экспорт диагностического отчёта (Diagnostic Report ZIP):**
  - В приложении добавлена функция мгновенного формирования полного архива диагностики (`portal-diagnostics-*.zip`).
  - Включает журналы хоста и Credential Provider, конфигурацию (без паролей), сведения об ОС, сетевых интерфейсах и статусе служб для быстрого поиска и решения неполадок.
- **🌐 Динамическая адаптация к смене сети (Network Adaptation):**
  - Мониторинг системных событий сетевых интерфейсов (`NetworkChange`).
  - При переключении между Wi-Fi сетями, Ethernet или VPN хост автоматически с дебаунсингом обновляет сетевые привязки и переанонсирует mDNS-сервис (`_portal._tcp.local.`), не требуя перезапуска службы.

#### ⚡ Улучшения и безопасность
- **🔐 Строгая валидация идентификаторов запросов (Strict RequestId Matching):**
  - Все входящие сигналы разблокировки (REST, WebSocket, BLE) строго сверяются с актуальным `requestId`. Устранены любые ложные срабатывания от устаревших пакетов или сетевых повторов.
  - Добавлен надежный механизм восстановления сессии при повторном подключении устройств во время ожидания.
- **📦 Чистая компоновка релизного пакета без дублей:**
  - Оптимизированы сценарии сборки в `Portal.Host.csproj`: ликвидировано дублирование вложенных папок `win-x64\publish` внутри `CredentialProvider` и `Updater`.
  - Все файлы разложены по целевым системным каталогам в соответствии с архитектурой проекта.
- **🗜 Максимальное сжатие дистрибутива:**
  - Обновлён скрипт упаковки `Package-Release.ps1` с режимом `-CompressionLevel Optimal`, уменьшившим размер архива `PortalWin-1.5.5-win-x64.zip` с ~31 МБ до ~21 МБ при сохранении полной функциональности.
- **✍️ Доверенная цифровая подпись:**
  - Все бинарные модули подписаны сертификатом Authenticode с доверенными метками времени Sectigo RFC 3161.

#### 🔄 Совместимость
- **📱 Мобильное приложение:** 100% совместимо с текущими версиями Portal для Android и Wear OS.
- **🔐 Сетевые протоколы:** Без критических изменений.

---

### 🇬🇧 English

#### 🌟 What's New
- **⏳ Dynamic Lock Screen Progress Bar & Centered Timer (LogonUI Tile):**
  - Real-time ASCII progress bar and countdown timer embedded directly into the Windows logon user tile.
  - **Dead-Center Native Alignment:** Decoupled status headline and progress bar into dedicated LogonUI controls, guaranteeing pixel-perfect native DirectUI centering with zero left-edge bias.
  - **Calibrated Segoe UI Font Metrics:** Widths precisely adapted to fit the DirectUI container limit (~305 px) — 20 blocks for "Waiting for confirmation" (~289 px) and 14 blocks for "Searching for device" (~204 px).
  - **DirectUI Line-Wrap Protection:** Formatted using non-breaking spaces (`\u00A0`), eliminating Windows word-wrap splits at space boundaries.
  - **Symmetric Infinity Indicators:** For infinite timeouts, renders an ambient marquee wave with symmetric infinity badges on both sides of elapsed time: `[██░░░░  (∞) 0:15 (∞)  ░░░░██]`.
  - **Precision Countdown Mode:** Clearly displays remaining time centered within the bar `[█████████  1:44  ██████▒░░░]` with dynamic filling and active pulse indicator.
  - **Host Configuration Option:** Toggle the lock screen progress bar and timer on/off directly from Settings.
- **📦 One-Click Diagnostic Report Export (ZIP Archive):**
  - Generates a consolidated `portal-diagnostics-*.zip` bundle containing sanitized settings, host logs, Credential Provider logs, network interfaces, and environment status for troubleshooting.
- **🌐 Dynamic Network Change Adaptation:**
  - Listens to `NetworkChange.NetworkAddressChanged` and `NetworkAvailabilityChanged` events.
  - Automatically refreshes network listeners and debounces mDNS re-advertisement (`_portal._tcp.local.`) across Wi-Fi/Ethernet/VPN switches without restarting the host.

#### ⚡ Improvements & Hardening
- **🔐 Strict RequestId Validation:**
  - Enforced strict `requestId` matching on all incoming unlock responses (REST, WebSocket, BLE), preventing stale or duplicate network packet triggers.
  - Added session recovery fallback for reconnected devices.
- **📦 Clean Package Distribution:**
  - Corrected MSBuild targets in `Portal.Host.csproj` to eliminate duplicate nested `win-x64\publish` trees inside `CredentialProvider` and `Updater`.
- **🗜 Maximum Release Compression:**
  - Switched `Package-Release.ps1` to `-CompressionLevel Optimal`, reducing archive size to ~21 MB (down from ~31 MB).
- **✍️ Digital Code Signing:**
  - All binaries and COM components are Authenticode-signed with trusted Sectigo RFC 3161 timestamps.

#### 🔄 Compatibility
- **📱 Mobile App:** Fully compatible with Portal for Android and Wear OS.
- **🔐 Protocols:** Backward-compatible mTLS, WebSocket, and REST endpoints.

---

## 📝 What's Changed
* feat(credential-provider): visual progress bar and infinite-timeout marquee on lock screen tile by @KoksMen
* feat(network): dynamic network change adaptation via NetworkChange and debounced mDNS re-advertisement by @KoksMen
* feat(diagnostics): add diagnostic report archive export (ZIP) with system info and logs by @KoksMen
* feat(config): add setting to toggle lock screen progress bar and timer by @KoksMen
* fix(tile): decouple headline and progress bar for native Windows DirectUI centering by @KoksMen
* fix(tile): calibrate Segoe UI font metrics and container limits to prevent truncation by @KoksMen
* fix(tile): prevent line wrap with non-breaking spaces and width fitting by @KoksMen
* feat(tile): display infinity symbol symmetrically on both sides of elapsed time by @KoksMen
* fix(security): enforce strict requestId matching on all unlock responses by @KoksMen
* build(packaging): clean layout without duplicate nested publish folders and optimal ZIP compression by @KoksMen

**Full Changelog**: https://github.com/KoksMen/Portal-Windows/compare/v1.5.4...v1.5.5
