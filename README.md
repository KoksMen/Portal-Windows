# Portal-Windows

## About the Project
PortalWin is a custom Windows Credential Provider designed for convenient unlocking of the Windows operating system using a mobile device.

## Purpose
The project solves the problem of the daily login routine. Instead of manually typing credentials on the keyboard every time, the user can delegate this task to their trusted smartphone. This is especially relevant for complex passwords that are inconvenient to enter frequently but are necessary for security.

## How It Works & Key Features
The system consists of several key components: a host application for configuration (`Portal.Host`) and a system DLL provider library (`Portal.CredentialProvider`) that integrates into the Windows login screen (LogonUI).

1. **Pairing**: A QR code is generated through the convenient interface of the desktop application. The user scans it with their mobile device, after which a secure key exchange (Pairing Context) takes place.
2. **Discovery**: The computer announces itself on the local network using the mDNS protocol so the smartphone can find it automatically, without manual IP address entry.
3. **Unlocking**: While on the lock screen, the computer opens a secure channel (via Wi-Fi or Bluetooth) and waits for a command from the smartphone. Upon receiving and successfully validating the request, the system passes the credentials to Windows, and the desktop is unlocked.

**Key features:**
- Automatic PC discovery on the network (mDNS).
- Local network unlocking (Wi-Fi) via WebSockets.
- Direct unlocking via Bluetooth (for cases when the network is unavailable).
- One-click device pairing configuration via QR code.
- Creation of encrypted credential backups.

## Technologies Used
- **Programming Language:** C#
- **UI Framework:** WPF (Windows Presentation Foundation) for the client configuration interface.
- **OS Integration:** Windows Credential Provider API (COM interfaces).
- **Networking:** WebSockets, mDNS (Multicast DNS).
- **Security:** TLS (Transport Layer Security) for network traffic protection, cryptography for backup encryption.
- **Data Storage:** Windows LSA (Local Security Authority) Secret Store.
- **Communication:** Bluetooth RFCOMM / duplex streams.

---

## Project Advantages

### 🛡️ Security
* **No plaintext passwords:** User credentials are not stored in standard configuration files. The secure Windows LSA storage is used for saving them, accessible only by the system.
* **Traffic encryption:** All unlock commands and service data transfers over Wi-Fi are wrapped in TLS. Custom protection mechanisms are implemented for the duplex channel over Bluetooth.
* **Strict authorization:** The computer can only be unlocked by a device that has completed the cryptographic pairing process. External attempts to hijack the connection are rejected.
* **Isolation:** Even if network traffic is intercepted, an attacker will not be able to extract the original password for a Microsoft account or local user.

### ⚡ Convenience
* **Seamless login:** Unlocking the computer requires just one action on the smartphone, which significantly saves time.
* **Zero-Configuration:** Thanks to the implementation of mDNS, the user does not need to be a system administrator. The smartphone "sees" the computer on the network by itself — no setup required.
* **Fallback communication channels:** If the router goes offline, unlocking will continue to work via a direct Bluetooth connection between the phone and PC.
* **Intuitive setup interface:** The process of adding a new phone comes down to simply opening the `Portal.Host` application and scanning the generated QR code.

---
---

# Portal-Windows

## О проекте
PortalWin — это кастомный поставщик учетных данных (Windows Credential Provider), предназначенный для удобной разблокировки операционной системы Windows с помощью мобильного устройства. 

## Для чего он нужен
Проект решает проблему повседневной рутины при входе в систему. Вместо того чтобы каждый раз вручную вводить учетные данные с клавиатуры, пользователь может делегировать эту задачу своему доверенному смартфону. Это особенно актуально для сложных паролей, которые неудобно вводить часто, но необходимо использовать для обеспечения безопасности.

## Как это работает и основные возможности
Система состоит из нескольких ключевых компонентов: приложения-хоста для настройки (`Portal.Host`) и системной DLL-библиотеки провайдера (`Portal.CredentialProvider`), которая интегрируется в экран входа Windows (LogonUI).

1. **Сопряжение**: Через удобный интерфейс десктопного приложения генерируется QR-код. Пользователь сканирует его мобильным устройством, после чего происходит безопасный обмен ключами (Pairing Context).
2. **Обнаружение**: Компьютер объявляет о себе в локальной сети с помощью протокола mDNS, чтобы смартфон мог найти его автоматически, без ручного ввода IP-адреса.
3. **Разблокировка**: Находясь на экране блокировки, компьютер открывает защищенный канал (по Wi-Fi или Bluetooth) и ожидает команду от смартфона. При получении и успешной валидации запроса, система передает учетные данные в Windows и рабочий стол разблокируется.

**Ключевые возможности:**
- Автоматическое обнаружение ПК в сети (mDNS).
- Разблокировка по локальной сети (Wi-Fi) через WebSockets.
- Разблокировка напрямую по Bluetooth (для случаев, когда сеть недоступна).
- Настройка сопряжения устройств в один клик через QR-код.
- Создание зашифрованных резервных копий учетных данных.

## Используемые технологии
- **Язык разработки:** C#
- **UI Фреймворк:** WPF (Windows Presentation Foundation) для клиентского интерфейса настройки.
- **Интеграция с ОС:** Windows Credential Provider API (COM-интерфейсы).
- **Сетевое взаимодействие:** WebSockets, mDNS (Multicast DNS).
- **Безопасность:** TLS (Transport Layer Security) для защиты сетевого трафика, криптография для шифрования бэкапов.
- **Хранение данных:** Windows LSA (Local Security Authority) Secret Store.
- **Связь:** Bluetooth RFCOMM / дуплексные стримы.

---

## Преимущества проекта

### 🛡️ Безопасность
* **Отсутствие паролей в открытом виде:** Учетные данные пользователя не хранятся в обычных конфигурационных файлах. Для их сохранения используется защищенное хранилище Windows LSA, к которому имеет доступ только система.
* **Шифрование трафика:** Все команды на разблокировку и передача служебных данных по сети Wi-Fi обернуты в TLS. Для Bluetooth реализованы собственные механизмы защиты дуплексного канала.
* **Строгая авторизация:** Разблокировать компьютер может только то устройство, которое прошло процесс криптографического сопряжения. Внешние попытки подобрать подключение отвергаются.
* **Изолированность:** Даже при перехвате сетевого трафика злоумышленник не сможет извлечь оригинальный пароль от учетной записи Microsoft или локального пользователя.

### ⚡ Удобство
* **Бесшовный вход:** Для разблокировки компьютера достаточно одного действия на смартфоне, что существенно экономит время.
* **Zero-Configuration:** Благодаря внедрению mDNS, пользователю не нужно быть системным администратором. Смартфон сам "видит" компьютер в сети — ничего настраивать не нужно.
* **Резервные каналы связи:** Если отключился роутер, разблокировка продолжит работать через прямое Bluetooth-соединение между телефоном и ПК.
* **Интуитивный интерфейс настройки:** Процесс добавления нового телефона сводится к простому открытию приложения `Portal.Host` и сканированию сгенерированного QR-кода.
