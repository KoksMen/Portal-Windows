## Portal-Windows 1.5.5 Herta 🪄✨

---

### 🇷🇺 Русский язык

#### 🌟 Новые возможности
- **⏳ Живой динамический прогресс-бар и выбор стиля (LogonUI Tile Progress Bar):**
  - Прямо в плитке пользователя на экране блокировки Windows отображается стилизованный прогресс-бар и таймер отсчёта.
  - **3 визуальных стиля шкалы:** блочный `[████]`, тонкий `[━━━━]` и точечный `[●●●●]`, доступные для выбора в разделе «Оформление и стиль» приложения Host.
  - **Идеальное нативное центрирование:** Статусный заголовок и строка прогресса разнесены по независимым элементам управления LogonUI — текст всегда строго выровнен по центру контейнера плитки без смещений влево.
  - **Адаптивная геометрия Segoe UI:** Длина шкалы и промежутки автоматически рассчитываются под точную геометрическую ширину символов (`█`, `━`, `●` и др.) и лимит контейнера DirectUI (~305 px) — полоса гармонично дополняет текст и никогда не обрезается.
  - **Симметричный режим бесконечного ожидания:** При отключенном тайм-ауте отображается плавная бегущая волна (marquee) и симметричные бейджи бесконечности: `[██░░░░  (∞) 0:15 (∞)  ░░░░██]`.
  - **Защита от переноса строк (No-Wrap):** Неразрывные пробелы (`\u00A0`) исключают перенос строк компонентами Windows.
- **🔍 Поиск и быстрая фильтрация в окне логов (LogsWindow):**
  - Поисковая строка со стилизацией Windows Fluent, векторной иконкой лупы Segoe MDL2 (`\uE721`), плейсхолдером и кнопкой быстрой очистки (`\uE711`).
  - Быстрые чип-фильтры с цветной подсветкой: **«Все»**, **«Ошибки»** (перехват `[ERR]`, `[WRN]`, `Exception`, `Failed`), **«Сеть (WS)»** (события WebSocket, TLS, mDNS), **«Bluetooth (BLE)»**.
  - Мгновенная фильтрация в памяти без повторного считывания файлов с диска и зависаний.
  - Живой счётчик совпадений вида *«Найдено: X / Y записей»*.
- **🔤 Кастомный текст на плитке экрана блокировки:**
  - В настройках оформления добавлено поле ввода произвольной фразы вместо стандартной «Ожидание подтверждения...».
  - Предусмотрена кнопка быстрого сброса на дефолт и защита от переполнения (санитизация до 40 символов).
  - Прогресс-бар на плитке автоматически пересчитывает ширину сегментов под длину вашей фразы.
- **⌨️ Быстрый перезапуск по клавише Enter:**
  - Нажатие клавиши `Enter` на плитке экрана блокировки при пустом поле пароля моментально отправляет удалённый запрос разблокировки на смартфон без необходимости тянуться к мыши.
  - Если в поле пароля набраны символы — `Enter` выполняет стандартный локальный вход по введённому паролю.
- **📊 Индикатор активного транспорта в подробностях плитки:**
  - По кнопке «Показать подробности» на экране блокировки выводится живой статус канала связи: `Wi-Fi: Активен (192.168.1.5)` / `Bluetooth: Подключён (1)`.
- **🚨 Точная расшифровка ошибок входа Windows (NTSTATUS OnLogonStatusReported):**
  - Перехват кодов Winlogon с выводом понятной причины на плитке и записью в журнал событий с иконками:
    * `0xC000006A` / `0xC000006D` -> «Неверный пароль Windows» `[🔑]`
    * `0xC0000234` -> «Учётная запись заблокирована» `[🔒]`
    * `0xC0000071` / `0xC0000224` -> «Срок действия пароля истёк» `[⌛]`
    * `0xC0000072` -> «Учётная запись отключена» `[🚫]`
- **🎯 Детализированные причины отмены запроса:**
  - Разделение точных причин отмены: «Отменено пользователем», «Ввод пароля вручную», «Смена пользователя», «Время запроса истекло», «Отклонено на устройстве».
- **⌨️ Умная авто-отмена при ручном вводе пароля:**
  - При начале набора первого символа пароля в поле ввода активный сетевой запрос немедленно отменяется, а сокеты закрываются, предотвращая конфликты входа.
