using System.Collections.Generic;

namespace SparrowClang.Protocol
{
    public static class ClangProtocol
    {
        public const int Version = 1;
    }

    public sealed class ClangAnalysisRequest
    {
        public int ProtocolVersion { get; set; } = ClangProtocol.Version;
        public string? ProjectRoot { get; set; }
        public string? ClangExecutablePath { get; set; }
        public int TimeoutMilliseconds { get; set; } = 30_000;
        public List<string> AdditionalArguments { get; set; } = new List<string>();
        public List<ClangFileRequest> Files { get; set; } = new List<ClangFileRequest>();
    }

    public sealed class ClangFileRequest
    {
        public string Path { get; set; } = "";
        public List<string> AdditionalArguments { get; set; } = new List<string>();
    }

    public sealed class ClangAnalysisResponse
    {
        public int ProtocolVersion { get; set; } = ClangProtocol.Version;
        public string AnalyzerVersion { get; set; } = "1.0";
        public string? ClangVersion { get; set; }
        public string? FatalError { get; set; }
        public List<ClangFileAnalysis> Files { get; set; } = new List<ClangFileAnalysis>();
    }

    public sealed class ClangFileAnalysis
    {
        public string Path { get; set; } = "";
        public bool Success { get; set; }
        public bool UsedCompilationDatabase { get; set; }
        public string? CompilationDatabasePath { get; set; }
        public string? WorkingDirectory { get; set; }
        public string Fingerprint { get; set; } = "";
        public int FunctionCount { get; set; }
        public int VariableCount { get; set; }
        public int CallCount { get; set; }
        public int LogicalExpressionCount { get; set; }
        public List<ClangTextEdit> LogicalEdits { get; set; } = new List<ClangTextEdit>();
        public List<string> Diagnostics { get; set; } = new List<string>();
    }

    public sealed class ClangTextEdit
    {
        public int Start { get; set; }
        public int Length { get; set; }
        public string Replacement { get; set; } = "";
        public string Rule { get; set; } = "";
    }
}
