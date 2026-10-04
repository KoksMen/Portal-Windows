## Portal-Windows 1.5.4 Herta 🪄✨

---

### 🇷🇺 Русский язык

#### 🌟 Новые возможности
- **⚡ Интерактивный тест разблокировки и связи (Test Unlock Connection):**
  - Возможность прямо из карточки сопряжённого устройства запустить проверку связи одним нажатием на золотистую кнопку **«⚡ Проверить связь»**.
  - Развёртывание временного легковесного mTLS Kestrel-сервера на динамическом порту с анонсированием в локальной сети через mDNS (`_portal-test._tcp.local.`).
  - Полная поддержка двусторонней проверки через **WebSocket (`/ws`)** и **REST (`/api/unlock`)** с валидацией отпечатка клиентского сертификата.
  - Измерение и вывод точного времени отклика (RTT Latency в мс) в диалоговом окне результата и в журнале активности.
  - Мгновенное завершение Kestrel-сервера и освобождение сокетов сразу после получения ответа (ликвидированы подвисания окна при закрытии сокета).
- **🪟 Поддержка эффектов Windows 11 Fluent Design (Mica & Acrylic):**
  - Интеграция системных эффектов размытия подложки Mica Alt и Acrylic через DWM API с автоматическим корректным переключением на контрастный тёмный режим в Windows 10.
- **📱 Полный редизайн карточек устройств:**
  - **Чёткая векторная графика (SVG):** Вместо старых монохромных эмодзи Segoe UI (выглядевших как ретро-калькулятор 🖩 и лестница 📶) внедрены чёткие векторные иконки смартфона, Wi-Fi волн, геометрии Bluetooth, пользователя, ключа и календаря.
  - **Эргономичный переключатель питания:** Добавлена выразительная кнопка состояния сопряжения `[ ⏻ Включено ]` / `[ ⏻ Отключено ]` с изумрудной подсветкой и всплывающей подсказкой.
  - **Широкий контейнер метаданных:** Идентификатор клиента (GUID) вынесен в полноразмерный нижний блок — текст больше не обрезается и не наезжает на соседние элементы.
  - **Исправление слипания текста:** Добавлены аккуратные отступы между метками и значениями (`Аккаунт: rinshima`).
- **📐 Адаптивный главный экран (Dashboard):**
  - Главный экран обёрнут в адаптивный ScrollViewer — исключено вертикальное обрезание содержимого на ноутбуках и экранах с высоким масштабированием (125%, 150%).
  - Сбалансированы пропорции логотипа Portal (100×100 px), убраны лишние отступы и дублирующая кнопка скачивания клиента.
  - Локализованы системные статусы плитки под кнопкой СТАРТ (`⚠ Требуется настройка службы` вместо технических путей COM-компонентов).

#### ⚡ Улучшения и оптимизация
- **🚀 Ускорение загрузки экрана блокировки (LogonUI ReadyToRun & Tiered Compilation):**
  - Для `Portal.CredentialProvider` включена AOT-компиляция ReadyToRun (`<PublishReadyToRun>true</PublishReadyToRun>`) и многоуровневая компиляция CLR (`TieredCompilationQuickJit`), что устраняет задержки JIT-компилятора при отображении LogonUI в Windows.
- **🌐 Нечувствительность к регистру в WebSocket-ответах:**
  - Десериализация JSON-ответов от мобильного клиента переведена на регистронезависимый парсинг (`PropertyNameCaseInsensitive = true`), поддерживая как PascalCase, так и camelCase структуры.
- **🧱 Карточки компонентов здоровья системы:**
  - Список модулей переведён на независимые микро-карточки, исключая перекрытия заголовков компонентов, индикаторов состояния и кнопок действий.
- **✍️ Автоматическая цифровая подпись:**
  - Все исполняемые файлы и сборки (`Portal.Host.exe`, `Portal.Updater.exe`, `Portal.CredentialProvider.comhost.dll` и др.) подписаны сертификатом с доверенной меткой времени Sectigo RFC 3161.

#### 🔄 Совместимость
- **📱 Мобильный клиент:** 100% обратная совместимость с текущими приложениями Portal для Android и Wear OS.
- **🔐 Протоколы:** Форматы mTLS, WebSocket и REST сохранены без изменений.

