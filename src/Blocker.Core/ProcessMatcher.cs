namespace Blocker.Core;

public static class ProcessMatcher
{
    public static IReadOnlyList<string> ProtectedProcessesList => ProtectedProcesses.Names;

    public static string NormalizeProcessName(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return string.Empty;
        }

        string trimmed = processName.Trim();
        if (trimmed.EndsWith(".mui", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4].Trim();
        }
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4].Trim();
        }

        return trimmed;
    }

    public static bool IsBlocked(string? processName, IEnumerable<string?>? blockedList, string? originalFileName = null)
    {
        if (blockedList == null)
        {
            return false;
        }

        // Apply ProtectedProcesses check to BOTH names
        if (ProtectedProcesses.IsProtected(processName))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(originalFileName) && ProtectedProcesses.IsProtected(originalFileName))
        {
            return false;
        }

        string normalizedProcessName = NormalizeProcessName(processName);
        string normalizedOriginalFileName = NormalizeProcessName(originalFileName);

        if (string.IsNullOrEmpty(normalizedProcessName) && string.IsNullOrEmpty(normalizedOriginalFileName))
        {
            return false;
        }

        foreach (string? item in blockedList)
        {
            if (string.IsNullOrWhiteSpace(item))
            {
                continue;
            }

            string normalizedBlocked = NormalizeProcessName(item);
            if (string.IsNullOrEmpty(normalizedBlocked))
            {
                continue;
            }

            if (!string.IsNullOrEmpty(normalizedProcessName) &&
                string.Equals(normalizedProcessName, normalizedBlocked, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (!string.IsNullOrEmpty(normalizedOriginalFileName) &&
                string.Equals(normalizedOriginalFileName, normalizedBlocked, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