- **🛡️ Валидация пароля Windows в реальном времени при сохранении:**
  - Проверка пароля через `LogonUserW` перед записью в LSA Secret Store с расшифровкой кодов ошибок Windows и гарантированным занулением открытого пароля в оперативной памяти (`ZeroFreeGlobalAlloc`).
- **🛡️ Фоновый аудит целостности LSA-секретов:**
  - При запуске Host проверяет читаемость и валидность DPAPI/LSA секрета каждого сопряженного устройства с немедленным вызовом `Dispose` для `SecureString` и отображением жёлтого бейджа `[⚠️ Ошибка секрета]` при неполадках.
- **🆔 Поддержка учетных записей Microsoft (MSA) и доменных UPN:**
  - Корректное сопоставление логинов вида `user@outlook.com` и доменных имен через SID (`LookupAccountName`/`Win32_UserProfile`).
- **⏱️ Кнопки быстрых пресетов таймаута:**
  - Интерактивные чипы `[15с]`, `[30с]`, `[1м]`, `[2м]`, `[♾️ Без лимита]` для мгновенной настройки в один клик.
- **🔄 Обновление здоровья компонентов с плавной анимацией:**
  - Кнопка «Проверить заново» с векторным глифом Segoe MDL2 Refresh (`\uE72C`), непрерывной аппаратной анимацией вращения и пошаговой анимацией загрузки (`[🔄 Проверка...]`) у самих диагностических пунктов.
- **👁️ Кнопка «Показать/Скрыть пароль» (глазок):**
  - Fluent-кнопка со скруглением и векторными глифами Segoe MDL2 (`\uE7B3`/`\uED1A`), идеальным выравниванием с PasswordBox без съезжания текста и защитой от утечек открытого пароля.
- **⚠️ Защита от случайного удаления устройства:**
  - Диалог подтверждения с подстановкой реального имени устройства («Вы действительно хотите удалить устройство «{0}»?»).
- **📦 Экспорт диагностического отчёта в один клик:**
  - Формирование единого архива `portal-diagnostics-*.zip` со всеми логами, сетевыми параметрами и конфигурацией без паролей.
- **🌐 Динамическая адаптация к смене сети:**
  - Интеграция с системным API `NetworkChange` для бесшовного обновления слушателей и debounced mDNS-анонсов при переключении сетей Wi-Fi/Ethernet/VPN.

#### ⚡ Улучшения и архитектура
- **🏷️ Централизация версионирования решения:**
  - Создан единый файл `Directory.Build.props` в корне репозитория; версия задаётся в одном месте и автоматически транслируется во все сборки, манифесты и пакеты.
- **🛡️ Глобальный перехват и логирование неперехваченных исключений:**
  - Добавлены обработчики `AppDomain.CurrentDomain.UnhandledException` и `DispatcherUnhandledException` с прямой записью полного стека ошибок в `host.log`.
- **🐛 Исправление прав доступа (ACL) на директорию Logs:**
  - Назначены наследуемые списки управления доступом (`Modify` для `Authenticated Users`) на `ProgramData\Portal-Windows\Logs`, а опасный вызов `File.Move` заменен на атомарную запись потока.
- **🔐 Строгая валидация идентификаторов запросов (Strict RequestId Matching):**
  - Защита от повторного воспроизведения пакетов (replay-атак) и надежное восстановление сессии при повторном подключении.
- **🚀 Максимальная скорость отрисовки LogonUI (ReadyToRun AOT):**
  - Провайдер предварительно скомпилирован в нативный машинный код ReadyToRun, исключая паузы JIT-компилятора при блокировке экрана.
- **🗜 Оптимальное сжатие дистрибутива:**
  - Чистая компоновка папок и оптимальное сжатие снизили размер архива до ~22 МБ.
- **✍️ Доверенная цифровая подпись:**
  - Все исполняемые файлы и сборки подписаны сертификатом Authenticode с RFC 3161 метками времени Sectigo. Для архива сформированы цифровая подпись PKCS#7 (`.sig`) и контрольная сумма SHA-256 (`.sha256`).

---

### 🇬🇧 English

