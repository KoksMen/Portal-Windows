using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Portal.Common;

namespace Portal.Host.ViewModels;

public partial class LogsWindowViewModel : ObservableObject
{
    private static readonly string LogDirectoryPath = PortalStoragePaths.LogsDirectory;

    private readonly DispatcherTimer _refreshTimer = new()
    {
        Interval = TimeSpan.FromSeconds(2)
    };
    private bool _isRefreshing;
    private DateTime _loadedDate = DateTime.MinValue;
    private string _hostBody = string.Empty;
    private string _providerBody = string.Empty;
    private HashSet<string> _hostSeenSignatures = new(StringComparer.Ordinal);
    private HashSet<string> _providerSeenSignatures = new(StringComparer.Ordinal);

    [ObservableProperty] private DateTime _selectedDate = DateTime.Today;
    [ObservableProperty] private string _hostLogsText = "No host logs loaded yet.";
    [ObservableProperty] private string _providerLogsText = "No provider logs loaded yet.";
    [ObservableProperty] private string _logsUpdatedAtText = "Not updated yet.";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string _searchText = string.Empty;
    [ObservableProperty] private LogFilterCategory _selectedCategory = LogFilterCategory.All;
    [ObservableProperty] private string _filterSummaryText = string.Empty;

    public bool IsFilterAll => SelectedCategory == LogFilterCategory.All;
    public bool IsFilterErrors => SelectedCategory == LogFilterCategory.Errors;
    public bool IsFilterNetwork => SelectedCategory == LogFilterCategory.NetworkWs;
    public bool IsFilterBluetooth => SelectedCategory == LogFilterCategory.BluetoothBle;

    private ColumnSnapshot? _latestHostSnapshot;
    private ColumnSnapshot? _latestProviderSnapshot;

    public event Action? CloseRequested;

    public LogsWindowViewModel()
    {
        _refreshTimer.Tick += async (_, _) => await RefreshLogsInternalAsync(forceRebuild: false, showLoading: false);
    }

    public void Start()
    {
        _ = RefreshLogsInternalAsync(forceRebuild: true, showLoading: true);
        _refreshTimer.Start();
    }

    public void Stop()
    {
        _refreshTimer.Stop();
    }

    partial void OnSelectedDateChanged(DateTime value)
    {
        _ = RefreshLogsInternalAsync(forceRebuild: true, showLoading: true);
    }

    partial void OnSearchTextChanged(string value)
    {
        ApplyCurrentFilter();
    }

    partial void OnSelectedCategoryChanged(LogFilterCategory value)
    {
        OnPropertyChanged(nameof(IsFilterAll));
        OnPropertyChanged(nameof(IsFilterErrors));
        OnPropertyChanged(nameof(IsFilterNetwork));
        OnPropertyChanged(nameof(IsFilterBluetooth));
        ApplyCurrentFilter();
    }

    [RelayCommand]
    private void SelectFilter(string category)
    {
        if (Enum.TryParse<LogFilterCategory>(category, true, out var parsed))
        {
            SelectedCategory = parsed;
        }
    }

    [RelayCommand]
    private void ClearSearch()
    {
        SearchText = string.Empty;
    }

    [RelayCommand]
    private void CloseWindow()
    {
        CloseRequested?.Invoke();
    }

    [RelayCommand]
    private async Task RefreshLogsAsync()
    {
        await RefreshLogsInternalAsync(forceRebuild: false, showLoading: false);
    }

