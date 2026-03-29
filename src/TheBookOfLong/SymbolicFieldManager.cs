using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace TheBookOfLong;

internal static class SymbolicFieldManager
{
    private static readonly object Sync = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true
    };

    private static readonly Dictionary<string, SourceRecord> SourcesByPath = new(StringComparer.OrdinalIgnoreCase);

    private static string _gameRoot = string.Empty;
    private static string _latestRoot = string.Empty;

    internal static void Initialize()
    {
        lock (Sync)
        {
            EnsureInitialized();
        }
    }

    internal static void Reset()
    {
        lock (Sync)
        {
            SourcesByPath.Clear();
        }
    }

    internal static void RegisterReference(
        string sourcePath,
        string symbolicId,
        string modName,
        string filePath,
        string? location,
        string referenceType)
    {
        string normalizedSourcePath = NormalizePath(sourcePath);
        string normalizedFilePath = NormalizePath(filePath);

        lock (Sync)
        {
            SourceRecord sourceRecord = GetOrCreateSourceRecord(normalizedSourcePath);
            AssignmentRecord assignmentRecord = GetOrCreateAssignmentRecord(sourceRecord, symbolicId);

            string locationText = location?.Trim() ?? string.Empty;
            for (int i = 0; i < assignmentRecord.References.Count; i += 1)
            {
                ReferenceRecord existing = assignmentRecord.References[i];
                if (string.Equals(existing.ModName, modName, StringComparison.Ordinal)
                    && string.Equals(existing.FilePath, normalizedFilePath, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(existing.Location, locationText, StringComparison.Ordinal)
                    && string.Equals(existing.ReferenceType, referenceType, StringComparison.Ordinal))
                {
                    return;
                }
            }

            assignmentRecord.References.Add(new ReferenceRecord
            {
                ModName = modName,
                FilePath = normalizedFilePath,
                Location = locationText,
                ReferenceType = referenceType
            });
        }
    }

    internal static void UpdateSourceBaseInfo(string sourcePath, bool hasBaseMaxId, int baseMaxId)
    {
        lock (Sync)
        {
            SourceRecord sourceRecord = GetOrCreateSourceRecord(NormalizePath(sourcePath));
            sourceRecord.HasBaseMaxId = hasBaseMaxId;
            sourceRecord.BaseMaxId = hasBaseMaxId ? baseMaxId : 0;
        }
    }

    internal static void UpdateSourceAssignments(string sourcePath, IReadOnlyDictionary<string, int> assignedIds)
    {
        lock (Sync)
        {
            SourceRecord sourceRecord = GetOrCreateSourceRecord(NormalizePath(sourcePath));
            foreach ((string symbolicId, int assignedId) in assignedIds)
            {
                AssignmentRecord assignmentRecord = GetOrCreateAssignmentRecord(sourceRecord, symbolicId);
                assignmentRecord.AssignedId = assignedId;
            }

            int maxAssignedId = 0;
            bool hasAssignedId = false;
            foreach (AssignmentRecord assignmentRecord in sourceRecord.Assignments.Values)
            {
                if (!assignmentRecord.AssignedId.HasValue)
                {
                    continue;
                }

                if (!hasAssignedId || assignmentRecord.AssignedId.Value > maxAssignedId)
                {
                    maxAssignedId = assignmentRecord.AssignedId.Value;
                    hasAssignedId = true;
                }
            }

            sourceRecord.HasAssignedIds = hasAssignedId;
            sourceRecord.MaxAssignedId = hasAssignedId ? maxAssignedId : 0;
        }
    }

    internal static void WriteReport()
    {
        lock (Sync)
        {
            if (!EnsureInitialized())
            {
                return;
            }

            List<object> sourceReports = new();
            List<string> orderedSourcePaths = new(SourcesByPath.Keys);
            orderedSourcePaths.Sort(StringComparer.OrdinalIgnoreCase);

            for (int sourceIndex = 0; sourceIndex < orderedSourcePaths.Count; sourceIndex += 1)
            {
                SourceRecord sourceRecord = SourcesByPath[orderedSourcePaths[sourceIndex]];
                List<object> assignmentReports = new();

                List<string> orderedSymbolicIds = new(sourceRecord.Assignments.Keys);
                orderedSymbolicIds.Sort(StringComparer.OrdinalIgnoreCase);

                for (int assignmentIndex = 0; assignmentIndex < orderedSymbolicIds.Count; assignmentIndex += 1)
                {
                    AssignmentRecord assignmentRecord = sourceRecord.Assignments[orderedSymbolicIds[assignmentIndex]];
                    List<object> referenceReports = new();

                    assignmentRecord.References.Sort(static (left, right) =>
                    {
                        int compare = string.Compare(left.FilePath, right.FilePath, StringComparison.OrdinalIgnoreCase);
                        if (compare != 0)
                        {
                            return compare;
                        }

                        compare = string.Compare(left.Location, right.Location, StringComparison.Ordinal);
                        if (compare != 0)
                        {
                            return compare;
                        }

                        compare = string.Compare(left.ModName, right.ModName, StringComparison.Ordinal);
                        return compare != 0
                            ? compare
                            : string.Compare(left.ReferenceType, right.ReferenceType, StringComparison.Ordinal);
                    });

                    for (int referenceIndex = 0; referenceIndex < assignmentRecord.References.Count; referenceIndex += 1)
                    {
                        ReferenceRecord referenceRecord = assignmentRecord.References[referenceIndex];
                        referenceReports.Add(new
                        {
                            referenceRecord.ModName,
                            referenceRecord.FilePath,
                            referenceRecord.Location,
                            referenceRecord.ReferenceType
                        });
                    }

                    assignmentReports.Add(new
                    {
                        assignmentRecord.SymbolicId,
                        assignmentRecord.AssignedId,
                        References = referenceReports
                    });
                }

                sourceReports.Add(new
                {
                    sourceRecord.SourcePath,
                    sourceRecord.HasBaseMaxId,
                    sourceRecord.BaseMaxId,
                    sourceRecord.HasAssignedIds,
                    sourceRecord.MaxAssignedId,
                    Assignments = assignmentReports
                });
            }

            string reportPath = Path.Combine(_latestRoot, "SymbolicFieldReport.json");
            string json = JsonSerializer.Serialize(
                new
                {
                    GeneratedAtUtc = DateTime.UtcNow,
                    Sources = sourceReports
                },
                JsonOptions);

            File.WriteAllText(reportPath, json, Utf8NoBom);
        }
    }

    private static SourceRecord GetOrCreateSourceRecord(string sourcePath)
    {
        if (!SourcesByPath.TryGetValue(sourcePath, out SourceRecord? sourceRecord))
        {
            sourceRecord = new SourceRecord
            {
                SourcePath = sourcePath
            };

            SourcesByPath[sourcePath] = sourceRecord;
        }

        return sourceRecord;
    }

    private static AssignmentRecord GetOrCreateAssignmentRecord(SourceRecord sourceRecord, string symbolicId)
    {
        if (!sourceRecord.Assignments.TryGetValue(symbolicId, out AssignmentRecord? assignmentRecord))
        {
            assignmentRecord = new AssignmentRecord
            {
                SymbolicId = symbolicId
            };

            sourceRecord.Assignments[symbolicId] = assignmentRecord;
        }

        return assignmentRecord;
    }

    private static bool EnsureInitialized()
    {
        if (!string.IsNullOrWhiteSpace(_latestRoot))
        {
            return true;
        }

        string? gameRoot = null;
        try
        {
            gameRoot = MelonLoader.Utils.MelonEnvironment.GameRootDirectory;
        }
        catch
        {
        }

        if (string.IsNullOrWhiteSpace(gameRoot))
        {
            try
            {
                string dataPath = global::UnityEngine.Application.dataPath;
                if (!string.IsNullOrWhiteSpace(dataPath))
                {
                    DirectoryInfo? parent = Directory.GetParent(dataPath);
                    gameRoot = parent?.FullName;
                }
            }
            catch
            {
            }
        }

        if (string.IsNullOrWhiteSpace(gameRoot))
        {
            MelonLoader.MelonLogger.Warning("Symbolic field report path is unavailable. Could not resolve game root directory.");
            return false;
        }

        _gameRoot = Path.GetFullPath(gameRoot);
        _latestRoot = Path.Combine(_gameRoot, "DataDump", "Latest");
        Directory.CreateDirectory(_latestRoot);
        return true;
    }

    private static string NormalizePath(string path)
    {
        string normalized = path.Replace('\\', '/').TrimStart('/');
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    private sealed class SourceRecord
    {
        public string SourcePath { get; set; } = string.Empty;

        public bool HasBaseMaxId { get; set; }

        public int BaseMaxId { get; set; }

        public bool HasAssignedIds { get; set; }

        public int MaxAssignedId { get; set; }

        public Dictionary<string, AssignmentRecord> Assignments { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class AssignmentRecord
    {
        public string SymbolicId { get; set; } = string.Empty;

        public int? AssignedId { get; set; }

        public List<ReferenceRecord> References { get; } = new();
    }

    private sealed class ReferenceRecord
    {
        public string ModName { get; set; } = string.Empty;

        public string FilePath { get; set; } = string.Empty;

        public string Location { get; set; } = string.Empty;

        public string ReferenceType { get; set; } = string.Empty;
    }
}
