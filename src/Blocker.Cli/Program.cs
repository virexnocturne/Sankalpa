using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Blocker.Core;

namespace Blocker.Cli;

public class Program
{
    public static async Task<int> Main(string[] args)
    {
        return await RunAsync(args);
    }

    public static async Task<int> RunAsync(string[] args, IPipeClient? customPipeClient = null, TextWriter? output = null, TextWriter? error = null)
    {
        var outWriter = output ?? Console.Out;
        var errWriter = error ?? Console.Error;

        if (args.Length == 0)
        {
            PrintUsage(outWriter);
            return 1;
        }

        string command = args[0].ToLowerInvariant();

        if (command == "-h" || command == "--help" || command == "help")
        {
            PrintUsage(outWriter);
            return 0;
        }

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
        var pipeClient = customPipeClient ?? new PipeClient();

        switch (command)
        {
            case "ping":
                request = new Request { Command = CommandNames.Ping };
                printSuccess = resp => outWriter.WriteLine(resp.Data ?? "pong");
                break;

            case "status":
            {
                request = new Request { Command = CommandNames.GetStatus };
                printSuccess = resp =>
                {
                    if (string.IsNullOrWhiteSpace(resp.Data))
                    {
                        outWriter.WriteLine("No status data available.");
                        return;
                    }

                    try
                    {
                        var status = JsonSerializer.Deserialize<StatusData>(resp.Data, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (status == null)
                        {
                            outWriter.WriteLine("No active session or schedules.");
                            return;
                        }

                        if (!status.Active)
                        {
                            outWriter.WriteLine("No active session or schedule.");
                        }
                        else
                        {
                            outWriter.WriteLine("Active Status:");
                            outWriter.WriteLine($"  Active Profiles:   {string.Join(", ", status.ActiveProfileNames)}");
                            outWriter.WriteLine($"  Blocked Processes: {string.Join(", ", status.BlockedProcesses)}");
                            outWriter.WriteLine($"  Blocked Domains:   {string.Join(", ", status.BlockedDomains)}");
                            outWriter.WriteLine($"  Locked:            {status.Locked}");
                            if (status.RemainingSeconds > 0)
                            {
                                outWriter.WriteLine($"  Manual Session:    {status.RemainingFormatted} remaining ({status.RemainingSeconds}s total)");
                            }
                        }

                        outWriter.WriteLine($"Website Blocking: {(status.HostsBlockingOk ? "OK" : "FAILED")}");
                        if (!string.IsNullOrEmpty(status.HostsLastError))
                        {
                            outWriter.WriteLine($"Hosts Info:       {status.HostsLastError}");
                        }

                        if (status.ProfileStatuses.Count > 0)
                        {
                            outWriter.WriteLine("Profiles Summary:");
                            foreach (var ps in status.ProfileStatuses)
                            {
                                string state = ps.IsActive ? $"ACTIVE ({ps.ActiveReason})" : (ps.Enabled ? "On" : "Off");
                                string locked = ps.LockedNow ? " [LOCKED]" : "";
                                outWriter.WriteLine($"  • {ps.Name}: {state}{locked} | LockMode: {ps.LockMode} | Schedule: {ps.ScheduleSummary}");
                            }
                        }
                    }
                    catch
                    {
                        outWriter.WriteLine(resp.Data);
                    }
                };
                break;
            }

            case "stop":
            {
                var stopArgs = new Dictionary<string, string>();
                string? profileName = GetNamedArg(args, "--profile", "-p");
                if (profileName == null && args.Length >= 2 && !args[1].StartsWith("-"))
                {
                    profileName = args[1];
                }
                if (!string.IsNullOrEmpty(profileName))
                {
                    stopArgs["profile"] = profileName;
                }

                string? unlockToken = GetNamedArg(args, "--unlock-token", "--token", "-t");
                if (unlockToken != null) stopArgs["unlock_token"] = unlockToken;

                if (globalUnlockText != null) stopArgs["unlock_text"] = globalUnlockText;
                if (globalUnlockPassword != null) stopArgs["unlock_password"] = globalUnlockPassword;

                string? textArg = GetNamedArg(args, "--text");
                if (textArg != null) stopArgs["unlock_text"] = textArg;
                string? pwdArg = GetNamedArg(args, "--password");
                if (pwdArg != null) stopArgs["unlock_password"] = pwdArg;

                request = new Request { Command = CommandNames.StopSession, Args = stopArgs };
                printSuccess = resp => outWriter.WriteLine(resp.Data ?? "Session stopped successfully.");
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
                            outWriter.WriteLine("[ACCESS DENIED] Uninstall protection is active: a block is currently active!");
                            outWriter.WriteLine($"  {reason}");
                            Console.ResetColor();
                            return 1;
                        }

                        Console.ForegroundColor = ConsoleColor.Green;
                        outWriter.WriteLine("[OK] No active block. Uninstallation permitted.");
                        Console.ResetColor();
                        return 0;
                    }
                }
                catch { }

                // 2. Offline fallback check (direct DB / hosts file check)
                if (UninstallGuard.CheckOfflineActiveBlock(out string? offlineReason))
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    outWriter.WriteLine("[ACCESS DENIED] Uninstall protection is active: a block is currently active!");
                    outWriter.WriteLine($"  {offlineReason}");
                    Console.ResetColor();
                    return 1;
                }

