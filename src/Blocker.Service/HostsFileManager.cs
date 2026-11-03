using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Blocker.Core;

namespace Blocker.Service;

public class HostsFileManager
{
    private readonly string _hostsPath;
    private readonly string _backupPath;
    private readonly ILogger? _logger;
    private string? _lastLoggedError;
    private bool _hadError;
    private DateTime _lastDnsFlushUtc = DateTime.MinValue;
    private readonly object _flushLock = new();

    public HostsFileManager(string? customHostsPath = null, ILogger? logger = null)
    {
        _logger = logger;

        if (string.IsNullOrEmpty(customHostsPath))
        {
            string systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            _hostsPath = Path.Combine(systemDir, "drivers", "etc", "hosts");
        }
        else
        {
            _hostsPath = customHostsPath;
        }

        string? dir = Path.GetDirectoryName(_hostsPath);
        _backupPath = Path.Combine(dir ?? string.Empty, "hosts.blocker.bak");
    }

    public string HostsPath => _hostsPath;
    public string BackupPath => _backupPath;
    public HostsState State { get; private set; } = HostsState.Idle;
    public int CurrentDomainCount { get; private set; }
    public bool LastApplySuccess { get; private set; } = true;
    public string? LastError { get; private set; }
    public int MaxRetries { get; set; } = 5;
    public int RetryDelayMs { get; set; } = 200;

