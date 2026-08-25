using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace Portal.Host.Services;

/// <summary>Applies the selected display language to static WPF captions without touching bound data.</summary>
public static class LocalizationService
{
    private sealed class OriginalValues
    {
        public string? Text { get; init; }
        public string? Content { get; init; }
        public string? Header { get; init; }
        public string? ToolTip { get; init; }
    }

    private static readonly ConditionalWeakTable<DependencyObject, OriginalValues> Originals = new();

    private static readonly IReadOnlyDictionary<string, string> Russian = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Interface language"] = "Язык интерфейса",
        ["Choose the language for the Portal interface. The selection is saved automatically."] = "Выберите язык интерфейса Portal. Выбор сохраняется автоматически.",
        ["Settings"] = "Настройки",
        ["← Back"] = "← Назад",
        ["Connected Devices"] = "Подключённые устройства",
        ["Configuration"] = "Конфигурация",
        ["Backup"] = "Резервная копия",
        ["Diagnostics"] = "Диагностика",
        ["Updates"] = "Обновления",
        ["Unlock Mode"] = "Режим разблокировки",
        ["Client-Initiated (Type 1)"] = "По запросу телефона (тип 1)",
        ["Host-Initiated (Type 2)"] = "По запросу компьютера (тип 2)",
        ["Both (Type 3)"] = "Оба режима (тип 3)",
        ["Auto-Request Trigger"] = "Автозапрос разблокировки",
        ["Request on click"] = "По нажатию",
        ["On click + PC startup"] = "По нажатию и при запуске ПК",
        ["On click + any lock screen"] = "По нажатию и на любом экране блокировки",
        ["Request validity time (minutes)"] = "Время ожидания запроса (мин.)",
        ["Block duplicate account pairing on same transport"] = "Запретить повторную привязку аккаунта в том же канале",
        ["Also block pairing this account on another transport"] = "Также запретить привязку этого аккаунта в другом канале",
        ["VPN compatibility mode (ignore VPN/virtual adapters)"] = "Режим совместимости с VPN (игнорировать VPN/виртуальные адаптеры)",
        ["Encrypted backup includes config settings, trusted devices and host certificate."] = "Зашифрованная копия включает настройки, доверенные устройства и сертификат компьютера.",
        ["Create Backup"] = "Создать копию",
        ["Restore From Backup"] = "Восстановить из копии",
        ["Troubleshooting tools."] = "Инструменты поиска неполадок.",
        ["View Logs"] = "Открыть журнал",
        ["Check update"] = "Проверить обновления",
        ["Enable automatic update checks"] = "Автоматически проверять обновления",
        ["Save Configuration"] = "Сохранить настройки",
        ["Enabled"] = "Включено",
        ["Install"] = "Установить",
        ["Uninstall"] = "Удалить",
        ["Fix / Update"] = "Исправить / обновить",
        ["Delete Rules"] = "Удалить правила",
        ["Regenerate"] = "Пересоздать",
        ["Run Full Setup Wizard"] = "Запустить мастер настройки",
        ["Uninstall Everything"] = "Удалить всё",
        ["Recent activity"] = "Последняя активность",
        ["SHOW"] = "ПОКАЗАТЬ",
        ["ORDER"] = "ПОРЯДОК",
        ["FROM DATE"] = "С ДАТЫ",
        ["Reset"] = "Сбросить",
        ["Close"] = "Закрыть",
        ["Refresh activity"] = "Обновить активность",
        ["Close activity"] = "Закрыть активность",
        ["All events"] = "Все события",
        ["Newest first"] = "Сначала новые",
        ["Oldest first"] = "Сначала старые",
        ["Advanced Settings"] = "Расширенные настройки",
        ["Download Mobile Client"] = "Скачать приложение",
        ["About Portal"] = "О Portal",
        ["START / ACTIVATE"] = "ЗАПУСТИТЬ / АКТИВИРОВАТЬ",
        ["PC unlock approved"] = "Разблокировка ПК подтверждена",
        ["Unlock request cancelled"] = "Запрос разблокировки отменён",
        ["Unlock request declined"] = "Запрос разблокировки отклонён",
        ["Portal started"] = "Portal запущен"
        ,["System Health & Maintenance"] = "Состояние системы и обслуживание"
        ,["Credential Provider"] = "Поставщик учётных данных"
        ,["Firewall Rule"] = "Правило брандмауэра"
        ,["SSL Certificate"] = "SSL-сертификат"
        ,["DLL Files"] = "Файлы DLL"
        ,["Certificate information"] = "Сведения о сертификате"
        ,["Host certificate information"] = "Сведения о сертификате компьютера"
        ,["Edit Account"] = "Изменить аккаунт"
        ,["Remove Device / Удалить"] = "Удалить устройство"
        ,["Port"] = "Порт"
        ,["How long Host waits for device approval. Use 0 to wait until manual cancellation."] = "Как долго компьютер ждёт подтверждения от устройства. Укажите 0, чтобы ждать до ручной отмены."
        ,["Second option depends on the first one. If the first is off, this one is disabled and turned off."] = "Вторая опция зависит от первой. Если первая выключена, вторая недоступна и отключается."
        ,["Recommended for stable pairing/restore IP routing when VPN is active."] = "Рекомендуется для стабильной привязки и восстановления маршрута при активном VPN."
        ,["Update Wizard"] = "Мастер обновления"
        ,["File"] = "Файл"
        ,["Transfer"] = "Передача"
        ,["Speed"] = "Скорость"
        ,["About app"] = "О приложении"
        ,["Terms of Use"] = "Условия использования"
        ,["Privacy Policy"] = "Политика конфиденциальности"
        ,["Download mobile app"] = "Скачать мобильное приложение"
        ,["Source Code of android app"] = "Исходный код приложения Android"
        ,["Source Code of PC client"] = "Исходный код клиента ПК"
        ,["Android app developer"] = "Разработчик Android-приложения"
        ,["PC client developer"] = "Разработчик клиента ПК"
        ,["App Version"] = "Версия приложения"
        ,["Configuring..."] = "Настройка..."
        ,["Cancel Setup"] = "Отменить настройку"
        ,["PC Login Details"] = "Данные для входа на ПК"
        ,["Required for Windows to unlock."] = "Нужно Windows для разблокировки."
        ,["Choose Windows Account"] = "Выберите учётную запись Windows"
        ,["Password"] = "Пароль"
        ,["Device Name"] = "Имя устройства"
        ,["Back"] = "Назад"
        ,["Cancel"] = "Отмена"
        ,["Choose Pairing Method"] = "Выберите способ привязки"
        ,["Select how you want to pair with the device."] = "Выберите способ привязки устройства."
        ,["Next →"] = "Далее →"
        ,["Connect Phone"] = "Подключите телефон"
        ,["Enter this code in the mobile app:"] = "Введите этот код в мобильном приложении:"
        ,["IP Interface:"] = "Сетевой интерфейс:"
        ,["Port: "] = "Порт: "
        ,["BT: "] = "Bluetooth: "
        ,["Name Device"] = "Назовите устройство"
        ,["Give this device a friendly name."] = "Укажите понятное имя для устройства."
        ,["Save & Finish"] = "Сохранить и завершить"
        ,["Success!"] = "Готово!"
        ,["Error"] = "Ошибка"
        ,["Action is in progress. Please wait and avoid interacting with Host until it finishes."] = "Выполняется операция. Подождите и не используйте Portal до её завершения."
        ,["Cancelling can interrupt configuration and may require manual recovery or reinstall."] = "Отмена может прервать настройку и потребовать ручного восстановления или переустановки."
        ,["Backup includes config settings, trusted devices and host server certificate."] = "Копия включает настройки, доверенные устройства и сертификат сервера компьютера."
        ,["Confirm Password"] = "Подтвердите пароль"
        ,["Backup File"] = "Файл копии"
        ,["Browse"] = "Выбрать"
        ,["Restore"] = "Восстановить"
        ,["Restore replaces current config settings, trusted devices and host server certificate."] = "Восстановление заменит текущие настройки, доверенные устройства и сертификат сервера."
        ,["Certificate SHA-256"] = "Сертификат SHA-256"
        ,["Activity helps you understand pairing, unlocks and network recovery. It never includes passwords, IP addresses or certificate data."] = "Журнал показывает привязку, разблокировки и восстановление сети. Пароли, IP-адреса и данные сертификатов в него не записываются."
        ,["Day"] = "День"
        ,["Month"] = "Месяц"
        ,["Year"] = "Год"
        ,["Notification"] = "Уведомление"
        ,["YES"] = "ДА"
        ,["NO"] = "НЕТ"
    };

    public static void ApplyToMainWindow(string language)
    {
        if (Application.Current?.MainWindow is not Window window)
            return;

        if (!window.Dispatcher.CheckAccess())
        {
            window.Dispatcher.BeginInvoke(() => ApplyToMainWindow(language));
            return;
        }

        Apply(window, string.Equals(language, "ru", StringComparison.OrdinalIgnoreCase));
    }

    private static void Apply(DependencyObject root, bool useRussian)
    {
        var visited = new HashSet<DependencyObject>();
        ApplyRecursive(root, useRussian, visited);
    }

    private static void ApplyRecursive(DependencyObject element, bool useRussian, ISet<DependencyObject> visited)
    {
        if (!visited.Add(element)) return;

        // Language names must always be readable in their native form and must not be translated.
        if (element is ComboBox { Name: "LanguageSelector" })
            return;

        if (!Originals.TryGetValue(element, out var original))
        {
            original = new OriginalValues
            {
                Text = element is TextBlock textBlock && !BindingOperations.IsDataBound(textBlock, TextBlock.TextProperty) ? textBlock.Text : null,
                Content = element is ContentControl contentControl && contentControl.Content is string content && !BindingOperations.IsDataBound(contentControl, ContentControl.ContentProperty) ? content : null,
                Header = element is HeaderedContentControl headered && headered.Header is string header && !BindingOperations.IsDataBound(headered, HeaderedContentControl.HeaderProperty) ? header : null,
                ToolTip = element is FrameworkElement frameworkElement && frameworkElement.ToolTip is string toolTip ? toolTip : null
            };
            Originals.Add(element, original);
        }

        if (element is TextBlock targetTextBlock && original.Text != null)
            targetTextBlock.Text = Translate(original.Text, useRussian);
        if (element is ContentControl targetContent && original.Content != null)
            targetContent.Content = Translate(original.Content, useRussian);
        if (element is HeaderedContentControl targetHeader && original.Header != null)
            targetHeader.Header = Translate(original.Header, useRussian);
        if (element is FrameworkElement targetElement && original.ToolTip != null)
            targetElement.ToolTip = Translate(original.ToolTip, useRussian);

        var visualChildren = element is Visual || element is System.Windows.Media.Media3D.Visual3D
            ? VisualTreeHelper.GetChildrenCount(element)
            : 0;
        for (var index = 0; index < visualChildren; index++)
            ApplyRecursive(VisualTreeHelper.GetChild(element, index), useRussian, visited);

        foreach (var child in LogicalTreeHelper.GetChildren(element))
        {
            if (child is DependencyObject dependencyObject)
                ApplyRecursive(dependencyObject, useRussian, visited);
        }
    }

    private static string Translate(string original, bool useRussian) => useRussian && Russian.TryGetValue(original, out var translated) ? translated : original;
}