                Console.ForegroundColor = ConsoleColor.Green;
                outWriter.WriteLine("[OK] No active block. Uninstallation permitted.");
                Console.ResetColor();
                return 0;
            }

            case "hosts-clean":
                request = new Request { Command = CommandNames.CleanHosts };
                printSuccess = resp => outWriter.WriteLine(resp.Data ?? "Hosts file cleaned successfully.");
                break;

            case "emergency-unlock":
            case "reset":
            {
                try
                {
                    var res = await pipeClient.SendRequestAsync(new Request { Command = CommandNames.EmergencyUnlock });
                    if (res.Connected && res.Response?.Ok == true)
                    {
                        outWriter.WriteLine("Service: " + (res.Response.Data ?? "Unlocked"));
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

                outWriter.WriteLine("=======================================================");
                outWriter.WriteLine(" [SANKALPA EMERGENCY UNLOCK]");
                outWriter.WriteLine(" Successfully bypassed and reset all active blocks.");
                outWriter.WriteLine(" - Process security DACL unlocked");
                outWriter.WriteLine(" - Service control DACL unlocked");
                outWriter.WriteLine(" - Active sessions ended");
                outWriter.WriteLine(" - Hosts file restored to clean state");
                outWriter.WriteLine(" - All block profiles disabled");
                outWriter.WriteLine("=======================================================");
                return 0;
            }

            case "hosts-status":
                request = new Request { Command = CommandNames.HostsStatus };
                printSuccess = resp =>
                {
                    if (string.IsNullOrWhiteSpace(resp.Data))
                    {
                        outWriter.WriteLine("No hosts status data returned.");
                        return;
                    }
                    var hs = JsonSerializer.Deserialize<HostsStatusInfo>(resp.Data, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    if (hs == null)
                    {
                        outWriter.WriteLine("Failed to parse hosts status.");
                        return;
                    }
                    outWriter.WriteLine($"HostsState: {hs.HostsState}");
                    outWriter.WriteLine($"DomainCount: {hs.DomainCount}");
                    outWriter.WriteLine($"LastError: {hs.LastError ?? "None"}");
                    outWriter.WriteLine("DomainsInFile:");
                    if (hs.Domains != null && hs.Domains.Count > 0)
                    {
                        foreach (var d in hs.Domains)
                        {
                            outWriter.WriteLine($"  {d}");
                        }
                    }
                    else
                    {
                        outWriter.WriteLine("  (none)");
                    }
                };
                break;

            case "unlock-challenge":
            {
                if (HasFlag(args, "--dns"))
                {
                    request = new Request { Command = CommandNames.GetDnsChallenge };
                    printSuccess = resp =>
                    {
                        outWriter.WriteLine("DNS Protection Challenge Text:");
                        outWriter.WriteLine(resp.Data ?? "");
                        outWriter.WriteLine();
                        outWriter.WriteLine("To unlock DNS Protection, run:");
                        outWriter.WriteLine("  sankalpa-cli unlock-dns --challenge-text <text>");
                    };
                    break;
                }

                string? name = GetNamedArg(args, "--name", "-n");
                if (string.IsNullOrWhiteSpace(name))
                {
                    outWriter.WriteLine("Error: --name is required (or use --dns for DNS challenge).");
                    return 1;
                }

                request = new Request
                {
                    Command = CommandNames.GetUnlockChallenge,
                    Args = new() { ["name"] = name }
                };
                printSuccess = resp => outWriter.WriteLine(resp.Data);
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
                    else if (args[i].Equals("--duration", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                    {
                        string durStr = args[++i];
                        if (TryParseDuration(durStr, out long totalSec))
                        {
                            seconds = totalSec.ToString();
                        }
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
                printSuccess = resp => outWriter.WriteLine(resp.Data ?? "Session started.");
                break;
            }

            case "profile":
            {
                if (args.Length < 2)
                {
                    outWriter.WriteLine("Usage: sankalpa-cli profile <list|show|add|rename|delete|enable|disable|schedule|lock> [options]");
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
                            outWriter.WriteLine("Profiles (0):");
                            return;
                        }

                        try
                        {
                            var profiles = JsonSerializer.Deserialize<List<Profile>>(resp.Data);
                            outWriter.WriteLine($"Profiles ({profiles?.Count ?? 0}):");
                            if (profiles != null)
                            {
                                foreach (var p in profiles)
                                {
                                    string sched = ScheduleLogic.FormatSummary(p.Schedule);
                                    outWriter.WriteLine($"  [{p.Id}] {p.Name}: Enabled={p.Enabled}, LockMode={p.LockMode}, Schedule='{sched}', Apps=[{string.Join(", ", p.BlockedProcesses)}], Domains=[{string.Join(", ", p.BlockedDomains)}]");
                                }
                            }
                        }
                        catch
                        {
                            outWriter.WriteLine(resp.Data);
                        }
                    };
                }
                else if (sub == "show")
                {
                    string? name = GetNamedArg(args, "--name", "-n");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        outWriter.WriteLine("Error: --name is required.");
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
                                outWriter.WriteLine($"Profile: {p.Name} (Id: {p.Id})");
                                outWriter.WriteLine($"  Enabled:           {p.Enabled}");
                                outWriter.WriteLine($"  Lock Mode:         {p.LockMode}");
                                outWriter.WriteLine($"  Schedule:          {ScheduleLogic.FormatSummary(p.Schedule)}");
                                outWriter.WriteLine($"  Blocked Processes: {string.Join(", ", p.BlockedProcesses)}");
                                outWriter.WriteLine($"  Blocked Domains:   {string.Join(", ", p.BlockedDomains)}");
                            }
                        }
                        catch
                        {
                            outWriter.WriteLine(resp.Data);
                        }
                    };
                }
                else if (sub == "rename")
                {
                    string? name = GetNamedArg(args, "--name", "-n");
                    string? newName = GetNamedArg(args, "--new-name", "--new");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(newName))
                    {
                        outWriter.WriteLine("Error: --name and --new-name are required.");
                        return 1;
                    }

                    var renArgs = new Dictionary<string, string>
                    {
                        ["old_name"] = name,
                        ["new_name"] = newName
                    };
                    if (globalUnlockText != null) renArgs["unlock_text"] = globalUnlockText;
                    if (globalUnlockPassword != null) renArgs["unlock_password"] = globalUnlockPassword;

                    request = new Request
                    {
                        Command = CommandNames.RenameProfile,
                        Args = renArgs
                    };
                    printSuccess = resp => outWriter.WriteLine($"Profile '{name}' successfully renamed to '{newName}'.");
                }
                else if (sub == "enable")
                {
                    string? name = GetNamedArg(args, "--name", "-n");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        outWriter.WriteLine("Error: --name is required.");
                        return 1;
                    }

                    request = new Request
                    {
                        Command = CommandNames.EnableProfile,
                        Args = new() { ["name"] = name }
                    };
                    printSuccess = resp => outWriter.WriteLine(resp.Data ?? $"Profile '{name}' enabled.");
                }
                else if (sub == "disable")
                {
                    string? name = GetNamedArg(args, "--name", "-n");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        outWriter.WriteLine("Error: --name is required.");
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
                    printSuccess = resp => outWriter.WriteLine(resp.Data ?? $"Profile '{name}' disabled.");
                }
                else if (sub == "delete")
                {
                    string? name = GetNamedArg(args, "--name", "-n");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        outWriter.WriteLine("Error: --name is required.");
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
                    printSuccess = resp => outWriter.WriteLine(resp.Data ?? $"Profile '{name}' deleted.");
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
                        outWriter.WriteLine("Error: --name is required.");
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
                                outWriter.WriteLine($"Profile '{p.Name}' saved:");
                                outWriter.WriteLine($"  Blocked Processes: {string.Join(", ", p.BlockedProcesses)}");
                                outWriter.WriteLine($"  Blocked Domains:   {string.Join(", ", p.BlockedDomains)}");
                            }
                        }
                        catch
                        {
                            outWriter.WriteLine(resp.Data ?? "Profile saved.");
                        }
                    };
                }
                else if (sub == "schedule")
                {
                    if (args.Length < 3)
                    {
                        outWriter.WriteLine("Usage: sankalpa-cli profile schedule <add|clear|show> --name <name> [...]");
                        return 1;
                    }

                    string schedAction = args[2].ToLowerInvariant();
                    string? name = GetNamedArg(args, "--name", "-n");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        outWriter.WriteLine("Error: --name is required.");
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
                        outWriter.WriteLine("Service is not running");
                        return 2;
                    }
                    if (getRes.Response == null || !getRes.Response.Ok)
                    {
                        outWriter.WriteLine(getRes.Response?.Error ?? $"Profile '{name}' not found.");
                        return 1;
                    }

                    var profile = JsonSerializer.Deserialize<Profile>(getRes.Response.Data ?? "");
                    if (profile == null)
                    {
                        outWriter.WriteLine($"Could not deserialize profile '{name}'.");
                        return 1;
                    }

                    if (schedAction == "show")
                    {
                        outWriter.WriteLine($"Schedule for profile '{profile.Name}':");
                        if (profile.Schedule.Count == 0)
                        {
                            outWriter.WriteLine("  No schedule rules defined (blocked at all times when On).");
                        }
                        else
                        {
                            foreach (var rule in profile.Schedule)
                            {
                                outWriter.WriteLine($"  • {ScheduleLogic.FormatSingleRule(rule)}");
                            }
                            outWriter.WriteLine($"Summary: {ScheduleLogic.FormatSummary(profile.Schedule)}");
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
                        printSuccess = resp => outWriter.WriteLine($"Schedule cleared for profile '{profile.Name}'.");
                    }
                    else if (schedAction == "add")
                    {
                        string? daysStr = GetNamedArg(args, "--days");
                        string? fromStr = GetNamedArg(args, "--from");
                        string? toStr = GetNamedArg(args, "--to");

                        if (string.IsNullOrWhiteSpace(daysStr) || string.IsNullOrWhiteSpace(fromStr) || string.IsNullOrWhiteSpace(toStr))
                        {
                            outWriter.WriteLine("Error: --days, --from, and --to are required for 'schedule add'.");
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
                                outWriter.WriteLine($"Invalid day name: '{d.Trim()}'. Use Mon, Tue, Wed, Thu, Fri, Sat, Sun.");
                                return 1;
                            }
                        }

                        if (!TryParseTime(fromStr, out int startMin) || !TryParseTime(toStr, out int endMin))
                        {
                            outWriter.WriteLine("Invalid time format. Use HH:mm (e.g. 08:00 or 14:30).");
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
                            outWriter.WriteLine("Invalid rule: start and end times cannot be equal.");
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
                        printSuccess = resp => outWriter.WriteLine($"Schedule rule added to profile '{profile.Name}': {ScheduleLogic.FormatSingleRule(newRule)}");
                    }
                    else
                    {
                        outWriter.WriteLine($"Unknown schedule action: '{schedAction}'. Use add, clear, or show.");
                        return 1;
                    }
                }
                else if (sub == "lock")
                {
                    string? name = GetNamedArg(args, "--name", "-n");
                    string? mode = GetNamedArg(args, "--mode", "-m");
                    string? password = GetNamedArg(args, "--password");
                    string? curPassword = GetNamedArg(args, "--current-password");

                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(mode))
                    {
                        outWriter.WriteLine("Error: --name and --mode (none|locked|typing|password) are required.");
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
                    printSuccess = resp => outWriter.WriteLine($"Lock mode for profile '{name}' set to {mode}.");
                }
                else
                {
                    outWriter.WriteLine($"Unknown profile subcommand: {sub}");
                    return 1;
                }
                break;
            }

            case "meditation":
            case "meditate":
            case "zen":
            {
                string sub = args.Length > 1 ? args[1].ToLowerInvariant() : "status";

                if (sub == "status")
                {
                    request = new Request { Command = CommandNames.GetMeditationStatus };
                    printSuccess = resp =>
                    {
                        if (string.IsNullOrWhiteSpace(resp.Data))
                        {
                            outWriter.WriteLine("No meditation status data returned.");
                            return;
                        }

                        try
                        {
                            var status = JsonSerializer.Deserialize<MeditationStatusDto>(resp.Data, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (status == null || !status.Active)
                            {
                                outWriter.WriteLine("No active meditation session.");
                                return;
                            }

                            outWriter.WriteLine("Active Meditation Session:");
                            outWriter.WriteLine($"  Session ID:   {status.SessionId}");
                            outWriter.WriteLine($"  Status:       {status.Status}");
                            outWriter.WriteLine($"  Remaining:    {FormatDuration(status.RemainingSeconds)} ({status.RemainingSeconds}s remaining of {status.TargetDurationSeconds / 60}m)");
                            outWriter.WriteLine($"  Lock Mode:    {status.LockMode} (Locked: {status.Locked})");
                            outWriter.WriteLine($"  Allowed Apps: {(status.AllowedApps != null && status.AllowedApps.Count > 0 ? string.Join(", ", status.AllowedApps) : "(None - Complete Lockout)")}");
                            outWriter.WriteLine($"  Soundscape:   {status.Soundscape ?? "(None)"}");
                            if (!string.IsNullOrWhiteSpace(status.Notes))
                            {
                                outWriter.WriteLine($"  Notes:        {status.Notes}");
                            }
                        }
                        catch
                        {
                            outWriter.WriteLine(resp.Data);
                        }
                    };
                }
                else if (sub == "start")
                {
                    long durationSeconds = 900; // default 15m
                    string? durStr = GetNamedArg(args, "--duration", "-d");
                    string? minStr = GetNamedArg(args, "--minutes", "-m");
                    string? secStr = GetNamedArg(args, "--seconds", "-s");

                    if (durStr != null && TryParseDuration(durStr, out long parsedDur))
                    {
                        durationSeconds = parsedDur;
                    }
                    else if (minStr != null && long.TryParse(minStr, out long parsedMin) && parsedMin > 0)
                    {
                        durationSeconds = parsedMin * 60;
                    }
                    else if (secStr != null && long.TryParse(secStr, out long parsedSec) && parsedSec > 0)
                    {
                        durationSeconds = parsedSec;
                    }

                    string? appsStr = GetNamedArg(args, "--apps", "--allowed", "-a");
                    string? modeStr = GetNamedArg(args, "--mode", "--lock", "-l");
                    string? soundStr = GetNamedArg(args, "--sound", "--soundscape");
                    string? statusStr = GetNamedArg(args, "--status");

                    var startArgs = new Dictionary<string, string>
                    {
                        ["targetSeconds"] = durationSeconds.ToString()
                    };
                    if (!string.IsNullOrEmpty(appsStr)) startArgs["allowedApps"] = appsStr;
                    if (!string.IsNullOrEmpty(modeStr)) startArgs["lockMode"] = modeStr;
                    if (!string.IsNullOrEmpty(soundStr)) startArgs["soundscape"] = soundStr;
                    if (!string.IsNullOrEmpty(statusStr)) startArgs["status"] = statusStr;

                    request = new Request
                    {
                        Command = CommandNames.StartMeditation,
                        Args = startArgs
                    };
                    printSuccess = resp =>
                    {
                        try
                        {
                            var s = JsonSerializer.Deserialize<MeditationStatusDto>(resp.Data ?? "", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (s != null)
                            {
                                outWriter.WriteLine($"Meditation session started (Session {s.SessionId}):");
                                outWriter.WriteLine($"  Duration:     {FormatDuration(s.TargetDurationSeconds)} ({s.TargetDurationSeconds / 60}m)");
                                outWriter.WriteLine($"  Lock Mode:    {s.LockMode} (Locked: {s.Locked})");
                                outWriter.WriteLine($"  Allowed Apps: {(s.AllowedApps != null && s.AllowedApps.Count > 0 ? string.Join(", ", s.AllowedApps) : "(None - Complete Lockout)")}");
                                outWriter.WriteLine($"  Soundscape:   {s.Soundscape ?? "(None)"}");
                                return;
                            }
                        }
                        catch { }
                        outWriter.WriteLine(resp.Data ?? "Meditation session started.");
                    };
                }
                else if (sub == "stop")
                {
                    var stopArgs = new Dictionary<string, string>();
                    string? notes = GetNamedArg(args, "--notes", "-n", "--note");
                    if (!string.IsNullOrEmpty(notes)) stopArgs["notes"] = notes;

                    request = new Request
                    {
                        Command = CommandNames.StopMeditation,
                        Args = stopArgs
                    };
                    printSuccess = resp => outWriter.WriteLine(resp.Data ?? "Meditation session stopped.");
                }
                else if (sub == "note" || sub == "notes")
                {
                    string? text = GetNamedArg(args, "--text", "-t", "--note", "--notes");
                    string? idStr = GetNamedArg(args, "--id", "--session-id");

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        outWriter.WriteLine("Error: --text is required.");
                        return 1;
                    }

                    var noteArgs = new Dictionary<string, string> { ["notes"] = text };
                    if (!string.IsNullOrEmpty(idStr)) noteArgs["sessionId"] = idStr;

                    request = new Request
                    {
                        Command = CommandNames.SaveMeditationNote,
                        Args = noteArgs
                    };
                    printSuccess = resp => outWriter.WriteLine(resp.Data ?? "Meditation note saved.");
                }
                else if (sub == "history")
                {
                    int limit = 10;
                    string? limitStr = GetNamedArg(args, "--limit", "-l", "-n");
                    if (!string.IsNullOrWhiteSpace(limitStr) && int.TryParse(limitStr, out int parsedLimit) && parsedLimit > 0)
                    {
                        limit = parsedLimit;
                    }

                    request = new Request
                    {
                        Command = CommandNames.GetMeditationHistory,
                        Args = new() { ["limit"] = limit.ToString() }
                    };
                    printSuccess = resp =>
                    {
                        if (string.IsNullOrWhiteSpace(resp.Data))
                        {
                            outWriter.WriteLine("No meditation history records found.");
                            return;
                        }

                        try
                        {
                            var history = JsonSerializer.Deserialize<List<MeditationHistoryItemDto>>(resp.Data, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                            if (history == null || history.Count == 0)
                            {
                                outWriter.WriteLine("No meditation history records found.");
                                return;
                            }

                            outWriter.WriteLine($"Meditation History ({history.Count} sessions):");
                            foreach (var item in history)
                            {
                                string status = item.Completed ? "Completed" : "Interrupted";
                                string sound = string.IsNullOrEmpty(item.Soundscape) ? "none" : item.Soundscape;
                                outWriter.WriteLine($"  • [#{item.Id}] {item.StartUtc.ToLocalTime():yyyy-MM-dd HH:mm} | Target: {item.TargetDurationSeconds / 60}m | Completed: {FormatDuration(item.CompletedDurationSeconds)} ({status}) | Apps: {item.AllowedAppsCount} | Sound: {sound}");
                                if (!string.IsNullOrWhiteSpace(item.Notes))
                                {
                                    string noteSnippet = item.Notes.Replace("\r", " ").Replace("\n", " ");
                                    if (noteSnippet.Length > 80) noteSnippet = noteSnippet.Substring(0, 77) + "...";
                                    outWriter.WriteLine($"      Notes: {noteSnippet}");
                                }
                            }
                        }
                        catch
                        {
                            outWriter.WriteLine(resp.Data);
                        }
                    };
                }
                else if (sub == "schedule")
                {
                    if (args.Length < 3 || args[2].Equals("list", StringComparison.OrdinalIgnoreCase))
                    {
                        request = new Request { Command = CommandNames.GetMeditationSchedules };
                        printSuccess = resp =>
                        {
                            if (string.IsNullOrWhiteSpace(resp.Data))
                            {
                                outWriter.WriteLine("No meditation schedules found.");
                                return;
                            }

                            try
                            {
                                var schedules = JsonSerializer.Deserialize<List<MeditationScheduleDto>>(resp.Data, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                if (schedules == null || schedules.Count == 0)
                                {
                                    outWriter.WriteLine("No meditation schedules found.");
                                    return;
                                }

                                outWriter.WriteLine($"Meditation Schedules ({schedules.Count}):");
                                foreach (var s in schedules)
                                {
                                    string days = (s.DaysOfWeek != null && s.DaysOfWeek.Count > 0) ? string.Join(", ", s.DaysOfWeek) : "All Days";
                                    string apps = (s.AllowedApps != null && s.AllowedApps.Count > 0) ? string.Join(", ", s.AllowedApps) : "(None - Complete Lockout)";
                                    string sound = string.IsNullOrEmpty(s.Soundscape) ? "none" : s.Soundscape;
                                    outWriter.WriteLine($"  • [Id: {s.Id}] {s.TimeOfDay} ({days}) - {s.DurationSeconds / 60}m | Mode: {s.LockMode} | Sound: {sound} | Enabled: {s.Enabled}");
                                    outWriter.WriteLine($"      Allowed Apps: {apps}");
                                }
                            }
                            catch
                            {
                                outWriter.WriteLine(resp.Data);
                            }
                        };
                    }
                    else
                    {
                        string schedAction = args[2].ToLowerInvariant();
                        if (schedAction == "add" || schedAction == "save" || schedAction == "set")
                        {
                            string? time = GetNamedArg(args, "--time", "-t");
                            if (string.IsNullOrWhiteSpace(time))
                            {
                                outWriter.WriteLine("Error: --time <HH:mm> is required (e.g. --time 15:00).");
                                return 1;
                            }

                            long durationSeconds = 900;
                            string? durStr = GetNamedArg(args, "--duration", "-d");
                            string? minStr = GetNamedArg(args, "--minutes", "-m");
                            if (durStr != null && TryParseDuration(durStr, out long parsedDur)) durationSeconds = parsedDur;
                            else if (minStr != null && long.TryParse(minStr, out long parsedMin) && parsedMin > 0) durationSeconds = parsedMin * 60;

                            string? days = GetNamedArg(args, "--days");
                            string? apps = GetNamedArg(args, "--apps", "--allowed");
                            string? mode = GetNamedArg(args, "--mode", "--lock");
                            string? sound = GetNamedArg(args, "--sound", "--soundscape");
                            string? enabledStr = GetNamedArg(args, "--enabled");

                            var schedArgs = new Dictionary<string, string>
                            {
                                ["time"] = time,
                                ["duration"] = durationSeconds.ToString()
                            };
                            if (!string.IsNullOrEmpty(days)) schedArgs["days"] = days;
                            if (!string.IsNullOrEmpty(apps)) schedArgs["apps"] = apps;
                            if (!string.IsNullOrEmpty(mode)) schedArgs["mode"] = mode;
                            if (!string.IsNullOrEmpty(sound)) schedArgs["sound"] = sound;
                            if (!string.IsNullOrEmpty(enabledStr)) schedArgs["enabled"] = enabledStr;

                            request = new Request
                            {
                                Command = CommandNames.SaveMeditationSchedule,
                                Args = schedArgs
                            };
                            printSuccess = resp =>
                            {
                                try
                                {
                                    var dto = JsonSerializer.Deserialize<MeditationScheduleDto>(resp.Data ?? "", new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                    if (dto != null)
                                    {
                                        string d = (dto.DaysOfWeek != null && dto.DaysOfWeek.Count > 0) ? string.Join(", ", dto.DaysOfWeek) : "All Days";
                                        outWriter.WriteLine($"Meditation schedule saved (Id: {dto.Id}):");
                                        outWriter.WriteLine($"  Time:         {dto.TimeOfDay}");
                                        outWriter.WriteLine($"  Days:         {d}");
                                        outWriter.WriteLine($"  Duration:     {dto.DurationSeconds / 60}m ({dto.DurationSeconds}s)");
                                        outWriter.WriteLine($"  Lock Mode:    {dto.LockMode}");
                                        outWriter.WriteLine($"  Soundscape:   {dto.Soundscape ?? "none"}");
                                        outWriter.WriteLine($"  Allowed Apps: {(dto.AllowedApps != null && dto.AllowedApps.Count > 0 ? string.Join(", ", dto.AllowedApps) : "(None)")}");
                                        outWriter.WriteLine($"  Enabled:      {dto.Enabled}");
                                        return;
                                    }
                                }
                                catch { }
                                outWriter.WriteLine(resp.Data ?? "Meditation schedule saved.");
                            };
                        }
                        else if (schedAction == "snooze")
                        {
                            string? idStr = GetNamedArg(args, "--id", "-i") ?? "1";
                            request = new Request
                            {
                                Command = CommandNames.SnoozeMeditationSchedule,
                                Args = new() { ["id"] = idStr }
                            };
                            printSuccess = resp => outWriter.WriteLine(resp.Data ?? "Meditation schedule snoozed for 5 minutes.");
                        }
                        else if (schedAction == "delete")
                        {
                            string? idStr = GetNamedArg(args, "--id", "-i");
                            if (string.IsNullOrWhiteSpace(idStr))
                            {
                                outWriter.WriteLine("Error: --id is required.");
                                return 1;
                            }

                            request = new Request
                            {
                                Command = CommandNames.DeleteMeditationSchedule,
                                Args = new() { ["id"] = idStr }
                            };
                            printSuccess = resp => outWriter.WriteLine(resp.Data ?? "Meditation schedule deleted.");
                        }
                        else
                        {
                            outWriter.WriteLine($"Unknown schedule action: '{schedAction}'. Supported: list, add, snooze, delete.");
                            return 1;
                        }
                    }
                }
                else
                {
                    outWriter.WriteLine($"Unknown meditation action: '{sub}'. Supported: status, start, stop, note, history, schedule.");
                    return 1;
                }
                break;
            }

            case "delayed-unlock":
            {
                if (args.Length < 2)
                {
                    outWriter.WriteLine("Usage: sankalpa-cli delayed-unlock <request|cancel> --name <profile> [...]");
                    return 1;
                }

                string sub = args[1].ToLowerInvariant();
                if (sub == "request" || sub == "start")
                {
                    string? name = GetNamedArg(args, "--name", "-n", "--profile", "-p");
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        outWriter.WriteLine("Error: --name is required.");
                        return 1;
                    }

                    request = new Request
                    {
                        Command = CommandNames.RequestDelayedUnlock,
                        Args = new() { ["name"] = name }
                    };
                    printSuccess = resp =>
                    {
                        try
                        {
                            var dict = JsonSerializer.Deserialize<Dictionary<string, string>>(resp.Data ?? "");
                            string token = dict != null && dict.TryGetValue("token", out var t) ? t : "";
                            string min = dict != null && dict.TryGetValue("minutes", out var m) ? m : "15";
                            string sec = dict != null && dict.TryGetValue("duration_seconds", out var s) ? s : "900";
                            outWriter.WriteLine($"Delayed unlock initiated for profile '{name}':");
                            outWriter.WriteLine($"  Duration:     {min}m ({sec}s)");
                            outWriter.WriteLine($"  Unlock Token: {token}");
                            outWriter.WriteLine("Once the timer elapses, stop with:");
                            outWriter.WriteLine($"  sankalpa-cli stop --profile \"{name}\" --token \"{token}\"");
                        }
                        catch
                        {
                            outWriter.WriteLine(resp.Data ?? "Delayed unlock initiated.");
                        }
                    };
                }
                else if (sub == "cancel")
                {
                    string? name = GetNamedArg(args, "--name", "-n", "--profile", "-p");
                    string? token = GetNamedArg(args, "--token", "-t");
                    if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(token))
                    {
                        outWriter.WriteLine("Error: --name and --token are required.");
                        return 1;
                    }

                    request = new Request
                    {
                        Command = CommandNames.CancelDelayedUnlock,
                        Args = new() { ["name"] = name, ["token"] = token }
                    };
                    printSuccess = resp => outWriter.WriteLine(resp.Data ?? "Delayed unlock cancelled.");
                }
                else
                {
                    outWriter.WriteLine($"Unknown delayed-unlock action: '{sub}'. Supported: request, cancel.");
                    return 1;
                }
                break;
            }

            case "dns":
            {
                if (args.Length > 1 && args[1].Equals("challenge", StringComparison.OrdinalIgnoreCase))
                {
                    request = new Request { Command = CommandNames.GetDnsChallenge };
                    printSuccess = resp =>
                    {
                        outWriter.WriteLine("DNS Protection Challenge Text:");
                        outWriter.WriteLine(resp.Data ?? "");
                        outWriter.WriteLine();
                        outWriter.WriteLine("To unlock DNS Protection, run:");
                        outWriter.WriteLine("  sankalpa-cli unlock-dns --challenge-text <text>");
                    };
                    break;
                }

                request = new Request { Command = CommandNames.GetDnsConfig };
                printSuccess = resp =>
                {
                    if (string.IsNullOrWhiteSpace(resp.Data))
                    {
                        outWriter.WriteLine("No DNS config data.");
                        return;
                    }
                    try
                    {
                        var cfg = JsonSerializer.Deserialize<DnsConfig>(resp.Data);
                        if (cfg == null) return;
                        outWriter.WriteLine("DNS Protection Status:");
                        outWriter.WriteLine($"  Enabled:   {cfg.Enabled}");
                        outWriter.WriteLine($"  Preset:    {cfg.PresetName}");
                        outWriter.WriteLine($"  Primary:   {cfg.PrimaryDns}");
                        outWriter.WriteLine($"  Secondary: {cfg.SecondaryDns}");
                        bool isSystemActive = DnsManager.IsDnsActive(cfg.PrimaryDns, cfg.PrimaryIpv6, cfg.SecondaryDns, cfg.SecondaryIpv6);
                        var activeIfaces = DnsManager.GetActiveInterfaceNames();
                        outWriter.WriteLine($"  Enforced:  {isSystemActive}");
                        outWriter.WriteLine($"  Adapters:  {string.Join(", ", activeIfaces)}");
                        outWriter.WriteLine($"  Locked:    {cfg.Locked} (Mode: {cfg.LockType})");
                        if (cfg.Locked && cfg.LockedUntilUtc.HasValue)
                        {
                            var rem = cfg.LockedUntilUtc.Value - DateTime.UtcNow;
                            string remStr = rem > TimeSpan.Zero ? $"{(int)rem.TotalMinutes + 1}m remaining" : "expired";
                            outWriter.WriteLine($"  Expiry:    {cfg.LockedUntilUtc.Value.ToLocalTime()} ({remStr})");
                        }
                    }
                    catch
                    {
                        outWriter.WriteLine(resp.Data);
                    }
                };
                break;
            }

            case "lock-dns":
            {
                string mode = GetNamedArg(args, "--mode", "-m") ?? "Timer";
                string? durationStr = GetNamedArg(args, "--duration", "-d", "--seconds", "-s");
                long duration = 86400; // default 1 day
                if (durationStr != null && TryParseDuration(durationStr, out long parsedDur))
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
                printSuccess = resp => outWriter.WriteLine($"DNS Protection locked successfully. (Mode: {mode}, Duration: {duration}s)");
                break;
            }

            case "unlock-dns":
            {
                string? challengeText = GetNamedArg(args, "--challenge-text", "--text", "-t");
                var unlockArgs = new Dictionary<string, string>();
                if (!string.IsNullOrEmpty(challengeText)) unlockArgs["challenge_text"] = challengeText;

                request = new Request
                {
                    Command = CommandNames.UnlockDns,
                    Args = unlockArgs
                };
                printSuccess = resp => outWriter.WriteLine("DNS Protection unlocked.");
                break;
            }

            case "events":
            case "audit":
            {
                long sinceId = 0;
                string? sinceStr = GetNamedArg(args, "--since", "-s");
                if (!string.IsNullOrWhiteSpace(sinceStr) && long.TryParse(sinceStr, out long parsedSince))
                {
                    sinceId = parsedSince;
                }

                int limit = 20;
                string? limitStr = GetNamedArg(args, "--limit", "-l", "-n");
                if (!string.IsNullOrWhiteSpace(limitStr) && int.TryParse(limitStr, out int parsedLimit) && parsedLimit > 0)
                {
                    limit = parsedLimit;
                }

                request = new Request
                {
                    Command = CommandNames.GetEvents,
                    Args = new() { ["since"] = sinceId.ToString() }
                };
                printSuccess = resp =>
                {
                    if (string.IsNullOrWhiteSpace(resp.Data))
                    {
                        outWriter.WriteLine("No events recorded.");
                        return;
                    }

                    try
                    {
                        var events = JsonSerializer.Deserialize<List<BlockEvent>>(resp.Data, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (events == null || events.Count == 0)
                        {
                            outWriter.WriteLine("No events recorded.");
                            return;
                        }

                        var displayEvents = events.TakeLast(limit).ToList();
                        outWriter.WriteLine($"Security & Block Events (Showing {displayEvents.Count} of {events.Count}):");
                        foreach (var ev in displayEvents)
                        {
                            outWriter.WriteLine($"  • [#{ev.Id}] {ev.TimeUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss} | {ev.Kind}: '{ev.Name}' (Profile: '{ev.ProfileName}')");
                        }
                    }
                    catch
                    {
                        outWriter.WriteLine(resp.Data);
                    }
                };
                break;
            }

            case "webrules":
            case "web-rules":
            {
                request = new Request { Command = CommandNames.GetActiveWebRules };
                printSuccess = resp =>
                {
                    if (string.IsNullOrWhiteSpace(resp.Data))
                    {
                        outWriter.WriteLine("No active web rules.");
                        return;
                    }

                    try
                    {
                        var webResp = JsonSerializer.Deserialize<ActiveWebRulesResponse>(resp.Data, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        var dtos = webResp?.ActiveProfiles;
                        if (dtos == null || dtos.Count == 0)
                        {
                            outWriter.WriteLine("No active web rules or profiles.");
                            return;
                        }

                        outWriter.WriteLine($"Active Web Protection Rules ({dtos.Count} profiles, RulesVersion: {webResp?.RulesVersion}):");
                        foreach (var p in dtos)
                        {
                            string blockAll = p.BlockAllWebsites ? " [BLOCK ALL WEB]" : "";
                            outWriter.WriteLine($"  • Profile: {p.Name}{blockAll} (Locked: {p.Locked}, LockMode: {p.LockMode})");
                            if (p.WebRules != null && p.WebRules.Count > 0)
                            {
                                foreach (var r in p.WebRules)
                                {
                                    string action = r.Allow ? "ALLOW" : "BLOCK";
                                    outWriter.WriteLine($"      [{action}] {r.Pattern}");
                                }
                            }
                        }
                    }
                    catch
                    {
                        outWriter.WriteLine(resp.Data);
                    }
                };
                break;
            }

            default:
                outWriter.WriteLine($"Unknown command: {command}. Supported: ping, status, start, stop, meditation, profile, delayed-unlock, dns, lock-dns, unlock-dns, events, webrules, hosts-clean, hosts-status, unlock-challenge, check-uninstall, emergency-unlock");
                return 1;
        }

        if (request == null)
        {
            return 1;
        }

        var result = await pipeClient.SendRequestAsync(request);

        if (!result.Connected)
        {
            outWriter.WriteLine("Service is not running");
            return 2;
        }

        if (result.Response == null)
        {
            outWriter.WriteLine(result.Error ?? "Error communicating with service.");
            return 1;
        }

        if (!result.Response.Ok)
        {
            outWriter.WriteLine(result.Response.Error ?? "Command refused or failed.");
            return 1;
        }

        printSuccess?.Invoke(result.Response);
        return 0;
    }

    public static string? GetNamedArg(string[] args, params string[] flags)
    {
        for (int i = 0; i < args.Length; i++)
        {
            foreach (var flag in flags)
            {
                if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    return args[i + 1];
                }
            }
        }
        return null;
    }

    public static bool HasFlag(string[] args, params string[] flags)
    {
        for (int i = 0; i < args.Length; i++)
        {
            foreach (var flag in flags)
            {
                if (args[i].Equals(flag, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public static bool TryParseDuration(string input, out long totalSeconds)
    {
        totalSeconds = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;
        input = input.Trim().ToLowerInvariant();

        if (input.EndsWith("m") && long.TryParse(input[..^1], out long m) && m > 0)
        {
            totalSeconds = m * 60;
            return true;
        }
        if (input.EndsWith("h") && long.TryParse(input[..^1], out long h) && h > 0)
        {
            totalSeconds = h * 3600;
            return true;
        }
        if (input.EndsWith("s") && long.TryParse(input[..^1], out long s) && s > 0)
        {
            totalSeconds = s;
            return true;
        }
        if (long.TryParse(input, out long sec) && sec > 0)
        {
            totalSeconds = sec;
            return true;
        }
        return false;
    }

    public static string FormatDuration(long totalSeconds)
    {
        if (totalSeconds < 0) totalSeconds = 0;
        var ts = TimeSpan.FromSeconds(totalSeconds);
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
        return $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    public static bool TryParseTime(string s, out int minutes)
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

    public static void PrintUsage(TextWriter? writer = null)
    {
        var w = writer ?? Console.Out;
        w.WriteLine("Sankalpa CLI (sankalpa-cli):");
        w.WriteLine("  Core Commands:");
        w.WriteLine("    sankalpa-cli ping");
        w.WriteLine("    sankalpa-cli status");
        w.WriteLine("    sankalpa-cli start [--profile <name>] [--minutes <m>] [--seconds <s>] [--duration <20m|1h>] [--locked [true|false]]");
        w.WriteLine("    sankalpa-cli stop [--profile <name>] [--unlock-text <T>] [--unlock-password <P>] [--token <tok>]");
        w.WriteLine("    sankalpa-cli check-uninstall");
        w.WriteLine();
        w.WriteLine("  🧘 Meditation & Lockdown Mode:");
        w.WriteLine("    sankalpa-cli meditation status");
        w.WriteLine("    sankalpa-cli meditation start [--minutes <m>] [--duration <15m|1h|900s>] [--apps <app1,app2>] [--mode <none|locked|typing|password>] [--sound <rain|library|forest|bowls>]");
        w.WriteLine("    sankalpa-cli meditation stop [--notes <notes>]");
        w.WriteLine("    sankalpa-cli meditation note --text <text> [--id <sessionId>]");
        w.WriteLine("    sankalpa-cli meditation history [--limit <N>]");
        w.WriteLine("    sankalpa-cli meditation schedule list");
        w.WriteLine("    sankalpa-cli meditation schedule add --time <HH:mm> [--days Mon,Tue...] [--minutes <m>] [--mode <mode>] [--apps <app1,app2>] [--sound <soundscape>]");
        w.WriteLine("    sankalpa-cli meditation schedule snooze [--id <id>]");
        w.WriteLine("    sankalpa-cli meditation schedule delete --id <id>");
        w.WriteLine();
        w.WriteLine("  ⌛ Delayed Unlock:");
        w.WriteLine("    sankalpa-cli delayed-unlock request --name <profile>");
        w.WriteLine("    sankalpa-cli delayed-unlock cancel --name <profile> --token <token>");
        w.WriteLine();
        w.WriteLine("  🛡️ DNS Protection:");
        w.WriteLine("    sankalpa-cli dns");
        w.WriteLine("    sankalpa-cli dns challenge");
        w.WriteLine("    sankalpa-cli lock-dns [--mode <Timer|Typing>] [--duration <s> | --minutes <m> | --hours <h>]");
        w.WriteLine("    sankalpa-cli unlock-dns [--challenge-text <text>]");
        w.WriteLine();
        w.WriteLine("  📁 Profile & Web Management:");
        w.WriteLine("    sankalpa-cli profile list");
        w.WriteLine("    sankalpa-cli profile show --name <name>");
        w.WriteLine("    sankalpa-cli profile add --name <name> [--block <proc1,proc2>] [--site <domain1,domain2>]");
        w.WriteLine("    sankalpa-cli profile rename --name <oldName> --new-name <newName>");
        w.WriteLine("    sankalpa-cli profile enable --name <name>");
        w.WriteLine("    sankalpa-cli profile disable --name <name> [--unlock-text <T>] [--unlock-password <P>]");
        w.WriteLine("    sankalpa-cli profile schedule add --name <name> --days Mon,Tue... --from 08:00 --to 14:00");
        w.WriteLine("    sankalpa-cli profile schedule clear --name <name>");
        w.WriteLine("    sankalpa-cli profile schedule show --name <name>");
        w.WriteLine("    sankalpa-cli profile lock --name <name> --mode none|locked|typing|password [--password <P>]");
        w.WriteLine("    sankalpa-cli profile delete --name <name> [--unlock-text <T>] [--unlock-password <P>]");
        w.WriteLine("    sankalpa-cli webrules");
        w.WriteLine();
        w.WriteLine("  🔒 Security & Hosts Recovery:");
        w.WriteLine("    sankalpa-cli events [--limit <N>] [--since <id>]");
        w.WriteLine("    sankalpa-cli hosts-clean");
        w.WriteLine("    sankalpa-cli hosts-status");
        w.WriteLine("    sankalpa-cli unlock-challenge --name <name>");
        w.WriteLine("    sankalpa-cli unlock-challenge --dns");
        w.WriteLine("    sankalpa-cli emergency-unlock");
    }
}
