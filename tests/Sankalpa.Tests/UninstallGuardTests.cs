using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Blocker.Core;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Sankalpa.Tests;

public class UninstallGuardTests : IDisposable
{
    private readonly string _tempDbPath;

    public UninstallGuardTests()
    {
        _tempDbPath = Path.Combine(Path.GetTempPath(), $"sankalpa_uninstall_test_{Guid.NewGuid():N}.db");
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
    public void CanUninstallFromStatus_WhenNoActiveBlock_ReturnsTrue()
    {
        var status = new StatusData
        {
            Active = false,
            RemainingSeconds = 0,
            ActiveProfileNames = new List<string>()
        };

        bool allowed = UninstallGuard.CanUninstallFromStatus(status, out string? reason);

        Assert.True(allowed);
        Assert.Null(reason);
    }

    [Fact]
    public void CanUninstallFromStatus_WhenActiveManualSession_ReturnsFalse()
    {
        var status = new StatusData
        {
            Active = true,
            Profile = "Deep Work",
            RemainingSeconds = 1500,
            RemainingFormatted = "25:00",
            Locked = true,
            ActiveProfileNames = new List<string> { "Deep Work" }
        };

        bool allowed = UninstallGuard.CanUninstallFromStatus(status, out string? reason);

        Assert.False(allowed);
        Assert.NotNull(reason);
        Assert.Contains("Deep Work", reason);
        Assert.Contains("25:00", reason);
    }

    [Fact]
    public void CanUninstallFromStatus_WhenActiveSchedule_ReturnsFalse()
    {
        var status = new StatusData
        {
            Active = true,
            Profile = "Work Hours",
            RemainingSeconds = 0,
            ActiveProfileNames = new List<string> { "Work Hours" }
        };

        bool allowed = UninstallGuard.CanUninstallFromStatus(status, out string? reason);

        Assert.False(allowed);
        Assert.NotNull(reason);
        Assert.Contains("Work Hours", reason);
    }

    [Fact]
    public void CheckDatabaseForActiveBlock_WhenActiveSessionInDb_ReturnsTrue()
    {
        // Setup SQLite test database with an active session
        using (var conn = new SqliteConnection($"Data Source={_tempDbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE BlockSessions (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProfileId INTEGER NOT NULL,
                    StartUtc TEXT NOT NULL,
                    DurationSeconds INTEGER NOT NULL,
                    Locked INTEGER NOT NULL,
                    EndedUtc TEXT
                );
            ";
            cmd.ExecuteNonQuery();

            DateTime startUtc = DateTime.UtcNow.AddMinutes(-5);
            cmd.CommandText = $@"
                INSERT INTO BlockSessions (ProfileId, StartUtc, DurationSeconds, Locked, EndedUtc)
                VALUES (1, '{startUtc:O}', 1800, 1, NULL);
            ";
            cmd.ExecuteNonQuery();
        }

        bool blocked = UninstallGuard.CheckDatabaseForActiveBlock(_tempDbPath, DateTime.UtcNow, DateTime.Now, out string? reason);

        Assert.True(blocked);
        Assert.NotNull(reason);
        Assert.Contains("Active session in database", reason);
    }

    [Fact]
    public void CheckDatabaseForActiveBlock_WhenSessionEndedOrExpired_ReturnsFalse()
    {
        // Setup SQLite test database with an expired session and an ended session
        using (var conn = new SqliteConnection($"Data Source={_tempDbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE BlockSessions (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    ProfileId INTEGER NOT NULL,
                    StartUtc TEXT NOT NULL,
                    DurationSeconds INTEGER NOT NULL,
                    Locked INTEGER NOT NULL,
                    EndedUtc TEXT
                );
            ";
            cmd.ExecuteNonQuery();

            // Expired session
            DateTime startUtc = DateTime.UtcNow.AddHours(-2);
            cmd.CommandText = $@"
                INSERT INTO BlockSessions (ProfileId, StartUtc, DurationSeconds, Locked, EndedUtc)
                VALUES (1, '{startUtc:O}', 1800, 1, NULL);
            ";
            cmd.ExecuteNonQuery();
        }

        bool blocked = UninstallGuard.CheckDatabaseForActiveBlock(_tempDbPath, DateTime.UtcNow, DateTime.Now, out string? reason);

        Assert.False(blocked);
        Assert.Null(reason);
    }

    [Fact]
    public void CheckDatabaseForActiveBlock_WhenAlwaysOnProfileEnabled_ReturnsTrue()
    {
        using (var conn = new SqliteConnection($"Data Source={_tempDbPath}"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
                CREATE TABLE Profiles (
                    Id INTEGER PRIMARY KEY AUTOINCREMENT,
                    Name TEXT NOT NULL,
                    Enabled INTEGER NOT NULL,
                    ScheduleJson TEXT
                );
            ";
            cmd.ExecuteNonQuery();

            cmd.CommandText = @"
                INSERT INTO Profiles (Name, Enabled, ScheduleJson)
                VALUES ('Strict Block', 1, '[]');
            ";
            cmd.ExecuteNonQuery();
        }

        bool blocked = UninstallGuard.CheckDatabaseForActiveBlock(_tempDbPath, DateTime.UtcNow, DateTime.Now, out string? reason);

        Assert.True(blocked);
        Assert.NotNull(reason);
        Assert.Contains("Strict Block", reason);
        Assert.Contains("always-on", reason);
    }
}
