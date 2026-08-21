using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Sparrow.ClangAnalyzer
{
    internal static class ClangLocator
    {
        internal static string Resolve(string? requestedPath)
        {
            var candidates = new List<string?>
            {
                requestedPath,
                Environment.GetEnvironmentVariable("SPARROW_CLANG_PATH"),
                Path.Combine(AppContext.BaseDirectory, "clang", "clang.exe"),
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "clang", "clang.exe")),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LLVM", "bin", "clang.exe"),
            };
            string? fromPath = FindOnPath("clang.exe");
            if (!string.IsNullOrWhiteSpace(fromPath)) candidates.Add(fromPath);

            foreach (string? candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                string fullPath;
                try { fullPath = Path.GetFullPath(candidate); }
                catch (Exception) { continue; }
                if (File.Exists(fullPath)) return fullPath;
            }
            throw new FileNotFoundException(
                "clang.exe를 찾을 수 없습니다. publish/clang에 포함하거나 SPARROW_CLANG_PATH를 설정하세요.");
        }

        internal static async Task<string> ReadVersionAsync(string clangPath)
        {
            var startInfo = new ProcessStartInfo(clangPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("--version");
            using Process process = Process.Start(startInfo) ??
                throw new InvalidOperationException("clang --version 프로세스를 시작하지 못했습니다.");
            string output = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            await process.WaitForExitAsync().ConfigureAwait(false);
            string firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "clang";
            return firstLine.Trim();
        }

        private static string? FindOnPath(string fileName)
        {
            string? path = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrWhiteSpace(path)) return null;
            foreach (string directory in path.Split(Path.PathSeparator))
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                try
                {
                    string candidate = Path.Combine(directory.Trim().Trim('"'), fileName);
                    if (File.Exists(candidate)) return candidate;
                }
                catch (Exception) { }
            }
            return null;
        }
    }
}
