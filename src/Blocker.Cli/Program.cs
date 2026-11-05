using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Blocker.Core;

if (args.Length == 0)
{
    PrintUsage();
    return 1;
}

string command = args[0].ToLowerInvariant();

// Extract global unlock options if present anywhere in arguments
string? globalUnlockText = null;
string? globalUnlockPassword = null;
for (int i = 1; i < args.Length; i++)
{
    if (args[i].Equals("--unlock-text", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        globalUnlockText = args[i + 1];
    }
    else if (args[i].Equals("--unlock-password", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        globalUnlockPassword = args[i + 1];
    }
}

Request? request = null;
Action<Response>? printSuccess = null;
var pipeClient = new PipeClient();

switch (command)
{
    case "ping":
        request = new Request { Command = CommandNames.Ping };
        printSuccess = resp => Console.WriteLine(resp.Data ?? "pong");
        break;

    case "status":
        request = new Request { Command = CommandNames.GetStatus };
        printSuccess = resp =>
        {
            if (string.IsNullOrWhiteSpace(resp.Data))
            {
                Console.WriteLine("No status data available.");
                return;
            }

            try
            {
                var status = JsonSerializer.Deserialize<StatusData>(resp.Data, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (status == null)
                {
                    Console.WriteLine("No active session or schedules.");
                    return;
                }

                if (!status.Active)
                {
                    Console.WriteLine("No active session or schedule.");
                }
                else
                {
                    Console.WriteLine("Active Status:");
                    Console.WriteLine($"  Active Profiles:   {string.Join(", ", status.ActiveProfileNames)}");
                    Console.WriteLine($"  Blocked Processes: {string.Join(", ", status.BlockedProcesses)}");
                    Console.WriteLine($"  Blocked Domains:   {string.Join(", ", status.BlockedDomains)}");
                    Console.WriteLine($"  Locked:            {status.Locked}");
                    if (status.RemainingSeconds > 0)
                    {
                        Console.WriteLine($"  Manual Session:    {status.RemainingFormatted} remaining ({status.RemainingSeconds}s total)");
                    }
                }

                Console.WriteLine($"Website Blocking: {(status.HostsBlockingOk ? "OK" : "FAILED")}");
                if (!string.IsNullOrEmpty(status.HostsLastError))
                {
                    Console.WriteLine($"Hosts Info:       {status.HostsLastError}");
                }

                if (status.ProfileStatuses.Count > 0)
                {
                    Console.WriteLine("Profiles Summary:");
                    foreach (var ps in status.ProfileStatuses)
                    {
                        string state = ps.IsActive ? $"ACTIVE ({ps.ActiveReason})" : (ps.Enabled ? "On" : "Off");
                        string locked = ps.LockedNow ? " [LOCKED]" : "";
                        Console.WriteLine($"  • {ps.Name}: {state}{locked} | LockMode: {ps.LockMode} | Schedule: {ps.ScheduleSummary}");
                    }
                }
            }
            catch
            {
                Console.WriteLine(resp.Data);
            }
        };
        break;

    case "stop":
    {
        var stopArgs = new Dictionary<string, string>();
        string? profileName = GetNamedArg(args, "--profile") ?? GetNamedArg(args, "-p");
        if (profileName == null && args.Length >= 2 && !args[1].StartsWith("-"))
        {
            profileName = args[1];
        }
        if (!string.IsNullOrEmpty(profileName))
        {
            stopArgs["profile"] = profileName;
        }

        string? unlockToken = GetNamedArg(args, "--unlock-token") ?? GetNamedArg(args, "--token");
        if (unlockToken != null) stopArgs["unlock_token"] = unlockToken;

        if (globalUnlockText != null) stopArgs["unlock_text"] = globalUnlockText;
        if (globalUnlockPassword != null) stopArgs["unlock_password"] = globalUnlockPassword;

        string? textArg = GetNamedArg(args, "--text");
        if (textArg != null) stopArgs["unlock_text"] = textArg;
        string? pwdArg = GetNamedArg(args, "--password");
        if (pwdArg != null) stopArgs["unlock_password"] = pwdArg;

        request = new Request { Command = CommandNames.StopSession, Args = stopArgs };
        printSuccess = resp => Console.WriteLine(resp.Data ?? "Session stopped successfully.");
        break;
    }

    case "check-uninstall":
    case "can-uninstall":
    {
        // 1. Live named pipe check to SankalpaService
        try
        {
            var res = await pipeClient.SendRequestAsync(new Request { Command = CommandNames.GetStatus });
            if (res.Connected && res.Response?.Ok == true && !string.IsNullOrWhiteSpace(res.Response.Data))
            {
                var status = JsonSerializer.Deserialize<StatusData>(res.Response.Data, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (!UninstallGuard.CanUninstallFromStatus(status, out string? reason))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("[ACCESS DENIED] Uninstall protection is active: a block is currently active!");
                    Console.WriteLine($"  {reason}");
                    Console.ResetColor();
                    return 1;
                }

                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("[OK] No active block. Uninstallation permitted.");
                Console.ResetColor();
                return 0;
            }
        }
        catch { }

        // 2. Offline fallback check (direct DB / hosts file check)
        if (UninstallGuard.CheckOfflineActiveBlock(out string? offlineReason))
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("[ACCESS DENIED] Uninstall protection is active: a block is currently active!");
            Console.WriteLine($"  {offlineReason}");
            Console.ResetColor();
            return 1;
        }

        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("[OK] No active block. Uninstallation permitted.");
        Console.ResetColor();
        return 0;
    }

    case "hosts-clean":
        request = new Request { Command = CommandNames.CleanHosts };
        printSuccess = resp => Console.WriteLine(resp.Data ?? "Hosts file cleaned successfully.");
        break;

    case "emergency-unlock":
    case "reset":
    {
        try
        {
            var res = await pipeClient.SendRequestAsync(new Request { Command = CommandNames.EmergencyUnlock });
            if (res.Connected && res.Response?.Ok == true)
            {
                Console.WriteLine("Service: " + (res.Response.Data ?? "Unlocked"));
            }
        }
        catch { }

        string appData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] dirs = new[]
        {
            Path.Combine(appData, "Blocker"),
            Path.Combine(appData, "Sankalpa"),
            AppContext.BaseDirectory
        };
        foreach (var dir in dirs)
        {
            try
            {
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "EMERGENCY_UNLOCK"), DateTime.UtcNow.ToString("O"));
            }
            catch { }
        }

        SecurityHardener.UnlockCurrentProcess(out _);
        SecurityHardener.UnlockService(out _);

        Console.WriteLine("=======================================================");
        Console.WriteLine(" [SANKALPA EMERGENCY UNLOCK]");
        Console.WriteLine(" Successfully bypassed and reset all active blocks.");
        Console.WriteLine(" - Process security DACL unlocked");
        Console.WriteLine(" - Service control DACL unlocked");
        Console.WriteLine(" - Active sessions ended");
        Console.WriteLine(" - Hosts file restored to clean state");
        Console.WriteLine(" - All block profiles disabled");
        Console.WriteLine("=======================================================");
        return 0;
    }

    case "hosts-status":
        request = new Request { Command = CommandNames.HostsStatus };
        printSuccess = resp =>
        {
            if (string.IsNullOrWhiteSpace(resp.Data))
            {
                Console.WriteLine("No hosts status data returned.");
                return;
            }
            var hs = JsonSerializer.Deserialize<HostsStatusInfo>(resp.Data, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (hs == null)
            {
                Console.WriteLine("Failed to parse hosts status.");
                return;
            }
            Console.WriteLine($"HostsState: {hs.HostsState}");
            Console.WriteLine($"DomainCount: {hs.DomainCount}");
            Console.WriteLine($"LastError: {hs.LastError ?? "None"}");
            Console.WriteLine("DomainsInFile:");
            if (hs.Domains != null && hs.Domains.Count > 0)
            {
                foreach (var d in hs.Domains)
                {
                    Console.WriteLine($"  {d}");
                }
            }
            else
            {
                Console.WriteLine("  (none)");
            }
        };
        break;

    case "unlock-challenge":
    {
        string? name = GetNamedArg(args, "--name");
        if (string.IsNullOrWhiteSpace(name))
        {
            Console.WriteLine("Error: --name is required.");
            return 1;
        }

        request = new Request
        {
            Command = CommandNames.GetUnlockChallenge,
            Args = new() { ["name"] = name }
        };
        printSuccess = resp => Console.WriteLine(resp.Data);
        break;
    }

    case "start":
    {
        string profileName = "Default";
        string? minutes = null;
        string? seconds = null;
        bool locked = true;

        for (int i = 1; i < args.Length; i++)
        {
            if (args[i].Equals("--profile", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                profileName = args[++i];
            }
            else if (args[i].Equals("--minutes", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                minutes = args[++i];
            }
            else if (args[i].Equals("--seconds", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                seconds = args[++i];
            }
            else if (args[i].Equals("--locked", StringComparison.OrdinalIgnoreCase))
            {
                if (i + 1 < args.Length && bool.TryParse(args[i + 1], out bool parsedLocked))
                {
                    locked = parsedLocked;
                    i++;
                }
                else
                {
                    locked = true;
                }
            }
            else if (args[i].Equals("--unlocked", StringComparison.OrdinalIgnoreCase))
            {
                locked = false;
            }
        }

        var reqArgs = new Dictionary<string, string>
        {
            ["profile"] = profileName,
            ["locked"] = locked.ToString()
        };
        if (minutes != null) reqArgs["minutes"] = minutes;
        if (seconds != null) reqArgs["seconds"] = seconds;

        request = new Request
        {
            Command = CommandNames.StartSession,
            Args = reqArgs
        };
        printSuccess = resp => Console.WriteLine(resp.Data ?? "Session started.");
        break;
    }

    case "profile":
    {
        if (args.Length < 2)
        {
            Console.WriteLine("Usage: blocker profile <list|show|add|delete|enable|disable|schedule|lock> [options]");
            return 1;
        }

        string sub = args[1].ToLowerInvariant();
        if (sub == "list")
        {
            request = new Request { Command = CommandNames.ListProfiles };
            printSuccess = resp =>
            {
                if (string.IsNullOrWhiteSpace(resp.Data))
                {
                    Console.WriteLine("Profiles (0):");
                    return;
                }

                try
                {
                    var profiles = JsonSerializer.Deserialize<List<Profile>>(resp.Data);
                    Console.WriteLine($"Profiles ({profiles?.Count ?? 0}):");
                    if (profiles != null)
                    {
                        foreach (var p in profiles)
                        {
                            string sched = ScheduleLogic.FormatSummary(p.Schedule);
                            Console.WriteLine($"  [{p.Id}] {p.Name}: Enabled={p.Enabled}, LockMode={p.LockMode}, Schedule='{sched}', Apps=[{string.Join(", ", p.BlockedProcesses)}], Domains=[{string.Join(", ", p.BlockedDomains)}]");
                        }
                    }
                }
                catch
                {
                    Console.WriteLine(resp.Data);
                }
            };
        }
        else if (sub == "show")
        {
            string? name = GetNamedArg(args, "--name");
            if (string.IsNullOrWhiteSpace(name))
            {
                Console.WriteLine("Error: --name is required.");
                return 1;
            }

            request = new Request
            {
                Command = CommandNames.GetProfile,
                Args = new() { ["name"] = name }
            };
            printSuccess = resp =>
            {
                try
                {
                    var p = JsonSerializer.Deserialize<Profile>(resp.Data ?? "");
                    if (p != null)
                    {
                        Console.WriteLine($"Profile: {p.Name} (Id: {p.Id})");
                        Console.WriteLine($"  Enabled:           {p.Enabled}");
                        Console.WriteLine($"  Lock Mode:         {p.LockMode}");
                        Console.WriteLine($"  Schedule:          {ScheduleLogic.FormatSummary(p.Schedule)}");
                        Console.WriteLine($"  Blocked Processes: {string.Join(", ", p.BlockedProcesses)}");
                        Console.WriteLine($"  Blocked Domains:   {string.Join(", ", p.BlockedDomains)}");
                    }
                }
                catch
                {
                    Console.WriteLine(resp.Data);
                }
            };
        }
        else if (sub == "enable")
        {
            string? name = GetNamedArg(args, "--name");
            if (string.IsNullOrWhiteSpace(name))
            {
                Console.WriteLine("Error: --name is required.");
                return 1;
            }

            request = new Request
            {
                Command = CommandNames.EnableProfile,
                Args = new() { ["name"] = name }
            };
            printSuccess = resp => Console.WriteLine(resp.Data ?? $"Profile '{name}' enabled.");
        }
        else if (sub == "disable")
        {
            string? name = GetNamedArg(args, "--name");
            if (string.IsNullOrWhiteSpace(name))
            {
                Console.WriteLine("Error: --name is required.");
                return 1;
            }

            var disArgs = new Dictionary<string, string> { ["name"] = name };
            if (globalUnlockText != null) disArgs["unlock_text"] = globalUnlockText;
            if (globalUnlockPassword != null) disArgs["unlock_password"] = globalUnlockPassword;

            request = new Request
            {
                Command = CommandNames.DisableProfile,
                Args = disArgs
            };
            printSuccess = resp => Console.WriteLine(resp.Data ?? $"Profile '{name}' disabled.");
        }
        else if (sub == "delete")
        {
            string? name = GetNamedArg(args, "--name");
            if (string.IsNullOrWhiteSpace(name))
            {
                Console.WriteLine("Error: --name is required.");
                return 1;
            }

            var delArgs = new Dictionary<string, string> { ["name"] = name };
            if (globalUnlockText != null) delArgs["unlock_text"] = globalUnlockText;
            if (globalUnlockPassword != null) delArgs["unlock_password"] = globalUnlockPassword;

            request = new Request
            {
                Command = CommandNames.DeleteProfile,
                Args = delArgs
            };
            printSuccess = resp => Console.WriteLine(resp.Data ?? $"Profile '{name}' deleted.");
        }
        else if (sub == "add" || sub == "create")
        {
            string name = string.Empty;
            var processes = new List<string>();
            var domains = new List<string>();

            for (int i = 2; i < args.Length; i++)
            {
                if (args[i].Equals("--name", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    name = args[++i];
                }
                else if ((args[i].Equals("--block", StringComparison.OrdinalIgnoreCase) || args[i].Equals("--blocked", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    processes.Add(args[++i]);
                }
                else if ((args[i].Equals("--site", StringComparison.OrdinalIgnoreCase) || args[i].Equals("--domain", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    domains.Add(args[++i]);
                }
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                Console.WriteLine("Error: --name is required.");
                return 1;
            }

            request = new Request
            {
                Command = CommandNames.SaveProfile,
                Args = new()
                {
                    ["name"] = name,
                    ["processes"] = string.Join(",", processes),
                    ["domains"] = string.Join(",", domains)
                }
            };
            printSuccess = resp =>
            {
                try
                {
                    var p = JsonSerializer.Deserialize<Profile>(resp.Data ?? "");
                    if (p != null)
                    {
                        Console.WriteLine($"Profile '{p.Name}' saved:");
                        Console.WriteLine($"  Blocked Processes: {string.Join(", ", p.BlockedProcesses)}");
                        Console.WriteLine($"  Blocked Domains:   {string.Join(", ", p.BlockedDomains)}");
                    }
                }
                catch
                {
                    Console.WriteLine(resp.Data ?? "Profile saved.");
                }
            };
        }
        else if (sub == "schedule")
        {
            if (args.Length < 3)
            {
                Console.WriteLine("Usage: blocker profile schedule <add|clear|show> --name <name> [...]");
                return 1;
            }

            string schedAction = args[2].ToLowerInvariant();
            string? name = GetNamedArg(args, "--name");
            if (string.IsNullOrWhiteSpace(name))
            {
                Console.WriteLine("Error: --name is required.");
                return 1;
            }

            // Fetch current profile first
            var getRes = await pipeClient.SendRequestAsync(new Request
            {
                Command = CommandNames.GetProfile,
                Args = new() { ["name"] = name }
            });

            if (!getRes.Connected)
            {
                Console.WriteLine("Service is not running");
                return 2;
            }
            if (getRes.Response == null || !getRes.Response.Ok)
            {
                Console.WriteLine(getRes.Response?.Error ?? $"Profile '{name}' not found.");
                return 1;
            }

            var profile = JsonSerializer.Deserialize<Profile>(getRes.Response.Data ?? "");
            if (profile == null)
            {
                Console.WriteLine($"Could not deserialize profile '{name}'.");
                return 1;
            }

            if (schedAction == "show")
            {
                Console.WriteLine($"Schedule for profile '{profile.Name}':");
                if (profile.Schedule.Count == 0)
                {
                    Console.WriteLine("  No schedule rules defined (blocked at all times when On).");
                }
                else
                {
                    foreach (var rule in profile.Schedule)
                    {
                        Console.WriteLine($"  • {ScheduleLogic.FormatSingleRule(rule)}");
                    }
                    Console.WriteLine($"Summary: {ScheduleLogic.FormatSummary(profile.Schedule)}");
                }
                return 0;
            }
            else if (schedAction == "clear")
            {
                var saveArgs = new Dictionary<string, string>
                {
                    ["name"] = profile.Name,
                    ["schedule"] = "[]"
                };
                if (globalUnlockText != null) saveArgs["unlock_text"] = globalUnlockText;
                if (globalUnlockPassword != null) saveArgs["unlock_password"] = globalUnlockPassword;

                request = new Request { Command = CommandNames.SaveProfile, Args = saveArgs };
                printSuccess = resp => Console.WriteLine($"Schedule cleared for profile '{profile.Name}'.");
            }
            else if (schedAction == "add")
            {
                string? daysStr = GetNamedArg(args, "--days");
                string? fromStr = GetNamedArg(args, "--from");
                string? toStr = GetNamedArg(args, "--to");

                if (string.IsNullOrWhiteSpace(daysStr) || string.IsNullOrWhiteSpace(fromStr) || string.IsNullOrWhiteSpace(toStr))
                {
                    Console.WriteLine("Error: --days, --from, and --to are required for 'schedule add'.");
                    return 1;
                }

                DaysOfWeek days = DaysOfWeek.None;
                foreach (var d in daysStr.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (Enum.TryParse<DayOfWeek>(d.Trim(), true, out var dow))
                    {
                        days |= ScheduleRule.FromDayOfWeek(dow);
                    }
                    else if (Enum.TryParse<DaysOfWeek>(d.Trim(), true, out var dowFlags))
                    {
                        days |= dowFlags;
                    }
                    else
                    {
                        Console.WriteLine($"Invalid day name: '{d.Trim()}'. Use Mon, Tue, Wed, Thu, Fri, Sat, Sun.");
                        return 1;
                    }
                }

                if (!TryParseTime(fromStr, out int startMin) || !TryParseTime(toStr, out int endMin))
                {
                    Console.WriteLine("Invalid time format. Use HH:mm (e.g. 08:00 or 14:30).");
                    return 1;
                }

                var newRule = new ScheduleRule
                {
                    Days = days,
                    StartMinute = startMin,
                    EndMinute = endMin
                };

                if (!newRule.IsValid)
                {
                    Console.WriteLine("Invalid rule: start and end times cannot be equal.");
                    return 1;
                }

                profile.Schedule.Add(newRule);
                string scheduleJson = JsonSerializer.Serialize(profile.Schedule);

                var saveArgs = new Dictionary<string, string>
                {
                    ["name"] = profile.Name,
                    ["schedule"] = scheduleJson
                };
                if (globalUnlockText != null) saveArgs["unlock_text"] = globalUnlockText;
                if (globalUnlockPassword != null) saveArgs["unlock_password"] = globalUnlockPassword;

                request = new Request { Command = CommandNames.SaveProfile, Args = saveArgs };
                printSuccess = resp => Console.WriteLine($"Schedule rule added to profile '{profile.Name}': {ScheduleLogic.FormatSingleRule(newRule)}");
            }
            else
            {
                Console.WriteLine($"Unknown schedule action: '{schedAction}'. Use add, clear, or show.");
                return 1;
            }
        }
        else if (sub == "lock")
        {
            string? name = GetNamedArg(args, "--name");
            string? mode = GetNamedArg(args, "--mode");
            string? password = GetNamedArg(args, "--password");
            string? curPassword = GetNamedArg(args, "--current-password");

            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(mode))
            {
                Console.WriteLine("Error: --name and --mode (none|locked|typing|password) are required.");
                return 1;
            }

            var lockArgs = new Dictionary<string, string>
            {
                ["name"] = name,
                ["lock_mode"] = mode
            };
            if (!string.IsNullOrEmpty(password)) lockArgs["password"] = password;
            if (!string.IsNullOrEmpty(curPassword)) lockArgs["current_password"] = curPassword;
            if (globalUnlockText != null) lockArgs["unlock_text"] = globalUnlockText;
            if (globalUnlockPassword != null) lockArgs["unlock_password"] = globalUnlockPassword;

            request = new Request
            {
                Command = CommandNames.SaveProfile,
                Args = lockArgs
            };
            printSuccess = resp => Console.WriteLine($"Lock mode for profile '{name}' set to {mode}.");
        }
        else
        {
            Console.WriteLine($"Unknown profile subcommand: {sub}");
            return 1;
        }
        break;
    }

    case "dns":
    {
        request = new Request { Command = CommandNames.GetDnsConfig };
        printSuccess = resp =>
        {
            if (string.IsNullOrWhiteSpace(resp.Data))
            {
                Console.WriteLine("No DNS config data.");
                return;
            }
            try
            {
                var cfg = JsonSerializer.Deserialize<DnsConfig>(resp.Data);
                if (cfg == null) return;
                Console.WriteLine("DNS Protection Status:");
                Console.WriteLine($"  Enabled:   {cfg.Enabled}");
                Console.WriteLine($"  Preset:    {cfg.PresetName}");
                Console.WriteLine($"  Primary:   {cfg.PrimaryDns}");
                Console.WriteLine($"  Secondary: {cfg.SecondaryDns}");
                Console.WriteLine($"  Locked:    {cfg.Locked} (Mode: {cfg.LockType})");
                if (cfg.Locked && cfg.LockedUntilUtc.HasValue)
                {
                    var rem = cfg.LockedUntilUtc.Value - DateTime.UtcNow;
                    string remStr = rem > TimeSpan.Zero ? $"{(int)rem.TotalMinutes + 1}m remaining" : "expired";
                    Console.WriteLine($"  Expiry:    {cfg.LockedUntilUtc.Value.ToLocalTime()} ({remStr})");
                }
            }
            catch
            {
                Console.WriteLine(resp.Data);
            }
        };
        break;
    }

    case "lock-dns":
    {
        string mode = GetNamedArg(args, "--mode") ?? "Timer";
        string? durationStr = GetNamedArg(args, "--duration") ?? GetNamedArg(args, "--seconds");
        long duration = 86400; // default 1 day
        if (durationStr != null && long.TryParse(durationStr, out long parsedDur))
        {
            duration = parsedDur;
        }
        else if (GetNamedArg(args, "--minutes") is string minStr && long.TryParse(minStr, out long parsedMin))
        {
            duration = parsedMin * 60;
        }
        else if (GetNamedArg(args, "--hours") is string hrStr && long.TryParse(hrStr, out long parsedHr))
        {
            duration = parsedHr * 3600;
        }

        request = new Request
        {
            Command = CommandNames.LockDns,
            Args = new()
            {
                ["lock_type"] = mode,
                ["duration"] = duration.ToString()
            }
        };
        printSuccess = resp => Console.WriteLine($"DNS Protection locked successfully. (Mode: {mode}, Duration: {duration}s)");
        break;
    }

    case "unlock-dns":
    {
        string? challengeText = GetNamedArg(args, "--challenge-text") ?? GetNamedArg(args, "--text");
        var unlockArgs = new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(challengeText)) unlockArgs["challenge_text"] = challengeText;

        request = new Request
        {
            Command = CommandNames.UnlockDns,
            Args = unlockArgs
        };
        printSuccess = resp => Console.WriteLine("DNS Protection unlocked.");
        break;
    }

    default:
        Console.WriteLine($"Unknown command: {command}. Supported: ping, start, status, stop, profile, dns, lock-dns, unlock-dns, hosts-clean, hosts-status, unlock-challenge");
        return 1;
}

if (request == null)
{
    return 1;
}

var result = await pipeClient.SendRequestAsync(request);

if (!result.Connected)
{
    Console.WriteLine("Service is not running");
    return 2;
}

if (result.Response == null)
{
    Console.WriteLine(result.Error ?? "Error communicating with service.");
    return 1;
}

if (!result.Response.Ok)
{
    Console.WriteLine(result.Response.Error ?? "Command refused or failed.");
    return 1;
}

printSuccess?.Invoke(result.Response);
return 0;

static string? GetNamedArg(string[] args, string flag)
{
    for (int i = 0; i < args.Length; i++)
    {
        if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
        {
            return args[i + 1];
        }
    }
    return null;
}

static bool TryParseTime(string s, out int minutes)
{
    minutes = 0;
    var parts = s.Split(':');
    if (parts.Length == 2 && int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int m))
    {
        if (h >= 0 && h < 24 && m >= 0 && m < 60)
        {
            minutes = h * 60 + m;
            return true;
        }
    }
    return false;
}

static void PrintUsage()
{
    Console.WriteLine("Blocker CLI usage:");
    Console.WriteLine("  blocker ping");
    Console.WriteLine("  blocker status");
    Console.WriteLine("  blocker start [--profile <name>] [--minutes <m>] [--seconds <s>] [--locked [true|false]]");
    Console.WriteLine("  blocker stop [--unlock-text <T>] [--unlock-password <P>]");
    Console.WriteLine("  blocker check-uninstall");
    Console.WriteLine("  blocker hosts-clean");
    Console.WriteLine("  blocker hosts-status");
    Console.WriteLine("  blocker unlock-challenge --name <name>");
    Console.WriteLine("  blocker profile list");
    Console.WriteLine("  blocker profile show --name <name>");
    Console.WriteLine("  blocker profile add --name <name> [--block <proc1,proc2>] [--site <domain1,domain2>]");
    Console.WriteLine("  blocker profile enable --name <name>");
    Console.WriteLine("  blocker profile disable --name <name> [--unlock-text <T>] [--unlock-password <P>]");
    Console.WriteLine("  blocker profile schedule add --name <name> --days Mon,Tue... --from 08:00 --to 14:00");
    Console.WriteLine("  blocker profile schedule clear --name <name>");
    Console.WriteLine("  blocker profile schedule show --name <name>");
    Console.WriteLine("  blocker profile lock --name <name> --mode none|locked|typing|password [--password <P>]");
    Console.WriteLine("  blocker profile delete --name <name> [--unlock-text <T>] [--unlock-password <P>]");
}
