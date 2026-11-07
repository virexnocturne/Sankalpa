using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Blocker.Core;

public static class UninstallGuard
{
    public static bool CanUninstallFromStatus(StatusData? status, out string? reason)
    {
        if (status != null && status.Active)
        {
            if (status.RemainingSeconds > 0)
            {
                reason = $"Active block session '{status.Profile}' has {status.RemainingFormatted} remaining (Locked: {status.Locked}).";
                return false;
            }

            if (status.ActiveProfileNames.Count > 0)
            {
                reason = $"Active block profile(s) running: {string.Join(", ", status.ActiveProfileNames)}.";
                return false;
            }

            if ((status.BlockedProcesses != null && status.BlockedProcesses.Count > 0) ||
                (status.BlockedDomains != null && status.BlockedDomains.Count > 0))
            {
                reason = "Active application or website blocking rules are currently being enforced.";
                return false;
            }

            reason = "A block session or schedule is currently active.";
            return false;
        }

        reason = null;
        return true;
    }

    public static bool CheckDatabaseForActiveBlock(string dbPath, DateTime utcNow, DateTime localNow, out string? reason)
    {
        reason = null;
        if (!File.Exists(dbPath)) return false;

        try
        {
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly
            };

            using var conn = new SqliteConnection(csb.ToString());
            conn.Open();

            // 1. Check BlockSessions table
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='BlockSessions';";
                var tableExists = cmd.ExecuteScalar();
                if (tableExists != null)
                {
                    cmd.CommandText = "SELECT ProfileId, StartUtc, DurationSeconds, Locked, EndedUtc FROM BlockSessions WHERE EndedUtc IS NULL;";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        string startStr = reader.GetString(1);
                        int durationSeconds = reader.GetInt32(2);
                        int locked = reader.GetInt32(3);

                        if (DateTime.TryParse(startStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var startUtc))
                        {
                            if (startUtc.Kind != DateTimeKind.Utc)
                            {
                                startUtc = DateTime.SpecifyKind(startUtc, DateTimeKind.Utc);
                            }

                            DateTime endUtc = startUtc.AddSeconds(durationSeconds);
                            if (utcNow < endUtc)
                            {
                                int remSec = Math.Max(1, (int)(endUtc - utcNow).TotalSeconds);
                                reason = $"Active session in database (ends in {remSec}s, Locked: {locked == 1}).";
                                return true;
                            }
                        }
                    }
                }
            }

            // 2. Check Profiles table for active schedules or always-on enabled profiles
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='Profiles';";
                var tableExists = cmd.ExecuteScalar();
                if (tableExists != null)
                {
                    cmd.CommandText = "SELECT Name, Enabled, ScheduleJson FROM Profiles WHERE Enabled = 1;";
                    using var reader = cmd.ExecuteReader();
                    while (reader.Read())
                    {
                        string name = reader.GetString(0);
                        int enabled = reader.GetInt32(1);
                        string? schedJson = reader.IsDBNull(2) ? null : reader.GetString(2);

                        if (enabled == 1)
                        {
                            List<ScheduleRule>? rules = null;
                            if (!string.IsNullOrWhiteSpace(schedJson))
                            {
                                try
                                {
                                    rules = JsonSerializer.Deserialize<List<ScheduleRule>>(schedJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                                }
                                catch { }
                            }

                            if (rules == null || rules.Count == 0)
                            {
                                // Always-on enabled profile
                                reason = $"Profile '{name}' is enabled in always-on mode.";
                                return true;
                            }

                            if (ScheduleLogic.IsActive(rules, localNow))
                            {
                                reason = $"Profile '{name}' has a schedule actively blocking right now.";
                                return true;
                            }
                        }
                    }
                }
            }

            // 3. Check DnsSettings table for active lock
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name='DnsSettings';";
                var tableExists = cmd.ExecuteScalar();
                if (tableExists != null)
                {
                    cmd.CommandText = "SELECT Enabled, LockedUntilUtc FROM DnsSettings WHERE Id = 1;";
                    using var reader = cmd.ExecuteReader();
                    if (reader.Read())
                    {
                        int enabled = reader.GetInt32(0);
                        string? lockedUntilStr = reader.IsDBNull(1) ? null : reader.GetString(1);

                        if (enabled == 1 && !string.IsNullOrWhiteSpace(lockedUntilStr))
                        {
                            if (DateTime.TryParse(lockedUntilStr, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var lockedUntilUtc))
                            {
                                if (lockedUntilUtc.Kind != DateTimeKind.Utc)
                                {
                                    lockedUntilUtc = DateTime.SpecifyKind(lockedUntilUtc, DateTimeKind.Utc);
                                }

                                if (utcNow < lockedUntilUtc)
                                {
                                    reason = $"DNS lock active until {lockedUntilUtc:yyyy-MM-dd HH:mm:ss} UTC.";
                                    return true;
                                }
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            // Database might be locked by running service; if so, assume protected if service status is unclear
            if (ex is SqliteException sqlEx && sqlEx.SqliteErrorCode == 5) // SQLITE_BUSY
            {
                reason = "Database is actively locked by the running Sankalpa enforcer service.";
                return true;
            }
        }

        return false;
    }

    public static bool CheckSystemHostsForActiveBlock(out string? reason)
    {
        reason = null;
        try
        {
            string sysDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string hostsPath = Path.Combine(sysDir, "drivers", "etc", "hosts");
            if (File.Exists(hostsPath))
            {
                var lines = File.ReadAllLines(hostsPath);
                bool inBlock = false;
                int domainCount = 0;

                foreach (var line in lines)
                {
                    string trimmed = line.Trim();
                    if (trimmed.IndexOf(HostsFileBuilder.BeginMarker, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        trimmed.IndexOf("# === SANKALPA BLOCKER START ===", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        trimmed.IndexOf("# === BLOCKER START ===", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        inBlock = true;
                        continue;
                    }
                    if (trimmed.IndexOf(HostsFileBuilder.EndMarker, StringComparison.OrdinalIgnoreCase) >= 0 ||
                        trimmed.IndexOf("# === SANKALPA BLOCKER END ===", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        trimmed.IndexOf("# === BLOCKER END ===", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        inBlock = false;
                        continue;
                    }

                    if (inBlock && (trimmed.StartsWith("127.0.0.1") || trimmed.StartsWith("0.0.0.0") || trimmed.StartsWith("::1")))
                    {
                        domainCount++;
                    }
                }

                if (domainCount > 0)
                {
                    reason = $"System hosts file currently contains {domainCount} active website blocking redirects.";
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    public static bool CheckOfflineActiveBlock(out string? reason)
    {
        reason = null;
        DateTime utcNow = DateTime.UtcNow;
        DateTime localNow = DateTime.Now;

        string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string progData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);

        var candidateDirs = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "data"),
            Path.Combine(localApp, "Sankalpa", "data"),
            Path.Combine(localApp, "Blocker", "data"),
            Path.Combine(progData, "Sankalpa", "data"),
            Path.Combine(progData, "Blocker", "data")
        };

        foreach (var dir in candidateDirs)
        {
            string dbPath = Path.Combine(dir, "blocker.db");
            if (CheckDatabaseForActiveBlock(dbPath, utcNow, localNow, out reason))
            {
                return true;
            }
        }

        if (CheckSystemHostsForActiveBlock(out reason))
        {
            return true;
        }

        return false;
    }
}