---

### 🇬🇧 English

#### 🌟 What's New
- **⚡ Interactive Device Unlock Connection Test:**
  - Test connectivity and certificate exchange directly from any paired device card via the new golden **"⚡ Check Connection"** button.
  - Ephemeral lightweight mTLS Kestrel listener on a dynamic port with local mDNS advertising (`_portal-test._tcp.local.`).
  - Supports both **WebSocket (`/ws`)** and **REST (`/api/unlock`)** transports with client certificate thumbprint verification.
  - Accurate Round-Trip Time (RTT) latency reporting in both the completion dialog and the activity journal.
  - Zero-latency Kestrel server shutdown via proactive socket aborting upon first response.
- **🪟 Windows 11 Fluent Design (Mica & Acrylic Backdrops):**
  - Native Windows 11 Mica Alt and Acrylic backdrop effects via DWM API, with seamless dark fallback on Windows 10.
- **📱 Complete Device Card Redesign:**
  - **Crisp SVG Vector Graphics:** Replaced retro monochrome Segoe UI glyphs with resolution-independent vector icons (smartphone, Wi-Fi signal, Bluetooth geometry, user, key, calendar).
  - **Ergonomic Power Switch:** Dedicated `[ ⏻ Enabled ]` / `[ ⏻ Disabled ]` toggle with emerald glow and responsive tooltip.
  - **Full-Width Metadata Container:** Client ID GUID is placed in an unrestricted container, eliminating truncation and overlap.
  - **Clean Typography:** Fixed label-to-value text collisions (`Account: rinshima`).
- **📐 Adaptive Root Dashboard:**
  - Entire dashboard wrapped in an adaptive ScrollViewer, preventing vertical clipping on high-DPI displays.
  - Streamlined branding proportions (100×100 px) and removed redundant controls.
  - Localized setup status under the START button instead of internal COM registration paths.

#### ⚡ Improvements & Hardening
- **🚀 Instant Lock Screen Rendering (LogonUI ReadyToRun & Tiered Compilation):**
  - Enabled `<PublishReadyToRun>` and CLR Tiered JIT in `Portal.CredentialProvider`, eliminating JIT pauses when Windows loads `logonui.exe`.
- **🌐 Case-Insensitive WebSocket Parsing:**
  - Handled both PascalCase and camelCase JSON payloads from mobile devices seamlessly.
- **🧱 Responsive System Health Layout:**
  - Modern micro-card layout preventing collisions between status badges, labels, and action buttons.
- **✍️ Code Signing with Sectigo RFC 3161 Timestamp:**
  - All distributed binaries and COM assemblies are Authenticode-signed with trusted timestamps.

#### 🔄 Compatibility
- **📱 Mobile App:** Fully compatible with Portal for Android and Wear OS.
- **🔐 Protocols:** WebSocket, REST, and Bluetooth transport specifications remain fully backward-compatible.

---

## 📝 What's Changed
* feat(ui): complete device card redesign with vector SVG icons, power switch, and clean layout by @KoksMen in https://github.com/KoksMen/Portal-Windows/pull/19
* feat(diagnostics): interactive unlock test service with mDNS advertising, WebSocket/REST listener, and RTT measurement by @KoksMen in https://github.com/KoksMen/Portal-Windows/pull/19
* perf(credential-provider): enable ReadyToRun AOT compilation and Tiered JIT for instantaneous LogonUI loading by @KoksMen in https://github.com/KoksMen/Portal-Windows/pull/19
* feat(design): add Windows 11 Fluent Mica Alt and Acrylic window backdrops with Windows 10 fallback by @KoksMen in https://github.com/KoksMen/Portal-Windows/pull/19
* fix(dashboard): wrap root view in adaptive ScrollViewer and replace raw COM paths with localized status hints by @KoksMen in https://github.com/KoksMen/Portal-Windows/pull/19
* build(packaging): add Package-Release.ps1, bump version to 1.5.4, and digitally sign all publish targets by @KoksMen in https://github.com/KoksMen/Portal-Windows/pull/19

**Full Changelog**: https://github.com/KoksMen/Portal-Windows/compare/v1.5.3...v1.5.4
