namespace Blocker.Core;

public static class UiLogic
{
    public static string FormatCountdown(TimeSpan remaining)
    {
        if (remaining <= TimeSpan.Zero)
        {
            return "00:00";
        }

        if (remaining.TotalDays >= 365)
        {
            int years = (int)(remaining.TotalDays / 365);
            int days = (int)(remaining.TotalDays % 365);
            return days > 0 ? $"{years}y {days}d" : $"{years}y";
        }

        if (remaining.TotalDays >= 30)
        {
            int months = (int)(remaining.TotalDays / 30);
            int days = (int)(remaining.TotalDays % 30);
            return days > 0 ? $"{months}mo {days}d" : $"{months}mo";
        }

        if (remaining.TotalDays >= 7)
        {
            int weeks = (int)(remaining.TotalDays / 7);
            int days = (int)(remaining.TotalDays % 7);
            return days > 0 ? $"{weeks}w {days}d" : $"{weeks}w";
        }

        if (remaining.TotalDays >= 1)
        {
            int days = (int)remaining.TotalDays;
            int hours = remaining.Hours;
            return hours > 0 ? $"{days}d {hours}h" : $"{days}d";
        }

        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours:D2}:{remaining.Minutes:D2}:{remaining.Seconds:D2}";
        }

        return $"{remaining.Minutes:D2}:{remaining.Seconds:D2}";
    }

    public static string FormatDurationDescription(long minutes)
    {
        if (minutes <= 0) return "0 minutes";
        if (minutes % 525600L == 0) return $"{minutes / 525600L} year(s)";
        if (minutes % 43200L == 0) return $"{minutes / 43200L} month(s)";
        if (minutes % 10080L == 0) return $"{minutes / 10080L} week(s)";
        if (minutes % 1440L == 0) return $"{minutes / 1440L} day(s)";
        if (minutes % 60L == 0) return $"{minutes / 60L} hour(s)";
        return $"{minutes} minute(s)";
    }

    public static string FormatCountdown(long totalSeconds)
    {
        return FormatCountdown(TimeSpan.FromSeconds(Math.Max(0, totalSeconds)));
    }

    public static string FormatTime(DateTime dateTime, string timeFormat = "24h")
    {
        return string.Equals(timeFormat, "12h", StringComparison.OrdinalIgnoreCase)
            ? dateTime.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture)
            : dateTime.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string FormatTimeWithDay(DateTime dateTime, string timeFormat = "24h")
    {
        return string.Equals(timeFormat, "12h", StringComparison.OrdinalIgnoreCase)
            ? dateTime.ToString("ddd h:mm tt", System.Globalization.CultureInfo.InvariantCulture)
            : dateTime.ToString("ddd HH:mm", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static bool ValidateProcessInput(string? input, out string? normalized, out string? error)
    {
        normalized = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Process name cannot be empty.";
            return false;
        }

        string trimmed = input.Trim();
        // If a file path was provided, extract just the file name
        string fileName = Path.GetFileName(trimmed);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = trimmed;
        }

        string cleanName = ProcessMatcher.NormalizeProcessName(fileName).ToLowerInvariant();
        if (string.IsNullOrEmpty(cleanName))
        {
            error = "Invalid process name.";
            return false;
        }

        if (ProtectedProcesses.IsProtected(cleanName))
        {
            error = $"'{fileName}' is a protected system process and cannot be blocked.";
            return false;
        }

        // Standardize with .exe extension if none was present
        normalized = cleanName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? cleanName : $"{cleanName}.exe";
        error = null;
        return true;
    }

    public static bool ValidateDomainInput(string? input, out string? normalized, out string? error)
    {
        normalized = null;

        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Domain cannot be empty.";
            return false;
        }

        if (HostsFileBuilder.TryNormalizeDomain(input, out string norm))
        {
            normalized = norm;
            error = null;
            return true;
        }

        error = "Invalid domain format. Enter a domain like 'example.com' (no protocols, paths, wildcards, spaces, or localhost).";
        return false;
    }

    public static bool CanEditProfile(Profile? profile, StatusData? activeStatus, out string? reason)
    {
        if (profile == null)
        {
            reason = "No profile selected.";
            return false;
        }

        if (activeStatus != null && activeStatus.Active && activeStatus.Locked)
        {
            if (string.Equals(profile.Name, activeStatus.Profile, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"Profile '{profile.Name}' cannot be edited while an active locked session is using it.";
                return false;
            }
        }

        reason = null;
        return true;
    }

    public static bool CanDeleteProfile(Profile? profile, StatusData? activeStatus, out string? reason)
    {
        if (profile == null)
        {
            reason = "No profile selected.";
            return false;
        }

        if (string.Equals(profile.Name, "Default", StringComparison.OrdinalIgnoreCase))
        {
            reason = "The 'Default' profile cannot be deleted.";
            return false;
        }

        if (activeStatus != null && activeStatus.Active && activeStatus.Locked)
        {
            if (string.Equals(profile.Name, activeStatus.Profile, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"Profile '{profile.Name}' cannot be deleted while an active locked session is using it.";
                return false;
            }
        }

        reason = null;
        return true;
    }

    public static bool CanStartSession(StatusData? activeStatus, string? profileName, int minutes, out string? error)
    {
        if (activeStatus != null && activeStatus.Active)
        {
            error = "A session is already active. Stop the current session before starting a new one.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(profileName))
        {
            error = "Please select a profile.";
            return false;
        }

        if (minutes <= 0)
        {
            error = "Duration must be greater than zero.";
            return false;
        }

        error = null;
        return true;
    }

    public static bool CalculateHostsBlockingOk(bool serviceElevated, bool hostsApplySuccess, IEnumerable<string>? activeBlockedDomains = null)
    {
        if (!hostsApplySuccess)
        {
            return false;
        }

        if (!serviceElevated && activeBlockedDomains != null && activeBlockedDomains.Any())
        {
            return false;
        }

        return true;
    }

    public static bool ValidateProfileSave(
        string? profileName,
        int appsCount,
        int sitesCount,
        IEnumerable<string>? existingNames,
        string? originalName,
        out string? error)
    {
        return ValidateProfileSave(profileName, appsCount, sitesCount, existingNames, originalName, false, out error);
    }

    public static bool ValidateProfileSave(
        string? profileName,
        int appsCount,
        int sitesCount,
        IEnumerable<string>? existingNames,
        string? originalName,
        bool blockAllWebsites,
        out string? error)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            error = "Profile name cannot be empty.";
            return false;
        }

        string trimmedName = profileName.Trim();

        if (existingNames != null)
        {
            foreach (var existing in existingNames)
            {
                if (string.Equals(existing?.Trim(), trimmedName, StringComparison.OrdinalIgnoreCase))
                {
                    if (originalName == null || !string.Equals(existing?.Trim(), originalName.Trim(), StringComparison.OrdinalIgnoreCase))
                    {
                        error = $"A profile named '{trimmedName}' already exists.";
                        return false;
                    }
                }
            }
        }

        if (appsCount <= 0 && sitesCount <= 0 && !blockAllWebsites)
        {
            error = "A profile must contain at least one blocked app or website.";
            return false;
        }

        error = null;
        return true;
    }

    public static string FormatAppsBadge(int appsCount) =>
        appsCount == 1 ? "1 app" : $"{appsCount} apps";

    public static string FormatSitesBadge(int sitesCount, bool blockAll = false) =>
        blockAll ? "all sites" : (sitesCount == 1 ? "1 site" : $"{sitesCount} sites");

    public static string FormatExceptionsBadge(int exceptionsCount) =>
        exceptionsCount == 1 ? "1 exception" : $"{exceptionsCount} exceptions";

    public static string FormatCardSummary(int appsCount, int sitesCount, int exceptionsCount, bool blockAll = false)
    {
        string appsStr = FormatAppsBadge(appsCount);
        string sitesStr = FormatSitesBadge(sitesCount, blockAll);
        if (exceptionsCount > 0)
        {
            string exStr = FormatExceptionsBadge(exceptionsCount);
            return $"{appsStr}, {sitesStr}, {exStr}";
        }
        return $"{appsStr}, {sitesStr}";
    }

    public const string ExtensionNoteText = "Page rules, exceptions and block-all mode are enforced by the browser extension (coming in the next update).";

    public static bool RequiresExtensionNote(bool blockAllWebsites, IEnumerable<WebRule>? webRules)
    {
        if (blockAllWebsites)
        {
            return true;
        }

        if (webRules == null)
        {
            return false;
        }

        foreach (var rule in webRules)
        {
            if (rule.Allow)
            {
                return true;
            }

            string p = rule.Pattern?.Trim() ?? string.Empty;
            if (p.Contains('/') || p.Contains('*') || p.Contains('$'))
            {
                return true;
            }
        }

        return false;
    }

    public static string TestUrlAgainstRules(string? url, bool blockAllWebsites, IEnumerable<WebRule>? webRules)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return "Enter a URL above to test";
        }

        var tempProfile = new Profile
        {
            Name = "Test",
            BlockAllWebsites = blockAllWebsites,
            WebRules = webRules?.ToList() ?? new List<WebRule>()
        };

        if (!WebRuleMatcher.TryParseUrl(url, out _, out _))
        {
            return "Enter a valid http:// or https:// URL to test";
        }

        var res = WebRuleMatcher.Evaluate(tempProfile, url);
        if (res.Blocked)
        {
            return $"Blocked by {res.MatchedBlockPattern}";
        }
        else if (res.MatchedExceptionPattern != null)
        {
            return $"Allowed by exception {res.MatchedExceptionPattern}";
        }
        else
        {
            return "Not blocked";
        }
    }

    public static bool TryAddRuleToList(
        IList<string> existingList,
        string? input,
        out string normalized,
        out string? error)
    {
        normalized = string.Empty;
        if (!WebRuleMatcher.TryNormalizePattern(input, out normalized, out error))
        {
            return false;
        }

        string norm = normalized;
        if (existingList.Any(p => string.Equals(p, norm, StringComparison.OrdinalIgnoreCase)))
        {
            error = $"'{normalized}' is already in the list.";
            return false;
        }

        existingList.Add(normalized);
        error = null;
        return true;
    }

    public static bool RemoveRuleFromList(IList<string> list, string? pattern)
    {
        if (string.IsNullOrEmpty(pattern)) return false;
        for (int i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i], pattern, StringComparison.OrdinalIgnoreCase))
            {
                list.RemoveAt(i);
                return true;
            }
        }
        return false;
    }

    public static bool CanEnableSystemWide(bool blockAllWebsites, IEnumerable<WebRule>? webRules, out string? hint)
    {
        if (blockAllWebsites)
        {
            hint = "System-wide blocking is not supported with block-all mode.";
            return false;
        }

        var list = webRules?.ToList() ?? new List<WebRule>();
        var blockRules = list.Where(r => !r.Allow).ToList();
        if (blockRules.Count == 0)
        {
            hint = "System-wide blocking requires at least one domain rule.";
            return false;
        }

        foreach (var rule in list)
        {
            if (rule.Allow)
            {
                hint = "System-wide blocking cannot enforce exceptions.";
                return false;
            }
            string p = rule.Pattern?.Trim() ?? string.Empty;
            if (p.Contains('/') || p.Contains('*') || p.Contains('$'))
            {
                hint = "System-wide blocking only supports plain domain rules.";
                return false;
            }
        }

        hint = null;
        return true;
    }

    public static string FormatExtensionStatusSegment(IEnumerable<string>? connectedBrowsers)
    {
        var list = connectedBrowsers?.Where(b => !string.IsNullOrWhiteSpace(b)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new();
        if (list.Count > 0)
        {
            return $"Extension: connected ({string.Join(", ", list)})";
        }
        return "Extension: not connected";
    }

    public static List<string> FormatWarningBanners(
        bool fallbackActive,
        IEnumerable<string>? missingBrowsers,
        bool blockAllUncovered,
        Dictionary<string, bool>? perBrowserIncognito)
    {
        var banners = new List<string>();

        var missing = missingBrowsers?.Where(b => !string.IsNullOrWhiteSpace(b)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? new();
        string missingStr = missing.Count > 0 ? string.Join(", ", missing) : "browsers";

        if (fallbackActive)
        {
            banners.Add($"The browser extension is missing in {missingStr}. Websites in active blocks are blocked system-wide until it is restored.");
        }

        if (blockAllUncovered)
        {
            banners.Add($"Block-all mode cannot be enforced in {missingStr} without the extension.");
        }

        if (perBrowserIncognito != null)
        {
            foreach (var (browser, incognitoAllowed) in perBrowserIncognito)
            {
                if (!incognitoAllowed)
                {
                    banners.Add("Allow the extension in incognito or it can be bypassed.");
                    break;
                }
            }
        }

        return banners;
    }
}
