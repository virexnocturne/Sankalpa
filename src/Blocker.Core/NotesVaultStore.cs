using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Blocker.Core;

/// <summary>
/// Unified, thread-safe asynchronous vault store for active session notes.
/// Coalesces rapid save requests, writes on a background thread with crash-safe tmp-then-move,
/// and provides synchronous flush for shutdown/session termination.
/// </summary>
public static class NotesVaultStore
{
    private static readonly object _lock = new();
    private static Timer? _debounceTimer;
    private static string? _pendingJson;
    private static string? _lastWrittenJson;
    private static Task? _activeWriteTask;
    private static string? _customVaultPathForTesting;
    private static readonly TimeSpan DefaultDebounceDelay = TimeSpan.FromMilliseconds(500);

    static NotesVaultStore()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushSync(1500);
    }

    public static string GetNotesVaultPath()
    {
        if (!string.IsNullOrEmpty(_customVaultPathForTesting))
        {
            return _customVaultPathForTesting;
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "Sankalpa", "notes", "active_notes.json");
    }

    public static void SetVaultPathForTesting(string? path)
    {
        lock (_lock)
        {
            _customVaultPathForTesting = path;
            _pendingJson = null;
            _lastWrittenJson = null;
            _debounceTimer?.Dispose();
            _debounceTimer = null;
        }
    }

    public static List<NoteCard> LoadNotesFromVault()
    {
        try
        {
            string path = GetNotesVaultPath();
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                return NoteCardCollection.Deserialize(json);
            }
        }
        catch
        {
            // Fail open on read errors, return empty list
        }
        return new List<NoteCard>();
    }

    /// <summary>
    /// Queues an asynchronous, debounced, coalesced write of cards to the vault file.
    /// Fast in-memory operation on caller/UI thread.
    /// </summary>
    public static void QueueSave(IEnumerable<NoteCard> cards, TimeSpan? debounceDelay = null)
    {
        var cardList = cards is List<NoteCard> list ? list : cards.ToList();
        string json = NoteCardCollection.Serialize(cardList);
        QueueSaveJson(json, debounceDelay);
    }

    /// <summary>
    /// Queues serialized JSON directly.
    /// </summary>
    public static void QueueSaveJson(string json, TimeSpan? debounceDelay = null)
    {
        lock (_lock)
        {
            // If what is pending or already written is identical, do not rewrite
            if (string.Equals(_pendingJson ?? _lastWrittenJson, json, StringComparison.Ordinal))
            {
                return;
            }

            _pendingJson = json;

            TimeSpan delay = debounceDelay ?? DefaultDebounceDelay;
            if (_debounceTimer == null)
            {
                _debounceTimer = new Timer(OnDebounceTimerElapsed, null, delay, Timeout.InfiniteTimeSpan);
            }
            else
            {
                _debounceTimer.Change(delay, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private static void OnDebounceTimerElapsed(object? state)
    {
        string? toWrite = null;
        lock (_lock)
        {
            toWrite = _pendingJson;
            _pendingJson = null;
        }

        if (toWrite != null)
        {
            var task = Task.Run(() => PerformAtomicWrite(toWrite));
            lock (_lock)
            {
                _activeWriteTask = task;
            }
        }
    }

    private static void PerformAtomicWrite(string json)
    {
        try
        {
            string path = GetNotesVaultPath();
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            string tmp = path + ".tmp." + Guid.NewGuid().ToString("N");
            File.WriteAllText(tmp, json);
            File.Move(tmp, path, overwrite: true);

            lock (_lock)
            {
                _lastWrittenJson = json;
            }
        }
        catch
        {
            // Disk write failure must fail safe and not crash app
        }
    }

    /// <summary>
    /// Synchronously flushes any pending queued write immediately.
    /// Used on session end, window close, and application exit.
    /// </summary>
    public static void FlushSync(int timeoutMs = 2000)
    {
        string? toWrite = null;
        Task? inFlightTask = null;

        lock (_lock)
        {
            _debounceTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            toWrite = _pendingJson;
            _pendingJson = null;
            inFlightTask = _activeWriteTask;
        }

        if (inFlightTask != null && !inFlightTask.IsCompleted)
        {
            try
            {
                inFlightTask.Wait(timeoutMs);
            }
            catch { }
        }

        if (toWrite != null)
        {
            PerformAtomicWrite(toWrite);
        }
    }
}