    private async Task RefreshLogsInternalAsync(bool forceRebuild, bool showLoading)
    {
        if (_isRefreshing)
        {
            return;
        }

        _isRefreshing = true;
        if (showLoading)
        {
            IsLoading = true;
        }

        try
        {
            var day = SelectedDate.Date;
            var hostSnapshot = await Task.Run(() => BuildSnapshot("Host", day, new[] { "host*.log" }));
            var providerSnapshot = await Task.Run(() => BuildSnapshot("Provider", day, new[] { "provider*.log", "portalwin_default*.log" }));

            var dayChanged = _loadedDate != day;
            if (dayChanged)
            {
                _loadedDate = day;
            }

            var hostSignatures = hostSnapshot.Entries.Select(BuildSignature).ToHashSet(StringComparer.Ordinal);
            var providerSignatures = providerSnapshot.Entries.Select(BuildSignature).ToHashSet(StringComparer.Ordinal);
            bool hostChanged = !_hostSeenSignatures.SetEquals(hostSignatures);
            bool providerChanged = !_providerSeenSignatures.SetEquals(providerSignatures);

            _hostSeenSignatures = hostSignatures;
            _providerSeenSignatures = providerSignatures;
            _latestHostSnapshot = hostSnapshot;
            _latestProviderSnapshot = providerSnapshot;

            if (forceRebuild || dayChanged || hostChanged || providerChanged)
            {
                ApplyCurrentFilter();
            }

            LogsUpdatedAtText = Services.LocalizationService.T("Updated: ") + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
        }
        catch (Exception ex)
        {
            HostLogsText = $"Failed to load host logs: {ex.Message}";
            ProviderLogsText = $"Failed to load provider logs: {ex.Message}";
            LogsUpdatedAtText = Services.LocalizationService.T("Updated: ") + Services.LocalizationService.T("failed");
        }
        finally
        {
            _isRefreshing = false;
            if (showLoading)
            {
                IsLoading = false;
            }
        }
    }

    private void ApplyCurrentFilter()
    {
        var day = SelectedDate.Date;
        var hostEntries = _latestHostSnapshot?.Entries ?? (IReadOnlyList<LogEntry>)Array.Empty<LogEntry>();
        var providerEntries = _latestProviderSnapshot?.Entries ?? (IReadOnlyList<LogEntry>)Array.Empty<LogEntry>();

        var filteredHost = FilterEntries(hostEntries);
        var filteredProvider = FilterEntries(providerEntries);

        var hostBody = filteredHost.Count == 0
            ? string.Empty
            : FormatEntries(filteredHost);
        HostLogsText = ComposeColumnText(day, hostBody, _latestHostSnapshot?.EmptyMessage ?? "No host logs loaded yet.");

        var providerBody = filteredProvider.Count == 0
            ? string.Empty
            : FormatEntries(filteredProvider);
        ProviderLogsText = ComposeColumnText(day, providerBody, _latestProviderSnapshot?.EmptyMessage ?? "No provider logs loaded yet.");

        int totalFound = filteredHost.Count + filteredProvider.Count;
        int totalEntries = hostEntries.Count + providerEntries.Count;

        if (SelectedCategory != LogFilterCategory.All || !string.IsNullOrWhiteSpace(SearchText))
        {
            FilterSummaryText = Services.LocalizationService.TF("Found: {0}", $"{totalFound} / {totalEntries}");
        }
        else
        {
            FilterSummaryText = Services.LocalizationService.TF("Found: {0}", totalEntries);
        }
    }

    private IReadOnlyList<LogEntry> FilterEntries(IReadOnlyList<LogEntry> entries)
    {
        if (entries.Count == 0) return entries;

        var search = SearchText?.Trim();
        var hasSearch = !string.IsNullOrEmpty(search);
        var category = SelectedCategory;

        if (!hasSearch && category == LogFilterCategory.All)
        {
            return entries;
        }

        var result = new List<LogEntry>();
        foreach (var entry in entries)
        {
            if (category != LogFilterCategory.All && !MatchesCategory(entry, category))
            {
                continue;
            }

            if (hasSearch && !MatchesSearch(entry, search!))
            {
                continue;
            }

            result.Add(entry);
        }

        return result;
    }