#### 🌟 What's New
- **⏳ Dynamic Lock Screen Progress Bar & Visual Styles (LogonUI Tile):**
  - Live progress bar and countdown timer embedded directly into the Windows logon user tile.
  - **3 selectable bar styles:** Block `[████]`, Thin `[━━━━]`, and Dots `[●●●●]`, configurable in Host Settings.
  - **Dead-Center Native Alignment:** Decoupled status headline and progress bar into dedicated LogonUI controls, guaranteeing pixel-perfect native DirectUI centering without any left-edge bias.
  - **Adaptive Font Geometry:** Bar length automatically calibrates to Segoe UI character metrics (`█`, `━`, `●`, etc.) and the DirectUI container limit (~305 px) to prevent clipping.
  - **Symmetric Infinity Indicators:** Ambient marquee wave with symmetric infinity badges on both sides of elapsed time: `[██░░░░  (∞) 0:15 (∞)  ░░░░██]`.
  - **Line-Wrap Protection (No-Wrap):** Non-breaking spaces (`\u00A0`) prevent Windows word-wrap splits at space boundaries.
- **🔍 Log Search and Fast Filtering (LogsWindow):**
  - Fluent search box with Segoe MDL2 magnifying glass (`\uE721`), watermark placeholder, and instant clear button (`\uE711`).
  - Real-time category chips: **"All"**, **"Errors"** (matches `[ERR]`, `[WRN]`, `Exception`, `Failed`), **"Network (WS)"**, **"Bluetooth (BLE)"**.
  - Blazing-fast in-memory filtering without disk I/O lag and match counter (*"Found: X / Y entries"*).
- **🔤 Custom Lock Screen Tile Status Text:**
  - Host Settings option to specify custom text replacing the default "Awaiting approval...".
  - One-click reset button and dynamic bar length scaling matching the user's custom headline.
- **⌨️ Fast Remote Unlock Retry via Enter Key:**
  - Pressing `Enter` on the lock screen tile while the password field is empty instantly triggers a remote unlock request without touching the mouse.
  - If characters are typed, `Enter` performs standard local password authentication.
- **📊 Live Active Transport & IP Indicator in Details:**
  - Tile details button displays real-time connectivity status: `Wi-Fi: Active (192.168.1.5)` / `Bluetooth: Connected (1)`.
- **🚨 Winlogon Failure Reason Diagnostics (NTSTATUS OnLogonStatusReported):**
  - Parses Windows logon NTSTATUS codes and displays clear user-friendly reasons on the tile and activity journal:
    * `0xC000006A` / `0xC000006D` -> "Wrong Windows Password" `[🔑]`
    * `0xC0000234` -> "Account Locked Out" `[🔒]`
    * `0xC0000071` / `0xC0000224` -> "Password Expired" `[⌛]`
    * `0xC0000072` -> "Account Disabled" `[🚫]`
- **🎯 Granular Cancellation & Rejection Reasons:**
  - Clearly reports cancellation source: "Cancelled by User", "Manual Password Input", "User Switched", "Request Timed Out", "Rejected on Device".
- **⌨️ Smart Auto-Cancel on Manual Password Input:**
  - Remote unlock request and sockets are cancelled immediately upon the first keystroke in the password box.
- **🛡️ Real-Time Windows Password Pre-Validation:**
  - Validates Windows credentials via `LogonUserW` before storing to LSA Secret Store with zero plaintext memory leaks (`ZeroFreeGlobalAlloc`).
- **🛡️ Startup LSA Secret Integrity Audit:**
  - Verifies DPAPI/LSA secret readability upon Host launch with immediate `SecureString.Dispose()` and a warning badge `[⚠️ Secret Issue]` on compromised device cards.
- **🆔 Microsoft Account (MSA) & Domain UPN Resolution:**
  - Accurate account matching for `user@outlook.com` and domain UPNs via SID resolution (`LookupAccountName`/`Win32_UserProfile`).
- **⏱️ Quick Timeout Preset Chips:**
  - One-click buttons `[15s]`, `[30s]`, `[1m]`, `[2m]`, `[♾️ No Limit]`.
- **🔄 Smooth Component Health Refresh Animation:**
  - Refresh button with Segoe MDL2 icon (`\uE72C`), continuous hardware-accelerated spin animation, and step-by-step progress spinners (`[🔄 Checking...]`) for each component.
- **👁️ Password Reveal Eye Toggle Button:**
  - Fluent button with Segoe MDL2 glyphs (`\uE7B3`/`\uED1A`), seamless alignment with PasswordBox without text shifting, and zero plaintext memory retention.
