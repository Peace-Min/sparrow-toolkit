using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Sparrow.ClangAnalyzer
{
    internal sealed class ResolvedCompilationCommand
    {
        internal string WorkingDirectory { get; init; } = "";
        internal string? DatabasePath { get; init; }
        internal List<string> Arguments { get; init; } = new List<string>();
        internal bool UsedDatabase => !string.IsNullOrWhiteSpace(DatabasePath);
    }

    internal static class CompilationCommandResolver
    {
        internal static ResolvedCompilationCommand Resolve(string file, string? projectRoot)
        {
            string fullFile = Path.GetFullPath(file);
            foreach (string database in FindDatabases(fullFile, projectRoot))
            {
                ResolvedCompilationCommand? resolved = TryReadDatabase(database, fullFile);
                if (resolved != null) return resolved;
            }

            string extension = Path.GetExtension(fullFile).ToLowerInvariant();
            bool cpp = extension is ".cpp" or ".cxx" or ".cc" or ".hpp" or ".hxx" or ".hh";
            return new ResolvedCompilationCommand
            {
                WorkingDirectory = Path.GetDirectoryName(fullFile) ?? Environment.CurrentDirectory,
                Arguments = new List<string>
                {
                    "-x", cpp ? "c++" : "c",
                    "-std=" + (cpp ? "gnu++17" : "gnu11"),
                    "-I", Path.GetDirectoryName(fullFile) ?? Environment.CurrentDirectory,
                },
            };
        }

        private static IEnumerable<string> FindDatabases(string file, string? projectRoot)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? boundary = NormalizeDirectory(projectRoot);
            DirectoryInfo? current = new DirectoryInfo(Path.GetDirectoryName(file) ?? Environment.CurrentDirectory);
            while (current != null)
            {
                foreach (string candidate in CommonCandidates(current.FullName))
                {
                    if (File.Exists(candidate) && seen.Add(candidate)) yield return candidate;
                }
                if (boundary != null && string.Equals(current.FullName, boundary, StringComparison.OrdinalIgnoreCase)) break;
                current = current.Parent;
            }

            if (boundary == null || !Directory.Exists(boundary)) yield break;
            var queue = new Queue<(string Directory, int Depth)>();
            queue.Enqueue((boundary, 0));
            int visited = 0;
            while (queue.Count > 0 && visited < 128)
            {
                (string directory, int depth) = queue.Dequeue();
                visited++;
                string candidate = Path.Combine(directory, "compile_commands.json");
                if (File.Exists(candidate) && seen.Add(candidate)) yield return candidate;
                if (depth >= 3) continue;
                try
                {
                    foreach (string child in Directory.EnumerateDirectories(directory))
                    {
                        string name = Path.GetFileName(child);
                        if (name.StartsWith(".", StringComparison.Ordinal) ||
                            name.Equals("node_modules", StringComparison.OrdinalIgnoreCase)) continue;
                        queue.Enqueue((child, depth + 1));
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }

        private static IEnumerable<string> CommonCandidates(string directory)
        {
            yield return Path.Combine(directory, "compile_commands.json");
            yield return Path.Combine(directory, "build", "compile_commands.json");
            yield return Path.Combine(directory, "Build", "compile_commands.json");
            yield return Path.Combine(directory, "out", "build", "compile_commands.json");
            yield return Path.Combine(directory, "cmake-build-debug", "compile_commands.json");
            yield return Path.Combine(directory, "cmake-build-release", "compile_commands.json");
        }

        private static ResolvedCompilationCommand? TryReadDatabase(string database, string fullFile)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(database));
                if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
                foreach (JsonElement entry in document.RootElement.EnumerateArray())
                {
                    if (!entry.TryGetProperty("file", out JsonElement fileElement)) continue;
                    string directory = entry.TryGetProperty("directory", out JsonElement directoryElement)
                        ? directoryElement.GetString() ?? Path.GetDirectoryName(database)!
                        : Path.GetDirectoryName(database)!;
                    string entryFile = fileElement.GetString() ?? "";
                    string candidate = Path.GetFullPath(Path.IsPathRooted(entryFile) ? entryFile : Path.Combine(directory, entryFile));
                    if (!string.Equals(candidate, fullFile, StringComparison.OrdinalIgnoreCase)) continue;

                    List<string> arguments;
                    if (entry.TryGetProperty("arguments", out JsonElement argumentsElement) &&
                        argumentsElement.ValueKind == JsonValueKind.Array)
                    {
                        arguments = argumentsElement.EnumerateArray()
                            .Select(value => value.GetString() ?? "")
                            .Where(value => value.Length > 0)
                            .ToList();
                    }
                    else if (entry.TryGetProperty("command", out JsonElement commandElement))
                    {
                        arguments = SplitCommandLine(commandElement.GetString() ?? "");
                    }
                    else
                    {
                        continue;
                    }

                    return new ResolvedCompilationCommand
                    {
                        WorkingDirectory = Path.GetFullPath(directory),
                        DatabasePath = database,
                        Arguments = Sanitize(arguments, fullFile),
                    };
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (JsonException) { }
            catch (ArgumentException) { }
            return null;
        }

        private static List<string> Sanitize(IReadOnlyList<string> arguments, string sourceFile)
        {
            var result = new List<string>();
            int start = arguments.Count > 0 ? 1 : 0;
            bool clStyle = arguments.Count > 0 &&
                (Path.GetFileNameWithoutExtension(arguments[0]).Equals("cl", StringComparison.OrdinalIgnoreCase) ||
                 Path.GetFileNameWithoutExtension(arguments[0]).Equals("clang-cl", StringComparison.OrdinalIgnoreCase));
            if (clStyle) result.Add("--driver-mode=cl");

            for (int i = start; i < arguments.Count; i++)
            {
                string argument = arguments[i];
                if (argument is "-c" or "/c" or "-fsyntax-only") continue;
                if (argument is "-o" or "-MF" or "-MT" or "-MQ" or "--serialize-diagnostics")
                {
                    i++;
                    continue;
                }
                if (argument.StartsWith("/Fo", StringComparison.OrdinalIgnoreCase) ||
                    argument.StartsWith("/Fe", StringComparison.OrdinalIgnoreCase) ||
                    argument.StartsWith("-M", StringComparison.Ordinal) ||
                    argument.StartsWith("-o", StringComparison.Ordinal) && argument.Length > 2) continue;
                if (LooksLikeSourceArgument(argument, sourceFile)) continue;
                result.Add(argument);
            }
            return result;
        }

        private static bool LooksLikeSourceArgument(string argument, string sourceFile)
        {
            string extension = Path.GetExtension(argument).ToLowerInvariant();
            if (extension is not (".c" or ".cc" or ".cpp" or ".cxx" or ".h" or ".hh" or ".hpp" or ".hxx"))
                return false;
            try
            {
                return string.Equals(Path.GetFullPath(argument), sourceFile, StringComparison.OrdinalIgnoreCase) ||
                       string.Equals(Path.GetFileName(argument), Path.GetFileName(sourceFile), StringComparison.OrdinalIgnoreCase);
            }
            catch (ArgumentException) { return false; }
        }

        private static List<string> SplitCommandLine(string command)
        {
            var result = new List<string>();
            var current = new StringBuilder();
            bool quoted = false;
            int backslashes = 0;
            for (int i = 0; i < command.Length; i++)
            {
                char character = command[i];
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (character == '"')
                {
                    current.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1) current.Append('"');
                    else quoted = !quoted;
                    backslashes = 0;
                    continue;
                }
                if (backslashes > 0)
                {
                    current.Append('\\', backslashes);
                    backslashes = 0;
                }
                if (char.IsWhiteSpace(character) && !quoted)
                {
                    if (current.Length > 0)
                    {
                        result.Add(current.ToString());
                        current.Clear();
                    }
                }
                else
                {
                    current.Append(character);
                }
            }
            if (backslashes > 0) current.Append('\\', backslashes);
            if (current.Length > 0) result.Add(current.ToString());
            return result;
        }

        private static string? NormalizeDirectory(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar); }
            catch (ArgumentException) { return null; }
        }
    }
}
