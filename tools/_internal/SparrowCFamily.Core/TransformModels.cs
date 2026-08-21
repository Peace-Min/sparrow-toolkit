using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SparrowCFamily.Core
{
    internal sealed class CFamilyAstFileAnalysis
    {
        internal CFamilyAstFileAnalysis(
            string path,
            bool success,
            string fingerprint,
            IReadOnlyList<TextEdit> logicalEdits)
        {
            Path = path;
            Success = success;
            Fingerprint = fingerprint;
            LogicalEdits = logicalEdits;
        }

        internal string Path { get; }
        internal bool Success { get; }
        internal string Fingerprint { get; }
        internal IReadOnlyList<TextEdit> LogicalEdits { get; }
    }

    internal sealed class CFamilyTransformStageResult
    {
        internal CFamilyTransformStageResult(
            string name,
            int tokenizationPasses,
            int appliedEdits,
            long elapsedMilliseconds)
        {
            Name = name;
            TokenizationPasses = tokenizationPasses;
            AppliedEdits = appliedEdits;
            ElapsedMilliseconds = elapsedMilliseconds;
        }

        internal string Name { get; }
        internal int TokenizationPasses { get; }
        internal int AppliedEdits { get; }
        internal long ElapsedMilliseconds { get; }
    }

    internal sealed class CFamilyFileTransformResult
    {
        internal CFamilyFileTransformResult(
            string path,
            bool changed,
            bool cacheSkipped,
            int tokenizationPasses,
            int appliedEdits,
            long elapsedMilliseconds,
            IReadOnlyList<CFamilyTransformStageResult> stages,
            string? logMessage)
        {
            Path = path;
            Changed = changed;
            CacheSkipped = cacheSkipped;
            TokenizationPasses = tokenizationPasses;
            AppliedEdits = appliedEdits;
            ElapsedMilliseconds = elapsedMilliseconds;
            Stages = stages;
            LogMessage = logMessage;
        }

        internal string Path { get; }
        internal bool Changed { get; }
        internal bool CacheSkipped { get; }
        internal int TokenizationPasses { get; }
        internal int AppliedEdits { get; }
        internal long ElapsedMilliseconds { get; }
        internal IReadOnlyList<CFamilyTransformStageResult> Stages { get; }
        internal string? LogMessage { get; }
    }

    internal sealed class CFamilyTransformRunResult
    {
        internal CFamilyTransformRunResult(IReadOnlyList<CFamilyFileTransformResult> files)
        {
            Files = files;
            foreach (CFamilyFileTransformResult file in files)
            {
                if (file.Changed) ChangedFiles++;
                if (file.CacheSkipped) CacheSkippedFiles++;
                TokenizationPasses += file.TokenizationPasses;
                AppliedEdits += file.AppliedEdits;
                ElapsedMilliseconds += file.ElapsedMilliseconds;
            }
        }

        internal IReadOnlyList<CFamilyFileTransformResult> Files { get; }
        internal int ChangedFiles { get; }
        internal int CacheSkippedFiles { get; }
        internal int TokenizationPasses { get; }
        internal int AppliedEdits { get; }
        internal long ElapsedMilliseconds { get; }
    }

    internal static class CFamilyIncrementalCache
    {
        private const int SchemaVersion = 1;

        private sealed class Entry
        {
            internal Entry(long length, long lastWriteTicks, string optionKey)
            {
                Length = length;
                LastWriteTicks = lastWriteTicks;
                OptionKey = optionKey;
            }

            internal long Length { get; }
            internal long LastWriteTicks { get; }
            internal string OptionKey { get; }
        }

        private sealed class PersistedEntry
        {
            public string Path { get; set; } = "";
            public long Length { get; set; }
            public long LastWriteTicks { get; set; }
            public string OptionKey { get; set; } = "";
        }

        private sealed class PersistedCache
        {
            public int Version { get; set; }
            public List<PersistedEntry> Entries { get; set; } = new List<PersistedEntry>();
        }

        private static readonly ConcurrentDictionary<string, Entry> Entries =
            new ConcurrentDictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        private static readonly object PersistenceGate = new object();
        private static volatile bool _loaded;
        private static volatile bool _dirty;

        private static string CachePath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SparrowRunner",
            "cache",
            "cfamily-transform-cache.json");

        internal static bool IsCurrent(string path, string optionKey)
        {
            EnsureLoaded();
            var info = new FileInfo(path);
            if (!Entries.TryGetValue(path, out Entry? entry)) return false;
            return entry.Length == info.Length &&
                   entry.LastWriteTicks == info.LastWriteTimeUtc.Ticks &&
                   string.Equals(entry.OptionKey, optionKey, StringComparison.Ordinal);
        }

        internal static void Record(string path, string optionKey)
        {
            EnsureLoaded();
            var info = new FileInfo(path);
            Entries[path] = new Entry(info.Length, info.LastWriteTimeUtc.Ticks, optionKey);
            _dirty = true;
        }

        internal static void Invalidate(string path)
        {
            EnsureLoaded();
            if (Entries.TryRemove(path, out _)) _dirty = true;
        }

        internal static void Flush()
        {
            EnsureLoaded();
            lock (PersistenceGate)
            {
                if (!_dirty) return;
                try
                {
                    string cachePath = CachePath;
                    string? directory = Path.GetDirectoryName(cachePath);
                    if (string.IsNullOrWhiteSpace(directory)) return;
                    Directory.CreateDirectory(directory);
                    var snapshot = new PersistedCache
                    {
                        Version = SchemaVersion,
                        Entries = Entries
                            .Where(item => File.Exists(item.Key))
                            .Take(50_000)
                            .Select(item => new PersistedEntry
                            {
                                Path = item.Key,
                                Length = item.Value.Length,
                                LastWriteTicks = item.Value.LastWriteTicks,
                                OptionKey = item.Value.OptionKey,
                            })
                            .ToList(),
                    };
                    string temp = cachePath + ".tmp";
                    File.WriteAllText(temp, JsonSerializer.Serialize(snapshot), new UTF8Encoding(false));
                    File.Move(temp, cachePath, overwrite: true);
                    _dirty = false;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (JsonException) { }
            }
        }

        private static void EnsureLoaded()
        {
            if (_loaded) return;
            lock (PersistenceGate)
            {
                if (_loaded) return;
                _loaded = true;
                try
                {
                    if (!File.Exists(CachePath)) return;
                    PersistedCache? cache = JsonSerializer.Deserialize<PersistedCache>(File.ReadAllText(CachePath));
                    if (cache == null || cache.Version != SchemaVersion) return;
                    foreach (PersistedEntry item in cache.Entries)
                    {
                        if (string.IsNullOrWhiteSpace(item.Path) || string.IsNullOrWhiteSpace(item.OptionKey) ||
                            !File.Exists(item.Path)) continue;
                        Entries[item.Path] = new Entry(item.Length, item.LastWriteTicks, item.OptionKey);
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                catch (JsonException) { }
            }
        }
    }
}
