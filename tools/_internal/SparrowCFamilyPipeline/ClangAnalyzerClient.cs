using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SparrowClang.Protocol;

namespace SparrowCFamilyPipeline
{
    internal static class ClangAnalyzerClient
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
        };

        internal static ClangAnalysisResponse? Analyze(
            IReadOnlyList<string> files,
            CFamilyPipelineEngine.Options options,
            CancellationToken token,
            Action<string> log) =>
            AnalyzeAsync(files, options, token, log).GetAwaiter().GetResult();

        private static async Task<ClangAnalysisResponse?> AnalyzeAsync(
            IReadOnlyList<string> files,
            CFamilyPipelineEngine.Options options,
            CancellationToken token,
            Action<string> log)
        {
            string? analyzerPath = ResolveAnalyzerPath(options.ClangAnalyzerPath);
            if (analyzerPath == null)
            {
                log("Clang AST 분석기를 찾지 못해 C/C++ 토큰 엔진으로 처리합니다.");
                return null;
            }

            var request = new ClangAnalysisRequest
            {
                ProjectRoot = options.ProjectRoot,
                ClangExecutablePath = options.ClangExecutablePath,
                TimeoutMilliseconds = Math.Max(1_000, options.ClangTimeoutMilliseconds),
                AdditionalArguments = options.ClangAdditionalArguments.ToList(),
                Files = files.Select(path => new ClangFileRequest { Path = Path.GetFullPath(path) }).ToList(),
            };
            string requestJson = JsonSerializer.Serialize(request, JsonOptions);
            ProcessStartInfo startInfo = CreateStartInfo(analyzerPath);
            using Process process = Process.Start(startInfo) ??
                throw new InvalidOperationException("Sparrow.ClangAnalyzer 프로세스를 시작하지 못했습니다.");
            using CancellationTokenRegistration registration = token.Register(() =>
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            });

            Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
            Task<string> errorTask = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteAsync(requestJson).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            string output = await outputTask.ConfigureAwait(false);
            string error = await errorTask.ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(output))
            {
                log("Clang AST 분석기 응답이 비어 있어 토큰 엔진으로 처리합니다." +
                    (string.IsNullOrWhiteSpace(error) ? "" : " " + FirstLine(error)));
                return null;
            }

            try
            {
                ClangAnalysisResponse? response = JsonSerializer.Deserialize<ClangAnalysisResponse>(output, JsonOptions);
                if (response == null || response.ProtocolVersion != ClangProtocol.Version)
                {
                    log("Clang AST 분석기 프로토콜이 일치하지 않아 토큰 엔진으로 처리합니다.");
                    return null;
                }
                if (!string.IsNullOrWhiteSpace(response.FatalError))
                {
                    log("Clang AST 분석기 오류: " + response.FatalError + " 토큰 엔진으로 처리합니다.");
                    return null;
                }
                return response;
            }
            catch (JsonException exception)
            {
                log("Clang AST 분석기 JSON 오류: " + exception.Message + " 토큰 엔진으로 처리합니다.");
                return null;
            }
        }

        private static ProcessStartInfo CreateStartInfo(string analyzerPath)
        {
            bool dll = string.Equals(Path.GetExtension(analyzerPath), ".dll", StringComparison.OrdinalIgnoreCase);
            var startInfo = new ProcessStartInfo(dll ? "dotnet" : analyzerPath)
            {
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            if (dll) startInfo.ArgumentList.Add(analyzerPath);
            return startInfo;
        }

        private static string? ResolveAnalyzerPath(string? requested)
        {
            var candidates = new List<string?>
            {
                requested,
                Environment.GetEnvironmentVariable("SPARROW_CLANG_ANALYZER_PATH"),
                Path.Combine(AppContext.BaseDirectory, "clang-analyzer", "Sparrow.ClangAnalyzer.exe"),
                Path.Combine(AppContext.BaseDirectory, "Sparrow.ClangAnalyzer.exe"),
            };
            DirectoryInfo? current = new DirectoryInfo(AppContext.BaseDirectory);
            for (int depth = 0; current != null && depth < 8; depth++, current = current.Parent)
            {
                candidates.Add(Path.Combine(current.FullName, "tools", "_internal", "Sparrow.ClangAnalyzer",
                    "bin", "Release", "net8.0", "Sparrow.ClangAnalyzer.exe"));
            }
            foreach (string? candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate)) continue;
                try
                {
                    string full = Path.GetFullPath(candidate);
                    if (File.Exists(full)) return full;
                    string dll = Path.ChangeExtension(full, ".dll");
                    if (File.Exists(dll)) return dll;
                }
                catch (ArgumentException) { }
            }
            return null;
        }

        private static string FirstLine(string text) =>
            text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? "";
    }
}
