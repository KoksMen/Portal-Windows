using System.Text.Json;
using Portal.Common.Models;

namespace Portal.Common;

/// <summary>
/// Stores compact, user-readable activity records shared by Host and Credential Provider.
/// Newline-delimited JSON makes an interrupted write non-destructive: a malformed final line is ignored.
/// </summary>
public static class ActivityJournal
{
    private const int DefaultReadLimit = 250;
    private static readonly object ProcessSync = new();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static string JournalPath => Path.Combine(PortalStoragePaths.LogsDirectory, "activity.journal.jsonl");

    public static void Record(
        string category,
        string icon,
        string title,
        string details,
        bool isSuccess = true,
        string? deviceName = null,
        string? transport = null)
    {
        var entry = new ActivityEntry
        {
            OccurredAtUtc = DateTime.UtcNow,
            Category = category,
            Icon = icon,
            Title = title,
            Details = details,
            IsSuccess = isSuccess,
            DeviceName = deviceName,
            Transport = transport
        };

        try
        {
            lock (ProcessSync)
            {
                using var mutex = new Mutex(false, @"Global\Portal.ActivityJournal");
                var hasMutex = false;
                try
                {
                    hasMutex = mutex.WaitOne(TimeSpan.FromSeconds(2));
                    if (!hasMutex)
                    {
                        return;
                    }

                    var line = JsonSerializer.Serialize(entry, JsonOptions) + Environment.NewLine;
                    using var writer = new StreamWriter(new FileStream(
                        JournalPath,
                        FileMode.Append,
                        FileAccess.Write,
                        FileShare.Read), System.Text.Encoding.UTF8);
                    writer.Write(line);
                }
                finally
                {
                    if (hasMutex)
                    {
                        mutex.ReleaseMutex();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // The journal must never prevent unlock or pairing.
            Logger.LogWarning($"[ActivityJournal] Failed to save activity record: {ex.Message}");
        }
    }

    public static IReadOnlyList<ActivityEntry> ReadLatest(int limit = DefaultReadLimit)
    {
        if (limit <= 0 || !File.Exists(JournalPath))
        {
            return Array.Empty<ActivityEntry>();
        }

        try
        {
            var entries = new List<ActivityEntry>();
            using var stream = new FileStream(JournalPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var entry = JsonSerializer.Deserialize<ActivityEntry>(line, JsonOptions);
                    if (entry != null)
                    {
                        entries.Add(entry);
                    }
                }
                catch (JsonException)
                {
                    // A partial final line can result from a process ending during append.
                }
            }

            return entries
                .OrderByDescending(entry => entry.OccurredAtUtc)
                .Take(limit)
                .ToList();
        }
        catch (Exception ex)
        {
            Logger.LogWarning($"[ActivityJournal] Failed to read activity records: {ex.Message}");
            return Array.Empty<ActivityEntry>();
        }
    }
}
