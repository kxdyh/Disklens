using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using DiskLens.Core.Models;

namespace DiskLens.App.Localization;

/// <summary>Application-wide display language. Paths, identifiers and model payloads are never translated here.</summary>
public sealed class L : INotifyPropertyChanged
{
    public static L Current { get; } = new();
    private static readonly Dictionary<string, string> Keys = Load("keys");
    private static readonly Dictionary<string, string> English = Load("en");
    private static readonly Dictionary<string, string> Reverse = English.GroupBy(p => p.Value).Where(g => g.Count() == 1).ToDictionary(g => g.Key, g => g.Single().Key);
    private static readonly Lazy<IReadOnlyList<(Regex Pattern, string English)>> Templates = new(() => English
        .Where(p => Regex.IsMatch(p.Key, @"\{[0-9]+(?:[^{}]*)\}"))
        .OrderByDescending(p => p.Key.Length)
        .Select(p => (BuildTemplate(p.Key), p.Value)).ToArray());
    private static readonly Dictionary<string, (string Chinese, string English)> Recent = new(StringComparer.Ordinal);
    private string _language = "zh";
    public string Language => _language;
    public static bool IsEnglish => Current._language == "en";
    public event PropertyChangedEventHandler? PropertyChanged;
    public static event EventHandler? LanguageChanged;
    public string this[string source] => T(source);
    public static string Normalize(string? language) => language?.StartsWith("en", StringComparison.OrdinalIgnoreCase) == true ? "en" : "zh";
    public static void SetLanguage(string? language)
    {
        var normalized = Normalize(language);
        var changed = Current._language != normalized;
        Current._language = normalized;
        var culture = CultureInfo.GetCultureInfo(normalized == "en" ? "en-US" : "zh-CN");
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = culture;
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = culture;
        if (!changed) return;
        Current.PropertyChanged?.Invoke(Current, new PropertyChangedEventArgs(nameof(Language)));
        Current.PropertyChanged?.Invoke(Current, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(Current, EventArgs.Empty);
    }
    public static string T(string? source)
    {
        if (string.IsNullOrEmpty(source)) return source ?? "";
        source = Keys.GetValueOrDefault(source, source);
        lock (Recent)
        {
            if (Recent.TryGetValue(source, out var pair)) return IsEnglish ? pair.English : pair.Chinese;
        }
        if (!IsEnglish) return Reverse.TryGetValue(source, out var original) ? original : source;
        if (English.TryGetValue(source, out var english)) return english;
        if (!source.Any(c => c >= '\u4e00' && c <= '\u9fff') || source.Length > 12000) return source;
        foreach (var (pattern, template) in Templates.Value)
        {
            var match = pattern.Match(source);
            if (!match.Success) continue;
            var translated = Regex.Replace(template, @"\{([0-9]+)(?:[^{}]*)\}", m => match.Groups["v" + m.Groups[1].Value].Value);
            lock (Recent)
            {
                if (Recent.Count > 8192) Recent.Clear();
                Recent[source] = (source, translated);
                Recent[translated] = (source, translated);
            }
            return translated;
        }
        return source;
    }
    private static Regex BuildTemplate(string source)
    {
        var text = new System.Text.StringBuilder("\\A");
        var offset = 0;
        foreach (Match match in Regex.Matches(source, @"\{([0-9]+)(?:[^{}]*)\}"))
        {
            text.Append(Regex.Escape(source[offset..match.Index]));
            text.Append("(?<v").Append(match.Groups[1].Value).Append(">.*?)");
            offset = match.Index + match.Length;
        }
        text.Append(Regex.Escape(source[offset..])).Append("\\z");
        return new Regex(text.ToString(), RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
    }
    public static string F(FormattableString source)
    {
        var chinese = string.Format(CultureInfo.CurrentCulture, source.Format, source.GetArguments());
        var english = string.Format(CultureInfo.CurrentCulture, English.GetValueOrDefault(source.Format, source.Format), source.GetArguments());
        lock (Recent)
        {
            if (Recent.Count > 8192) Recent.Clear();
            Recent[chinese] = (chinese, english);
            Recent[english] = (chinese, english);
        }
        return IsEnglish ? english : chinese;
    }
    public static string Purpose(AiAnalysisResult analysis)
    {
        var text = IsEnglish ? analysis.ShortPurposeEn : analysis.ShortPurpose;
        if (!string.IsNullOrWhiteSpace(text)) return text;
        // Older cloud entries may contain only a controlled purpose category.
        // Display that category without treating it as original AI text.
        if (analysis.Origin == AiAnalysisResultOrigin.SharedExperience && analysis.SharedEntry is { } shared)
        {
            var display = IsEnglish ? shared.DisplayTexts?.ShortPurposeEn : shared.DisplayTexts?.ShortPurpose;
            if (!string.IsNullOrWhiteSpace(display)) return display;
            var category = shared.Purpose switch
            {
                "cache" => "缓存或临时数据", "runtime" => "运行依赖", "application" => "应用组件",
                "configuration" => "配置数据", "logs" => "日志记录", "downloads" => "下载内容",
                "user-data" => "用户资料", "unknown" => "用途暂不明确", _ => string.Empty
            };
            if (category.Length > 0) return T(category);
        }
        return IsEnglish ? "English summary unavailable" : string.Empty;
    }
    public static string Reason(AiAnalysisResult analysis)
    {
        if (analysis.LooksLikeFallback()) return T(analysis.Reason);
        if (!IsEnglish) return analysis.Reason;
        if (!string.IsNullOrWhiteSpace(analysis.ReasonEn)) return analysis.ReasonEn;
        if (analysis.Origin == AiAnalysisResultOrigin.SharedExperience && analysis.SharedEntry is { } shared)
        {
            if (!string.IsNullOrWhiteSpace(shared.DisplayTexts?.ReasonEn)) return shared.DisplayTexts.ReasonEn;
            // Older category-only cloud records use the controlled local translation.
            if (string.IsNullOrWhiteSpace(analysis.ShortPurpose)) return T(analysis.Reason);
        }
        return "English analysis is unavailable for this older result. Reanalyze to generate both languages.";
    }
    public static string NodeName(FileSystemNode? node) => node is null ? T("尚未选择项目")
        : DisplayNodeMetadata.IsOverflow(node) ? F($"其余 {node.Children.Count:N0} 项") : node.Name;
    private static Dictionary<string, string> Load(string name)
    {
        using var stream = typeof(L).Assembly.GetManifestResourceStream("DiskLens.App.Localization." + name + ".json");
        return stream is null ? new() : JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
    }
}
