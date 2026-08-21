using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using SparrowCFamily.Core;
using SparrowCFamilyCommentFix;
using SparrowCFamilySyntaxFix;
using SparrowClang.Protocol;

namespace SparrowCFamilyPipeline
{
    /// <summary>C/C++ 코드와 주석 엔진을 파일 1회 읽기·쓰기 파이프라인으로 실행합니다.</summary>
    public static class CFamilyPipelineEngine
    {
        public sealed class Options
        {
            public CFamilySyntaxFixEngine.Options Syntax { get; init; } = new CFamilySyntaxFixEngine.Options();
            public CFamilyCommentFixEngine.Options Comment { get; init; } = new CFamilyCommentFixEngine.Options();
            public bool EnableIncrementalCache { get; init; } = true;
            public int MaxDegreeOfParallelism { get; init; }
            public bool EnableClangAst { get; init; }
            public string? ClangAnalyzerPath { get; init; }
            public string? ClangExecutablePath { get; init; }
            public string? ProjectRoot { get; init; }
            public int ClangTimeoutMilliseconds { get; init; } = 30_000;
            public IReadOnlyList<string> ClangAdditionalArguments { get; init; } = Array.Empty<string>();
        }

        public sealed class FileResult
        {
            internal FileResult(
                string path,
                bool changed,
                bool cacheSkipped,
                int tokenizationPasses,
                int appliedEdits,
                long elapsedMilliseconds,
                IReadOnlyList<StageResult> stages)
            {
                Path = path;
                Changed = changed;
                CacheSkipped = cacheSkipped;
                TokenizationPasses = tokenizationPasses;
                AppliedEdits = appliedEdits;
                ElapsedMilliseconds = elapsedMilliseconds;
                Stages = stages;
            }

            public string Path { get; }
            public bool Changed { get; }
            public bool CacheSkipped { get; }
            public int TokenizationPasses { get; }
            public int AppliedEdits { get; }
            public long ElapsedMilliseconds { get; }
            public IReadOnlyList<StageResult> Stages { get; }
        }

        public sealed class StageResult
        {
            internal StageResult(string name, int tokenizationPasses, int appliedEdits, long elapsedMilliseconds)
            {
                Name = name;
                TokenizationPasses = tokenizationPasses;
                AppliedEdits = appliedEdits;
                ElapsedMilliseconds = elapsedMilliseconds;
            }

            public string Name { get; }
            public int TokenizationPasses { get; }
            public int AppliedEdits { get; }
            public long ElapsedMilliseconds { get; }
        }

        public sealed class Result
        {
            internal Result(
                CFamilyTransformRunResult result,
                int clangAnalyzedFiles,
                int clangFallbackFiles,
                string? clangVersion)
            {
                ChangedFiles = result.ChangedFiles;
                CacheSkippedFiles = result.CacheSkippedFiles;
                TokenizationPasses = result.TokenizationPasses;
                AppliedEdits = result.AppliedEdits;
                ElapsedMilliseconds = result.ElapsedMilliseconds;
                Files = result.Files
                    .Select(file => new FileResult(
                        file.Path,
                        file.Changed,
                        file.CacheSkipped,
                        file.TokenizationPasses,
                        file.AppliedEdits,
                        file.ElapsedMilliseconds,
                        file.Stages.Select(stage => new StageResult(
                            stage.Name,
                            stage.TokenizationPasses,
                            stage.AppliedEdits,
                            stage.ElapsedMilliseconds)).ToArray()))
                    .ToArray();
                ClangAnalyzedFiles = clangAnalyzedFiles;
                ClangFallbackFiles = clangFallbackFiles;
                ClangVersion = clangVersion;
            }

            public int ChangedFiles { get; }
            public int CacheSkippedFiles { get; }
            public int TokenizationPasses { get; }
            public int AppliedEdits { get; }
            public long ElapsedMilliseconds { get; }
            public IReadOnlyList<FileResult> Files { get; }
            public int ClangAnalyzedFiles { get; }
            public int ClangFallbackFiles { get; }
            public string? ClangVersion { get; }
        }

