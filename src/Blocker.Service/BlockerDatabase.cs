using System.Globalization;
using System.Text.Json;
using Blocker.Core;
using Microsoft.Data.Sqlite;

namespace Blocker.Service;

public class BlockerDatabase : IBlockStore
{
    private readonly string _connectionString;
    private readonly string _dbPath;
    private readonly HostsFileManager _hostsManager;

    private readonly object _cacheLock = new();
    private List<Profile>? _cachedProfiles;
    private DnsConfig? _cachedDnsConfig;
    private List<BlockSession>? _cachedActiveSessions;
    private bool _hasLoadedActiveSessions;
    private DateTime _lastActiveSessionsFetchUtc = DateTime.MinValue;
    private static readonly TimeSpan ActiveSessionsCacheTtl = TimeSpan.FromMilliseconds(500);

    public void InvalidateCache()
    {
        lock (_cacheLock)
        {
            _cachedProfiles = null;
            _cachedDnsConfig = null;
            _cachedActiveSessions = null;
            _hasLoadedActiveSessions = false;
            _lastActiveSessionsFetchUtc = DateTime.MinValue;
        }
    }

    public bool ServiceElevated { get; set; } = true;
    public bool HostsBlockingOk { get; set; } = true;
    public HostsState HostsState { get; set; } = HostsState.Idle;
    public int HostsDomainCount { get; set; }
    public string? HostsLastError { get; set; }
    public HostsFileManager HostsManager => _hostsManager;
    public Dictionary<string, string> ActiveAllowedAppRoots { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public BlockerDatabase(string? customDbPath = null, HostsFileManager? customHostsManager = null)
    {
        _hostsManager = customHostsManager ?? new HostsFileManager();

        if (string.IsNullOrEmpty(customDbPath))
        {
            string dataDir;
            try
            {
                dataDir = Path.Combine(AppContext.BaseDirectory, "data");
                if (!Directory.Exists(dataDir))
                {
                    Directory.CreateDirectory(dataDir);
                }
            }
            catch
            {
                string localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                dataDir = Path.Combine(localApp, "Sankalpa", "data");
                Directory.CreateDirectory(dataDir);
            }
            _dbPath = Path.Combine(dataDir, "blocker.db");
        }
        else
        {
            _dbPath = customDbPath;
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 30
        }.ToString();

        Initialize();
    }

    private void Initialize()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;";
            cmd.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS Profiles (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                BlockedProcessesJson TEXT NOT NULL,
                BlockedDomainsJson TEXT NOT NULL DEFAULT '[]',
                Enabled INTEGER NOT NULL DEFAULT 0,
                ScheduleJson TEXT NOT NULL DEFAULT '[]',
                LockMode INTEGER NOT NULL DEFAULT 0,
                PasswordSalt TEXT NULL,
                PasswordHash TEXT NULL,
                WebRulesJson TEXT NOT NULL DEFAULT '[]',
                BlockAllWebsites INTEGER NOT NULL DEFAULT 0,
                SystemWide INTEGER NOT NULL DEFAULT 0,
                DelayedUnlockMinutes INTEGER NOT NULL DEFAULT 15
            );

            CREATE TABLE IF NOT EXISTS BlockSessions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProfileId INTEGER NOT NULL,
                StartUtc TEXT NOT NULL,
                DurationSeconds INTEGER NOT NULL,
                Locked INTEGER NOT NULL,
                EndedUtc TEXT NULL,
                FOREIGN KEY(ProfileId) REFERENCES Profiles(Id)
            );

            CREATE TABLE IF NOT EXISTS DnsSettings (
                Id INTEGER PRIMARY KEY CHECK (Id = 1),
                Enabled INTEGER NOT NULL DEFAULT 0,
                PresetName TEXT NOT NULL DEFAULT 'Cloudflare Family',
                PrimaryDns TEXT NOT NULL DEFAULT '1.1.1.3',
                SecondaryDns TEXT NOT NULL DEFAULT '1.0.0.3',
                PrimaryIpv6 TEXT NOT NULL DEFAULT '2606:4700:4700::1113',
                SecondaryIpv6 TEXT NOT NULL DEFAULT '2606:4700:4700::1003',
                Locked INTEGER NOT NULL DEFAULT 0,
                LockType TEXT NOT NULL DEFAULT 'Challenge',
                LockDurationSeconds INTEGER NOT NULL DEFAULT 3600,
                LockedUntilUtc TEXT NULL,
                UnlockChallenge TEXT NULL,
                ScheduleJson TEXT NOT NULL DEFAULT '[]'
            );

            INSERT OR IGNORE INTO DnsSettings (Id) VALUES (1);

