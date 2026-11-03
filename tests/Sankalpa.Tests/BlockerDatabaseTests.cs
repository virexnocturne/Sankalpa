using System.Runtime.Versioning;
using Blocker.Core;
using Blocker.Service;
using Xunit;

namespace Blocker.Core.Tests;

[SupportedOSPlatform("windows")]
public class BlockerDatabaseTests : IDisposable
{
    private readonly string _tempDbPath;

    public BlockerDatabaseTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"blocker_test_{Guid.NewGuid():N}.db");
    }

    public void Dispose()
    {
        try
        {
            if (File.Exists(_tempDbPath))
            {
                File.Delete(_tempDbPath);
            }
        }
        catch { }
    }

    [Fact]
    public void SaveProfile_AWithDomains_SaveNewB_AUnchanged()
    {
        var db = new BlockerDatabase(_tempDbPath);

        // 1. Save profile A ("Default") with domains
        var savedA = db.SaveProfile("Default", new List<string> { "notepad.exe" }, new List<string> { "example.com" });
        Assert.NotNull(savedA);
        Assert.Contains("example.com", savedA.BlockedDomains);

        // 2. Save new profile B ("Study")
        var savedB = db.SaveProfile("Study", new List<string> { "calc.exe" }, new List<string> { "example.org" });
        Assert.NotNull(savedB);
        Assert.Equal("Study", savedB.Name);
        Assert.Contains("example.org", savedB.BlockedDomains);

        // 3. Assert profile A ("Default") is unchanged
        var profileA = db.GetProfileByName("Default");
        Assert.NotNull(profileA);
        Assert.Contains("example.com", profileA.BlockedDomains);
        Assert.Contains("notepad.exe", profileA.BlockedProcesses);

        // 4. Assert ListProfiles returns A with domains intact
        var list = db.ListProfiles();
        var listedA = list.FirstOrDefault(p => p.Name.Equals("Default", StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(listedA);
        Assert.Contains("example.com", listedA.BlockedDomains);
    }

    [Fact]
    public void EnsureDefaultProfile_OnlySeedsWhenDatabaseHasNoProfilesAtAll()
    {
        var db = new BlockerDatabase(_tempDbPath);

        // Rename/delete "Default" by saving a custom profile and deleting Default
        db.SaveProfile("Work", new List<string> { "slack.exe" }, new List<string> { "github.com" });
        db.DeleteProfileByName("Default");

        // Now the database has 1 profile ("Work"), but NO "Default" profile.
        Assert.Single(db.ListProfiles());
        Assert.Null(db.GetProfileByName("Default"));

        // Creating a new BlockerDatabase connection (simulating restart)
        var restartedDb = new BlockerDatabase(_tempDbPath);

        // Rule: config.json must be used ONLY ONCE when the database has no profiles at all.
        // Since database already has "Work", it must NOT re-seed "Default"!
        var profiles = restartedDb.ListProfiles();
        Assert.Single(profiles);
        Assert.Equal("Work", profiles[0].Name);
        Assert.Null(restartedDb.GetProfileByName("Default"));
    }

    [Fact]
    public void MigrateFromOldSchema_PreservesExistingProfilesAndSessions()
    {
        // 1. Manually create a database with the Phase 2/3 OLD schema (user_version = 0, no Phase 6 columns)
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_tempDbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
            CREATE TABLE Profiles (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                BlockedProcessesJson TEXT NOT NULL,
                BlockedDomainsJson TEXT NOT NULL DEFAULT '[]'
            );

            CREATE TABLE BlockSessions (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ProfileId INTEGER NOT NULL,
                StartUtc TEXT NOT NULL,
                DurationSeconds INTEGER NOT NULL,
                Locked INTEGER NOT NULL,
                EndedUtc TEXT NULL
            );

            INSERT INTO Profiles (Name, BlockedProcessesJson, BlockedDomainsJson)
            VALUES ('LegacyWork', '["legacy.exe"]', '["legacy.com"]');

            INSERT INTO BlockSessions (ProfileId, StartUtc, DurationSeconds, Locked, EndedUtc)
            VALUES (1, '2026-10-01T12:00:00.0000000Z', 1800, 1, '2026-10-01T12:30:00.0000000Z');
            """;
            cmd.ExecuteNonQuery();
        }

        // 2. Open with BlockerDatabase (triggers migration to user_version 3)
        var db = new BlockerDatabase(_tempDbPath);

        // 3. Verify PRAGMA user_version is 4
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_tempDbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            int version = Convert.ToInt32(cmd.ExecuteScalar());
            Assert.Equal(4, version);
        }

        // 4. Verify existing profile data is intact and converted to WebRules
        var profile = db.GetProfileByName("LegacyWork");
        Assert.NotNull(profile);
        Assert.Equal("LegacyWork", profile.Name);
        Assert.Contains("legacy.exe", profile.BlockedProcesses);
        Assert.Contains("legacy.com", profile.BlockedDomains);
        Assert.Single(profile.WebRules);
        Assert.Equal("legacy.com", profile.WebRules[0].Pattern);
        Assert.False(profile.WebRules[0].Allow);
        Assert.False(profile.BlockAllWebsites);
        Assert.False(profile.Enabled);
        Assert.Empty(profile.Schedule);
        Assert.Equal(LockMode.None, profile.LockMode);

        // 5. Verify existing session is intact
        var session = db.GetSessionById(1);
        Assert.NotNull(session);
        Assert.Equal(1800, session.DurationSeconds);
        Assert.True(session.Locked);

        // 6. Verify we can save Phase 6 schedule and lock mode on the migrated profile
        profile.Enabled = true;
        profile.Schedule.Add(new ScheduleRule
        {
            Days = DaysOfWeek.Weekdays,
            StartMinute = 480,
            EndMinute = 1020
        });
        profile.LockMode = LockMode.TypingChallenge;

        db.SaveProfile(profile);

        var reloaded = db.GetProfileByName("LegacyWork");
        Assert.NotNull(reloaded);
        Assert.True(reloaded.Enabled);
        Assert.Single(reloaded.Schedule);
        Assert.Equal(LockMode.TypingChallenge, reloaded.LockMode);
    }

    [Fact]
    public void MigrateFromVersion2Schema_AddsWebRulesAndBlockAllWebsites()
    {
        // 1. Create a database at PRAGMA user_version = 2
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_tempDbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
            CREATE TABLE Profiles (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Name TEXT NOT NULL UNIQUE COLLATE NOCASE,
                BlockedProcessesJson TEXT NOT NULL,
                BlockedDomainsJson TEXT NOT NULL DEFAULT '[]',
                Enabled INTEGER NOT NULL DEFAULT 0,
                ScheduleJson TEXT NOT NULL DEFAULT '[]',
                LockMode INTEGER NOT NULL DEFAULT 0,
                PasswordSalt TEXT NULL,
                PasswordHash TEXT NULL
            );

            PRAGMA user_version = 2;

            INSERT INTO Profiles (Name, BlockedProcessesJson, BlockedDomainsJson, Enabled, ScheduleJson, LockMode)
            VALUES ('V2Profile', '["proc1.exe"]', '["example.com", "example.org"]', 1, '[]', 0);
            """;
            cmd.ExecuteNonQuery();
        }

        // 2. Open with BlockerDatabase to migrate to version 3
        var db = new BlockerDatabase(_tempDbPath);

        // 3. Verify user_version is 4
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_tempDbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            int version = Convert.ToInt32(cmd.ExecuteScalar());
            Assert.Equal(4, version);
        }

        // 4. Verify V2Profile was migrated: WebRules populated from BlockedDomainsJson
        var profile = db.GetProfileByName("V2Profile");
        Assert.NotNull(profile);
        Assert.Equal(2, profile.WebRules.Count);
        Assert.Contains(profile.WebRules, r => r.Pattern == "example.com" && !r.Allow);
        Assert.Contains(profile.WebRules, r => r.Pattern == "example.org" && !r.Allow);
        Assert.Equal(2, profile.BlockedDomains.Count);
        Assert.Contains("example.com", profile.BlockedDomains);
        Assert.Contains("example.org", profile.BlockedDomains);
        Assert.False(profile.BlockAllWebsites);

        // 5. Verify saving with an exception rule clears derived BlockedDomains
        profile.WebRules.Add(new WebRule { Pattern = "example.com/allowed", Allow = true });
        db.SaveProfile(profile);

        var afterException = db.GetProfileByName("V2Profile");
        Assert.NotNull(afterException);
        Assert.Equal(3, afterException.WebRules.Count);
        // Hosts cannot enforce exceptions, so BlockedDomains should now be empty!
        Assert.Empty(afterException.BlockedDomains);
    }

    [Fact]
    public void MigrateDatabase_FromVersion3ToVersion4_AddsSystemWideColumnAndSetsUserVersion4()
    {
        // 1. Create a V3 database manually
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_tempDbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
            CREATE TABLE Profiles (
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
                BlockAllWebsites INTEGER NOT NULL DEFAULT 0
            );

            PRAGMA user_version = 3;

            INSERT INTO Profiles (Name, BlockedProcessesJson, BlockedDomainsJson, Enabled, ScheduleJson, LockMode, WebRulesJson, BlockAllWebsites)
            VALUES ('V3Profile', '["proc1.exe"]', '["example.com"]', 1, '[]', 0, '[{"Pattern":"example.com","Allow":false}]', 0);
            """;
            cmd.ExecuteNonQuery();
        }

        // 2. Open with BlockerDatabase to migrate to version 4
        var db = new BlockerDatabase(_tempDbPath);

        // 3. Verify user_version is 4
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={_tempDbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            int version = Convert.ToInt32(cmd.ExecuteScalar());
            Assert.Equal(4, version);
        }

        // 4. Verify V3Profile was migrated with SystemWide = false
        var profile = db.GetProfileByName("V3Profile");
        Assert.NotNull(profile);
        Assert.False(profile.SystemWide);

        // 5. Update SystemWide to true and save
        profile.SystemWide = true;
        db.SaveProfile(profile);

        var updated = db.GetProfileByName("V3Profile");
        Assert.NotNull(updated);
        Assert.True(updated.SystemWide);
    }

    [Fact]
    public void DeleteProfile_WithHistoricalSession_DeletesSuccessfullyWithoutForeignKeyViolation()
    {
        var db = new BlockerDatabase(_tempDbPath);
        var profile = db.SaveProfile(new Profile { Name = "ProfileWithHistory" });

        // Start and end a session to create historical BlockSessions row
        var session = db.StartSession(profile.Id, 10, locked: false);
        db.EndSession(session.Id);

        // Delete should cascade session deletion and succeed without SQLite Error 19
        bool deleted = db.DeleteProfile(profile.Id);
        Assert.True(deleted);
        Assert.Null(db.GetProfileById(profile.Id));
        Assert.Null(db.GetSessionById(session.Id));
    }
}
