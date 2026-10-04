## 🚀 Portal-Windows v1.5.5 Herta — Release Summary / Описание изменений

### 🇷🇺 Описание релиза

В данное обновление вошли ключевые улучшения стабильности сети, визуального взаимодействия на экране блокировки и инструменты для решения проблем:

1. **⏳ Визуальный центрированный прогресс-бар и индикатор ожидания (Lock Screen Tile):**
   - На плитке экрана блокировки таймер теперь встроен строго по центру графической шкалы: `[████  38s  ░░░░]`.
   - Для бесконечного режима ожидания (`0` мин) добавлен анимированный пульс с секундомером и знаком бесконечности: `[■□  0:42 (∞)  □■]`.
   - Текст статуса и индикатор разнесены на две сбалансированные строки, исключая прилипание к левому краю.
   - Поле «Время ожидания ответа» в настройках выровнено по центру карточки.

2. **📦 Экспорт отчёта для решения проблем («Собрать диагностический архив» в ZIP):**
   - В секции **Settings $\rightarrow$ Diagnostics** добавлена кнопка **«Export Diagnostic Report»**.
   - В один клик собирает ZIP-архив с краткой сводкой `summary.txt`, техническим отчётом `system_environment.json` (сборка Windows, статус компонентов, сеть, Bluetooth) и полными логами `host*.log`, `provider*.log`, `activity.journal.jsonl`.
   - Конфигурация полностью обезличена: пароли учетных записей, приватные ключи сертификатов и токены обновлений исключены.
   - Предлагает сразу открыть папку с готовым архивом в Проводнике.

3. **🌐 Динамическая адаптация к смене сети (NetworkChange):**
   - Интеграция с `NetworkChange.NetworkAddressChanged` и `NetworkAvailabilityChanged`.
   - Автоматическое обнаружение изменений IP при переключении между Wi-Fi сетями, кабелем и VPN с дебаунсом 750 мс.
   - Автоматическое переоповещение службы mDNS (`_portal._tcp.local.`) без необходимости перезапуска хоста.

4. **✍️ Цифровая подпись и сборка:**
   - Все бинарники подписаны сертификатами xXTeam и меткой времени Sectigo RFC 3161 SHA-256.
   - Сформирован релизный архив: `PortalWin-1.5.5-win-x64.zip`.

---

### 🇬🇧 Release Summary

1. **⏳ Centered Lock Screen Progress Bar & Timer:**
   - Countdown is rendered directly in the center of the unicode bar: `[████  38s  ░░░░]`.
   - Animated marquee pulse with elapsed stopwatch and infinity badge for infinite wait mode: `[■□  0:42 (∞)  □■]`.
   - Centered timeout duration input in Host settings.

2. **📦 Diagnostic Report ZIP Export:**
   - One-click diagnostic export in **Settings $\rightarrow$ Diagnostics**.
   - Collects `summary.txt`, `system_environment.json`, and all active logs (`host*.log`, `provider*.log`, `activity.journal.jsonl`).
   - Secure and sanitized: passwords, private keys, and tokens are never included.

3. **🌐 Dynamic Network Adaptation:**
   - Proactive network monitoring via `NetworkChange.NetworkAddressChanged`.
   - 750 ms debounce for smooth transitions between Wi-Fi, Ethernet, and VPN interfaces.
   - Automatic mDNS service re-advertisement on address change.

4. **✍️ Code Signing & Packaging:**
   - Authenticode signatures with trusted Sectigo RFC 3161 timestamps.
   - Packaged artifact: `PortalWin-1.5.5-win-x64.zip`.