        public static Result Apply(
            IEnumerable<string> files,
            Options options,
            CancellationToken token,
            Action<string> log)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (options.Syntax == null) throw new ArgumentException("코드 규칙 옵션이 필요합니다.", nameof(options));
            if (options.Comment == null) throw new ArgumentException("주석 규칙 옵션이 필요합니다.", nameof(options));

            CFamilySyntaxFixEngine.Options syntax = options.Syntax;
            CFamilyCommentFixEngine.Options comment = options.Comment;
            string[] paths = files
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var astAnalyses = new Dictionary<string, CFamilyAstFileAnalysis>(StringComparer.OrdinalIgnoreCase);
            int clangAnalyzedFiles = 0;
            int clangFallbackFiles = 0;
            string? clangVersion = null;
            if (options.EnableClangAst && syntax.LogicalParentheses && paths.Length > 0)
            {
                ClangAnalysisResponse? astResponse = ClangAnalyzerClient.Analyze(paths, options, token, log);
                if (astResponse != null)
                {
                    clangVersion = astResponse.ClangVersion;
                    foreach (ClangFileAnalysis file in astResponse.Files)
                    {
                        string path = Path.GetFullPath(file.Path);
                        var edits = file.LogicalEdits.Select(edit =>
                            new TextEdit(edit.Start, edit.Length, edit.Replacement, edit.Rule)).ToArray();
                        astAnalyses[path] = new CFamilyAstFileAnalysis(path, file.Success, file.Fingerprint, edits);
                        if (file.Success) clangAnalyzedFiles++;
                        else
                        {
                            clangFallbackFiles++;
                            string reason = file.Diagnostics.FirstOrDefault() ?? "파싱 실패";
                            log("Clang AST 폴백: " + Path.GetFileName(path) + " - " + reason);
                        }
                    }
                    clangFallbackFiles += Math.Max(0, paths.Length - astResponse.Files.Count);
                    log("Clang AST 분석: 성공 " + clangAnalyzedFiles + "개 / 토큰 폴백 " + clangFallbackFiles + "개" +
                        (string.IsNullOrWhiteSpace(clangVersion) ? "" : " / " + clangVersion));
                }
                else
                {
                    clangFallbackFiles = paths.Length;
                }
            }

            CFamilyTransformRunResult result = CFamilyTransformCore.ApplyDetailed(paths, new CFamilyTransformCore.Options
            {
                CompoundStatements = syntax.CompoundStatements,
                MissingElse = syntax.MissingElse,
                SwitchDefault = syntax.SwitchDefault,
                LogicalParentheses = syntax.LogicalParentheses,
                UnsignedSuffix = syntax.UnsignedSuffix,
                SizeOfPointee = syntax.SizeOfPointee,
                FixedWidthTypes = syntax.FixedWidthTypes,
                ConstantOnLeft = syntax.ConstantOnLeft,
                VariableInitialization = syntax.VariableInitialization,
                FileNoFollow = syntax.FileNoFollow,
                TrailingComment = comment.TrailingComment,
                CommentSpace = comment.CommentSpace,
                CommentPeriod = comment.CommentPeriod,
                CommentCapitalize = comment.CommentCapitalize,
                SingleLineDelimiter = comment.SingleLineDelimiter,
                SingleLineDelimiterText = comment.SingleLineDelimiterText,
                MultiLineDelimiter = comment.MultiLineDelimiter,
                MultiLineDelimiterText = comment.MultiLineDelimiterText,
                ParagraphDelimiter = comment.ParagraphDelimiter,
                ParagraphDelimiterText = comment.ParagraphDelimiterText,
                EnableIncrementalCache = options.EnableIncrementalCache,
                MaxDegreeOfParallelism = options.MaxDegreeOfParallelism,
                AstAnalyses = astAnalyses,
            }, token, log);
            return new Result(result, clangAnalyzedFiles, clangFallbackFiles, clangVersion);
        }
    }
}
