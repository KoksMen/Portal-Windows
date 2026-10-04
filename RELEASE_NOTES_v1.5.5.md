## Portal-Windows 1.5.5 Herta 🪄✨

---

### 🇷🇺 Русский язык

#### 🌟 Новые возможности и улучшения

- **⏳ Визуальный центрированный прогресс-бар на плитке экрана блокировки (Lock Screen Tile):**
  - **Центрированное время:** Время ожидания теперь выводится строго по центру графической шкалы: `[████  38s  ░░░░]`.
  - **Индикатор бесконечного ожидания:** Если в настройках выставлено ожидание без ограничений (`0`), плитка отображает циклический бегущий импульс вокруг центрированного секундомера и знака бесконечности: `[■□  0:42 (∞)  □■]`. Пользователь всегда уверен, что система активна и терпеливо ждёт разблокировки.
  - **Отдельная настройка включения/выключения:** В карточке настроек таймаута добавлен чекбокс *«Показывать прогресс-бар и таймер на экране блокировки»*. Пользователи, предпочитающие минималистичный вид плитки без графических шкал, могут отключить отображение прогресса в один клик.
  - **Двухстрочный сбалансированный лейаут:** Текстовый статус (`Ожидание подтверждения...`) и прогресс-бар аккуратно разнесены, исключая прижимание индикатора к левому краю экрана блокировки.
  - **Центрирование поля настройки:** Поле ввода *«Время ожидания ответа (мин.)»* в настройках Host сбалансировано по центру карточки.

- **📦 Экспорт отчёта для решения проблем («Собрать диагностический архив» в ZIP):**
  - В секции **Settings $\rightarrow$ Diagnostics** добавлена кнопка **«Export Diagnostic Report»** рядом с просмотром логов.
  - Формирует архив `PortalWin-Diagnostics-ГГГГММДД-ЧЧММСС.zip` в один клик.
  - Содержит:
    - `summary.txt` — краткая сводка для быстрого чтения пользователем или техподдержкой.
    - `system_environment.json` — технический отчет: сборка Windows, разрядность, процессор, аптайм, статус GUID/COM Credential Provider, активность брандмауэра для порта 29170, валидность TLS-сертификата, список сетевых интерфейсов и Bluetooth-адаптер.
    - `logs/` — полные файлы журналов `host*.log`, `provider*.log` и журнал событий `activity.journal.jsonl`. Копирование выполняется в разделяемом режиме без конфликтов с работающими службами.
  - **100% приватность:** Пароли учетных записей, приватные ключи сертификатов и токены обновлений гарантированно исключены из архива.
  - Предложение сразу открыть папку с готовым архивом в Проводнике Windows.

- **🌐 Динамическая адаптация к смене сети (NetworkChange):**
  - Автоматическое отслеживание смены IP-адресов при переподключении к Wi-Fi роутерам, кабельным сетям или включении/выключении VPN.
  - Интеллектуальный таймер подавления дребезга (debounce 750 мс), предотвращающий спам при временных переходных состояниях сетевых адаптеров.
  - Автоматический перезапуск анонсирования службы mDNS (`_portal._tcp.local.`) на актуальном IP-адресе без перезапуска приложения.

- **✍️ Доверенная цифровая подпись (Authenticode & Sectigo Timestamp):**
  - Все исполняемые файлы и библиотеки (`Portal.Host.exe`, `Portal.CredentialProvider.dll`, `Portal.Updater.exe`, `Portal.Common.dll`) подписаны сертификатами xXTeam с доверенной меткой времени Sectigo RFC 3161 SHA-256.

---

### 🇬🇧 English

#### 🌟 What's New & Improvements

- **⏳ Centered Visual Progress Bar on Lock Screen Tile:**
  - **Centered Timer:** The remaining countdown is now positioned right in the center of the dynamic progress bar: `[████  38s  ░░░░]`.
  - **Infinite Timeout Mode Support:** When infinite waiting is configured (`0`), the tile animates a continuous marquee pulse around an elapsed stopwatch with infinity badge: `[■□  0:42 (∞)  □■]`.
  - **Independent User Setting Toggle:** Added a dedicated toggle *“Show progress bar and countdown on lock screen”* under Host Settings. If you prefer a minimal lock screen tile without graphical bars or timers, you can turn it off anytime with one click.
  - **Ergonomic Multi-line Layout:** Status headline and progress indicators are formatted to avoid clipping or sticking to the left edge of LogonUI tiles.
  - **Settings UI Alignment:** Centered the *Request validity time (minutes)* input box inside the settings card.

- **📦 Diagnostic Report Archive Export (ZIP):**
  - One-click diagnostic archive generation under **Settings $\rightarrow$ Diagnostics $\rightarrow$ Export Diagnostic Report**.
  - Packages `summary.txt`, `system_environment.json`, and all Host/Provider log files into `PortalWin-Diagnostics-YYYYMMDD-HHmmss.zip`.
  - **Privacy Guard:** Passwords, certificate private keys, and update tokens are strictly sanitized and never exported.
  - Automatic prompt to open the archive location directly in Windows File Explorer.

- **🌐 Dynamic Network Topology Adaptation:**
  - Subscribed to `NetworkChange.NetworkAddressChanged` and `NetworkAvailabilityChanged`.
  - Seamlessly detects IP changes, Wi-Fi transitions, and VPN interface toggles with a 750 ms debounce.
  - Re-advertises mDNS service records automatically without requiring Host restart.

- **✍️ Digital Code Signing:**
  - All binaries and assemblies signed with xXTeam certificates and timestamped via Sectigo RFC 3161 SHA-256.

---

### 📦 Release Artifacts
- **Package Archive:** `PortalWin-1.5.5-win-x64.zip`
- **Target OS:** Windows 10 / Windows 11 (x64)
- **Target Framework:** .NET 8.0 Windows Desktop
