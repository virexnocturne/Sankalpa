using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Blocker.Core;

public static class CommandHandler
{
    public static Response Handle(Request? request, IBlockStore store, IClientIdentity? clientIdentity = null)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Command))
        {
            return Response.Fail("Empty or invalid request.");
        }

        string cmd = request.Command.ToUpperInvariant();

        if (clientIdentity == null || !clientIdentity.IsAuthenticated)
        {
            if (cmd == "PING")
            {
                return HandlePing();
            }

            return Response.Fail("Unauthorized: Authentication required.");
        }
        try
        {
            return cmd switch
            {
                "PING" => HandlePing(),
                "LISTPROFILES" => HandleListProfiles(store),
                "GETPROFILE" => HandleGetProfile(request, store),
                "SAVEPROFILE" => HandleSaveProfile(request, store),
                "DELETEPROFILE" => HandleDeleteProfile(request, store),
                "ENABLEPROFILE" => HandleEnableProfile(request, store),
                "DISABLEPROFILE" => HandleDisableProfile(request, store),
                "GETUNLOCKCHALLENGE" or "UNLOCKCHALLENGE" => HandleGetUnlockChallenge(request, store),
                "STARTSESSION" => HandleStartSession(request, store),
                "STOPSESSION" => HandleStopSession(request, store, clientIdentity),
                "GETSTATUS" => HandleGetStatus(store),
                "HOSTSSTATUS" => HandleHostsStatus(store),
                "CLEANHOSTS" => HandleCleanHosts(store, clientIdentity),
                "GETEVENTS" => HandleGetEvents(request, store),
                "GETACTIVEWEBRULES" => HandleGetActiveWebRules(store),
                "EXTHEARTBEAT" => HandleExtHeartbeat(request, store),
                "EMERGENCYUNLOCK" => HandleEmergencyUnlock(store, clientIdentity),
                "GETDNSCONFIG" => HandleGetDnsConfig(store),
                "SAVEDNSCONFIG" => HandleSaveDnsConfig(request, store, clientIdentity),
                "LOCKDNS" => HandleLockDns(request, store),
                "UNLOCKDNS" => HandleUnlockDns(request, store),
                "RENAMEPROFILE" => HandleRenameProfile(request, store),
                "ADDWEBSITE" => HandleAddWebsite(request, store),
                "GETDNSCHALLENGE" => HandleGetDnsChallenge(store),
                "REQUESTDELAYEDUNLOCK" => HandleRequestDelayedUnlock(request, store),
                "CANCELDELAYEDUNLOCK" => HandleCancelDelayedUnlock(request),
                "STARTMEDITATION" => HandleStartMeditation(request, store),
                "STOPMEDITATION" => HandleStopMeditation(request, store),
                "GETMEDITATIONSTATUS" => HandleGetMeditationStatus(store),
                "SAVEMEDITATIONNOTE" => HandleSaveMeditationNote(request, store),
                "GETMEDITATIONHISTORY" => HandleGetMeditationHistory(request, store),
                "SAVEMEDITATIONSCHEDULE" => HandleSaveMeditationSchedule(request, store),
                "GETMEDITATIONSCHEDULES" => HandleGetMeditationSchedules(store),
                "DELETEMEDITATIONSCHEDULE" => HandleDeleteMeditationSchedule(request, store),
                "SNOOZEMEDITATIONSCHEDULE" => HandleSnoozeMeditationSchedule(request, store),
                "CONFIRMTIMERREADY" => HandleConfirmTimerReady(request, store),
                _ => Response.Fail($"Unknown command: '{request.Command}'.")
            };
        }
        catch (Exception ex)
        {
            return Response.Fail($"Internal error processing request: {ex.Message}");
        }
    }

    private static Response HandlePing()
    {
        return Response.Success("pong");
    }

    private static Response HandleListProfiles(IBlockStore store)
    {
        var profiles = store.ListProfiles();
        return Response.Success(JsonSerializer.Serialize(profiles));
    }

    private static Response HandleGetProfile(Request request, IBlockStore store)
    {
        string? name = GetArg(request, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return Response.Fail("Missing required argument: 'name'.");
        }

        var profile = store.GetProfileByName(name);
        if (profile == null)
        {
            return Response.Fail($"Profile '{name}' not found.");
        }

        return Response.Success(JsonSerializer.Serialize(profile));
    }

    private static Response HandleSaveProfile(Request request, IBlockStore store)
    {
        string? name = GetArg(request, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return Response.Fail("Profile name cannot be empty.");
        }

        string trimmedName = name.Trim();
        string? oldName = GetArg(request, "old_name") ?? GetArg(request, "oldName");
        Profile? existing = !string.IsNullOrWhiteSpace(oldName)
            ? store.GetProfileByName(oldName)
            : store.GetProfileByName(trimmedName);

        if (existing == null && !string.IsNullOrWhiteSpace(oldName))
        {
            existing = store.GetProfileByName(trimmedName);
        }

        var activeSession = store.GetActiveSession(existing?.Id);
        bool isLockedSession = (activeSession != null && activeSession.Locked && existing != null && existing.Id == activeSession.ProfileId);

        // Parse LockMode if specified
        string? lockModeStr = GetArg(request, "lock_mode") ?? GetArg(request, "lockMode") ?? GetArg(request, "mode");
        LockMode targetLockMode = lockModeStr != null ? ParseLockMode(lockModeStr) : (existing?.LockMode ?? LockMode.None);
        if (existing != null && DelayedUnlockManager.IsDelayedUnlockActive(existing.Name))
        {
            return Response.Fail($"Refused: Cannot modify profile '{trimmedName}' while an active delayed unlock timer is running.");
        }
        string? delayedMinStr = GetArg(request, "delayed_unlock_minutes") ?? GetArg(request, "delayedUnlockMinutes");
        int targetDelayedMinutes = 15;
        if (!string.IsNullOrWhiteSpace(delayedMinStr) && int.TryParse(delayedMinStr, out int dm))
        {
            targetDelayedMinutes = dm >= 15 ? dm : 15;
        }
        else if (existing != null)
        {
            targetDelayedMinutes = existing.DelayedUnlockMinutes;
        }

        // Parse Schedule if specified
        string? schedStr = GetArg(request, "schedule");
        List<ScheduleRule> targetSchedule;
        if (schedStr != null)
        {
            if (!TryParseSchedule(schedStr, out targetSchedule, out string schedErr))
            {
                return Response.Fail($"Invalid schedule: {schedErr}");
            }
        }
        else
        {
            targetSchedule = existing?.Schedule != null ? new List<ScheduleRule>(existing.Schedule) : new List<ScheduleRule>();
        }

        // Validation: LockMode.Locked requires at least one schedule rule
        if (targetLockMode == LockMode.Locked && targetSchedule.Count == 0)
        {
            return Response.Fail("Locked mode requires at least one schedule rule.");
        }

        // Parse Enabled if specified - default to true for new profiles
        string? enabledStr = GetArg(request, "enabled");
        bool targetEnabled = enabledStr != null
            ? (bool.TryParse(enabledStr, out bool b) && b)
            : (existing?.Enabled ?? true);

        // Password resolution & Lock checks
        string? passwordArg = GetArg(request, "password") ?? GetArg(request, "new_password") ?? GetArg(request, "newPassword");
        string? currentPassword = GetArg(request, "current_password") ?? GetArg(request, "currentPassword") ?? GetArg(request, "unlock_password");

        string? finalSalt = existing?.PasswordSalt;
        string? finalHash = existing?.PasswordHash;

        if (existing != null)
        {
            var activeSessions = store.GetActiveSessions();
            bool isLockedNow = ProfileStateLogic.IsProfileLockedNowWithSessions(existing, activeSessions, DateTime.Now);

            // Switching away from Password mode counts as protected operation
            if (existing.LockMode == LockMode.Password && targetLockMode != LockMode.Password)
            {
                if (PasswordSecurity.IsLockedOut(existing.Name, out int remSec))
                {
                    return Response.Fail($"Refused: Too many failed password attempts. Profile '{existing.Name}' is locked out for {remSec} seconds.");
                }

                if (string.IsNullOrEmpty(currentPassword) || !PasswordSecurity.VerifyPassword(currentPassword, existing.PasswordSalt, existing.PasswordHash))
                {
                    PasswordSecurity.RecordFailedAttempt(existing.Name);
                    return Response.Fail("Refused: Current password required to change lock mode from Password.");
                }
                PasswordSecurity.RecordSuccessfulAttempt(existing.Name);
                finalSalt = null;
                finalHash = null;
            }

            // Setting/changing password while locked now needs current password
            if (existing.LockMode == LockMode.Password && targetLockMode == LockMode.Password && !string.IsNullOrEmpty(passwordArg))
            {
                if (isLockedNow)
                {
                    if (PasswordSecurity.IsLockedOut(existing.Name, out int remSec))
                    {
                        return Response.Fail($"Refused: Too many failed password attempts. Profile '{existing.Name}' is locked out for {remSec} seconds.");
                    }

                    if (string.IsNullOrEmpty(currentPassword) || !PasswordSecurity.VerifyPassword(currentPassword, existing.PasswordSalt, existing.PasswordHash))
                    {
                        PasswordSecurity.RecordFailedAttempt(existing.Name);
                        return Response.Fail("Refused: Current password required to change password while profile is active.");
                    }
                    PasswordSecurity.RecordSuccessfulAttempt(existing.Name);
                }

                PasswordSecurity.HashPassword(passwordArg, out finalSalt, out finalHash);
            }
            else if (targetLockMode == LockMode.Password && !string.IsNullOrEmpty(passwordArg))
            {
                // Password being set initially
                PasswordSecurity.HashPassword(passwordArg, out finalSalt, out finalHash);
            }
        }
        else
        {
            // Creating brand new profile
            if (targetLockMode == LockMode.Password)
            {
                if (string.IsNullOrEmpty(passwordArg))
                {
                    return Response.Fail("A password is required for Password lock mode.");
                }
                PasswordSecurity.HashPassword(passwordArg, out finalSalt, out finalHash);
            }
        }

        // Validate that Password mode has a hash
        if (targetLockMode == LockMode.Password && string.IsNullOrEmpty(finalHash))
        {
            return Response.Fail("A password is required for Password lock mode.");
        }

        // Processes and Domains
        string? procStr = GetArg(request, "processes") ?? GetArg(request, "block");
        List<string> processes = procStr != null
            ? ParseList(procStr).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            : (existing?.BlockedProcesses ?? new List<string>());

        string? domStr = GetArg(request, "domains") ?? GetArg(request, "site");
        List<string> domains;
        if (domStr != null)
        {
            domains = new List<string>();
            foreach (var d in ParseList(domStr))
            {
                if (HostsFileBuilder.TryNormalizeDomain(d, out string norm))
                {
                    domains.Add(norm);
                }
            }
            domains = domains.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
        else
        {
            domains = existing?.BlockedDomains ?? new List<string>();
        }

        // WebRules and BlockAllWebsites
        string? blockAllStr = GetArg(request, "block_all") ?? GetArg(request, "blockAll") ?? GetArg(request, "block_all_websites") ?? GetArg(request, "blockAllWebsites");
        bool targetBlockAll = blockAllStr != null
            ? (bool.TryParse(blockAllStr, out bool bAll) && bAll)
            : (existing?.BlockAllWebsites ?? false);

        string? webRulesJson = GetArg(request, "webrules") ?? GetArg(request, "web_rules");
        List<WebRule> rawWebRules;
        if (webRulesJson != null)
        {
            try
            {
                var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                rawWebRules = JsonSerializer.Deserialize<List<WebRule>>(webRulesJson, jsonOpts) ?? new List<WebRule>();
            }
            catch (Exception ex)
            {
                return Response.Fail($"Invalid web rules format: {ex.Message}");
            }
        }
        else if (domStr != null)
        {
            rawWebRules = domains.Select(d => new WebRule { Pattern = d, Allow = false }).ToList();
        }
        else
        {
            rawWebRules = existing?.WebRules != null ? new List<WebRule>(existing.WebRules) : new List<WebRule>();
        }

        if (rawWebRules.Count > 500)
        {
            return Response.Fail("A profile cannot have more than 500 website rules.");
        }

        var normalizedWebRules = new List<WebRule>();
        foreach (var rule in rawWebRules)
        {
            if (string.IsNullOrWhiteSpace(rule.Pattern))
            {
                return Response.Fail("Website pattern cannot be empty.");
            }

            if (!WebRuleMatcher.TryNormalizePattern(rule.Pattern, out string normPattern, out string? patternErr))
            {
                return Response.Fail($"Invalid website pattern '{rule.Pattern}': {patternErr}");
            }

            normalizedWebRules.Add(new WebRule { Pattern = normPattern, Allow = rule.Allow });
        }

        // Deduplicate identical rules
        normalizedWebRules = normalizedWebRules
            .GroupBy(r => (r.Pattern, r.Allow))
            .Select(g => g.First())
            .ToList();

        // Derived BlockedDomains
        var derivedDomains = WebRuleMatcher.DeriveBlockedDomains(normalizedWebRules, targetBlockAll);

        string? systemWideStr = GetArg(request, "system_wide") ?? GetArg(request, "systemWide");
        bool targetSystemWide = systemWideStr != null
            ? (bool.TryParse(systemWideStr, out bool sw) && sw)
            : (existing?.SystemWide ?? false);

        string? allowanceJson = GetArg(request, "allowance");
        AllowanceConfig targetAllowance;
        if (allowanceJson != null)
        {
            try
            {
                targetAllowance = JsonSerializer.Deserialize<AllowanceConfig>(allowanceJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AllowanceConfig();
            }
            catch
            {
                targetAllowance = existing?.Allowance ?? new AllowanceConfig();
            }
        }
        else
        {
            targetAllowance = existing?.Allowance ?? new AllowanceConfig();
        }

        if (existing != null)
        {
            bool isAdditiveRequested = string.Equals(GetArg(request, "additive_only") ?? GetArg(request, "additiveOnly"), "true", StringComparison.OrdinalIgnoreCase);

            if (isAdditiveRequested)
            {
                var additiveErr = ValidateAdditiveOnly(
                    existing,
                    processes,
                    domains,
                    normalizedWebRules,
                    targetBlockAll,
                    targetEnabled,
                    targetLockMode,
                    targetSchedule,
                    schedStr,
                    targetAllowance);

                if (additiveErr != null)
                {
                    return Response.Fail($"Refused: Additive-only edit for profile '{trimmedName}' failed: {additiveErr}.");
                }
            }
            else
            {
                if (isLockedSession)
                {
                    return Response.Fail($"Refused: Cannot edit profile '{trimmedName}' while an active LOCKED session is using it.");
                }

                var unlockErr = VerifyUnlock(existing, request, activeSession, store);
                if (unlockErr != null)
                {
                    return unlockErr;
                }
            }
        }

        var profile = new Profile
        {
            Id = existing?.Id ?? 0,
            Name = trimmedName,
            BlockedProcesses = processes,
            BlockedDomains = derivedDomains,
            WebRules = normalizedWebRules,
            BlockAllWebsites = targetBlockAll,
            SystemWide = targetSystemWide,
            Enabled = targetEnabled,
            Schedule = targetSchedule,
            Allowance = targetAllowance,
            LockMode = targetLockMode,
            PasswordSalt = finalSalt,
            PasswordHash = finalHash,
            DelayedUnlockMinutes = targetDelayedMinutes
        };

        var saved = store.SaveProfile(profile);
        return Response.Success(JsonSerializer.Serialize(saved));
    }

    private static string? ValidateAdditiveOnly(
        Profile existing,
        List<string> targetProcesses,
        List<string> targetDomains,
        List<WebRule> targetWebRules,
        bool targetBlockAllWebsites,
        bool targetEnabled,
        LockMode targetLockMode,
        List<ScheduleRule> targetSchedule,
        string? schedStr,
        AllowanceConfig? targetAllowance = null)
    {
        // 1. Existing blocked applications cannot be removed
        if (existing.BlockedProcesses != null)
        {
            foreach (var p in existing.BlockedProcesses)
            {
                if (!targetProcesses.Contains(p, StringComparer.OrdinalIgnoreCase))
                {
                    return $"cannot remove existing blocked application '{p}'";
                }
            }
        }

        // 2. Existing blocked domains cannot be removed
        if (existing.BlockedDomains != null)
        {
            foreach (var d in existing.BlockedDomains)
            {
                if (!targetDomains.Contains(d, StringComparer.OrdinalIgnoreCase))
                {
                    return $"cannot remove existing blocked website '{d}'";
                }
            }
        }

        // 3. Existing blocked web rules (Allow == false) cannot be removed
        if (existing.WebRules != null)
        {
            var existingBlocked = existing.WebRules
                .Where(r => !r.Allow)
                .Select(r => r.Pattern.Trim())
                .ToList();

            var targetBlocked = targetWebRules
                .Where(r => !r.Allow)
                .Select(r => r.Pattern.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var pat in existingBlocked)
            {
                if (!targetBlocked.Contains(pat))
                {
                    return $"cannot remove existing blocked website '{pat}'";
                }
            }

            // 4. EXCEPTIONS CANNOT BE ADDED!
            var existingExceptions = existing.WebRules
                .Where(r => r.Allow)
                .Select(r => r.Pattern.Trim())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var targetExceptions = targetWebRules
                .Where(r => r.Allow)
                .Select(r => r.Pattern.Trim())
                .ToList();

            foreach (var ex in targetExceptions)
            {
                if (!existingExceptions.Contains(ex))
                {
                    return "exceptions cannot be added while block is locked";
                }
            }

            // 5. Existing exceptions cannot be removed while locked either
            if (existingExceptions.Count != targetExceptions.Count)
            {
                return "cannot modify exceptions while block is locked";
            }
        }
        else
        {
            // If there were no web rules before, NO exceptions can be added while locked!
            if (targetWebRules.Any(r => r.Allow))
            {
                return "exceptions cannot be added while block is locked";
            }
        }

        // 6. BlockAllWebsites cannot be disabled if it was enabled
        if (existing.BlockAllWebsites && !targetBlockAllWebsites)
        {
            return "cannot disable 'Block all websites' while block is locked";
        }

        // 7. Profile cannot be disabled
        if (existing.Enabled && !targetEnabled)
        {
            return "cannot disable profile while block is locked";
        }

        // 8. Schedule cannot be modified to weaken
        if (schedStr != null && !ScheduleLogic.AreSchedulesEquivalent(existing.Schedule, targetSchedule))
        {
            return "cannot modify schedule while block is locked";
        }

        // 9. LockMode cannot be modified
        if (targetLockMode != existing.LockMode)
        {
            return "cannot change lock mode while block is locked";
        }

        // 10. Allowance cannot be enabled or increased while locked
        if (targetAllowance != null && existing.Allowance != null)
        {
            if (!existing.Allowance.Enabled && targetAllowance.Enabled)
            {
                return "cannot enable daily allowance while block is locked";
            }
            if (targetAllowance.DailyAllowanceMinutes > existing.Allowance.DailyAllowanceMinutes)
            {
                return "cannot increase daily allowance minutes while block is locked";
            }
            if (targetAllowance.OnlyWithinSchedule != existing.Allowance.OnlyWithinSchedule)
            {
                return "cannot modify allowance schedule constraint while block is locked";
            }
        }
        else if (targetAllowance != null && targetAllowance.Enabled && (existing.Allowance == null || !existing.Allowance.Enabled))
        {
            return "cannot enable daily allowance while block is locked";
        }

        return null;
    }

    private static Response HandleDeleteProfile(Request request, IBlockStore store)
    {
        string? name = GetArg(request, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return Response.Fail("Missing required argument: 'name'.");
        }

        var profile = store.GetProfileByName(name);
        if (profile == null)
        {
            return Response.Fail($"Profile '{name}' not found.");
        }

        if (string.Equals(profile.Name, "Default", StringComparison.OrdinalIgnoreCase))
        {
            return Response.Fail("The 'Default' profile cannot be deleted.");
        }

        var runningSessions = store.GetActiveSessions();
        var activeSession = runningSessions.FirstOrDefault(s => s.ProfileId == profile.Id) ?? store.GetActiveSession(profile.Id);
        if (activeSession != null)
        {
            if (activeSession.Locked)
            {
                return Response.Fail($"Cannot delete profile '{name}' while an active LOCKED session is using it.");
            }
            return Response.Fail($"Cannot delete profile '{name}' while an active session is running for it. Please stop the session first.");
        }

        if (profile.LockMode == LockMode.HardLocked)
        {
            if (store?.IsEmergencyUnlockPresent() != true)
            {
                return Response.Fail($"Refused: Profile '{profile.Name}' is hard-locked and cannot be modified or unlocked without emergency recovery.");
            }
        }

        if (profile.Enabled)
        {
            return Response.Fail($"Cannot delete profile '{name}' while it is turned On. Please turn it Off first.");
        }

        var unlockErr = VerifyUnlock(profile, request, activeSession, store);
        if (unlockErr != null)
        {
            return unlockErr;
        }

        bool deleted = store.DeleteProfileByName(name);
        if (deleted)
        {
            return Response.Success($"Profile '{name}' deleted.");
        }

        return Response.Fail($"Failed to delete profile '{name}'.");
    }

    private static Response HandleRenameProfile(Request request, IBlockStore store)
    {
        // 1. A rename request that carries other changes is rejected.
        if (request.Args != null)
        {
            foreach (var key in request.Args.Keys)
            {
                if (!string.Equals(key, "id", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(key, "profile_id", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(key, "profileId", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(key, "old_name", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(key, "oldName", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(key, "name", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(key, "new_name", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(key, "newName", StringComparison.OrdinalIgnoreCase))
                {
                    return Response.Fail("Rename operation cannot carry other modifications.");
                }
            }
        }

        // 2. Resolve profile by ID or old name
        string? idStr = GetArg(request, "id") ?? GetArg(request, "profile_id") ?? GetArg(request, "profileId");
        Profile? profile = null;
        if (!string.IsNullOrWhiteSpace(idStr) && int.TryParse(idStr, out int id))
        {
            profile = store.GetProfileById(id);
        }

        if (profile == null)
        {
            string? oldName = GetArg(request, "old_name") ?? GetArg(request, "oldName");
            if (!string.IsNullOrWhiteSpace(oldName))
            {
                profile = store.GetProfileByName(oldName);
            }
        }

        if (profile == null)
        {
            return Response.Fail("Profile not found.");
        }

        // 3. Validate name: trimmed, non-empty, length-limited
        string? newName = GetArg(request, "new_name") ?? GetArg(request, "newName") ?? GetArg(request, "name");
        if (string.IsNullOrWhiteSpace(newName))
        {
            return Response.Fail("Profile name cannot be empty.");
        }

        string trimmedName = newName.Trim();
        if (trimmedName.Length > 100)
        {
            return Response.Fail("Profile name cannot exceed 100 characters.");
        }

        // Check uniqueness
        var other = store.GetProfileByName(trimmedName);
        if (other != null && other.Id != profile.Id)
        {
            return Response.Fail($"A profile named '{trimmedName}' already exists.");
        }

        // 4. Update the display name in the store
        store.RenameProfile(profile.Id, trimmedName);
        profile.Name = trimmedName;

        return Response.Success(JsonSerializer.Serialize(profile));
    }

    private static Response HandleAddWebsite(Request request, IBlockStore store)
    {
        // Extension cannot perform remove, stop or exception actions
        if (request.Args != null)
        {
            foreach (var key in request.Args.Keys)
            {
                if (key.Contains("remove", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("delete", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("exception", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("allow", StringComparison.OrdinalIgnoreCase) ||
                    key.Contains("stop", StringComparison.OrdinalIgnoreCase))
                {
                    return Response.Fail("Refused: Extension cannot perform removal, stop, or exception actions.");
                }
            }
        }

        string? rawDomain = GetArg(request, "domain") ?? GetArg(request, "site");
        if (string.IsNullOrWhiteSpace(rawDomain))
        {
            return Response.Fail("Domain cannot be empty.");
        }

        // Validate and normalize domain (strip scheme, path, port, www, lowercase)
        if (!HostsFileBuilder.TryNormalizeDomain(rawDomain, out string normalizedDomain))
        {
            return Response.Fail($"Invalid domain '{rawDomain}'.");
        }

        // Resolve target profile
        string? idStr = GetArg(request, "profile_id") ?? GetArg(request, "profileId") ?? GetArg(request, "id");
        string? profileName = GetArg(request, "profile_name") ?? GetArg(request, "profileName") ?? GetArg(request, "profile");
        Profile? targetProfile = null;

        if (!string.IsNullOrWhiteSpace(idStr) && int.TryParse(idStr, out int pid) && pid > 0)
        {
            targetProfile = store.GetProfileById(pid);
        }

        if (targetProfile == null && !string.IsNullOrWhiteSpace(profileName) &&
            !string.Equals(profileName.Trim(), "New block", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(profileName.Trim(), "+ New block", StringComparison.OrdinalIgnoreCase))
        {
            targetProfile = store.GetProfileByName(profileName.Trim());
        }

        if (targetProfile == null)
        {
            // If user supplied a valid custom profile name, use it; otherwise generate "New Block" / "New Block X"
            string newName;
            if (!string.IsNullOrWhiteSpace(profileName) &&
                !string.Equals(profileName.Trim(), "New block", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(profileName.Trim(), "+ New block", StringComparison.OrdinalIgnoreCase))
            {
                newName = profileName.Trim();
            }
            else
            {
                newName = "New Block";
                int counter = 1;
                while (store.GetProfileByName(newName) != null)
                {
                    newName = $"New Block {++counter}";
                }
            }

            var newProfile = new Profile
            {
                Name = newName,
                Enabled = true,
                WebRules = new List<WebRule> { new WebRule { Pattern = normalizedDomain, Allow = false } },
                BlockedDomains = new List<string> { normalizedDomain }
            };
            var created = store.SaveProfile(newProfile);
            return Response.Success(JsonSerializer.Serialize(new
            {
                id = created.Id,
                name = created.Name,
                domain = normalizedDomain
            }));
        }

        // Target profile exists: check duplicate
        if ((targetProfile.WebRules != null && targetProfile.WebRules.Any(r => !r.Allow && string.Equals(r.Pattern, normalizedDomain, StringComparison.OrdinalIgnoreCase))) ||
            (targetProfile.BlockedDomains != null && targetProfile.BlockedDomains.Contains(normalizedDomain, StringComparer.OrdinalIgnoreCase)))
        {
            return Response.Fail($"Duplicate: Website '{normalizedDomain}' is already blocked in '{targetProfile.Name}'.");
        }

        // Reuse existing add-website / save path: append WebRule and re-derive domains
        targetProfile.WebRules ??= new List<WebRule>();
        targetProfile.WebRules.Add(new WebRule { Pattern = normalizedDomain, Allow = false });
        targetProfile.BlockedDomains = WebRuleMatcher.DeriveBlockedDomains(targetProfile.WebRules, targetProfile.BlockAllWebsites);

        var saved = store.SaveProfile(targetProfile);
        return Response.Success(JsonSerializer.Serialize(new
        {
            id = saved.Id,
            name = saved.Name,
            domain = normalizedDomain
        }));
    }

    private static Response HandleEnableProfile(Request request, IBlockStore store)
    {
        string? name = GetArg(request, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return Response.Fail("Missing required argument: 'name'.");
        }

        var profile = store.GetProfileByName(name);
        if (profile == null)
        {
            return Response.Fail($"Profile '{name}' not found.");
        }

        profile.Enabled = true;
        store.SaveProfile(profile);
        return Response.Success($"Profile '{profile.Name}' enabled.");
    }

    private static Response HandleDisableProfile(Request request, IBlockStore store)
    {
        string? name = GetArg(request, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return Response.Fail("Missing required argument: 'name'.");
        }

        var profile = store.GetProfileByName(name);
        if (profile == null)
        {
            return Response.Fail($"Profile '{name}' not found.");
        }

        var activeSession = store.GetActiveSession(profile.Id);
        if (activeSession != null && activeSession.Locked && activeSession.ProfileId == profile.Id)
        {
            return Response.Fail($"Cannot disable profile '{name}' while an active LOCKED session is running for it.");
        }

        var unlockErr = VerifyUnlock(profile, request, activeSession, store);
        if (unlockErr != null)
        {
            return unlockErr;
        }

        profile.Enabled = false;
        store.SaveProfile(profile);
        return Response.Success($"Profile '{profile.Name}' disabled.");
    }

    private static Response HandleGetUnlockChallenge(Request request, IBlockStore store)
    {
        string? name = GetArg(request, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return Response.Fail("Missing required argument: 'name'.");
        }

        var profile = store.GetProfileByName(name);
        if (profile == null)
        {
            return Response.Fail($"Profile '{name}' not found.");
        }

        string challenge = TypingChallengeManager.GenerateChallenge(profile.Name);
        return Response.Success(challenge);
    }

    private static Response HandleRequestDelayedUnlock(Request request, IBlockStore store)
    {
        string? name = GetArg(request, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return Response.Fail("Missing required argument: 'name'.");
        }

        var profile = store.GetProfileByName(name);
        if (profile == null)
        {
            return Response.Fail($"Profile '{name}' not found.");
        }

        int durationSeconds = profile.DelayedUnlockMinutes > 0 ? profile.DelayedUnlockMinutes * 60 : 900;
        string token = DelayedUnlockManager.StartDelayedUnlock(profile.Name, durationSeconds);

        var data = new Dictionary<string, string>
        {
            ["token"] = token,
            ["duration_seconds"] = durationSeconds.ToString(),
            ["minutes"] = profile.DelayedUnlockMinutes.ToString()
        };
        return Response.Success(System.Text.Json.JsonSerializer.Serialize(data));
    }

    private static Response HandleCancelDelayedUnlock(Request request)
    {
        string? name = GetArg(request, "name");
        if (string.IsNullOrWhiteSpace(name))
        {
            return Response.Fail("Missing required argument: 'name'.");
        }

        string? token = GetArg(request, "token");
        if (string.IsNullOrWhiteSpace(token))
        {
            return Response.Fail("Missing required argument: 'token'.");
        }

        bool cancelled = DelayedUnlockManager.CancelDelayedUnlock(name, token);
        return Response.Success(cancelled ? "Delayed unlock cancelled." : "No active delayed unlock timer found.");
    }

    private static Response HandleStartSession(Request request, IBlockStore store)
    {
        string profileName = GetArg(request, "profile") ?? "Default";
        string? minStr = GetArg(request, "minutes");
        string? secStr = GetArg(request, "seconds");
        string? lockedStr = GetArg(request, "locked");

        bool locked = true;
        if (!string.IsNullOrWhiteSpace(lockedStr) && bool.TryParse(lockedStr, out bool l))
        {
            locked = l;
        }

        long totalSeconds;
        if (!string.IsNullOrWhiteSpace(secStr))
        {
            if (!long.TryParse(secStr, out totalSeconds))
            {
                return Response.Fail("Invalid 'seconds' value.");
            }
        }
        else if (!string.IsNullOrWhiteSpace(minStr))
        {
            if (!long.TryParse(minStr, out long m))
            {
                return Response.Fail("Invalid 'minutes' value.");
            }
            totalSeconds = m * 60L;
        }
        else
        {
            totalSeconds = 2 * 60L; // default 2 minutes
        }

        if (totalSeconds <= 0)
        {
            return Response.Fail("Duration must be greater than zero.");
        }

        var profile = store.GetProfileByName(profileName);
        if (profile == null)
        {
            if (profileName.Equals("Study", StringComparison.OrdinalIgnoreCase))
            {
                profile = store.SaveProfile(new Profile
                {
                    Name = "Study",
                    BlockedProcesses = new List<string> { "notepad.exe" },
                    BlockedDomains = new List<string> { "example.com", "example.org" }
                });
            }
            else
            {
                return Response.Fail($"Profile '{profileName}' not found.");
            }
        }

        var existingSession = store.GetActiveSession(profile.Id);
        if (existingSession != null && existingSession.IsActive(DateTime.UtcNow))
        {
            var remaining = BlockSessionLogic.GetRemainingTime(existingSession, DateTime.UtcNow);
            return Response.Fail($"A session is already active: Profile '{profile.Name}', remaining {UiLogic.FormatCountdown(remaining)}. Stop it first or wait for completion.");
        }

        var session = store.StartSession(profile.Id, totalSeconds, locked);
        return Response.Success($"Session started: Profile='{profile.Name}', Duration={totalSeconds}s, Locked={locked}");
    }

    private static Response HandleStopSession(Request request, IBlockStore store, IClientIdentity clientIdentity)
    {
        bool emergency = store.IsEmergencyUnlockPresent();
        if (emergency)
        {
            var sessions = store.GetActiveSessions();
            foreach (var s in sessions)
            {
                store.EndSession(s.Id);
            }
            store.DisableAllProfiles();
            store.CleanHosts();
            store.ConsumeEmergencyUnlock();
            return Response.Success("Emergency unlock processed: active session stopped and all profiles disabled.");
        }

        string? profileNameOrId = GetArg(request, "profile") ?? GetArg(request, "name") ?? GetArg(request, "profile_id");
        BlockSession? targetSession = null;
        Profile? targetProfile = null;

        var activeSessions = store.GetActiveSessions();
        if (activeSessions.Count == 0)
        {
            return Response.Success("No active session to stop.");
        }

        if (!string.IsNullOrWhiteSpace(profileNameOrId))
        {
            targetProfile = int.TryParse(profileNameOrId, out int pid)
                ? store.GetProfileById(pid)
                : store.GetProfileByName(profileNameOrId);

            if (targetProfile != null)
            {
                targetSession = activeSessions.FirstOrDefault(s => s.ProfileId == targetProfile.Id);
                if (targetSession == null)
                {
                    return Response.Fail($"Profile '{targetProfile.Name}' does not have an active session to stop.");
                }
            }
            else
            {
                return Response.Fail($"Profile '{profileNameOrId}' not found.");
            }
        }
        else
        {
            if (activeSessions.Count == 1)
            {
                targetSession = activeSessions[0];
                targetProfile = store.GetProfileById(targetSession.ProfileId);
            }
            else
            {
                return Response.Fail("Multiple sessions are active. Please specify which profile to stop with 'profile=<name>'.");
            }
        }

        bool isElevated = clientIdentity.IsAdministrator || clientIdentity.IsLocalSystem;

        if (targetSession.Locked && !isElevated)
        {
            var (canStop, remaining, reason) = BlockSessionLogic.EvaluateStopRequest(targetSession, DateTime.UtcNow, false);
            if (!canStop)
            {
                return Response.Fail($"Stop refused: {reason}");
            }
        }

        if (targetProfile != null && !isElevated)
        {
            var unlockError = VerifyUnlock(targetProfile, request, targetSession, store);
            if (unlockError != null)
            {
                return unlockError;
            }
        }

        store.EndSession(targetSession.Id);
        store.CleanHosts();
        return Response.Success("Session stopped successfully.");
    }

    private static Response HandleGetStatus(IBlockStore store)
    {
        var allProfiles = store.ListProfiles();
        var sessions = store.GetActiveSessions();
        DateTime localNow = DateTime.Now;

        var activeProfileNames = new List<string>();
        var profileStatuses = new List<ProfileStatusItem>();

        foreach (var p in allProfiles)
        {
            bool isActive = ProfileStateLogic.IsProfileActiveWithSessions(p, sessions, localNow, out string reason);
            bool isLockedNow = ProfileStateLogic.IsProfileLockedNowWithSessions(p, sessions, localNow);
            DateTime? nextChange = ScheduleLogic.NextChange(p.Schedule, localNow);
            string schedSummary = ScheduleLogic.FormatSummary(p.Schedule);

            if (isActive)
            {
                activeProfileNames.Add(p.Name);
            }

            var pSession = sessions.FirstOrDefault(s => s.ProfileId == p.Id);
            bool isSessionRunning = (pSession != null);
            bool isSessionLocked = (pSession != null && pSession.Locked);
            long sessionRemainingSeconds = 0;
            string sessionRemainingFormatted = "00:00";
            if (pSession != null)
            {
                var rem = BlockSessionLogic.GetRemainingTime(pSession, DateTime.UtcNow);
                sessionRemainingSeconds = Math.Max(0, (long)rem.TotalSeconds);
                sessionRemainingFormatted = UiLogic.FormatCountdown(rem);
            }

            profileStatuses.Add(new ProfileStatusItem
            {
                Id = p.Id,
                Name = p.Name,
                Enabled = p.Enabled,
                IsActive = isActive,
                ActiveReason = reason,
                NextChangeLocal = nextChange,
                LockedNow = isLockedNow,
                LockMode = p.LockMode,
                ScheduleSummary = schedSummary,
                IsSessionRunning = isSessionRunning,
                IsSessionLocked = isSessionLocked,
                SessionRemainingSeconds = sessionRemainingSeconds,
                SessionRemainingFormatted = sessionRemainingFormatted
            });
        }

        var (unionApps, unionDomains) = ProfileStateLogic.ComputeActiveUnionWithSessions(allProfiles, sessions, localNow);

        var status = new StatusData
        {
            ServiceElevated = store.ServiceElevated,
            HostsState = store.HostsState,
            HostsDomainCount = store.HostsDomainCount,
            HostsBlockingOk = store.HostsBlockingOk,
            HostsLastError = store.HostsLastError,
            BlockedProcesses = unionApps,
            BlockedDomains = unionDomains,
            ActiveProfileNames = activeProfileNames,
            ProfileStatuses = profileStatuses
        };

        if (sessions.Count > 0)
        {
            var primarySession = sessions.OrderByDescending(s => BlockSessionLogic.GetRemainingTime(s, DateTime.UtcNow)).First();
            var sessionProfile = store.GetProfileById(primarySession.ProfileId);
            TimeSpan remaining = BlockSessionLogic.GetRemainingTime(primarySession, DateTime.UtcNow);

            status.Active = true;
            status.Profile = sessionProfile?.Name ?? "Manual";
            status.Locked = sessions.Any(s => s.Locked);
            status.RemainingSeconds = (long)remaining.TotalSeconds;
            status.RemainingFormatted = UiLogic.FormatCountdown(remaining);
            status.Message = sessions.Count == 1
                ? $"Active session for profile '{sessionProfile?.Name}'."
                : $"{sessions.Count} active sessions running ({string.Join(", ", sessions.Select(s => store.GetProfileById(s.ProfileId)?.Name).Where(n => n != null))}).";
        }
        else if (activeProfileNames.Count > 0)
        {
            status.Active = true;
            status.Profile = string.Join(", ", activeProfileNames);
            status.Locked = profileStatuses.Any(ps => ps.LockedNow);
            status.RemainingSeconds = 0;
            status.RemainingFormatted = "00:00";
            status.Message = $"Active profiles: {string.Join(", ", activeProfileNames)}.";
        }
        else
        {
            status.Active = false;
            status.Message = "No active session.";
        }

        var fallbackPlan = store.LastFallbackPlan ?? store.FallbackPlanner.Plan(
            allProfiles.Where(p => activeProfileNames.Contains(p.Name)),
            Array.Empty<string>(),
            DateTime.UtcNow
        );

        var missingBrowsers = fallbackPlan.BrowserStatuses.Where(b => !b.Connected).Select(b => b.Name);
        var incognitoMap = fallbackPlan.BrowserStatuses.Where(b => b.Connected).ToDictionary(b => b.Name, b => b.IncognitoAllowed);

        var warningBanners = UiLogic.FormatWarningBanners(
            fallbackPlan.FallbackActive,
            missingBrowsers,
            fallbackPlan.BlockAllUncovered,
            incognitoMap
        );

        status.ExtensionStatus = new ExtensionStatusData
        {
            Connected = fallbackPlan.BrowserStatuses.Any(b => b.Connected),
            Browsers = fallbackPlan.BrowserStatuses,
            FallbackActive = fallbackPlan.FallbackActive,
            FallbackReason = fallbackPlan.FallbackReason,
            BlockAllUncovered = fallbackPlan.BlockAllUncovered,
            WarningBanners = warningBanners
        };

        return Response.Success(JsonSerializer.Serialize(status));
    }

    private static Response HandleCleanHosts(IBlockStore store, IClientIdentity clientIdentity)
    {
        bool emergency = store.IsEmergencyUnlockPresent();
        var session = store.GetActiveSession();

        if (emergency)
        {
            store.ConsumeEmergencyUnlock();
            if (session != null)
            {
                store.EndSession(session.Id);
            }
            store.DisableAllProfiles();
            bool cleaned = store.CleanHosts();
            return cleaned
                ? Response.Success("Hosts file cleaned successfully via emergency unlock.")
                : Response.Fail("Failed to clean hosts file.");
        }

        bool isElevated = clientIdentity.IsAdministrator || clientIdentity.IsLocalSystem;

        if (session != null && session.Locked && !isElevated)
        {
            TimeSpan remaining = BlockSessionLogic.GetRemainingTime(session, DateTime.UtcNow);
            return Response.Fail($"Refused: Cannot clean hosts while a LOCKED session is active. Remaining time: {UiLogic.FormatCountdown(remaining)}.");
        }

        var allProfiles = store.ListProfiles();
        DateTime localNow = DateTime.Now;
        if (!isElevated)
        {
            foreach (var p in allProfiles)
            {
                if (p.Enabled && ProfileStateLogic.IsProfileLockedNow(p, session, localNow))
                {
                    return Response.Fail($"Refused: Cannot clean hosts while active profile '{p.Name}' is locked.");
                }
            }
        }

        bool success = store.CleanHosts();
        return success
            ? Response.Success("Hosts file cleaned successfully.")
            : Response.Fail($"Failed to clean hosts file: {store.HostsLastError ?? "Unknown error."}");
    }

    private static Response HandleHostsStatus(IBlockStore store)
    {
        var domains = store.GetCurrentHostsDomains();
        var info = new HostsStatusInfo
        {
            HostsState = store.HostsState,
            DomainCount = store.HostsDomainCount,
            LastError = store.HostsLastError,
            Domains = domains
        };
        return Response.Success(JsonSerializer.Serialize(info));
    }

    private static Response HandleGetEvents(Request request, IBlockStore store)
    {
        long sinceId = 0;
        string? sinceStr = GetArg(request, "sinceId") ?? GetArg(request, "since");
        if (!string.IsNullOrWhiteSpace(sinceStr) && long.TryParse(sinceStr, out long parsed))
        {
            sinceId = parsed;
        }

        var events = store.GetBlockEvents(sinceId);
        return Response.Success(JsonSerializer.Serialize(events));
    }

    private static Response? VerifyUnlock(Profile profile, Request request, BlockSession? activeSession, IBlockStore? store = null)
    {
        if (activeSession != null && activeSession.Locked && activeSession.ProfileId == profile.Id)
        {
            var remaining = BlockSessionLogic.GetRemainingTime(activeSession, DateTime.UtcNow);
            return Response.Fail($"Refused: Profile '{profile.Name}' has an active locked session ({UiLogic.FormatCountdown(remaining)} remaining).");
        }

        if (profile.Enabled && profile.Schedule != null && profile.Schedule.Count > 0 && ScheduleLogic.IsActive(profile.Schedule, DateTime.Now))
        {
            var nextChange = ScheduleLogic.NextChange(profile.Schedule, DateTime.Now);
            string timeStr = nextChange.HasValue ? nextChange.Value.ToString("HH:mm") : "the window ends";
            return Response.Fail($"Refused: Profile '{profile.Name}' is locked until {timeStr}.");
        }

        if (!profile.Enabled)
        {
            return null;
        }

        switch (profile.LockMode)
        {
            case LockMode.None:
            case LockMode.Locked:
                return null;

            case LockMode.HardLocked:
                if (store?.IsEmergencyUnlockPresent() == true)
                {
                    return null;
                }
                if (profile.Schedule == null || profile.Schedule.Count == 0 || ScheduleLogic.IsActive(profile.Schedule, DateTime.Now))
                {
                    return Response.Fail($"Refused: Profile '{profile.Name}' is hard-locked and cannot be modified or unlocked without emergency recovery.");
                }
                return null;

            case LockMode.TypingChallenge:
            {
                string? text = GetArg(request, "unlock_text") ?? GetArg(request, "unlockText");
                if (string.IsNullOrEmpty(text))
                {
                    return Response.Fail($"Refused: Profile '{profile.Name}' requires completing a typing challenge to unlock. Request a challenge first.");
                }

                if (!TypingChallengeManager.ValidateChallenge(profile.Name, text, out var err))
                {
                    return Response.Fail($"Refused: {err ?? "Invalid challenge text."}");
                }

                return null;
            }

            case LockMode.Password:
            {
                if (PasswordSecurity.IsLockedOut(profile.Name, out int remSec))
                {
                    return Response.Fail($"Refused: Too many failed password attempts. Profile '{profile.Name}' is locked out for {remSec} seconds.");
                }

                string? pwd = GetArg(request, "unlock_password") ?? GetArg(request, "unlockPassword") ?? GetArg(request, "password");
                if (string.IsNullOrEmpty(pwd))
                {
                    return Response.Fail($"Refused: Profile '{profile.Name}' requires an unlock password.");
                }

                if (!PasswordSecurity.VerifyPassword(pwd, profile.PasswordSalt, profile.PasswordHash))
                {
                    PasswordSecurity.RecordFailedAttempt(profile.Name);
                    if (PasswordSecurity.IsLockedOut(profile.Name, out int lockedRem))
                    {
                        return Response.Fail($"Refused: Too many failed password attempts. Profile '{profile.Name}' is locked out for {lockedRem} seconds.");
                    }
                    return Response.Fail("Refused: Incorrect unlock password.");
                }

                PasswordSecurity.RecordSuccessfulAttempt(profile.Name);
                return null;
            }


            case LockMode.DelayedUnlock:
            {
                string? token = GetArg(request, "delayed_unlock_token")
                             ?? GetArg(request, "unlock_token")
                             ?? GetArg(request, "token");
                if (string.IsNullOrEmpty(token))
                {
                    return Response.Fail($"Refused: Profile '{profile.Name}' requires waiting out an active delayed unlock timer. Start the timer first.");
                }

                if (!DelayedUnlockManager.ValidateUnlock(profile.Name, token, out var err))
                {
                    return Response.Fail($"Refused: {err ?? "Invalid or incomplete delayed unlock timer."}");
                }

                return null;
            }

            default:
                return null;
        }
    }

    private static LockMode ParseLockMode(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return LockMode.None;
        if (int.TryParse(raw, out int intVal))
        {
            return (LockMode)intVal;
        }

        if (Enum.TryParse<LockMode>(raw, true, out var mode))
        {
            return mode;
        }

        if (string.Equals(raw, "typing", StringComparison.OrdinalIgnoreCase))
        {
            return LockMode.TypingChallenge;
        }

        if (string.Equals(raw, "hardlocked", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "hard_locked", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(raw, "hardlock", StringComparison.OrdinalIgnoreCase))
        {
            return LockMode.HardLocked;
        }

        return LockMode.None;
    }

    private static bool TryParseSchedule(string json, out List<ScheduleRule> schedule, out string error)
    {
        schedule = new();
        error = string.Empty;
        if (string.IsNullOrWhiteSpace(json))
        {
            return true;
        }

        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            };
            options.Converters.Add(new JsonStringEnumConverter());
            var parsed = JsonSerializer.Deserialize<List<ScheduleRule>>(json, options);
            if (parsed == null)
            {
                error = "Schedule list cannot be null.";
                return false;
            }

            foreach (var rule in parsed)
            {
                if (!rule.IsValid)
                {
                    error = $"Invalid schedule rule: Start={rule.StartMinute}, End={rule.EndMinute}. Start and End cannot be equal and must be 0-1439.";
                    return false;
                }
            }

            schedule = parsed;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static string? GetArg(Request request, string key)
    {
        if (request.Args == null) return null;
        foreach (var kvp in request.Args)
        {
            if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }
        return null;
    }

    private static List<string> ParseList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();

        if (raw.TrimStart().StartsWith("["))
        {
            try
            {
                var deserialized = JsonSerializer.Deserialize<List<string>>(raw);
                if (deserialized != null) return deserialized;
            }
            catch { }
        }

        return raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                  .Select(s => s.Trim())
                  .Where(s => !string.IsNullOrEmpty(s))
                  .ToList();
    }

    private static Response HandleExtHeartbeat(Request request, IBlockStore store)
    {
        string browserProcess = GetArg(request, "browserProcess") ?? GetArg(request, "browser") ?? "chrome.exe";
        string incognitoStr = GetArg(request, "incognitoAllowed") ?? "true";
        bool incognitoAllowed = !string.Equals(incognitoStr, "false", StringComparison.OrdinalIgnoreCase);
        string extensionVersion = GetArg(request, "extensionVersion") ?? "1.0.0";

        store.FallbackPlanner.RecordHeartbeat(browserProcess, DateTime.UtcNow, incognitoAllowed, extensionVersion);

        return HandleGetActiveWebRules(store);
    }

    private static Response HandleEmergencyUnlock(IBlockStore store, IClientIdentity clientIdentity)
    {
        bool isElevated = clientIdentity.IsAdministrator || clientIdentity.IsLocalSystem;
        if (!isElevated)
        {
            if (!store.IsEmergencyUnlockPresent())
            {
                return Response.Fail("Unauthorized: EMERGENCYUNLOCK requires elevated administrator privileges or an emergency recovery file.");
            }
        }

        SecurityHardener.UnlockCurrentProcess(out _);
        SecurityHardener.UnlockService(out _);
        store.EndActiveSession();
        store.DisableAllProfiles();
        store.CleanHosts();
        store.ConsumeEmergencyUnlock();

        try
        {
            var dns = store.GetDnsConfig();
            dns.Locked = false;
            dns.Enabled = false;
            store.SaveDnsConfig(dns);
            DnsManager.ResetDns(out _);
        }
        catch { }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string emg1 = Path.Combine(localAppData, "Blocker", "EMERGENCY_UNLOCK");
        if (File.Exists(emg1)) { try { File.Delete(emg1); } catch { } }

        string emg2 = Path.Combine(localAppData, "Sankalpa", "EMERGENCY_UNLOCK");
        if (File.Exists(emg2)) { try { File.Delete(emg2); } catch { } }

        return Response.Success("Sankalpa emergency unlock executed successfully. All blocks reset.");
    }

    private static Response HandleGetActiveWebRules(IBlockStore store)
    {
        var allProfiles = store.ListProfiles();
        var session = store.GetActiveSession();
        DateTime localNow = DateTime.Now;

        var activeProfilesData = new List<ActiveProfileDto>();
        var allProfilesSummary = new List<ProfileSummaryDto>();

        foreach (var profile in allProfiles.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            bool isActive = ProfileStateLogic.IsProfileActive(profile, session, localNow, out string reason);
            bool isLocked = ProfileStateLogic.IsProfileLockedNow(profile, session, localNow) ||
                            (session != null && session.Locked && session.ProfileId == profile.Id) ||
                            (profile.LockMode != LockMode.None);

            int ruleCount = profile.WebRules?.Count ?? (profile.BlockedDomains?.Count ?? 0);

            allProfilesSummary.Add(new ProfileSummaryDto
            {
                Id = profile.Id,
                Name = profile.Name,
                Active = isActive,
                Locked = isLocked,
                LockMode = profile.LockMode.ToString(),
                RuleCount = ruleCount,
                BlockAllWebsites = profile.BlockAllWebsites
            });

            if (isActive)
            {
                DateTime? activeUntil = null;
                if (reason == "Manual" && session != null)
                {
                    activeUntil = session.StartUtc.ToLocalTime().AddSeconds(session.DurationSeconds);
                }
                else if (reason == "Schedule")
                {
                    activeUntil = ScheduleLogic.NextChange(profile.Schedule, localNow);
                }

                activeProfilesData.Add(new ActiveProfileDto
                {
                    Id = profile.Id,
                    Name = profile.Name,
                    BlockAllWebsites = profile.BlockAllWebsites,
                    WebRules = (profile.WebRules ?? new List<WebRule>())
                        .Select(r => new ActiveWebRuleDto { Pattern = r.Pattern, Allow = r.Allow })
                        .ToList(),
                    ActiveUntilLocal = activeUntil,
                    ActiveUntil = activeUntil?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    LockMode = profile.LockMode.ToString(),
                    Locked = isLocked
                });
            }
        }

        // Compute stable deterministic integer version
        var sb = new System.Text.StringBuilder();
        foreach (var p in activeProfilesData)
        {
            sb.Append(p.Name).Append('|').Append(p.BlockAllWebsites ? "1" : "0").Append('|');
            foreach (var r in p.WebRules.OrderBy(r => r.Pattern, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Allow))
            {
                sb.Append(r.Pattern).Append(':').Append(r.Allow ? "1" : "0").Append(';');
            }
            sb.Append("||");
        }

        using var sha = System.Security.Cryptography.SHA256.Create();
        byte[] hash = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(sb.ToString()));
        int rulesVersion = Math.Abs(BitConverter.ToInt32(hash, 0));
        if (rulesVersion == 0) rulesVersion = 1;

        string theme = "black";
        try
        {
            var uiSettings = UiSettingsStore.Load();
            theme = UiSettingsStore.NormalizeTheme(uiSettings.Theme);
        }
        catch { }

        var result = new ActiveWebRulesResponse
        {
            RulesVersion = rulesVersion,
            ActiveProfiles = activeProfilesData,
            AllProfiles = allProfilesSummary,
            ExtensionTheme = theme
        };

        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        return Response.Success(JsonSerializer.Serialize(result, options));
    }

    private static Response HandleGetDnsConfig(IBlockStore store)
    {
        var config = store.GetDnsConfig();
        string json = JsonSerializer.Serialize(config);
        return Response.Success(json);
    }

    private static Response HandleSaveDnsConfig(Request request, IBlockStore store, IClientIdentity clientIdentity)
    {
        var current = store.GetDnsConfig();
        if (current.Locked)
        {
            return Response.Fail("DNS settings are currently locked and cannot be modified.");
        }

        string? preset = GetArg(request, "preset");
        string? primary = GetArg(request, "primary");
        string? secondary = GetArg(request, "secondary");
        string? primaryIpv6 = GetArg(request, "primary_ipv6");
        string? secondaryIpv6 = GetArg(request, "secondary_ipv6");
        string? enabledStr = GetArg(request, "enabled");

        bool isCustom = string.Equals(preset, "Custom", StringComparison.OrdinalIgnoreCase) ||
                        (!string.IsNullOrWhiteSpace(primary) && !DnsPresets.All.Any(p => p.Primary == primary));
        if (isCustom && !clientIdentity.IsAdministrator && !clientIdentity.IsLocalSystem)
        {
            return Response.Fail("Administrator privileges are required to configure custom DNS IP addresses.");
        }

        if (!string.IsNullOrEmpty(primary))
        {
            if (!IPAddress.TryParse(primary, out var pIp) || pIp.AddressFamily != AddressFamily.InterNetwork)
            {
                return Response.Fail("Invalid IP address format for DNS configuration.");
            }
        }

        if (!string.IsNullOrEmpty(secondary))
        {
            if (!IPAddress.TryParse(secondary, out var sIp) || sIp.AddressFamily != AddressFamily.InterNetwork)
            {
                return Response.Fail("Invalid IP address format for DNS configuration.");
            }
        }

        if (!string.IsNullOrEmpty(primaryIpv6))
        {
            if (!IPAddress.TryParse(primaryIpv6, out var p6) || p6.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return Response.Fail("Invalid IP address format for DNS configuration.");
            }
        }

        if (!string.IsNullOrEmpty(secondaryIpv6))
        {
            if (!IPAddress.TryParse(secondaryIpv6, out var s6) || s6.AddressFamily != AddressFamily.InterNetworkV6)
            {
                return Response.Fail("Invalid IP address format for DNS configuration.");
            }
        }

        if (!string.IsNullOrWhiteSpace(preset)) current.PresetName = preset;
        if (!string.IsNullOrWhiteSpace(primary)) current.PrimaryDns = primary;
        if (secondary != null) current.SecondaryDns = secondary;

        // Auto-populate IPv6 addresses from known presets if not explicitly provided
        if (!string.IsNullOrWhiteSpace(current.PresetName))
        {
            var matched = DnsPresets.All.FirstOrDefault(p => string.Equals(p.Name, current.PresetName, StringComparison.OrdinalIgnoreCase));
            if (matched != null && matched.Name != "Custom")
            {
                current.PrimaryIpv6 = matched.PrimaryIpv6;
                current.SecondaryIpv6 = matched.SecondaryIpv6;
            }
        }
        if (!string.IsNullOrWhiteSpace(primaryIpv6)) current.PrimaryIpv6 = primaryIpv6;
        if (secondaryIpv6 != null) current.SecondaryIpv6 = secondaryIpv6;

        if (!string.IsNullOrWhiteSpace(enabledStr) && bool.TryParse(enabledStr, out bool enabled))
        {
            current.Enabled = enabled;
        }

        store.SaveDnsConfig(current);

        if (current.Enabled)
        {
            DnsManager.ApplyDns(current.PrimaryDns, current.SecondaryDns, current.PrimaryIpv6, current.SecondaryIpv6, out _);
        }
        else
        {
            DnsManager.ResetDns(out _);
        }

        return Response.Success(JsonSerializer.Serialize(current));
    }

    private static Response HandleLockDns(Request request, IBlockStore store)
    {
        var current = store.GetDnsConfig();
        string lockType = GetArg(request, "lock_type") ?? "Challenge";
        string durationStr = GetArg(request, "duration") ?? "3600";
        long duration = long.TryParse(durationStr, out long d) ? d : 3600;

        current.Locked = true;
        current.LockType = lockType;
        current.LockDurationSeconds = duration;
        current.Enabled = true;

        if (string.Equals(lockType, "Timer", StringComparison.OrdinalIgnoreCase))
        {
            current.LockedUntilUtc = DateTime.UtcNow.AddSeconds(duration);
        }
        else
        {
            current.LockedUntilUtc = null;
        }

        store.SaveDnsConfig(current);
        DnsManager.ApplyDns(current.PrimaryDns, current.SecondaryDns, current.PrimaryIpv6, current.SecondaryIpv6, out _);

        return Response.Success(JsonSerializer.Serialize(current));
    }

    private static Response HandleGetDnsChallenge(IBlockStore store)
    {
        string challenge = TypingChallengeManager.GenerateNumericChallenge("DNS_LOCK", length: 64);
        return Response.Success(challenge);
    }

    private static Response HandleUnlockDns(Request request, IBlockStore store)
    {
        var current = store.GetDnsConfig();
        if (!current.Locked)
        {
            return Response.Success("DNS is not locked.");
        }

        if (string.Equals(current.LockType, "Challenge", StringComparison.OrdinalIgnoreCase))
        {
            string? typedText = GetArg(request, "challenge_text") ?? GetArg(request, "unlock_text");
            if (!TypingChallengeManager.ValidateChallenge("DNS_LOCK", typedText, out string? err))
            {
                return Response.Fail(err ?? "Challenge text did not match.");
            }
        }
        else if (string.Equals(current.LockType, "Timer", StringComparison.OrdinalIgnoreCase))
        {
            if (current.LockedUntilUtc.HasValue && DateTime.UtcNow < current.LockedUntilUtc.Value)
            {
                TimeSpan rem = current.LockedUntilUtc.Value - DateTime.UtcNow;
                return Response.Fail($"DNS is locked for {(int)rem.TotalMinutes + 1} more minute(s).");
            }
        }
        else if (string.Equals(current.LockType, "Schedule", StringComparison.OrdinalIgnoreCase))
        {
            string? typedText = GetArg(request, "challenge_text") ?? GetArg(request, "unlock_text");
            if (!string.IsNullOrEmpty(typedText))
            {
                if (!TypingChallengeManager.ValidateChallenge("DNS_LOCK", typedText, out string? err))
                {
                    return Response.Fail(err ?? "Challenge text did not match.");
                }
            }
            else
            {
                return Response.Fail("DNS is schedule-locked. 64-digit challenge is required to unlock.");
            }
        }

        current.Locked = false;
        current.LockedUntilUtc = null;
        store.SaveDnsConfig(current);

        return Response.Success(JsonSerializer.Serialize(current));
    }

    #region Meditation Mode Handlers

    private static Response HandleStartMeditation(Request request, IBlockStore store)
    {
        long targetSeconds = 900;
        var allowedApps = new List<string>();
        LockMode lockMode = LockMode.None;
        string? soundscape = null;
        string? status = null;

        string? payload = GetArg(request, "data") ?? GetArg(request, "payload") ?? GetArg(request, "json");
        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                var reqDto = JsonSerializer.Deserialize<StartMeditationRequest>(payload);
                if (reqDto != null)
                {
                    if (reqDto.TargetDurationSeconds > 0) targetSeconds = reqDto.TargetDurationSeconds;
                    if (reqDto.AllowedApps != null && reqDto.AllowedApps.Count > 0) allowedApps = reqDto.AllowedApps;
                    lockMode = ParseLockMode(reqDto.LockMode);
                    soundscape = reqDto.Soundscape;
                    status = reqDto.Status;
                }
            }
            catch { }
        }

        string? secStr = GetArg(request, "targetDurationSeconds") ?? GetArg(request, "target_duration_seconds") ?? GetArg(request, "seconds") ?? GetArg(request, "duration");
        string? minStr = GetArg(request, "minutes");
        if (!string.IsNullOrWhiteSpace(secStr) && long.TryParse(secStr, out long parsedSec) && parsedSec > 0)
        {
            targetSeconds = parsedSec;
        }
        else if (!string.IsNullOrWhiteSpace(minStr) && long.TryParse(minStr, out long parsedMin) && parsedMin > 0)
        {
            targetSeconds = parsedMin * 60;
        }

        string? appsStr = GetArg(request, "allowedApps") ?? GetArg(request, "allowed_apps") ?? GetArg(request, "apps");
        if (!string.IsNullOrWhiteSpace(appsStr))
        {
            allowedApps = ParseList(appsStr);
        }

        string? lockStr = GetArg(request, "lockMode") ?? GetArg(request, "lock_mode") ?? GetArg(request, "mode");
        if (!string.IsNullOrWhiteSpace(lockStr))
        {
            lockMode = ParseLockMode(lockStr);
        }

        string? sound = GetArg(request, "soundscape") ?? GetArg(request, "sound");
        if (!string.IsNullOrWhiteSpace(sound))
        {
            soundscape = sound;
        }

        string? statusArg = GetArg(request, "status");
        if (!string.IsNullOrWhiteSpace(statusArg))
        {
            status = statusArg;
        }

        var existing = store.GetActiveMeditationSession();
        if (existing != null)
        {
            long rem = Math.Max(0, (long)(existing.StartUtc.AddSeconds(existing.TargetDurationSeconds) - DateTime.UtcNow).TotalSeconds);
            return Response.Fail($"A meditation session is already active (Session {existing.Id}, remaining {rem}s).");
        }

        var session = store.StartMeditationSession(targetSeconds, allowedApps, lockMode, soundscape, null, status ?? "Active");
        var statusDto = new MeditationStatusDto
        {
            Active = true,
            Status = session.Status ?? "Active",
            SessionId = session.Id,
            StartUtc = session.StartUtc,
            TargetDurationSeconds = session.TargetDurationSeconds,
            RemainingSeconds = session.TargetDurationSeconds,
            Locked = session.LockMode != LockMode.None,
            LockMode = session.LockMode.ToString(),
            AllowedApps = session.GetAllowedApps(),
            Soundscape = session.Soundscape,
            Notes = session.Notes
        };

        return Response.Success(JsonSerializer.Serialize(statusDto));
    }

    private static Response HandleStopMeditation(Request request, IBlockStore store)
    {
        var active = store.GetActiveMeditationSession();
        if (active == null)
        {
            return Response.Success("No active meditation session to stop.");
        }

        bool emergency = store.IsEmergencyUnlockPresent();
        if (!emergency && (active.LockMode == LockMode.Locked || active.LockMode == LockMode.HardLocked))
        {
            return Response.Fail("Meditation session is locked and cannot be stopped early.");
        }

        string? notes = GetArg(request, "notes");
        store.EndMeditationSession(active.Id, completed: false, notes);
        return Response.Success("Meditation session stopped.");
    }

    private static Response HandleGetMeditationStatus(IBlockStore store)
    {
        var active = store.GetActiveMeditationSession();
        if (active == null)
        {
            return Response.Success(JsonSerializer.Serialize(new MeditationStatusDto
            {
                Active = false,
                RemainingSeconds = 0,
                Locked = false,
                LockMode = "None"
            }));
        }

        long rem = Math.Max(0, (long)(active.StartUtc.AddSeconds(active.TargetDurationSeconds) - DateTime.UtcNow).TotalSeconds);
        var dto = new MeditationStatusDto
        {
            Active = true,
            Status = active.Status ?? "Active",
            SessionId = active.Id,
            StartUtc = active.StartUtc,
            TargetDurationSeconds = active.TargetDurationSeconds,
            RemainingSeconds = rem,
            Locked = active.LockMode != LockMode.None,
            LockMode = active.LockMode.ToString(),
            AllowedApps = active.GetAllowedApps(),
            Soundscape = active.Soundscape,
            Notes = active.Notes
        };
        return Response.Success(JsonSerializer.Serialize(dto));
    }

    private static Response HandleSaveMeditationNote(Request request, IBlockStore store)
    {
        int sessionId = 0;
        string notes = string.Empty;

        string? payload = GetArg(request, "data") ?? GetArg(request, "payload") ?? GetArg(request, "json");
        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                var reqDto = JsonSerializer.Deserialize<SaveMeditationNoteRequest>(payload);
                if (reqDto != null)
                {
                    sessionId = reqDto.SessionId;
                    notes = reqDto.Notes ?? string.Empty;
                }
            }
            catch { }
        }

        string? idStr = GetArg(request, "sessionId") ?? GetArg(request, "session_id") ?? GetArg(request, "id");
        if (!string.IsNullOrWhiteSpace(idStr) && int.TryParse(idStr, out int parsedId))
        {
            sessionId = parsedId;
        }

        string? noteArg = GetArg(request, "notes") ?? GetArg(request, "note");
        if (!string.IsNullOrWhiteSpace(noteArg))
        {
            notes = noteArg;
        }

        if (sessionId <= 0)
        {
            var active = store.GetActiveMeditationSession();
            if (active != null)
            {
                sessionId = active.Id;
            }
            else
            {
                return Response.Fail("Missing required argument: 'sessionId'.");
            }
        }

        store.UpdateMeditationNotes(sessionId, notes);
        return Response.Success("Meditation note saved.");
    }

    private static Response HandleGetMeditationHistory(Request request, IBlockStore store)
    {
        int limit = 50;
        string? limitStr = GetArg(request, "limit");
        if (!string.IsNullOrWhiteSpace(limitStr) && int.TryParse(limitStr, out int l) && l > 0)
        {
            limit = l;
        }

        var sessions = store.GetMeditationHistory(limit);
        var dtos = sessions.Select(s => new MeditationHistoryItemDto
        {
            Id = s.Id,
            StartUtc = s.StartUtc,
            TargetDurationSeconds = s.TargetDurationSeconds,
            CompletedDurationSeconds = s.CompletedDurationSeconds,
            Completed = s.Completed,
            Notes = s.Notes,
            Soundscape = s.Soundscape,
            AllowedAppsCount = s.GetAllowedApps().Count
        }).ToList();

        return Response.Success(JsonSerializer.Serialize(dtos));
    }

    private static Response HandleSaveMeditationSchedule(Request request, IBlockStore store)
    {
        var schedule = new MeditationSchedule();

        string? payload = GetArg(request, "data") ?? GetArg(request, "payload") ?? GetArg(request, "json");
        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                var dto = JsonSerializer.Deserialize<MeditationScheduleDto>(payload);
                if (dto != null)
                {
                    schedule.Id = dto.Id;
                    schedule.Enabled = dto.Enabled;
                    schedule.TimeOfDay = dto.TimeOfDay ?? "15:00";
                    schedule.DurationSeconds = dto.DurationSeconds > 0 ? dto.DurationSeconds : 900;
                    schedule.LockMode = ParseLockMode(dto.LockMode);
                    schedule.Soundscape = dto.Soundscape;
                    schedule.LastSnoozedUtc = dto.LastSnoozedUtc;
                    schedule.SetAllowedApps(dto.AllowedApps ?? new());
                    if (dto.DaysOfWeek != null && dto.DaysOfWeek.Count > 0)
                    {
                        var days = new List<DayOfWeek>();
                        foreach (var d in dto.DaysOfWeek)
                        {
                            if (Enum.TryParse<DayOfWeek>(d, true, out var dow)) days.Add(dow);
                        }
                        schedule.SetDays(days);
                    }
                }
            }
            catch { }
        }

        string? idStr = GetArg(request, "id") ?? GetArg(request, "scheduleId") ?? GetArg(request, "schedule_id");
        if (!string.IsNullOrWhiteSpace(idStr) && int.TryParse(idStr, out int sId))
        {
            schedule.Id = sId;
        }

        string? enStr = GetArg(request, "enabled");
        if (!string.IsNullOrWhiteSpace(enStr) && bool.TryParse(enStr, out bool en))
        {
            schedule.Enabled = en;
        }

        string? timeStr = GetArg(request, "timeOfDay") ?? GetArg(request, "time_of_day") ?? GetArg(request, "time");
        if (!string.IsNullOrWhiteSpace(timeStr))
        {
            schedule.TimeOfDay = timeStr;
        }

        string? durStr = GetArg(request, "durationSeconds") ?? GetArg(request, "duration_seconds") ?? GetArg(request, "duration");
        if (!string.IsNullOrWhiteSpace(durStr) && long.TryParse(durStr, out long dur) && dur > 0)
        {
            schedule.DurationSeconds = dur;
        }

        string? daysStr = GetArg(request, "daysOfWeek") ?? GetArg(request, "days_of_week") ?? GetArg(request, "days");
        if (!string.IsNullOrWhiteSpace(daysStr))
        {
            var dayNames = ParseList(daysStr);
            var days = new List<DayOfWeek>();
            foreach (var d in dayNames)
            {
                if (Enum.TryParse<DayOfWeek>(d, true, out var dow)) days.Add(dow);
            }
            schedule.SetDays(days);
        }

        string? appsStr = GetArg(request, "allowedApps") ?? GetArg(request, "allowed_apps") ?? GetArg(request, "apps");
        if (!string.IsNullOrWhiteSpace(appsStr))
        {
            schedule.SetAllowedApps(ParseList(appsStr));
        }

        string? lockStr = GetArg(request, "lockMode") ?? GetArg(request, "lock_mode") ?? GetArg(request, "mode");
        if (!string.IsNullOrWhiteSpace(lockStr))
        {
            schedule.LockMode = ParseLockMode(lockStr);
        }

        string? soundStr = GetArg(request, "soundscape") ?? GetArg(request, "sound");
        if (!string.IsNullOrWhiteSpace(soundStr))
        {
            schedule.Soundscape = soundStr;
        }

        schedule.Id = 1;
        var saved = store.SaveMeditationSchedule(schedule);
        var outDto = new MeditationScheduleDto
        {
            Id = saved.Id,
            Enabled = saved.Enabled,
            DaysOfWeek = saved.GetDays().Select(d => d.ToString()).ToList(),
            TimeOfDay = saved.TimeOfDay,
            DurationSeconds = saved.DurationSeconds,
            AllowedApps = saved.GetAllowedApps(),
            LockMode = saved.LockMode.ToString(),
            Soundscape = saved.Soundscape,
            LastSnoozedUtc = saved.LastSnoozedUtc
        };

        return Response.Success(JsonSerializer.Serialize(outDto));
    }

    private static Response HandleGetMeditationSchedules(IBlockStore store)
    {
        var schedules = store.GetMeditationSchedules();
        var dtos = schedules.Select(s => new MeditationScheduleDto
        {
            Id = s.Id,
            Enabled = s.Enabled,
            DaysOfWeek = s.GetDays().Select(d => d.ToString()).ToList(),
            TimeOfDay = s.TimeOfDay,
            DurationSeconds = s.DurationSeconds,
            AllowedApps = s.GetAllowedApps(),
            LockMode = s.LockMode.ToString(),
            Soundscape = s.Soundscape,
            LastSnoozedUtc = s.LastSnoozedUtc
        }).ToList();

        return Response.Success(JsonSerializer.Serialize(dtos));
    }

    private static Response HandleDeleteMeditationSchedule(Request request, IBlockStore store)
    {
        string? idStr = GetArg(request, "id") ?? GetArg(request, "scheduleId") ?? GetArg(request, "schedule_id");
        if (string.IsNullOrWhiteSpace(idStr) || !int.TryParse(idStr, out int id))
        {
            return Response.Fail("Missing required argument: 'id'.");
        }

        bool deleted = store.DeleteMeditationSchedule(id);
        return deleted ? Response.Success("Meditation schedule deleted.") : Response.Fail("Schedule not found.");
    }

    private static Response HandleSnoozeMeditationSchedule(Request request, IBlockStore store)
    {
        int scheduleId = 0;
        string? payload = GetArg(request, "data") ?? GetArg(request, "payload") ?? GetArg(request, "json");
        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                var reqDto = JsonSerializer.Deserialize<SnoozeMeditationScheduleRequest>(payload);
                if (reqDto != null && reqDto.ScheduleId > 0)
                {
                    scheduleId = reqDto.ScheduleId;
                }
            }
            catch { }
        }

        string? idStr = GetArg(request, "scheduleId") ?? GetArg(request, "schedule_id") ?? GetArg(request, "id");
        if (!string.IsNullOrWhiteSpace(idStr) && int.TryParse(idStr, out int parsedId))
        {
            scheduleId = parsedId;
        }

        if (scheduleId <= 0)
        {
            return Response.Fail("Missing required argument: 'scheduleId'.");
        }

        int minutes = 5;
        string? minStr = GetArg(request, "minutes");
        if (!string.IsNullOrWhiteSpace(minStr) && int.TryParse(minStr, out int m) && m > 0)
        {
            minutes = m;
        }

        bool snoozed = store.SnoozeMeditationSchedule(scheduleId, minutes);
        return snoozed ? Response.Success($"Meditation schedule snoozed for {minutes} minutes.") : Response.Fail("Schedule not found.");
    }

    private static Response HandleConfirmTimerReady(Request request, IBlockStore store)
    {
        ConfirmTimerReadyRequest? req = null;
        string? payload = GetArg(request, "data") ?? GetArg(request, "payload") ?? GetArg(request, "json");
        if (!string.IsNullOrWhiteSpace(payload))
        {
            try
            {
                req = JsonSerializer.Deserialize<ConfirmTimerReadyRequest>(payload);
            }
            catch { }
        }

        int sessionId = req?.SessionId ?? 0;
        string? sIdStr = GetArg(request, "sessionId") ?? GetArg(request, "session_id") ?? GetArg(request, "id");
        if (sessionId <= 0 && !string.IsNullOrWhiteSpace(sIdStr) && int.TryParse(sIdStr, out int parsedId))
        {
            sessionId = parsedId;
        }

        if (sessionId <= 0)
        {
            return Response.Fail("Missing or invalid sessionId.");
        }

        bool promoted = store.PromoteMeditationSessionToActive(sessionId);
        if (!promoted)
        {
            return Response.Fail($"Failed to promote meditation session {sessionId}.");
        }

        if (req?.AllowedAppRoots != null && req.AllowedAppRoots.Count > 0)
        {
            store.ActiveAllowedAppRoots = new Dictionary<string, string>(req.AllowedAppRoots, StringComparer.OrdinalIgnoreCase);
        }

        var activeSession = store.GetActiveMeditationSession();
        var allowedApps = activeSession?.GetAllowedApps() ?? new List<string>();

        int suspendedCount = 0;
        if (req?.VisibleWindowPids != null && req.VisibleWindowPids.Count > 0)
        {
            foreach (var pid in req.VisibleWindowPids)
            {
                if (pid <= 4) continue;

                string procName = string.Empty;
                string? imagePath = null;
                try
                {
                    using var p = Process.GetProcessById(pid);
                    procName = p.ProcessName;
                    try { imagePath = p.MainModule?.FileName; } catch { }
                }
                catch
                {
                    continue;
                }

                if (ProtectedProcesses.IsProtected(procName, imagePath))
                {
                    continue;
                }

                bool isAllowed = false;
                string normProc = ProcessMatcher.NormalizeProcessName(procName);
                foreach (var app in allowedApps)
                {
                    string normApp = ProcessMatcher.NormalizeProcessName(app);
                    if (string.Equals(normProc, normApp, StringComparison.OrdinalIgnoreCase))
                    {
                        if (store.ActiveAllowedAppRoots != null &&
                            store.ActiveAllowedAppRoots.TryGetValue(normApp, out var rootDir) &&
                            !string.IsNullOrWhiteSpace(rootDir))
                        {
                            if (!string.IsNullOrWhiteSpace(imagePath))
                            {
                                if (ProtectedProcesses.IsPathInsideDirectory(imagePath, rootDir))
                                {
                                    isAllowed = true;
                                    break;
                                }
                                continue;
                            }
                        }
                        isAllowed = true;
                        break;
                    }
                }

                if (isAllowed)
                {
                    continue;
                }

                if (ProcessSuspender.Default.SuspendProcess(pid, procName))
                {
                    suspendedCount++;
                }
            }
        }

        if (suspendedCount > 0)
        {
            ProcessSuspender.OnMeditationSuspensionSummary?.Invoke(suspendedCount);
        }

        return Response.Success(JsonSerializer.Serialize(new
        {
            promoted = true,
            sessionId,
            suspendedCount
        }));
    }

    #endregion
}