    /// <summary>
    /// Level-triggered synchronization of the hosts file with the desired blocked domain set.
    /// Reads actual hosts file, compares with desired, writes/removes only if they differ,
    /// retries up to MaxRetries on IOException/UnauthorizedAccessException,
    /// verifies state after write, and updates State/LastError/DomainCount.
    /// </summary>
    public bool SyncHosts(IEnumerable<string>? desiredDomains)
    {
        var desiredList = desiredDomains != null ? desiredDomains.ToList() : new List<string>();
        var expectedExpanded = HostsFileBuilder.ExpandDomains(desiredList);
        var expectedSet = new HashSet<string>(expectedExpanded, StringComparer.OrdinalIgnoreCase);

        try
        {
            string actualContent = ReadHostsFileText();
            bool hasBlockerSection = actualContent.Contains(HostsFileBuilder.BeginMarker, StringComparison.Ordinal);

            if (expectedSet.Count == 0)
            {
                if (!hasBlockerSection)
                {
                    // Desired is empty and file has no block -> actual == desired (ZERO writes)
                    State = HostsState.Idle;
                    CurrentDomainCount = 0;
                    LastError = null;
                    LastApplySuccess = true;
                    OnSuccessRecovery();
                    return true;
                }
            }
            else
            {
                if (hasBlockerSection)
                {
                    var actualDomains = HostsFileBuilder.GetBlockedDomains(actualContent);
                    var actualSet = new HashSet<string>(actualDomains, StringComparer.OrdinalIgnoreCase);

                    if (actualSet.SetEquals(expectedSet) && IsBlockIntactInternal(actualContent, desiredList))
                    {
                        // Desired domains match actual domains and block is intact -> actual == desired (ZERO writes)
                        State = HostsState.Blocking;
                        CurrentDomainCount = actualDomains.Count;
                        LastError = null;
                        LastApplySuccess = true;
                        OnSuccessRecovery();
                        return true;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
        {
            // Initial read error - will attempt in the retry loop below
        }

        // Actual != desired (or initial read failed) -> perform write with retries and verification
        int attempt = 0;
        while (true)
        {
            attempt++;
            try
            {
                EnsureBackupOnce();

                string currentContent = ReadHostsFileText();
                string newContent;
                if (expectedSet.Count == 0)
                {
                    newContent = HostsFileBuilder.RemoveBlock(currentContent);
                }
                else
                {
                    newContent = HostsFileBuilder.ApplyBlock(currentContent, desiredList);
                }

                WriteAtomically(newContent);
                FlushDns();

                // Re-read file and confirm it matches desired
                string verifyContent = ReadHostsFileText();
                bool ok;
                if (expectedSet.Count == 0)
                {
                    ok = !verifyContent.Contains(HostsFileBuilder.BeginMarker, StringComparison.Ordinal);
                }
                else
                {
                    var verifiedDomains = HostsFileBuilder.GetBlockedDomains(verifyContent);
                    var verifiedSet = new HashSet<string>(verifiedDomains, StringComparer.OrdinalIgnoreCase);
                    ok = verifiedSet.SetEquals(expectedSet) && IsBlockIntactInternal(verifyContent, desiredList);
                }

                if (!ok)
                {
                    throw new IOException("Hosts file verification failed: file content does not match desired domains after update.");
                }

                State = expectedSet.Count > 0 ? HostsState.Blocking : HostsState.Idle;
                CurrentDomainCount = expectedSet.Count > 0 ? expectedExpanded.Count : 0;
                LastError = null;
                LastApplySuccess = true;
                OnSuccessRecovery();
                return true;
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                if (attempt <= MaxRetries)
                {
                    if (RetryDelayMs > 0)
                    {
                        Thread.Sleep(RetryDelayMs);
                    }
                    continue;
                }

                // Exhausted retries for this cycle
                RecordFailure(ex);
                return false;
            }
            catch (Exception ex)
            {
                // Other unexpected exception
                RecordFailure(ex);
                return false;
            }
        }
    }

    public bool ApplyBlock(IEnumerable<string> domains)
    {
        return SyncHosts(domains);
    }

    public bool RemoveBlock()
    {
        return SyncHosts(Array.Empty<string>());
    }

    public List<string> GetBlockedDomainsInFile()
    {
        try
        {
            string content = ReadHostsFileText();
            return HostsFileBuilder.GetBlockedDomains(content);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Failed to read blocked domains from hosts file: {Message}", ex.Message);
            return new List<string>();
        }
    }

    public bool IsBlockIntact(IEnumerable<string> expectedDomains)
    {
        try
        {
            if (!File.Exists(_hostsPath))
            {
                return false;
            }

            string content = ReadHostsFileText();
            return IsBlockIntactInternal(content, expectedDomains);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning("Failed to inspect hosts file: {Message}", ex.Message);
            return false;
        }
    }

    private static bool IsBlockIntactInternal(string content, IEnumerable<string> expectedDomains)
    {
        int beginIdx = content.IndexOf(HostsFileBuilder.BeginMarker, StringComparison.Ordinal);
        int endIdx = content.IndexOf(HostsFileBuilder.EndMarker, StringComparison.Ordinal);

        if (beginIdx < 0 || endIdx < 0 || endIdx <= beginIdx)
        {
            return false;
        }

        string blockSection = content.Substring(beginIdx, endIdx - beginIdx + HostsFileBuilder.EndMarker.Length);
        var expanded = HostsFileBuilder.ExpandDomains(expectedDomains);

        if (expanded.Count == 0)
        {
            return true;
        }

        foreach (var domain in expanded)
        {
            if (!blockSection.Contains($"127.0.0.1 {domain}", StringComparison.OrdinalIgnoreCase) ||
                !blockSection.Contains($"::1 {domain}", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private string ReadHostsFileText()
    {
        if (!File.Exists(_hostsPath))
        {
            return string.Empty;
        }

        // Open with FileShare.ReadWrite | FileShare.Delete so other processes are never blocked
        using var stream = new FileStream(
            _hostsPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private void EnsureBackupOnce()
    {
        if (!File.Exists(_backupPath) && File.Exists(_hostsPath))
        {
            try
            {
                using var inStream = new FileStream(
                    _hostsPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                using var outStream = new FileStream(
                    _backupPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.Read);
                inStream.CopyTo(outStream);
                _logger?.LogInformation("Created one-time hosts backup at '{BackupPath}'.", _backupPath);
            }
            catch (Exception ex)
            {
                _logger?.LogWarning("Could not create hosts backup at '{BackupPath}': {Message}", _backupPath, ex.Message);
            }
        }
    }

    private void WriteAtomically(string content)
    {
        string? dir = Path.GetDirectoryName(_hostsPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        string tempPath = Path.Combine(dir ?? string.Empty, $"hosts.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete))
            using (var writer = new StreamWriter(stream, Encoding.UTF8))
            {
                writer.Write(content);
            }

            File.Move(tempPath, _hostsPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    private void FlushDns()
    {
        lock (_flushLock)
        {
            if (DateTime.UtcNow - _lastDnsFlushUtc < TimeSpan.FromSeconds(15))
            {
                return;
            }
            _lastDnsFlushUtc = DateTime.UtcNow;
        }

        Task.Run(() =>
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "ipconfig",
                    Arguments = "/flushdns",
                    CreateNoWindow = true,
                    UseShellExecute = false
                });
                proc?.WaitForExit(3000);
            }
            catch (Exception ex)
            {
                _logger?.LogDebug("ipconfig /flushdns failed: {Message}", ex.Message);
            }
        });
    }

    private void OnSuccessRecovery()
    {
        if (_hadError)
        {
            string msg = "Hosts file sync recovered.";
            _logger?.LogInformation("{Message}", msg);
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [INFO] {msg}");
            _hadError = false;
            _lastLoggedError = null;
        }
    }

    private void RecordFailure(Exception ex)
    {
        State = HostsState.Error;
        LastError = $"{ex.GetType().Name}: {ex.Message}";
        LastApplySuccess = false;

        string errKey = LastError;
        if (errKey != _lastLoggedError)
        {
            _logger?.LogError(ex, "Hosts file sync failed: {Error}", errKey);
            Console.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ERROR] Hosts file sync failed: {errKey}");
            _lastLoggedError = errKey;
        }
        _hadError = true;
    }
}