- **⚠️ Device Removal Confirmation Guard:**
  - Dialog confirmation showing the device's actual name to prevent accidental unpairing.
- **📦 One-Click Diagnostic ZIP Export:**
  - Consolidates logs, environment status, and configuration into `portal-diagnostics-*.zip`.
- **🌐 Dynamic Network Adaptation:**
  - Smooth listener migration and debounced mDNS re-advertisement on network changes via `NetworkChange`.

#### ⚡ Improvements & Hardening
- **🏷️ Centralized Solution Versioning:**
  - Root `Directory.Build.props` manages versioning across all 4 projects and scripts automatically.
- **🛡️ Global Unhandled Exception Logging in Host:**
  - Catches `AppDomain` and `Dispatcher` unhandled exceptions directly into `host.log`.
- **🐛 File System Access Permissions (ACL):**
  - Inherited `Modify` ACLs configured on `ProgramData\Portal-Windows\Logs` for `Authenticated Users`.
- **🔐 Strict RequestId Anti-Replay Validation:**
  - Replay attack mitigation and reliable session recovery on reconnection.
- **🚀 Instant LogonUI Rendering (ReadyToRun AOT):**
  - Credential provider precompiled to native machine code for stutter-free lock screen display.
- **🗜 Optimized Package Layout & Compression:**
  - Reduced release ZIP archive size to ~22 MB with optimal compression.
- **✍️ Authenticode Code Signing:**
  - All assemblies and binaries signed with Sectigo RFC 3161 timestamps. Detached PKCS#7 signature (`.sig`) and SHA-256 checksums (`.sha256`) provided for the archive.

---

### 📦 Хэш-суммы и цифровая подпись дистрибутива (Checksums)

```text
File: PortalWin-1.5.5-win-x64.zip
SHA256: 15d7ef5d491eba04ba101448f4a9bdf13856e15b76a39edba7746617fc5832bc
Signature: PortalWin-1.5.5-win-x64.zip.sig (PKCS#7 Detached Signature)
```

---

## 📝 What's Changed
* feat(credential-provider): visual progress bar with styles (Block, Thin, Dots) and infinite marquee on lock screen tile by @KoksMen
* feat(logs): search box and fast category filters (All, Errors, WS, BLE) in LogsWindow by @KoksMen
* feat(tile): custom waiting status text with dynamic bar scaling by @KoksMen
* feat(tile): quick remote unlock retry by pressing Enter on idle tile by @KoksMen
* feat(tile): display live active transport indicator (Wi-Fi/IP, Bluetooth) in details by @KoksMen
* feat(tile): handle OnLogonStatusReported to log exact NTSTATUS failure reasons by @KoksMen
* feat(tile): display precise cancellation and rejection reasons on lock screen by @KoksMen
* feat(tile): auto-cancel remote unlock request on manual password input by @KoksMen
* feat(auth): real-time Windows password validation before saving credentials (LogonUserW) by @KoksMen
* feat(host): background LSA secret integrity check on startup with UI badge by @KoksMen
* feat(host): enhance Microsoft Account (MSA) and domain UPN identity resolution by @KoksMen
* feat(host): quick timeout preset buttons (15s, 30s, 1m, 2m, infinite) by @KoksMen
* feat(host): component health refresh button with smooth spin and per-item loading animation by @KoksMen
* feat(host): password reveal eye toggle button in credentials dialog by @KoksMen
* feat(host): explicit device removal confirmation dialog with device name by @KoksMen
* feat(build): centralize solution versioning via Directory.Build.props and PortalVersionInfo by @KoksMen
* fix(host): configure explicit ACLs on Logs directory to fix Access Denied by @KoksMen
* fix(host): resolve missing InputBorder static resource and add unhandled exception logging by @KoksMen
* feat(diagnostics): add diagnostic report archive export (ZIP) with system info and logs by @KoksMen
* feat(network): dynamic network change adaptation via NetworkChange and debounced mDNS re-advertisement by @KoksMen
* build(packaging): clean layout without duplicate nested publish folders, optimal ZIP compression, and detached PKCS#7 signature by @KoksMen

**Full Changelog**: https://github.com/KoksMen/Portal-Windows/compare/v1.5.4...v1.5.5