    private static bool MatchesCategory(LogEntry entry, LogFilterCategory category)
    {
        return category switch
        {
            LogFilterCategory.Errors => entry.Lines.Any(l =>
                l.Contains("[ERR]", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("[WRN]", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("Error", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("Failed", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("Faulted", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("Critical", StringComparison.OrdinalIgnoreCase)),

            LogFilterCategory.NetworkWs => entry.Lines.Any(l =>
                l.Contains("[WebSocketManager]", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("[TlsUnlockService]", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("[UnlockHandler]", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("WebSocket", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("mDNS", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("TLS", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("Network", StringComparison.OrdinalIgnoreCase)),

            LogFilterCategory.BluetoothBle => entry.Lines.Any(l =>
                l.Contains("[BtUnlock]", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("BtProtocol", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("RFCOMM", StringComparison.OrdinalIgnoreCase) ||
                l.Contains("BLE", StringComparison.OrdinalIgnoreCase)),

            _ => true
        };
    }

    private static bool MatchesSearch(LogEntry entry, string query)
    {
        if (entry.FileName.Contains(query, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var line in entry.Lines)
        {
            if (line.Contains(query, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static void ApplySnapshotToColumn(
        DateTime day,
        ColumnSnapshot snapshot,
        bool rebuild,
        ref string currentBody,
        ref HashSet<string> seenSignatures,
        Action<string> setText)
    {
        if (rebuild)
        {
            seenSignatures = snapshot.Entries
                .Select(BuildSignature)
                .ToHashSet(StringComparer.Ordinal);

            currentBody = snapshot.Entries.Count == 0
                ? string.Empty
                : FormatEntries(snapshot.Entries);
            setText(ComposeColumnText(day, currentBody, snapshot.EmptyMessage));
            return;
        }

        if (snapshot.Entries.Count == 0)
        {
            return;
        }

        var latestSignatures = snapshot.Entries
            .Select(BuildSignature)
            .ToHashSet(StringComparer.Ordinal);

        var seen = seenSignatures;
        var newEntries = snapshot.Entries
            .Where(entry => !seen.Contains(BuildSignature(entry)))
            .ToList();

        seenSignatures = latestSignatures;
        if (newEntries.Count == 0)
        {
            return;
        }

        var prependBlock = FormatEntries(newEntries);
        currentBody = string.IsNullOrWhiteSpace(currentBody)
            ? prependBlock
            : $"{prependBlock}{Environment.NewLine}{Environment.NewLine}{currentBody}";
        setText(ComposeColumnText(day, currentBody, snapshot.EmptyMessage));
    }

    private static string ComposeColumnText(DateTime day, string body, string emptyMessage)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return emptyMessage;
        }

        var header = $"Date: {day:yyyy-MM-dd}{Environment.NewLine}Newest entries first.";
        return $"{header}{Environment.NewLine}{Environment.NewLine}{body}";
    }

    private static string FormatEntries(IReadOnlyList<LogEntry> entries)
    {
        var builder = new StringBuilder();
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            builder.AppendLine($"[{entry.FileName}]");
            foreach (var line in entry.Lines)
            {
                builder.AppendLine(line);
            }

            if (index < entries.Count - 1)
            {
                builder.AppendLine();
            }
        }

        return builder.ToString().TrimEnd();
    }

    private static string BuildSignature(LogEntry entry)
    {
        var firstLine = entry.Lines.Count > 0 ? entry.Lines[0] : string.Empty;
        return $"{entry.FileName}|{entry.Timestamp:O}|{entry.Sequence}|{entry.Lines.Count}|{firstLine}";
    }

    private static ColumnSnapshot BuildSnapshot(string kind, DateTime day, IReadOnlyCollection<string> patterns)
    {
        var files = GetLogFiles(patterns, day);
        if (files.Count == 0)
        {
            return new ColumnSnapshot(
                new List<LogEntry>(),
                $"No {kind.ToLowerInvariant()} logs found for {day:yyyy-MM-dd}.\nFolder: {LogDirectoryPath}");
        }

        var entries = new List<LogEntry>();
        foreach (var file in files)
        {
            entries.AddRange(ReadEntries(file, day));
        }

        var ordered = entries
            .OrderByDescending(entry => entry.Timestamp)
            .ThenByDescending(entry => entry.Sequence)
            .ToList();

        if (ordered.Count == 0)
        {
            return new ColumnSnapshot(
                ordered,
                $"No {kind.ToLowerInvariant()} logs for {day:yyyy-MM-dd}.");
        }

        return new ColumnSnapshot(ordered, string.Empty);
    }

    private static List<string> GetLogFiles(IReadOnlyCollection<string> patterns, DateTime day)
    {
        try
        {
            if (!Directory.Exists(LogDirectoryPath))
            {
                return new List<string>();
            }

            var dayStr = day.ToString("yyyyMMdd");
            var matchingFiles = new List<FileInfo>();

            foreach (var pattern in patterns)
            {
                foreach (var file in Directory.EnumerateFiles(LogDirectoryPath, pattern, SearchOption.TopDirectoryOnly))
                {
                    var fileInfo = new FileInfo(file);
                    var fileName = fileInfo.Name;

                    // If file name has a date pattern like host20261006.log or provider20261006.log
                    var match = System.Text.RegularExpressions.Regex.Match(fileName, @"\d{8}");
                    if (match.Success)
                    {
                        // File has explicit date - only include if it matches selected day!
                        if (match.Value == dayStr)
                        {
                            matchingFiles.Add(fileInfo);
                        }
                    }
                    else
                    {
                        // Non-dated file (e.g. host.log or provider.log)
                        // Include if modified today or if selected day is today
                        if (fileInfo.LastWriteTime.Date >= day.Date)
                        {
                            matchingFiles.Add(fileInfo);
                        }
                    }
                }
            }

            return matchingFiles
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static IEnumerable<LogEntry> ReadEntries(string filePath, DateTime day)
    {
        var entries = new List<LogEntry>();
        int sequence = 0;
        const long maxBytesToRead = 3 * 1024 * 1024; // 3 MB max tail

        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists || fileInfo.Length == 0)
            {
                return entries;
            }

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

            bool isTruncated = false;
            if (stream.Length > maxBytesToRead)
            {
                stream.Seek(stream.Length - maxBytesToRead, SeekOrigin.Begin);
                isTruncated = true;
            }

            using var reader = new StreamReader(stream, Encoding.UTF8);

            // If we seeked into the middle of the file, discard the first partial line
            if (isTruncated)
            {
                reader.ReadLine();
            }

            LogEntryBuilder? current = null;
            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine() ?? string.Empty;

                if (TryExtractTimestamp(line, out var timestamp))
                {
                    if (current != null && current.Timestamp.Date == day.Date)
                    {
                        entries.Add(current.Build(sequence++, Path.GetFileName(filePath)));
                    }

                    current = new LogEntryBuilder(timestamp);
                    current.Lines.Add(line);
                    continue;
                }

                if (current != null)
                {
                    current.Lines.Add(line);
                }
            }

            if (current != null && current.Timestamp.Date == day.Date)
            {
                entries.Add(current.Build(sequence++, Path.GetFileName(filePath)));
            }

            if (isTruncated)
            {
                entries.Add(new LogEntry(
                    DateTime.MinValue,
                    new[] { $"[... Showing newest entries from {fileInfo.Length / (1024.0 * 1024.0):F1} MB log file ...]" },
                    int.MaxValue,
                    Path.GetFileName(filePath)));
            }
        }
        catch (Exception ex)
        {
            entries.Add(new LogEntry(
                DateTime.MinValue,
                new[] { $"[Unable to read '{Path.GetFileName(filePath)}': {ex.Message}]" },
                int.MaxValue,
                Path.GetFileName(filePath)));
        }

        return entries;
    }

    private static bool TryExtractTimestamp(string line, out DateTime timestamp)
    {
        timestamp = default;
        if (string.IsNullOrWhiteSpace(line) || line.Length < 20 || line[0] != '[')
        {
            return false;
        }

        var stamp = line.Substring(1, 19);
        return DateTime.TryParseExact(
            stamp,
            "yyyy-MM-dd HH:mm:ss",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out timestamp);
    }

    private sealed class LogEntryBuilder
    {
        public LogEntryBuilder(DateTime timestamp)
        {
            Timestamp = timestamp;
        }

        public DateTime Timestamp { get; }
        public List<string> Lines { get; } = new();

        public LogEntry Build(int sequence, string fileName)
        {
            return new LogEntry(Timestamp, Lines.ToArray(), sequence, fileName);
        }
    }

    private sealed record LogEntry(DateTime Timestamp, IReadOnlyList<string> Lines, int Sequence, string FileName);
    private sealed record ColumnSnapshot(IReadOnlyList<LogEntry> Entries, string EmptyMessage);
}

public enum LogFilterCategory
{
    All = 0,
    Errors = 1,
    NetworkWs = 2,
    BluetoothBle = 3
}
