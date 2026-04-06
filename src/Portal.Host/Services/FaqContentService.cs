using System.IO;
using System.Text.Json;
using Portal.Common;
using Portal.Common.Models;

namespace Portal.Host.Services;

public sealed class FaqContentService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string GetFaqPath()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "faq.json");
    }

    public async Task<FaqDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        var faqPath = GetFaqPath();
        if (!File.Exists(faqPath))
        {
            var seeded = CreateDefaultDocument();
            Directory.CreateDirectory(Path.GetDirectoryName(faqPath)!);
            await File.WriteAllTextAsync(faqPath, JsonSerializer.Serialize(seeded, JsonOptions), cancellationToken);
            return seeded;
        }

        var json = await File.ReadAllTextAsync(faqPath, cancellationToken);
        return ParseDocument(json) ?? CreateDefaultDocument();
    }

    public async Task<FaqDocument> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var document = await LoadAsync(cancellationToken);
        document.Source = "local-file";
        document.UpdatedAt = File.GetLastWriteTimeUtc(GetFaqPath());
        return document;
    }

    private static FaqDocument? ParseDocument(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var document = JsonSerializer.Deserialize<FaqDocument>(json);
            if (document != null && document.Articles.Count > 0)
            {
                NormalizeDocument(document);
                return document;
            }
        }
        catch
        {
        }

        try
        {
            var legacyItems = JsonSerializer.Deserialize<List<LegacyFaqItem>>(json);
            if (legacyItems == null)
            {
                return null;
            }

            var document = new FaqDocument
            {
                Source = "local-file",
                UpdatedAt = DateTime.UtcNow,
                Articles = legacyItems
                    .Where(item => !string.IsNullOrWhiteSpace(item.Question) || !string.IsNullOrWhiteSpace(item.Answer))
                    .Select((item, index) => new FaqArticle
                    {
                        Id = $"legacy-{index + 1}",
                        Title = item.Question?.Trim() ?? $"Article {index + 1}",
                        Category = "General",
                        Tags = new List<string> { "legacy" },
                        Content = item.Answer?.Trim() ?? string.Empty,
                        UpdatedAt = DateTime.UtcNow
                    })
                    .ToList()
            };

            NormalizeDocument(document);
            return document;
        }
        catch (Exception ex)
        {
            Logger.LogError("[FaqContentService] Failed to parse faq file.", ex);
            return null;
        }
    }

    private static void NormalizeDocument(FaqDocument document)
    {
        document.Source = string.IsNullOrWhiteSpace(document.Source) ? "local-file" : document.Source.Trim();
        if (document.UpdatedAt == default)
        {
            document.UpdatedAt = DateTime.UtcNow;
        }

        foreach (var article in document.Articles)
        {
            article.Id = string.IsNullOrWhiteSpace(article.Id) ? Guid.NewGuid().ToString("N") : article.Id.Trim();
            article.Title = string.IsNullOrWhiteSpace(article.Title) ? "Untitled article" : article.Title.Trim();
            article.Category = string.IsNullOrWhiteSpace(article.Category) ? "General" : article.Category.Trim();
            article.Content = article.Content?.Trim() ?? string.Empty;
            article.Tags = article.Tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (article.UpdatedAt == default)
            {
                article.UpdatedAt = document.UpdatedAt;
            }
        }
    }

    private static FaqDocument CreateDefaultDocument()
    {
        return new FaqDocument
        {
            Source = "local-file",
            UpdatedAt = DateTime.UtcNow,
            Articles = new List<FaqArticle>
            {
                new()
                {
                    Id = "pair-extra-device",
                    Title = "How do I add another device?",
                    Category = "Pairing",
                    Tags = new List<string> { "pairing", "device", "setup" },
                    Content = "Open Host, click START / ACTIVATE or Add Another Device, and walk through the pairing wizard for the next phone.",
                    UpdatedAt = DateTime.UtcNow
                },
                new()
                {
                    Id = "reset-everything",
                    Title = "How do I reset the whole installation?",
                    Category = "Recovery",
                    Tags = new List<string> { "reset", "recovery", "maintenance" },
                    Content = "Use Uninstall Everything or Reset in Host. This removes paired devices, certificates, and related Provider/Firewall configuration.",
                    UpdatedAt = DateTime.UtcNow
                },
                new()
                {
                    Id = "edit-faq-file",
                    Title = "How do I edit this FAQ file?",
                    Category = "FAQ",
                    Tags = new List<string> { "faq", "wiki", "local-file" },
                    Content = "Edit faq.json next to Host, then click Update Wiki in the FAQ window to reload the local source without restarting the app.",
                    UpdatedAt = DateTime.UtcNow
                }
            }
        };
    }

    private sealed class LegacyFaqItem
    {
        public string? Question { get; set; }
        public string? Answer { get; set; }
    }
}
