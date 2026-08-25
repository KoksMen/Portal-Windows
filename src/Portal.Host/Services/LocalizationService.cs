using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Portal.Common.Helpers;
using Localization = Portal.Common.Helpers.Localization;

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
        public string? Tag { get; init; }
    }

    private static readonly ConditionalWeakTable<DependencyObject, OriginalValues> Originals = new();
    private static readonly ConditionalWeakTable<object, string> StyleOriginals = new();

    /// <summary>Currently applied UI language ("ru" or "en").</summary>
    public static string CurrentLanguage => Localization.CurrentLanguage;

    public static bool IsRussian => Localization.IsRussian;

    /// <summary>Translates an English UI string into the currently selected language.</summary>
    public static string T(string english) => Localization.T(english);

    /// <summary>Translates a format template first, then formats it with arguments.</summary>
    public static string TF(string template, params object[] args) => Localization.TF(template, args);

    /// <summary>Sets the current language without touching any UI (used before windows are shown).</summary>
    public static void SetCurrentLanguage(string language) => Localization.SetCurrentLanguage(language);

    public static void ApplyToMainWindow(string language)
    {
        if (Application.Current?.MainWindow is not Window window)
            return;

        if (!window.Dispatcher.CheckAccess())
        {
            window.Dispatcher.BeginInvoke(() => ApplyToMainWindow(language));
            return;
        }

        Localization.SetCurrentLanguage(language);
        Apply(window, IsRussian);
    }

    /// <summary>Applies current language to any window (logs, toast, dialogs).</summary>
    public static void ApplyToWindow(Window window)
    {
        if (window == null)
            return;

        if (!window.Dispatcher.CheckAccess())
        {
            window.Dispatcher.BeginInvoke(() => ApplyToWindow(window));
            return;
        }

        Apply(window, IsRussian);
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

        if (element is FrameworkElement styleOwner)
        {
            ApplyStyle(styleOwner.Style, useRussian);
        }

        if (!Originals.TryGetValue(element, out var original))
        {
            original = new OriginalValues
            {
                Text = element is TextBlock textBlock && !BindingOperations.IsDataBound(textBlock, TextBlock.TextProperty) ? textBlock.Text : null,
                Content = element is ContentControl contentControl && contentControl.Content is string content && !BindingOperations.IsDataBound(contentControl, ContentControl.ContentProperty) ? content : null,
                Header = element is HeaderedContentControl headered && headered.Header is string header && !BindingOperations.IsDataBound(headered, HeaderedContentControl.HeaderProperty) ? header : null,
                ToolTip = element is FrameworkElement frameworkElement && frameworkElement.ToolTip is string toolTip ? toolTip : null,
                Tag = element is FrameworkElement taggedElement && taggedElement.Tag is string tag ? tag : null
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
        if (element is FrameworkElement targetTaggedElement && original.Tag != null)
            targetTaggedElement.Tag = Translate(original.Tag, useRussian);

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

    /// <summary>
    /// Translates string values of style setters (including data trigger setters).
    /// Inline styles are per-element instances and can be mutated safely; shared
    /// (sealed) styles are skipped to avoid exceptions.
    /// </summary>
    private static void ApplyStyle(Style? style, bool useRussian)
    {
        if (style == null)
            return;

        ApplyStyleSetters(style.Setters, useRussian);
        foreach (var trigger in style.Triggers.OfType<Trigger>())
            ApplyStyleSetters(trigger.Setters, useRussian);
        foreach (var dataTrigger in style.Triggers.OfType<System.Windows.DataTrigger>())
            ApplyStyleSetters(dataTrigger.Setters, useRussian);
    }

    private static void ApplyStyleSetters(SetterBaseCollection setters, bool useRussian)
    {
        foreach (var setterBase in setters)
        {
            if (setterBase is not Setter setter || setter.Value is not string value || value.Length == 0)
                continue;

            // Only translate display text properties.
            if (!Equals(setter.Property, TextBlock.TextProperty)
                && !Equals(setter.Property, FrameworkElement.ToolTipProperty))
                continue;

            var original = StyleOriginals.GetValue(setter, _ => value);
            try
            {
                setter.Value = Translate(original, useRussian);
            }
            catch
            {
                // Style is sealed (shared resource) - leave as is.
            }
        }
    }

    private static string Translate(string original, bool useRussian) => useRussian ? Localization.T(original) : original;
}
