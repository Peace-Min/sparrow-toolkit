using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SparrowClang.Protocol;

namespace Sparrow.ClangAnalyzer
{
    internal static class Program
    {
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
        };

        private static async Task<int> Main()
        {
            var response = new ClangAnalysisResponse();
            try
            {
                string requestJson = await Console.In.ReadToEndAsync().ConfigureAwait(false);
                ClangAnalysisRequest? request = JsonSerializer.Deserialize<ClangAnalysisRequest>(requestJson, JsonOptions);
                if (request == null) throw new InvalidDataException("Clang 분석 요청 JSON이 비어 있습니다.");
                if (request.ProtocolVersion != ClangProtocol.Version)
                    throw new InvalidDataException(
                        "지원하지 않는 Clang 분석 프로토콜입니다: " + request.ProtocolVersion);

                string clangPath = ClangLocator.Resolve(request.ClangExecutablePath);
                response.ClangVersion = await ClangLocator.ReadVersionAsync(clangPath).ConfigureAwait(false);
                using var timeout = new CancellationTokenSource(
                    Math.Max(1_000, request.TimeoutMilliseconds) * Math.Max(1, request.Files.Count));
                foreach (ClangFileRequest file in request.Files)
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    response.Files.Add(await AstAnalyzer.AnalyzeAsync(
                        clangPath,
                        response.ClangVersion,
                        request,
                        file,
                        timeout.Token).ConfigureAwait(false));
                }
            }
            catch (Exception error)
            {
                response.FatalError = error.Message;
            }

            await Console.Out.WriteAsync(JsonSerializer.Serialize(response, JsonOptions)).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(response.FatalError) ? 0 : 2;
        }
    }
}
