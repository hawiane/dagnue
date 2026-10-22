using System.Text.RegularExpressions;

namespace Nexus.Core.Codex;

public static partial class RemoteTaskResultFormatter
{
    private const int MaximumSummaryLength = 360;

    public static string Format(string? finalText, bool hasResultFiles)
    {
        var sentences = Clean(finalText)
            .Where(sentence => !InternalNarration().IsMatch(sentence))
            .ToArray();

        if (hasResultFiles)
        {
            var observation = sentences.LastOrDefault(sentence =>
                UsefulObservation().IsMatch(sentence) && !ArtifactStatus().IsMatch(sentence));
            return Limit(observation is null
                ? "The requested file is ready."
                : $"The requested file is ready. {EnsureSentence(observation)}");
        }

        return sentences.Length == 0
            ? "Task completed."
            : Limit(string.Join(' ', sentences.Take(2).Select(EnsureSentence)));
    }

    public static string FormatError(string? error)
    {
        if (string.IsNullOrWhiteSpace(error)) return "The task could not be completed.";
        var cleaned = FileCitation().Replace(error, string.Empty);
        cleaned = WindowsPath().Replace(cleaned, "a required local resource");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return Limit(string.IsNullOrWhiteSpace(cleaned) ? "The task could not be completed." : cleaned);
    }

    private static IEnumerable<string> Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return [];
        var cleaned = FileCitation().Replace(value, string.Empty);
        cleaned = WindowsPath().Replace(cleaned, string.Empty);
        cleaned = cleaned.Replace('`', ' ');
        return SentenceBoundary().Split(cleaned)
            .Select(sentence => Regex.Replace(sentence, @"\s+", " ").Trim(' ', '-', ':'))
            .Where(sentence => sentence.Length > 1 && !sentence.StartsWith("::", StringComparison.Ordinal));
    }

    private static string EnsureSentence(string value) =>
        value.EndsWith('.') || value.EndsWith('!') || value.EndsWith('?') ? value : value + ".";

    private static string Limit(string value)
    {
        value = Regex.Replace(value, @"\s+", " ").Trim();
        if (value.Length <= MaximumSummaryLength) return value;
        var shortened = value[..MaximumSummaryLength].TrimEnd();
        var lastSpace = shortened.LastIndexOf(' ');
        if (lastSpace > MaximumSummaryLength / 2) shortened = shortened[..lastSpace];
        return shortened.TrimEnd('.', ',', ';', ':') + ".";
    }

    [GeneratedRegex(@"::codex-file-citation\{.*?\}", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex FileCitation();

    [GeneratedRegex("""(?i)(?:(?:the path\s+)?['"]?(?:[a-z]:\\|\\\\).*?['"]?(?=\s+(?:is|was|could|cannot)\b)|(?:[a-z]:\\|\\\\)\S+)""")]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"(?<=[.!?])\s+|[\r\n]+")]
    private static partial Regex SentenceBoundary();

    [GeneratedRegex(@"(?i)\b(?:i'll|i will|retry|runtime|fallback|workspace|results? folder|upload(?:ing|ed)?|download(?:ing|ed)?|codex|powershell|file path)\b|(?:^|\s)\.?[/\\]results\b")]
    private static partial Regex InternalNarration();

    [GeneratedRegex(@"(?i)\b(?:contains?|found|including|total|records?|entries|missing|complete|completed|days?|hours?|rows?)\b|\d")]
    private static partial Regex UsefulObservation();

    [GeneratedRegex(@"(?i)\b(?:created|generated|saved|ready|file|workbook|spreadsheet|csv)\b")]
    private static partial Regex ArtifactStatus();
}
