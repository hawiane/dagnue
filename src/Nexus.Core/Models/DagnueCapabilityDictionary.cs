namespace Nexus.Core.Models;

public sealed record CapabilityTagDefinition(string Key, string Label, string Description,
    IReadOnlyList<string> Keywords);

public static class DagnueCapabilityDictionary
{
    public static readonly IReadOnlyList<CapabilityTagDefinition> Definitions =
    [
        new("HRIS", "HRIS", "Employee master data, plantilla, appointments and personnel records.",
            ["hris", "employee", "personnel", "plantilla", "appointment", "permanent", "casual", "job order", "birthday"]),
        new("PAYROLL", "PAYROLL", "Payroll, salary, deductions, allowances, payslips and statutory contributions.",
            ["payroll", "salary", "payslip", "deduction", "allowance", "tax", "pagibig", "philhealth"]),
        new("BIOMETRIC", "BIOMETRIC", "Biometric attendance, time logs, device punches, lateness and attendance services.",
            ["biometric", "attendance", "timelog", "time log", "time-in", "time-out", "late", "punch"]),
        new("SALN", "SALN", "SALN documents, assets, liabilities, net worth and disclosure records.",
            ["saln", "assets", "liabilities", "net worth", "disclosure"]),
        new("FILES", "FILES", "Authorized folders, indexed files, documents and result-file delivery.",
            ["file", "folder", "document", "presentation", "powerpoint", "upload", "download", "copy", "logo"]),
        new("EMAIL", "EMAIL", "Authorized mailboxes, message search and reviewable email drafts.",
            ["email", "mail", "mailbox", "inbox", "outlook", "gmail", "message", "draft"]),
        new("SYSTEM_ADMIN", "SYSTEM ADMIN", "Approved Windows services, software and administrator operations.",
            ["administrator", "admin", "windows", "service", "install", "uninstall", "registry", "firewall"]),
        new("GENERAL", "GENERAL", "General reasoning and computer work when no specialized route is required.",
            ["general"])
    ];

    public static IReadOnlyList<string> NormalizeTags(IEnumerable<string>? tags)
    {
        var valid = Definitions.Select(x => x.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return (tags ?? []).Select(NormalizeTag).Where(valid.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(5).ToArray();
    }

    public static IReadOnlyList<string> SuggestTags(string? text)
    {
        var source = text ?? string.Empty;
        var matches = Definitions.Where(x => x.Key != "GENERAL")
            .Select(x => new { x.Key, Score = x.Keywords.Count(k => source.Contains(k, StringComparison.OrdinalIgnoreCase)) })
            .Where(x => x.Score > 0).OrderByDescending(x => x.Score).ThenBy(x => x.Key)
            .Take(4).Select(x => x.Key).ToList();
        if (matches.Count == 0) matches.Add("GENERAL");
        return matches;
    }

    public static IReadOnlyList<string> GetProfileTags(CapabilityProfile profile)
    {
        var stored = NormalizeTags(profile.Tags);
        if (stored.Count > 0) return stored;
        var inferred = SuggestTags($"{profile.Name} {profile.Endpoint} {profile.Resource} {profile.AllowedScopes}").ToList();
        var kindTag = profile.Kind switch
        {
            CapabilityKind.Email => "EMAIL",
            CapabilityKind.Folder => "FILES",
            CapabilityKind.WindowsAdministration => "SYSTEM_ADMIN",
            CapabilityKind.DesktopCapture => "SYSTEM_ADMIN",
            _ => null
        };
        if (kindTag is not null && !inferred.Contains(kindTag, StringComparer.OrdinalIgnoreCase)) inferred.Insert(0, kindTag);
        return NormalizeTags(inferred);
    }

    public static string DescribeProfile(CapabilityProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.DictionaryDescription)) return profile.DictionaryDescription.Trim();
        var descriptions = GetProfileTags(profile).Select(tag => Definitions.First(x => x.Key == tag).Description);
        return string.Join(" ", descriptions.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    public static string BuildRoutingContext(IEnumerable<string>? selectedTags, IEnumerable<CapabilityProfile> profiles)
    {
        var tags = NormalizeTags(selectedTags);
        var profileList = profiles.ToList();
        var primary = profileList.Where(p => GetProfileTags(p).Intersect(tags, StringComparer.OrdinalIgnoreCase).Any()).ToList();
        var fallback = profileList.Except(primary).ToList();
        static string Line(CapabilityProfile p) =>
            $"- {p.Id}: {p.Name} | tags={string.Join(',', GetProfileTags(p))} | purpose={DescribeProfile(p)} | scopes={p.AllowedScopes ?? "not specified"}";
        return "DAGNUE CAPABILITY ROUTE\n" +
               $"Selected tags: {string.Join(", ", tags)}\n" +
               "Check these matching capabilities first:\n" +
               (primary.Count == 0 ? "- No direct capability match; use the fallback route.\n" : string.Join("\n", primary.Select(Line)) + "\n") +
               "Only if the tagged route is insufficient, inspect these fallback capabilities:\n" +
               (fallback.Count == 0 ? "- None" : string.Join("\n", fallback.Select(Line)));
    }

    private static string NormalizeTag(string value) => value.Trim().Replace(' ', '_').ToUpperInvariant();
}
