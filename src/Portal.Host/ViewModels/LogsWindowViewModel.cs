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

            ApplySnapshotToColumn(
                day,
                hostSnapshot,
                dayChanged || forceRebuild,
                ref _hostBody,
                ref _hostSeenSignatures,
                text => HostLogsText = text);

            ApplySnapshotToColumn(
                day,
                providerSnapshot,
                dayChanged || forceRebuild,
                ref _providerBody,
                ref _providerSeenSignatures,
                text => ProviderLogsText = text);

            LogsUpdatedAtText = $"Updated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}";
        }
        catch (Exception ex)
        {
            HostLogsText = $"Failed to load host logs: {ex.Message}";
            ProviderLogsText = $"Failed to load provider logs: {ex.Message}";
            LogsUpdatedAtText = "Updated: failed";
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
        var files = GetLogFiles(patterns);
        if (files.Count == 0)
        {
            return new ColumnSnapshot(
                new List<LogEntry>(),
                $"No {kind.ToLowerInvariant()} logs found.\nFolder: {LogDirectoryPath}");
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

    private static List<string> GetLogFiles(IReadOnlyCollection<string> patterns)
    {
        try
        {
            if (!Directory.Exists(LogDirectoryPath))
            {
                return new List<string>();
            }

            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pattern in patterns)
            {
                foreach (var file in Directory.EnumerateFiles(LogDirectoryPath, pattern, SearchOption.TopDirectoryOnly))
                {
                    paths.Add(file);
                }
            }

            return paths
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(14)
                .Select(file => file.FullName)
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

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

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
                entries.Add(current.Build(sequence, Path.GetFileName(filePath)));
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