            CREATE TABLE IF NOT EXISTS MeditationSessions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                StartUtc TEXT NOT NULL,
                TargetDurationSeconds INTEGER NOT NULL,
                CompletedDurationSeconds INTEGER NOT NULL DEFAULT 0,
                Completed INTEGER NOT NULL DEFAULT 0,
                Notes TEXT NULL,
                AllowedAppsJson TEXT NOT NULL DEFAULT '[]',
                LockMode INTEGER NOT NULL DEFAULT 0,
                Soundscape TEXT NULL,
                EndedUtc TEXT NULL,
                PasswordHash TEXT NULL,
                Status TEXT DEFAULT 'Active'
            );

            CREATE TABLE IF NOT EXISTS MeditationSchedules (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Enabled INTEGER NOT NULL DEFAULT 1,
                DaysOfWeekJson TEXT NOT NULL DEFAULT '[]',
                TimeOfDay TEXT NOT NULL DEFAULT '00:00',
                DurationSeconds INTEGER NOT NULL DEFAULT 900,
                AllowedAppsJson TEXT NOT NULL DEFAULT '[]',
                LockMode INTEGER NOT NULL DEFAULT 0,
                Soundscape TEXT NULL,
                LastSnoozedUtc TEXT NULL,
                PasswordHash TEXT NULL
            );
            """;
            cmd.ExecuteNonQuery();
        }

        // Schema migration via PRAGMA table_info & PRAGMA user_version
        EnsureColumnsExist(connection);

        // Seed "Default" profile if missing
        EnsureDefaultProfile(connection);

        // Single Schedule Migration & Initialization
        EnsureSingleMeditationSchedule(connection);
    }

    private void EnsureColumnsExist(SqliteConnection connection)
    {
        var existingCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "PRAGMA table_info(Profiles);";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                existingCols.Add(reader.GetString(1));
            }
        }

        var requiredCols = new Dictionary<string, string>
        {
            ["BlockedDomainsJson"] = "TEXT NOT NULL DEFAULT '[]'",
            ["Enabled"] = "INTEGER NOT NULL DEFAULT 0",
            ["ScheduleJson"] = "TEXT NOT NULL DEFAULT '[]'",
            ["LockMode"] = "INTEGER NOT NULL DEFAULT 0",
            ["PasswordSalt"] = "TEXT NULL",
            ["PasswordHash"] = "TEXT NULL",
            ["WebRulesJson"] = "TEXT NOT NULL DEFAULT '[]'",
            ["BlockAllWebsites"] = "INTEGER NOT NULL DEFAULT 0",
            ["SystemWide"] = "INTEGER NOT NULL DEFAULT 0",
            ["AllowanceJson"] = "TEXT NOT NULL DEFAULT '{}'",
            ["DelayedUnlockMinutes"] = "INTEGER NOT NULL DEFAULT 15"
        };

        foreach (var (colName, colDef) in requiredCols)
        {
            if (!existingCols.Contains(colName))
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = $"ALTER TABLE Profiles ADD COLUMN {colName} {colDef};";
                alterCmd.ExecuteNonQuery();
            }
        }

        // Ensure IPv6 columns in DnsSettings
        var existingDnsCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var dnsColsCmd = connection.CreateCommand())
        {
            dnsColsCmd.CommandText = "PRAGMA table_info(DnsSettings);";
            using var r = dnsColsCmd.ExecuteReader();
            while (r.Read())
            {
                existingDnsCols.Add(r.GetString(1));
            }
        }

        try
        {
            if (!existingDnsCols.Contains("PrimaryIpv6"))
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE DnsSettings ADD COLUMN PrimaryIpv6 TEXT NOT NULL DEFAULT '2606:4700:4700::1113';";
                alterCmd.ExecuteNonQuery();
            }
        }
        catch { }

        try
        {
            if (!existingDnsCols.Contains("SecondaryIpv6"))
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE DnsSettings ADD COLUMN SecondaryIpv6 TEXT NOT NULL DEFAULT '2606:4700:4700::1003';";
                alterCmd.ExecuteNonQuery();
            }
        }
        catch { }

        try
        {
            var medCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var medColsCmd = connection.CreateCommand();
            medColsCmd.CommandText = "PRAGMA table_info(MeditationSessions);";
            using var r = medColsCmd.ExecuteReader();
            while (r.Read())
            {
                medCols.Add(r.GetString(1));
            }
            if (medCols.Count > 0 && !medCols.Contains("EndedUtc"))
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE MeditationSessions ADD COLUMN EndedUtc TEXT NULL;";
                alterCmd.ExecuteNonQuery();
            }
            if (medCols.Count > 0 && !medCols.Contains("PasswordHash"))
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE MeditationSessions ADD COLUMN PasswordHash TEXT NULL;";
                alterCmd.ExecuteNonQuery();
            }
            if (medCols.Count > 0 && !medCols.Contains("Status"))
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE MeditationSessions ADD COLUMN Status TEXT DEFAULT 'Active';";
                alterCmd.ExecuteNonQuery();
            }
        }
        catch { }

        try
        {
            var schedCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var schedColsCmd = connection.CreateCommand();
            schedColsCmd.CommandText = "PRAGMA table_info(MeditationSchedules);";
            using var r = schedColsCmd.ExecuteReader();
            while (r.Read())
            {
                schedCols.Add(r.GetString(1));
            }
            if (schedCols.Count > 0 && !schedCols.Contains("PasswordHash"))
            {
                using var alterCmd = connection.CreateCommand();
                alterCmd.CommandText = "ALTER TABLE MeditationSchedules ADD COLUMN PasswordHash TEXT NULL;";
                alterCmd.ExecuteNonQuery();
            }
        }
        catch { }

        int currentVersion = 0;
        using (var verCheckCmd = connection.CreateCommand())
        {
            verCheckCmd.CommandText = "PRAGMA user_version;";
            currentVersion = Convert.ToInt32(verCheckCmd.ExecuteScalar() ?? 0);
        }

        if (currentVersion < 3)
        {
            // Migrate existing BlockedDomainsJson into WebRulesJson and derive BlockedDomains
            using (var selectCmd = connection.CreateCommand())
            {
                selectCmd.CommandText = "SELECT Id, BlockedDomainsJson, WebRulesJson FROM Profiles;";
                var updates = new List<(int id, string webRulesJson, string derivedDomainsJson)>();
                using (var reader = selectCmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        int id = reader.GetInt32(0);
                        string domJson = reader.GetString(1);
                        string rulesJson = reader.GetString(2);

                        if ((rulesJson == "[]" || string.IsNullOrWhiteSpace(rulesJson)) && domJson != "[]")
                        {
                            var oldDomains = JsonSerializer.Deserialize<List<string>>(domJson) ?? new List<string>();
                            var webRules = oldDomains.Select(d => new WebRule { Pattern = d, Allow = false }).ToList();
                            var derived = WebRuleMatcher.DeriveBlockedDomains(webRules, blockAllWebsites: false);
                            updates.Add((id, JsonSerializer.Serialize(webRules), JsonSerializer.Serialize(derived)));
                        }
                    }
                }

                foreach (var (id, webRulesJson, derivedDomainsJson) in updates)
                {
                    using var updateCmd = connection.CreateCommand();
                    updateCmd.CommandText = "UPDATE Profiles SET WebRulesJson = @rules, BlockedDomainsJson = @doms WHERE Id = @id;";
                    updateCmd.Parameters.AddWithValue("@rules", webRulesJson);
                    updateCmd.Parameters.AddWithValue("@doms", derivedDomainsJson);
                    updateCmd.Parameters.AddWithValue("@id", id);
                    updateCmd.ExecuteNonQuery();
                }
            }

            using (var verCmd = connection.CreateCommand())
            {
                verCmd.CommandText = "PRAGMA user_version = 3;";
                verCmd.ExecuteNonQuery();
            }
        }

        if (currentVersion < 4)
        {
            using (var updateCmd = connection.CreateCommand())
            {
                updateCmd.CommandText = "UPDATE Profiles SET SystemWide = 0 WHERE SystemWide IS NULL;";
                updateCmd.ExecuteNonQuery();
            }

            using (var verCmd = connection.CreateCommand())
            {
                verCmd.CommandText = "PRAGMA user_version = 4;";
                verCmd.ExecuteNonQuery();
            }
        }
    }

    private void EnsureDefaultProfile(SqliteConnection connection)
    {
        using var checkCmd = connection.CreateCommand();
        checkCmd.CommandText = "SELECT COUNT(*) FROM Profiles;";
        long count = (long)(checkCmd.ExecuteScalar() ?? 0L);

        if (count == 0)
        {
            List<string> processes = ["notepad.exe"];
            string configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (File.Exists(configPath))
            {
                try
                {
                    string json = File.ReadAllText(configPath);
                    if (BlockConfigParser.TryParse(json, out var config, out _))
                    {
                        processes = config!.BlockedProcesses;
                    }
                }
                catch { }
            }

            using var insertCmd = connection.CreateCommand();
            insertCmd.CommandText = """
            INSERT INTO Profiles (Name, BlockedProcessesJson, BlockedDomainsJson, SystemWide)
            VALUES (@name, @procJson, @domJson, 0);
            """;
            insertCmd.Parameters.AddWithValue("@name", "Default");
            insertCmd.Parameters.AddWithValue("@procJson", JsonSerializer.Serialize(processes));
            insertCmd.Parameters.AddWithValue("@domJson", JsonSerializer.Serialize(new List<string>()));
            insertCmd.ExecuteNonQuery();
        }
    }

    private void EnsureSingleMeditationSchedule(SqliteConnection connection)
    {
        try
        {
            long schedCount = 0;
            using (var countCmd = connection.CreateCommand())
            {
                countCmd.CommandText = "SELECT COUNT(*) FROM MeditationSchedules;";
                schedCount = Convert.ToInt64(countCmd.ExecuteScalar() ?? 0);
            }

            if (schedCount > 1)
            {
                using var tx = connection.BeginTransaction();
                using (var copyCmd = connection.CreateCommand())
                {
                    copyCmd.Transaction = tx;
                    copyCmd.CommandText = """
                        INSERT INTO MeditationSchedules (Id, Enabled, DaysOfWeekJson, TimeOfDay, DurationSeconds, AllowedAppsJson, LockMode, Soundscape, LastSnoozedUtc, PasswordHash)
                        SELECT 1, Enabled, DaysOfWeekJson, TimeOfDay, DurationSeconds, AllowedAppsJson, LockMode, Soundscape, LastSnoozedUtc, PasswordHash
                        FROM MeditationSchedules
                        ORDER BY Id DESC
                        LIMIT 1
                        ON CONFLICT(Id) DO UPDATE SET
                            Enabled = excluded.Enabled,
                            DaysOfWeekJson = excluded.DaysOfWeekJson,
                            TimeOfDay = excluded.TimeOfDay,
                            DurationSeconds = excluded.DurationSeconds,
                            AllowedAppsJson = excluded.AllowedAppsJson,
                            LockMode = excluded.LockMode,
                            Soundscape = excluded.Soundscape,
                            LastSnoozedUtc = excluded.LastSnoozedUtc,
                            PasswordHash = excluded.PasswordHash;
                    """;
                    copyCmd.ExecuteNonQuery();
                }

                using (var delCmd = connection.CreateCommand())
                {
                    delCmd.Transaction = tx;
                    delCmd.CommandText = "DELETE FROM MeditationSchedules WHERE Id > 1;";
                    delCmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
            else if (schedCount == 0)
            {
                using var initCmd = connection.CreateCommand();
                initCmd.CommandText = """
                    INSERT OR IGNORE INTO MeditationSchedules (Id, Enabled, DaysOfWeekJson, TimeOfDay, DurationSeconds, AllowedAppsJson, LockMode, Soundscape, LastSnoozedUtc, PasswordHash)
                    VALUES (1, 0, '[]', '15:00', 900, '[]', 0, NULL, NULL, NULL);
                """;
                initCmd.ExecuteNonQuery();
            }
        }
        catch { }
    }

    private static Profile MapProfile(SqliteDataReader reader)
    {
        return new Profile
        {
            Id = reader.GetInt32(0),
            Name = reader.GetString(1),
            BlockedProcesses = JsonSerializer.Deserialize<List<string>>(reader.GetString(2)) ?? new List<string>(),
            BlockedDomains = JsonSerializer.Deserialize<List<string>>(reader.GetString(3)) ?? new List<string>(),
            Enabled = reader.GetInt32(4) != 0,
            Schedule = JsonSerializer.Deserialize<List<ScheduleRule>>(reader.GetString(5)) ?? new List<ScheduleRule>(),
            LockMode = (LockMode)reader.GetInt32(6),
            PasswordSalt = reader.IsDBNull(7) ? null : reader.GetString(7),
            PasswordHash = reader.IsDBNull(8) ? null : reader.GetString(8),
            WebRules = JsonSerializer.Deserialize<List<WebRule>>(reader.GetString(9)) ?? new List<WebRule>(),
            BlockAllWebsites = reader.GetInt32(10) != 0,
            SystemWide = reader.GetInt32(11) != 0,
            Allowance = reader.FieldCount > 12 && !reader.IsDBNull(12)
                ? JsonSerializer.Deserialize<AllowanceConfig>(reader.GetString(12)) ?? new AllowanceConfig()
                : new AllowanceConfig(),
            DelayedUnlockMinutes = reader.FieldCount > 13 && !reader.IsDBNull(13)
                ? reader.GetInt32(13)
                : 15
        };
    }

    public Profile? GetProfileByName(string name)
    {
        var list = ListProfiles();
        return list.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    public Profile? GetProfileById(int id)
    {
        var list = ListProfiles();
        return list.FirstOrDefault(p => p.Id == id);
    }

    public List<Profile> ListProfiles()
    {
        lock (_cacheLock)
        {
            if (_cachedProfiles != null)
            {
                return new List<Profile>(_cachedProfiles);
            }
        }

        var list = LoadProfilesFromDb();
        lock (_cacheLock)
        {
            _cachedProfiles = list;
            return new List<Profile>(_cachedProfiles);
        }
    }

    private List<Profile> LoadProfilesFromDb()
    {
        var list = new List<Profile>();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, Name, BlockedProcessesJson, BlockedDomainsJson, Enabled, ScheduleJson, LockMode, PasswordSalt, PasswordHash, WebRulesJson, BlockAllWebsites, SystemWide, AllowanceJson, DelayedUnlockMinutes FROM Profiles ORDER BY Id ASC;";

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(MapProfile(reader));
        }

        return list;
    }

    public Profile SaveProfile(Profile profile)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        // BlockedDomains is derived on save from WebRules
        profile.BlockedDomains = WebRuleMatcher.DeriveBlockedDomains(profile.WebRules, profile.BlockAllWebsites);

        string procJson = JsonSerializer.Serialize(profile.BlockedProcesses);
        string domJson = JsonSerializer.Serialize(profile.BlockedDomains);
        string rulesJson = JsonSerializer.Serialize(profile.WebRules ?? new List<WebRule>());
        string schedJson = JsonSerializer.Serialize(profile.Schedule ?? new List<ScheduleRule>());
        string allowanceJson = JsonSerializer.Serialize(profile.Allowance ?? new AllowanceConfig());

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
        INSERT INTO Profiles (Name, BlockedProcessesJson, BlockedDomainsJson, Enabled, ScheduleJson, LockMode, PasswordSalt, PasswordHash, WebRulesJson, BlockAllWebsites, SystemWide, AllowanceJson, DelayedUnlockMinutes)
        VALUES (@name, @procJson, @domJson, @enabled, @schedJson, @lockMode, @salt, @hash, @rulesJson, @blockAll, @systemWide, @allowanceJson, @delayedUnlockMinutes)
        ON CONFLICT(Name) DO UPDATE SET 
            BlockedProcessesJson = excluded.BlockedProcessesJson,
            BlockedDomainsJson = excluded.BlockedDomainsJson,
            Enabled = excluded.Enabled,
            ScheduleJson = excluded.ScheduleJson,
            LockMode = excluded.LockMode,
            PasswordSalt = excluded.PasswordSalt,
            PasswordHash = excluded.PasswordHash,
            WebRulesJson = excluded.WebRulesJson,
            BlockAllWebsites = excluded.BlockAllWebsites,
            SystemWide = excluded.SystemWide,
            AllowanceJson = excluded.AllowanceJson,
            DelayedUnlockMinutes = excluded.DelayedUnlockMinutes;
        """;
        cmd.Parameters.AddWithValue("@name", profile.Name);
        cmd.Parameters.AddWithValue("@procJson", procJson);
        cmd.Parameters.AddWithValue("@domJson", domJson);
        cmd.Parameters.AddWithValue("@enabled", profile.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@schedJson", schedJson);
        cmd.Parameters.AddWithValue("@lockMode", (int)profile.LockMode);
        cmd.Parameters.AddWithValue("@salt", (object?)profile.PasswordSalt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@hash", (object?)profile.PasswordHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@rulesJson", rulesJson);
        cmd.Parameters.AddWithValue("@blockAll", profile.BlockAllWebsites ? 1 : 0);
        cmd.Parameters.AddWithValue("@systemWide", profile.SystemWide ? 1 : 0);
        cmd.Parameters.AddWithValue("@allowanceJson", allowanceJson);
        cmd.Parameters.AddWithValue("@delayedUnlockMinutes", profile.DelayedUnlockMinutes > 0 ? profile.DelayedUnlockMinutes : 15);
        cmd.ExecuteNonQuery();

        lock (_cacheLock)
        {
            _cachedProfiles = null;
        }

        return GetProfileByName(profile.Name)!;
    }

    public bool RenameProfile(int id, string newName)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Profiles SET Name = @name WHERE Id = @id;";
        cmd.Parameters.AddWithValue("@name", newName);
        cmd.Parameters.AddWithValue("@id", id);
        int rows = cmd.ExecuteNonQuery();

        lock (_cacheLock)
        {
            _cachedProfiles = null;
        }

        return rows > 0;
    }

    public Profile SaveProfile(string name, List<string> blockedProcesses, List<string> blockedDomains)
    {
        var existing = GetProfileByName(name);
        var profile = existing ?? new Profile { Name = name };
        profile.BlockedProcesses = blockedProcesses;
        profile.BlockedDomains = blockedDomains;
        if (profile.WebRules.Count == 0 && blockedDomains.Count > 0)
        {
            profile.WebRules = blockedDomains.Select(d => new WebRule { Pattern = d, Allow = false }).ToList();
        }
        return SaveProfile(profile);
    }

    public void DisableAllProfiles()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE Profiles SET Enabled = 0;";
        cmd.ExecuteNonQuery();

        lock (_cacheLock)
        {
            _cachedProfiles = null;
        }
    }

    public bool DeleteProfileByName(string name)
    {
        var profile = GetProfileByName(name);
        if (profile == null) return false;
        return DeleteProfile(profile.Id);
    }

    public bool DeleteProfile(int id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var transaction = connection.BeginTransaction();
        try
        {
            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM BlockSessions WHERE ProfileId = @id;";
                cmd.Parameters.AddWithValue("@id", id);
                cmd.ExecuteNonQuery();
            }

            using (var cmd = connection.CreateCommand())
            {
                cmd.Transaction = transaction;
                cmd.CommandText = "DELETE FROM Profiles WHERE Id = @id;";
                cmd.Parameters.AddWithValue("@id", id);
                bool deleted = cmd.ExecuteNonQuery() > 0;
                transaction.Commit();

                lock (_cacheLock)
                {
                    _cachedProfiles = null;
                    _cachedActiveSessions = null;
                    _hasLoadedActiveSessions = false;
                    _lastActiveSessionsFetchUtc = DateTime.MinValue;
                }

                return deleted;
            }
        }
        catch
        {
            transaction.Rollback();
            throw;
        }
    }

    public List<BlockSession> GetActiveSessions() => GetActiveSessions(includeUnendedExpired: false);

    public List<BlockSession> GetActiveSessions(bool includeUnendedExpired)
    {
        lock (_cacheLock)
        {
            var nowUtc = DateTime.UtcNow;
            bool cacheExpired = !_hasLoadedActiveSessions
                || _cachedActiveSessions == null
                || (nowUtc - _lastActiveSessionsFetchUtc) >= ActiveSessionsCacheTtl;

            if (cacheExpired)
            {
                _cachedActiveSessions = LoadUnendedSessionsFromDb();
                _hasLoadedActiveSessions = true;
                _lastActiveSessionsFetchUtc = nowUtc;
            }

            return (_cachedActiveSessions ?? new List<BlockSession>())
                .Where(s =>
                {
                    if (s.EndedUtc != null) return false;
                    if (includeUnendedExpired) return true;
                    // If system clock moved backwards into the past, session is still unended and active
                    if (nowUtc < s.StartUtc) return true;
                    return s.IsActive(nowUtc);
                })
                .ToList();
        }
    }

    public BlockSession? GetActiveSession() => GetActiveSession(profileId: null);

    public BlockSession? GetActiveSession(int? profileId)
    {
        var sessions = GetActiveSessions(includeUnendedExpired: false);
        if (profileId.HasValue)
        {
            return sessions.FirstOrDefault(s => s.ProfileId == profileId.Value);
        }
        return sessions.FirstOrDefault();
    }

    public BlockSession? GetActiveOrUnendedSession(int? profileId = null)
    {
        var sessions = GetActiveSessions(includeUnendedExpired: true);
        if (profileId.HasValue)
        {
            return sessions.FirstOrDefault(s => s.ProfileId == profileId.Value);
        }
        return sessions.FirstOrDefault();
    }

    private List<BlockSession> LoadUnendedSessionsFromDb()
    {
        var list = new List<BlockSession>();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
        SELECT Id, ProfileId, StartUtc, DurationSeconds, Locked, EndedUtc
        FROM BlockSessions
        WHERE EndedUtc IS NULL
        ORDER BY Id DESC;
        """;

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new BlockSession
            {
                Id = reader.GetInt32(0),
                ProfileId = reader.GetInt32(1),
                StartUtc = DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DurationSeconds = reader.GetInt64(3),
                Locked = reader.GetInt32(4) != 0,
                EndedUtc = reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            });
        }

        return list;
    }

    public BlockSession? GetSessionById(int id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Id, ProfileId, StartUtc, DurationSeconds, Locked, EndedUtc FROM BlockSessions WHERE Id = @id;";
        cmd.Parameters.AddWithValue("@id", id);

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return new BlockSession
            {
                Id = reader.GetInt32(0),
                ProfileId = reader.GetInt32(1),
                StartUtc = DateTime.Parse(reader.GetString(2), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
                DurationSeconds = reader.GetInt64(3),
                Locked = reader.GetInt32(4) != 0,
                EndedUtc = reader.IsDBNull(5) ? null : DateTime.Parse(reader.GetString(5), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind)
            };
        }

        return null;
    }

    public BlockSession StartSession(int profileId, long durationSeconds, bool locked)
    {
        lock (_cacheLock)
        {
            var existingSession = GetActiveSession(profileId);
            if (existingSession != null && existingSession.Locked && (existingSession.IsActive(DateTime.UtcNow) || DateTime.UtcNow < existingSession.StartUtc))
            {
                throw new InvalidOperationException($"Cannot start session: a locked session is currently active for profile {profileId}.");
            }

            EndActiveSessionInternal(profileId);

            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            DateTime nowUtc = DateTime.UtcNow;

            using var cmd = connection.CreateCommand();
            cmd.CommandText = """
            INSERT INTO BlockSessions (ProfileId, StartUtc, DurationSeconds, Locked, EndedUtc)
            VALUES (@profileId, @startUtc, @durationSeconds, @locked, NULL);
            SELECT last_insert_rowid();
            """;
            cmd.Parameters.AddWithValue("@profileId", profileId);
            cmd.Parameters.AddWithValue("@startUtc", nowUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@durationSeconds", durationSeconds);
            cmd.Parameters.AddWithValue("@locked", locked ? 1 : 0);

            long newId = (long)(cmd.ExecuteScalar() ?? 0L);

            var session = new BlockSession
            {
                Id = (int)newId,
                ProfileId = profileId,
                StartUtc = nowUtc,
                DurationSeconds = durationSeconds,
                Locked = locked,
                EndedUtc = null
            };

            _cachedActiveSessions = null;
            _hasLoadedActiveSessions = false;
            _lastActiveSessionsFetchUtc = DateTime.MinValue;

            return session;
        }
    }

    public void EndActiveSession()
    {
        lock (_cacheLock)
        {
            EndActiveSessionInternal(null);
        }
    }

    public void EndActiveSession(int? profileId)
    {
        lock (_cacheLock)
        {
            EndActiveSessionInternal(profileId);
        }
    }

    private void EndActiveSessionInternal(int? profileId = null)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        if (profileId.HasValue)
        {
            cmd.CommandText = "UPDATE BlockSessions SET EndedUtc = @endedUtc WHERE ProfileId = @profileId AND EndedUtc IS NULL;";
            cmd.Parameters.AddWithValue("@profileId", profileId.Value);
        }
        else
        {
            cmd.CommandText = "UPDATE BlockSessions SET EndedUtc = @endedUtc WHERE EndedUtc IS NULL;";
        }
        cmd.Parameters.AddWithValue("@endedUtc", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();

        _cachedActiveSessions = null;
        _hasLoadedActiveSessions = false;
        _lastActiveSessionsFetchUtc = DateTime.MinValue;
    }

    public void EndSession(int sessionId)
    {
        lock (_cacheLock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE BlockSessions SET EndedUtc = @endedUtc WHERE Id = @id AND EndedUtc IS NULL;";
            cmd.Parameters.AddWithValue("@endedUtc", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@id", sessionId);
            cmd.ExecuteNonQuery();

            _cachedActiveSessions = null;
            _hasLoadedActiveSessions = false;
            _lastActiveSessionsFetchUtc = DateTime.MinValue;
        }
    }

    public void UpdateSessionStartUtc(int sessionId, DateTime newStartUtc)
    {
        lock (_cacheLock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();

            using var cmd = connection.CreateCommand();
            cmd.CommandText = "UPDATE BlockSessions SET StartUtc = @startUtc WHERE Id = @id AND EndedUtc IS NULL;";
            cmd.Parameters.AddWithValue("@startUtc", newStartUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@id", sessionId);
            cmd.ExecuteNonQuery();

            _cachedActiveSessions = null;
            _hasLoadedActiveSessions = false;
            _lastActiveSessionsFetchUtc = DateTime.MinValue;
        }
    }

    public bool IsEmergencyUnlockPresent()
    {
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string emergencyFile1 = Path.Combine(AppContext.BaseDirectory, "EMERGENCY_UNLOCK");
        string emergencyFile2 = Path.Combine(Directory.GetCurrentDirectory(), "EMERGENCY_UNLOCK");
        string emergencyFile3 = Path.Combine(programData, "Sankalpa", "EMERGENCY_UNLOCK");
        string emergencyFile4 = Path.Combine(programData, "Blocker", "EMERGENCY_UNLOCK");

        return File.Exists(emergencyFile1) || File.Exists(emergencyFile2) || File.Exists(emergencyFile3) || File.Exists(emergencyFile4);
    }

    public void ConsumeEmergencyUnlock()
    {
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        string emergencyFile1 = Path.Combine(AppContext.BaseDirectory, "EMERGENCY_UNLOCK");
        string emergencyFile2 = Path.Combine(Directory.GetCurrentDirectory(), "EMERGENCY_UNLOCK");
        string emergencyFile3 = Path.Combine(programData, "Sankalpa", "EMERGENCY_UNLOCK");
        string emergencyFile4 = Path.Combine(programData, "Blocker", "EMERGENCY_UNLOCK");

        var targets = new[] { emergencyFile1, emergencyFile2, emergencyFile3, emergencyFile4 };
        foreach (var file in targets)
        {
            if (File.Exists(file))
            {
                try { File.Delete(file); } catch { }
            }
        }
    }

    public bool CleanHosts()
    {
        bool success = _hostsManager.RemoveBlock();
        HostsState = _hostsManager.State;
        HostsDomainCount = _hostsManager.CurrentDomainCount;
        HostsBlockingOk = (_hostsManager.State != HostsState.Error);
        HostsLastError = _hostsManager.LastError;
        return success;
    }

    public List<string> GetCurrentHostsDomains()
    {
        return _hostsManager.GetBlockedDomainsInFile();
    }

    private readonly object _eventsLock = new();
    private readonly List<BlockEvent> _eventRingBuffer = new(50);
    private long _nextEventId = 1;

    public void RecordBlockEvent(BlockEvent evt)
    {
        lock (_eventsLock)
        {
            evt.Id = _nextEventId++;
            if (_eventRingBuffer.Count >= 50)
            {
                _eventRingBuffer.RemoveAt(0);
            }
            _eventRingBuffer.Add(evt);
        }
    }

    public List<BlockEvent> GetBlockEvents(long sinceId)
    {
        lock (_eventsLock)
        {
            return _eventRingBuffer.Where(e => e.Id > sinceId).ToList();
        }
    }

    public FallbackPlanner FallbackPlanner { get; } = new();
    public FallbackPlanResult? LastFallbackPlan { get; set; }

    public DnsConfig GetDnsConfig()
    {
        lock (_cacheLock)
        {
            if (_cachedDnsConfig != null)
            {
                return _cachedDnsConfig;
            }
        }

        var config = LoadDnsConfigFromDb();
        lock (_cacheLock)
        {
            _cachedDnsConfig = config;
            return _cachedDnsConfig;
        }
    }

    private DnsConfig LoadDnsConfigFromDb()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT Enabled, PresetName, PrimaryDns, SecondaryDns, Locked, LockType, LockDurationSeconds, LockedUntilUtc, UnlockChallenge, ScheduleJson, PrimaryIpv6, SecondaryIpv6 FROM DnsSettings WHERE Id = 1;";
        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return new DnsConfig
            {
                Enabled = reader.GetInt32(0) == 1,
                PresetName = reader.GetString(1),
                PrimaryDns = reader.GetString(2),
                SecondaryDns = reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                Locked = reader.GetInt32(4) == 1,
                LockType = reader.IsDBNull(5) ? "Challenge" : reader.GetString(5),
                LockDurationSeconds = reader.GetInt64(6),
                LockedUntilUtc = reader.IsDBNull(7) ? null : DateTime.Parse(reader.GetString(7), null, DateTimeStyles.RoundtripKind),
                UnlockChallenge = reader.IsDBNull(8) ? null : reader.GetString(8),
                ScheduleJson = reader.IsDBNull(9) ? "[]" : reader.GetString(9),
                PrimaryIpv6 = reader.IsDBNull(10) ? "2606:4700:4700::1113" : reader.GetString(10),
                SecondaryIpv6 = reader.IsDBNull(11) ? "2606:4700:4700::1003" : reader.GetString(11)
            };
        }

        return new DnsConfig();
    }

    public void SaveDnsConfig(DnsConfig config)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
        INSERT INTO DnsSettings (Id, Enabled, PresetName, PrimaryDns, SecondaryDns, PrimaryIpv6, SecondaryIpv6, Locked, LockType, LockDurationSeconds, LockedUntilUtc, UnlockChallenge, ScheduleJson)
        VALUES (1, $enabled, $preset, $primary, $secondary, $primaryIpv6, $secondaryIpv6, $locked, $lockType, $duration, $lockedUntil, $challenge, $schedule)
        ON CONFLICT(Id) DO UPDATE SET
            Enabled = excluded.Enabled,
            PresetName = excluded.PresetName,
            PrimaryDns = excluded.PrimaryDns,
            SecondaryDns = excluded.SecondaryDns,
            PrimaryIpv6 = excluded.PrimaryIpv6,
            SecondaryIpv6 = excluded.SecondaryIpv6,
            Locked = excluded.Locked,
            LockType = excluded.LockType,
            LockDurationSeconds = excluded.LockDurationSeconds,
            LockedUntilUtc = excluded.LockedUntilUtc,
            UnlockChallenge = excluded.UnlockChallenge,
            ScheduleJson = excluded.ScheduleJson;
        """;
        cmd.Parameters.AddWithValue("$enabled", config.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("$preset", config.PresetName ?? "Cloudflare Family");
        cmd.Parameters.AddWithValue("$primary", config.PrimaryDns ?? "1.1.1.3");
        cmd.Parameters.AddWithValue("$secondary", config.SecondaryDns ?? string.Empty);
        cmd.Parameters.AddWithValue("$primaryIpv6", config.PrimaryIpv6 ?? "2606:4700:4700::1113");
        cmd.Parameters.AddWithValue("$secondaryIpv6", config.SecondaryIpv6 ?? string.Empty);
        cmd.Parameters.AddWithValue("$locked", config.Locked ? 1 : 0);
        cmd.Parameters.AddWithValue("$lockType", config.LockType ?? "Challenge");
        cmd.Parameters.AddWithValue("$duration", config.LockDurationSeconds);
        cmd.Parameters.AddWithValue("$lockedUntil", config.LockedUntilUtc.HasValue ? config.LockedUntilUtc.Value.ToString("o") : (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$challenge", config.UnlockChallenge ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("$schedule", config.ScheduleJson ?? "[]");
        cmd.ExecuteNonQuery();

        lock (_cacheLock)
        {
            _cachedDnsConfig = config;
        }
    }

    #region Meditation Mode & System Lockdown

    private static MeditationSession ReadMeditationSession(SqliteDataReader reader)
    {
        return new MeditationSession
        {
            Id = reader.GetInt32(0),
            StartUtc = DateTime.Parse(reader.GetString(1), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            TargetDurationSeconds = reader.GetInt64(2),
            CompletedDurationSeconds = reader.GetInt64(3),
            Completed = reader.GetInt32(4) != 0,
            Notes = reader.IsDBNull(5) ? null : reader.GetString(5),
            AllowedAppsJson = reader.IsDBNull(6) ? "[]" : reader.GetString(6),
            LockMode = (LockMode)reader.GetInt32(7),
            Soundscape = reader.IsDBNull(8) ? null : reader.GetString(8),
            EndedUtc = reader.IsDBNull(9) ? null : DateTime.Parse(reader.GetString(9), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            PasswordHash = reader.FieldCount > 10 && !reader.IsDBNull(10) ? reader.GetString(10) : null,
            Status = reader.FieldCount > 11 && !reader.IsDBNull(11) ? reader.GetString(11) : "Active"
        };
    }

    private static MeditationSchedule ReadMeditationSchedule(SqliteDataReader reader)
    {
        return new MeditationSchedule
        {
            Id = reader.GetInt32(0),
            Enabled = reader.GetInt32(1) != 0,
            DaysOfWeekJson = reader.IsDBNull(2) ? "[]" : reader.GetString(2),
            TimeOfDay = reader.IsDBNull(3) ? "00:00" : reader.GetString(3),
            DurationSeconds = reader.GetInt64(4),
            AllowedAppsJson = reader.IsDBNull(5) ? "[]" : reader.GetString(5),
            LockMode = (LockMode)reader.GetInt32(6),
            Soundscape = reader.IsDBNull(7) ? null : reader.GetString(7),
            LastSnoozedUtc = reader.IsDBNull(8) ? null : DateTime.Parse(reader.GetString(8), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            PasswordHash = reader.FieldCount > 9 && !reader.IsDBNull(9) ? reader.GetString(9) : null
        };
    }

    public MeditationSession? GetActiveMeditationSession()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
        SELECT Id, StartUtc, TargetDurationSeconds, CompletedDurationSeconds, Completed, Notes, AllowedAppsJson, LockMode, Soundscape, EndedUtc, PasswordHash, Status
        FROM MeditationSessions
        WHERE EndedUtc IS NULL AND Completed = 0 AND (Status = 'Active' OR Status IS NULL)
        ORDER BY Id DESC
        LIMIT 1;
        """;

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            var session = ReadMeditationSession(reader);
            var elapsed = (DateTime.UtcNow - session.StartUtc).TotalSeconds;
            if (elapsed >= session.TargetDurationSeconds)
            {
                reader.Close();
                EndMeditationSession(session.Id, completed: true);
                return null;
            }
            return session;
        }

        return null;
    }

    public MeditationSession StartMeditationSession(long targetDurationSeconds, List<string> allowedApps, LockMode lockMode, string? soundscape)
        => StartMeditationSession(targetDurationSeconds, allowedApps, lockMode, soundscape, null, "Active");

    public MeditationSession StartMeditationSession(long targetDurationSeconds, List<string> allowedApps, LockMode lockMode, string? soundscape, string? passwordHash)
        => StartMeditationSession(targetDurationSeconds, allowedApps, lockMode, soundscape, passwordHash, "Active");

    public MeditationSession StartMeditationSession(long targetDurationSeconds, List<string> allowedApps, LockMode lockMode, string? soundscape, string? passwordHash, string status = "Active")
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            status = "Active";
        }

        var active = GetActiveMeditationSession();
        if (active != null)
        {
            EndMeditationSession(active.Id, completed: false);
        }

        // Clean up any older pending sessions so they do not dangle
        try
        {
            using var cleanConn = new SqliteConnection(_connectionString);
            cleanConn.Open();
            using var cleanCmd = cleanConn.CreateCommand();
            cleanCmd.CommandText = "UPDATE MeditationSessions SET Status = 'Stopped', EndedUtc = @now WHERE EndedUtc IS NULL AND Completed = 0 AND Status = 'Pending';";
            cleanCmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
            cleanCmd.ExecuteNonQuery();
        }
        catch { }

        // If passwordHash was not passed directly, but lockMode is Password, check if an enabled schedule has it
        if (string.IsNullOrEmpty(passwordHash) && lockMode == LockMode.Password)
        {
            try
            {
                var schedules = GetMeditationSchedules();
                var matching = schedules.FirstOrDefault(s => s.Enabled && !string.IsNullOrEmpty(s.PasswordHash));
                if (matching != null)
                {
                    passwordHash = matching.PasswordHash;
                }
            }
            catch { }
        }

        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
        INSERT INTO MeditationSessions (StartUtc, TargetDurationSeconds, CompletedDurationSeconds, Completed, Notes, AllowedAppsJson, LockMode, Soundscape, EndedUtc, PasswordHash, Status)
        VALUES (@startUtc, @targetDuration, 0, 0, NULL, @allowedApps, @lockMode, @soundscape, NULL, @passwordHash, @status);
        SELECT last_insert_rowid();
        """;

        var startUtc = DateTime.UtcNow;
        string allowedJson = JsonSerializer.Serialize(allowedApps ?? new List<string>());

        cmd.Parameters.AddWithValue("@startUtc", startUtc.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@targetDuration", targetDurationSeconds);
        cmd.Parameters.AddWithValue("@allowedApps", allowedJson);
        cmd.Parameters.AddWithValue("@lockMode", (int)lockMode);
        cmd.Parameters.AddWithValue("@soundscape", (object?)soundscape ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@passwordHash", (object?)passwordHash ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", status);

        long newId = Convert.ToInt64(cmd.ExecuteScalar());
        return new MeditationSession
        {
            Id = (int)newId,
            StartUtc = startUtc,
            TargetDurationSeconds = targetDurationSeconds,
            CompletedDurationSeconds = 0,
            Completed = false,
            Notes = null,
            AllowedAppsJson = allowedJson,
            LockMode = lockMode,
            Soundscape = soundscape,
            EndedUtc = null,
            PasswordHash = passwordHash,
            Status = status
        };
    }

    public void EndMeditationSession(int id, bool completed, string? finalNotes = null)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        var now = DateTime.UtcNow;
        DateTime startUtc = now;
        long targetSec = 0;

        using (var getCmd = connection.CreateCommand())
        {
            getCmd.CommandText = "SELECT StartUtc, TargetDurationSeconds FROM MeditationSessions WHERE Id = @id;";
            getCmd.Parameters.AddWithValue("@id", id);
            using var r = getCmd.ExecuteReader();
            if (r.Read())
            {
                startUtc = DateTime.Parse(r.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                targetSec = r.GetInt64(1);
            }
            else
            {
                return;
            }
        }

        long elapsedSec = (long)(now - startUtc).TotalSeconds;
        if (elapsedSec < 0) elapsedSec = 0;
        long completedSec = completed ? Math.Max(elapsedSec, targetSec) : elapsedSec;

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
        UPDATE MeditationSessions
        SET Completed = @completed,
            CompletedDurationSeconds = @completedSec,
            EndedUtc = @endedUtc,
            Notes = CASE WHEN @finalNotes IS NOT NULL THEN @finalNotes ELSE Notes END,
            Status = @status
        WHERE Id = @id;
        """;

        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@completed", completed ? 1 : 0);
        cmd.Parameters.AddWithValue("@completedSec", completedSec);
        cmd.Parameters.AddWithValue("@endedUtc", now.ToString("o", CultureInfo.InvariantCulture));
        cmd.Parameters.AddWithValue("@finalNotes", (object?)finalNotes ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@status", completed ? "Completed" : "Stopped");
        cmd.ExecuteNonQuery();

        ActiveAllowedAppRoots.Clear();
    }

    public bool PromoteMeditationSessionToActive(int sessionId)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE MeditationSessions SET Status = 'Active' WHERE Id = @id;";
        cmd.Parameters.AddWithValue("@id", sessionId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public void UpdateMeditationNotes(int id, string notes)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "UPDATE MeditationSessions SET Notes = @notes WHERE Id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@notes", (object?)notes ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    public List<MeditationSession> GetMeditationHistory(int limit = 50)
    {
        var list = new List<MeditationSession>();
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
        SELECT Id, StartUtc, TargetDurationSeconds, CompletedDurationSeconds, Completed, Notes, AllowedAppsJson, LockMode, Soundscape, EndedUtc, PasswordHash, Status
        FROM MeditationSessions
        ORDER BY Id DESC
        LIMIT @limit;
        """;
        cmd.Parameters.AddWithValue("@limit", limit > 0 ? limit : 50);

        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            list.Add(ReadMeditationSession(reader));
        }

        return list;
    }

    public MeditationSchedule? GetMeditationSchedule()
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
        SELECT Id, Enabled, DaysOfWeekJson, TimeOfDay, DurationSeconds, AllowedAppsJson, LockMode, Soundscape, LastSnoozedUtc, PasswordHash
        FROM MeditationSchedules
        WHERE Id = 1
        LIMIT 1;
        """;

        using var reader = cmd.ExecuteReader();
        if (reader.Read())
        {
            return ReadMeditationSchedule(reader);
        }

        return null;
    }

    public List<MeditationSchedule> GetMeditationSchedules()
    {
        var sched = GetMeditationSchedule();
        return sched != null ? new List<MeditationSchedule> { sched } : new List<MeditationSchedule>();
    }

    public MeditationSchedule SaveMeditationSchedule(MeditationSchedule schedule)
    {
        schedule.Id = 1;
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
        INSERT INTO MeditationSchedules (Id, Enabled, DaysOfWeekJson, TimeOfDay, DurationSeconds, AllowedAppsJson, LockMode, Soundscape, LastSnoozedUtc, PasswordHash)
        VALUES (1, @enabled, @days, @time, @dur, @apps, @lock, @sound, @snoozed, @pwdHash)
        ON CONFLICT(Id) DO UPDATE SET
            Enabled = excluded.Enabled,
            DaysOfWeekJson = excluded.DaysOfWeekJson,
            TimeOfDay = excluded.TimeOfDay,
            DurationSeconds = excluded.DurationSeconds,
            AllowedAppsJson = excluded.AllowedAppsJson,
            LockMode = excluded.LockMode,
            Soundscape = excluded.Soundscape,
            LastSnoozedUtc = excluded.LastSnoozedUtc,
            PasswordHash = excluded.PasswordHash;
        """;
        cmd.Parameters.AddWithValue("@enabled", schedule.Enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@days", schedule.DaysOfWeekJson ?? "[]");
        cmd.Parameters.AddWithValue("@time", schedule.TimeOfDay ?? "00:00");
        cmd.Parameters.AddWithValue("@dur", schedule.DurationSeconds);
        cmd.Parameters.AddWithValue("@apps", schedule.AllowedAppsJson ?? "[]");
        cmd.Parameters.AddWithValue("@lock", (int)schedule.LockMode);
        cmd.Parameters.AddWithValue("@sound", (object?)schedule.Soundscape ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@snoozed", schedule.LastSnoozedUtc.HasValue ? schedule.LastSnoozedUtc.Value.ToString("o", CultureInfo.InvariantCulture) : DBNull.Value);
        cmd.Parameters.AddWithValue("@pwdHash", (object?)schedule.PasswordHash ?? DBNull.Value);
        cmd.ExecuteNonQuery();
        return schedule;
    }

    public bool DeleteMeditationSchedule(int id)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM MeditationSchedules WHERE Id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool SnoozeMeditationSchedule(int id, int snoozeMinutes = 5)
    {
        using var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
        UPDATE MeditationSchedules
        SET LastSnoozedUtc = @snoozed
        WHERE Id = @id;
        """;
        cmd.Parameters.AddWithValue("@id", id);
        cmd.Parameters.AddWithValue("@snoozed", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        return cmd.ExecuteNonQuery() > 0;
    }

    #endregion
}
