using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace SparrowCFamily.Core
{
    /// <summary>
    /// C/C++ 헤더용 보수적 기본 규칙. C#의 var 규칙은 C에서 문법적으로 성립하지 않으므로 적용하지 않고,
    /// 코드 규칙에서는 의미를 바꾸지 않는 조건식 외곽 괄호만, 주석 규칙에서는 기본 4종만 처리한다.
    /// </summary>
    internal static class CFamilyTransformCore
    {
        internal sealed class Options
        {
            public bool CompoundStatements { get; init; }
            public bool MissingElse { get; init; }
            public bool SwitchDefault { get; init; }
            public bool LogicalParentheses { get; init; }
            public bool UnsignedSuffix { get; init; }
            public bool SizeOfPointee { get; init; }
            public bool FixedWidthTypes { get; init; }
            public bool ConstantOnLeft { get; init; }
            public bool VariableInitialization { get; init; }
            public bool FileNoFollow { get; init; }
            public bool TrailingComment { get; init; }
            public bool CommentSpace { get; init; }
            public bool CommentPeriod { get; init; }
            public bool CommentCapitalize { get; init; }
            public bool SingleLineDelimiter { get; init; }
            public string SingleLineDelimiterText { get; init; } = "///<";
            public bool MultiLineDelimiter { get; init; }
            public string MultiLineDelimiterText { get; init; } = "/**";
            public bool ParagraphDelimiter { get; init; }
            public string ParagraphDelimiterText { get; init; } = "//!";
            public bool EnableIncrementalCache { get; init; } = true;
            public int MaxDegreeOfParallelism { get; init; }
            public IReadOnlyDictionary<string, CFamilyAstFileAnalysis>? AstAnalyses { get; init; }

            internal string CacheKey()
            {
                var key = new StringBuilder(192);
                key.Append("cfamily-core-v4|")
                    .Append(CompoundStatements ? '1' : '0')
                    .Append(MissingElse ? '1' : '0')
                    .Append(SwitchDefault ? '1' : '0')
                    .Append(LogicalParentheses ? '1' : '0')
                    .Append(UnsignedSuffix ? '1' : '0')
                    .Append(SizeOfPointee ? '1' : '0')
                    .Append(FixedWidthTypes ? '1' : '0')
                    .Append(ConstantOnLeft ? '1' : '0')
                    .Append(VariableInitialization ? '1' : '0')
                    .Append(FileNoFollow ? '1' : '0')
                    .Append(TrailingComment ? '1' : '0')
                    .Append(CommentSpace ? '1' : '0')
                    .Append(CommentPeriod ? '1' : '0')
                    .Append(CommentCapitalize ? '1' : '0')
                    .Append(SingleLineDelimiter ? '1' : '0').Append(':').Append(SingleLineDelimiterText.Length).Append(':').Append(SingleLineDelimiterText)
                    .Append(MultiLineDelimiter ? '1' : '0').Append(':').Append(MultiLineDelimiterText.Length).Append(':').Append(MultiLineDelimiterText)
                    .Append(ParagraphDelimiter ? '1' : '0').Append(':').Append(ParagraphDelimiterText.Length).Append(':').Append(ParagraphDelimiterText);
                return key.ToString();
            }
        }

        internal static int Apply(IEnumerable<string> files, Options options, CancellationToken token, Action<string> log)
            => ApplyDetailed(files, options, token, log).ChangedFiles;

        internal static CFamilyTransformRunResult ApplyDetailed(
            IEnumerable<string> files,
            Options options,
            CancellationToken token,
            Action<string> log)
        {
            if (files == null) throw new ArgumentNullException(nameof(files));
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (log == null) throw new ArgumentNullException(nameof(log));
            ValidateOptions(options);

            string[] paths = files
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var results = new CFamilyFileTransformResult[paths.Length];
            string optionKey = options.CacheKey();
            Exception? firstError = null;
            int degree = CalculateParallelism(paths, options.MaxDegreeOfParallelism);

            Parallel.For(0, paths.Length, new ParallelOptions
            {
                CancellationToken = token,
                MaxDegreeOfParallelism = degree,
            }, (index, state) =>
            {
                if (Volatile.Read(ref firstError) != null)
                {
                    state.Stop();
                    return;
                }
                try
                {
                    results[index] = TransformFile(paths[index], options, optionKey, token);
                }
                catch (Exception error)
                {
                    Interlocked.CompareExchange(ref firstError, error, comparand: null);
                    state.Stop();
                }
            });

            if (firstError != null) ExceptionDispatchInfo.Capture(firstError).Throw();
            foreach (CFamilyFileTransformResult result in results)
            {
                if (!string.IsNullOrWhiteSpace(result.LogMessage)) log(result.LogMessage!);
            }
            if (options.EnableIncrementalCache) CFamilyIncrementalCache.Flush();
            return new CFamilyTransformRunResult(results);
        }

        private static int CalculateParallelism(IReadOnlyList<string> paths, int requestedDegree)
        {
            if (paths.Count <= 1) return 1;
            if (requestedDegree > 0) return Math.Min(paths.Count, requestedDegree);

            int cpuDegree = Math.Max(1, Environment.ProcessorCount - 1);
            bool networkPath = paths.Any(path => path.StartsWith("\\\\", StringComparison.Ordinal));
            if (networkPath) return Math.Min(paths.Count, Math.Min(cpuDegree, 2));

            long totalBytes = 0;
            int sampled = Math.Min(paths.Count, 128);
            for (int i = 0; i < sampled; i++)
            {
                try { totalBytes += new FileInfo(paths[i]).Length; }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            long averageBytes = sampled == 0 ? 0 : totalBytes / sampled;
            int storageLimit = averageBytes >= 4L * 1024 * 1024 ? 2 :
                averageBytes >= 512L * 1024 ? 4 : 8;
            return Math.Min(paths.Count, Math.Min(cpuDegree, storageLimit));
        }

        private static void ValidateOptions(Options options)
        {
            if (options.SingleLineDelimiter)
                ValidateLineDelimiter(options.SingleLineDelimiterText, nameof(options.SingleLineDelimiterText));
            if (options.MultiLineDelimiter)
                ValidateBlockDelimiter(options.MultiLineDelimiterText, nameof(options.MultiLineDelimiterText));
            if (options.ParagraphDelimiter)
                ValidateLineDelimiter(options.ParagraphDelimiterText, nameof(options.ParagraphDelimiterText));
        }

        private static void ValidateLineDelimiter(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("//", StringComparison.Ordinal) ||
                value.Length > 16 || value.Any(char.IsWhiteSpace))
                throw new ArgumentException("한 줄 주석 구분자는 공백 없이 //로 시작해야 합니다.", parameterName);
        }

        private static void ValidateBlockDelimiter(string value, string parameterName)
        {
            if (string.IsNullOrWhiteSpace(value) || !value.StartsWith("/*", StringComparison.Ordinal) ||
                value.Contains("*/", StringComparison.Ordinal) || value.Length > 16 || value.Any(char.IsWhiteSpace))
                throw new ArgumentException("블록 주석 구분자는 공백 없이 /*로 시작해야 하며 */를 포함할 수 없습니다.", parameterName);
        }

        private static CFamilyFileTransformResult TransformFile(
            string file,
            Options options,
            string optionKey,
            CancellationToken token)
        {
            var stopwatch = Stopwatch.StartNew();
            token.ThrowIfCancellationRequested();
            CFamilyAstFileAnalysis? astAnalysis = FindAstAnalysis(options.AstAnalyses, file);
            string fileOptionKey = optionKey + "|ast:" +
                (astAnalysis == null ? "none" : astAnalysis.Success ? astAnalysis.Fingerprint : "fallback");
            if (options.EnableIncrementalCache && CFamilyIncrementalCache.IsCurrent(file, fileOptionKey))
                return new CFamilyFileTransformResult(
                    file,
                    changed: false,
                    cacheSkipped: true,
                    tokenizationPasses: 0,
                    appliedEdits: 0,
                    elapsedMilliseconds: stopwatch.ElapsedMilliseconds,
                    stages: Array.Empty<CFamilyTransformStageResult>(),
                    logMessage: null);

            CFamilyIncrementalCache.Invalidate(file);
            CFamilySourceDocument document = CFamilySourceDocument.Load(file);
            TransformDocument(document, options, astAnalysis, token);
            bool changed = document.IsChanged;
            if (changed)
            {
                token.ThrowIfCancellationRequested();
                document.Save();
            }
            if (options.EnableIncrementalCache) CFamilyIncrementalCache.Record(file, fileOptionKey);
            stopwatch.Stop();
            return new CFamilyFileTransformResult(
                file,
                changed,
                cacheSkipped: false,
                document.TokenizationPasses,
                document.AppliedEditCount,
                stopwatch.ElapsedMilliseconds,
                document.Stages,
                changed
                    ? "C/C++ 선택 규칙 적용: " + file +
                      " (편집 " + document.AppliedEditCount + "건, 토큰화 " + document.TokenizationPasses +
                      "회, " + stopwatch.ElapsedMilliseconds + "ms)"
                    : null);
        }

        private static CFamilyAstFileAnalysis? FindAstAnalysis(
            IReadOnlyDictionary<string, CFamilyAstFileAnalysis>? analyses,
            string file)
        {
            if (analyses == null || analyses.Count == 0) return null;
            if (analyses.TryGetValue(file, out CFamilyAstFileAnalysis? direct)) return direct;
            string fullPath;
            try { fullPath = Path.GetFullPath(file); }
            catch (ArgumentException) { return null; }
            return analyses.TryGetValue(fullPath, out CFamilyAstFileAnalysis? normalized) ? normalized : null;
        }

        private static void TransformDocument(
            CFamilySourceDocument document,
            Options options,
            CFamilyAstFileAnalysis? astAnalysis,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            bool astLogicalAvailable = false;
            if (options.LogicalParentheses && astAnalysis?.Success == true)
            {
                document.MeasureStage("clang-ast-logical", () =>
                    astLogicalAvailable = TryApplyAstLogicalEdits(document, astAnalysis));
            }
            token.ThrowIfCancellationRequested();
            if (options.CompoundStatements)
                document.MeasureStage("compound-statements", () => AddCompoundStatements(document, token));
            token.ThrowIfCancellationRequested();
            document.MeasureStage("syntax-normalization", () => ApplySyntaxNormalization(document, options));
            token.ThrowIfCancellationRequested();
            document.MeasureStage("comparison-rules", () => ApplyComparisonRules(document, options));
            token.ThrowIfCancellationRequested();
            document.MeasureStage("logical-comment-rules", () =>
                ApplyLogicalAndCommentRules(document, options, useTokenLogicalFallback: !astLogicalAvailable));
        }

        private static bool TryApplyAstLogicalEdits(
            CFamilySourceDocument document,
            CFamilyAstFileAnalysis analysis)
        {
            var edits = new EditCollector();
            foreach (TextEdit edit in analysis.LogicalEdits)
            {
                if (edit.Start < 0 || edit.Length != 0 || edit.Start > document.Text.Length ||
                    (edit.Replacement != "(" && edit.Replacement != ")"))
                    return false;
                edits.Add(edit);
            }
            document.Apply(edits);
            return true;
        }

        private readonly struct WrapCandidate
        {
            internal WrapCandidate(CodeToken control, CodeToken bodyStart, CodeToken bodyEnd)
            {
                Control = control;
                BodyStart = bodyStart;
                BodyEnd = bodyEnd;
            }

            internal CodeToken Control { get; }
            internal CodeToken BodyStart { get; }
            internal CodeToken BodyEnd { get; }
        }

        private static void ApplySyntaxNormalization(CFamilySourceDocument document, Options options)
        {
            bool hasStructuralRules = options.MissingElse || options.SwitchDefault ||
                options.CompoundStatements;
            bool hasTokenRules = options.UnsignedSuffix || options.SizeOfPointee ||
                options.VariableInitialization || options.FileNoFollow || options.FixedWidthTypes;
            if (!hasStructuralRules && !hasTokenRules) return;

            IReadOnlyList<CodeToken> tokens = document.Tokens;
            var edits = new EditCollector();
            if (options.MissingElse)
            {
                CollectLegacyMissingElseComments(document.Text, document.ProtectedSpans, edits);
                CollectMissingElse(document.Text, document.PreferredNewLine, tokens, edits);
            }
            if (options.SwitchDefault)
                CollectMissingSwitchDefault(document.Text, document.PreferredNewLine, tokens, edits);
            if (hasStructuralRules)
                CollectControlBraceStyle(document.Text, document.PreferredNewLine, tokens, edits);

            if (options.UnsignedSuffix) CollectIntegerLiteralSuffixes(tokens, edits);
            if (options.SizeOfPointee) CollectSizeOfPointers(tokens, edits);
            if (options.VariableInitialization)
                CollectVariableInitializers(
                    tokens,
                    edits,
                    convertInitializerTypes: options.FixedWidthTypes,
                    addIntegerSuffix: options.UnsignedSuffix);
            if (options.FileNoFollow) CollectNoFollow(tokens, edits);
            if (options.FixedWidthTypes)
                CollectFixedWidthIntegerTypes(document.Text, document.PreferredNewLine, tokens, edits);
            document.Apply(edits);
        }

        private static void ApplyComparisonRules(CFamilySourceDocument document, Options options)
        {
            if (!options.ConstantOnLeft) return;
            var edits = new EditCollector();
            CollectConstantsOnLeft(document.Text, document.Tokens, edits);
            document.Apply(edits);
        }

        private static void ApplyLogicalAndCommentRules(
            CFamilySourceDocument document,
            Options options,
            bool useTokenLogicalFallback)
        {
            bool hasCommentRules = options.TrailingComment || options.CommentSpace ||
                options.CommentPeriod || options.CommentCapitalize || options.SingleLineDelimiter ||
                options.MultiLineDelimiter || options.ParagraphDelimiter;
            bool applyTokenLogical = options.LogicalParentheses && useTokenLogicalFallback;
            if (!applyTokenLogical && !hasCommentRules) return;

            var edits = new EditCollector();
            if (applyTokenLogical)
                CollectLogicalParentheses(document.Text, document.Tokens, edits);
            if (hasCommentRules)
            {
                CollectCommentEdits(
                    document.Text,
                    document.PreferredNewLine,
                    document.ProtectedSpans,
                    options,
                    edits);
            }
            document.Apply(edits);
        }

        private static void AddCompoundStatements(CFamilySourceDocument document, CancellationToken token)
        {
            // 안쪽 후보부터 편집을 수집하고 같은 줄의 중첩 깊이를 들여쓰기에 반영해
            // 모든 중괄호를 최초 토큰화 결과에서 한 번에 추가합니다.
            token.ThrowIfCancellationRequested();
            IReadOnlyList<CodeToken> tokens = document.Tokens;
            var candidates = new List<WrapCandidate>();
            for (int i = 0; i < tokens.Count; i++)
            {
                CodeToken control = tokens[i];
                int bodyStart;
                if (control.Is("if") || control.Is("for") || control.Is("while"))
                {
                    int open = i + 1 < tokens.Count && tokens[i + 1].Is("(") ? i + 1 : -1;
                    int close = open >= 0 ? FindMatchingToken(tokens, open, "(", ")") : -1;
                    if (close < 0 || close + 1 >= tokens.Count || tokens[close + 1].Is(";")) continue;
                    bodyStart = close + 1;
                }
                else if (control.Is("else"))
                {
                    if (i + 1 >= tokens.Count || tokens[i + 1].Is("if")) continue;
                    bodyStart = i + 1;
                }
                else if (control.Is("do"))
                {
                    if (i + 1 >= tokens.Count) continue;
                    bodyStart = i + 1;
                }
                else
                {
                    continue;
                }

                if (tokens[bodyStart].Is("{")) continue;
                int bodyEnd = FindStatementEnd(tokens, bodyStart);
                if (bodyEnd >= bodyStart)
                    candidates.Add(new WrapCandidate(tokens[i], tokens[bodyStart], tokens[bodyEnd]));
            }

            if (candidates.Count == 0) return;
            var edits = new EditCollector();
            IReadOnlyDictionary<int, int> sameLineIndentLevels = BuildSameLineIndentLevels(document.Text, candidates);
            foreach (WrapCandidate candidate in candidates
                .OrderBy(candidate => candidate.BodyEnd.End - candidate.BodyStart.Start)
                .ThenByDescending(candidate => candidate.Control.Start))
            {
                CollectWrapStatementBody(
                    document.Text,
                    document.PreferredNewLine,
                    candidate,
                    edits,
                    sameLineIndentLevels[candidate.Control.Start]);
            }
            document.Apply(edits);
        }

        private static IReadOnlyDictionary<int, int> BuildSameLineIndentLevels(
            string source,
            IReadOnlyList<WrapCandidate> candidates)
        {
            var levels = new Dictionary<int, int>(candidates.Count);
            foreach (IGrouping<int, WrapCandidate> line in candidates.GroupBy(candidate =>
                FindLineStart(source, candidate.Control.Start)))
            {
                var ancestors = new Stack<WrapCandidate>();
                foreach (WrapCandidate candidate in line
                    .OrderBy(candidate => candidate.Control.Start)
                    .ThenByDescending(candidate => candidate.BodyEnd.End))
                {
                    while (ancestors.Count > 0 &&
                           (candidate.Control.Start >= ancestors.Peek().BodyEnd.End ||
                            candidate.BodyEnd.End > ancestors.Peek().BodyEnd.End))
                    {
                        ancestors.Pop();
                    }
                    levels[candidate.Control.Start] = ancestors.Count;
                    ancestors.Push(candidate);
                }
            }
            return levels;
        }

        private static void CollectControlBraceStyle(
            string source,
            string newline,
            IReadOnlyList<CodeToken> tokens,
            EditCollector edits)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                CodeToken control = tokens[i];
                if (control.Is("if") || control.Is("for") || control.Is("while") || control.Is("switch"))
                {
                    if (i + 1 < tokens.Count && tokens[i + 1].Is("("))
                    {
                        int close = FindMatchingToken(tokens, i + 1, "(", ")");
                        if (close >= 0 && close + 1 < tokens.Count && tokens[close + 1].Is("{"))
                            AddWhitespaceJoin(source, tokens[close].End, tokens[close + 1].Start, edits);
                    }
                }
                else if (control.Is("else"))
                {
                    if (i > 0 && tokens[i - 1].Is("}"))
                        AddElseLineBreak(source, newline, tokens[i - 1].End, tokens[i].Start, tokens[i].Start, edits);
                    if (i + 1 < tokens.Count && tokens[i + 1].Is("if"))
                        AddWhitespaceJoin(source, tokens[i].End, tokens[i + 1].Start, edits);
                    if (i + 1 < tokens.Count && tokens[i + 1].Is("{"))
                        AddWhitespaceJoin(source, tokens[i].End, tokens[i + 1].Start, edits);
                }
                else if (control.Is("do") && i + 1 < tokens.Count && tokens[i + 1].Is("{"))
                {
                    AddWhitespaceJoin(source, tokens[i].End, tokens[i + 1].Start, edits);
                    int closeBrace = FindMatchingToken(tokens, i + 1, "{", "}");
                    if (closeBrace >= 0 && closeBrace + 1 < tokens.Count && tokens[closeBrace + 1].Is("while"))
                        AddWhitespaceJoin(source, tokens[closeBrace].End, tokens[closeBrace + 1].Start, edits);
                }
            }
        }

        private static void AddWhitespaceJoin(string source, int start, int end, EditCollector edits)
        {
            if (end <= start) return;
            string gap = source.Substring(start, end - start);
            if (gap.All(char.IsWhiteSpace) && gap != " ") edits.Add(start, end - start, " ", "brace-style-space");
        }

        private static void AddElseLineBreak(
            string source,
            string newline,
            int start,
            int end,
            int elsePosition,
            EditCollector edits)
        {
            if (end < start) return;
            string gap = source.Substring(start, end - start);
            if (!gap.All(char.IsWhiteSpace)) return;
            if (!gap.Contains('\n'))
            {
                int closingBrace = Math.Max(0, start - 1);
                int lineStart = FindLineStart(source, closingBrace);
                string beforeBrace = source.Substring(lineStart, closingBrace - lineStart);
                if (beforeBrace.Trim().Length > 0) return;
            }
            string replacement = newline + GetLineIndent(source, elsePosition);
            if (gap != replacement) edits.Add(start, end - start, replacement, "else-line-break");
        }

        private static void CollectMissingElse(
            string source,
            string newline,
            IReadOnlyList<CodeToken> tokens,
            EditCollector edits)
        {
            for (int i = tokens.Count - 1; i >= 0; i--)
            {
                if (!tokens[i].Is("if") || i + 1 >= tokens.Count || !tokens[i + 1].Is("(")) continue;
                int close = FindMatchingToken(tokens, i + 1, "(", ")");
                if (close < 0 || close + 1 >= tokens.Count) continue;
                int bodyEnd = FindStatementEnd(tokens, close + 1);
                if (bodyEnd < 0) continue;
                if (bodyEnd + 1 < tokens.Count && tokens[bodyEnd + 1].Is("else")) continue;
                string indent = GetLineIndent(source, tokens[i].Start);
                int insertion = StatementInsertionPoint(source, tokens[bodyEnd].End);
                string addition = newline + indent + "else {" + newline
                    + indent + "    // else 구문 추가" + newline + indent + "}";
                edits.Add(insertion, 0, addition, "missing-else");
            }
        }

        private static void CollectLegacyMissingElseComments(
            string source,
            IReadOnlyList<LexicalSpan> spans,
            EditCollector edits)
        {
            const string oldMarker = "///< else 구문 추가";
            const string newMarker = "// else 구문 추가";
            foreach (LexicalSpan span in spans)
            {
                if (span.Kind != LexicalSpanKind.LineComment || span.End - span.Start < oldMarker.Length) continue;
                if (source.IndexOf(oldMarker, span.Start, StringComparison.Ordinal) != span.Start) continue;
                int lineEnd = FindLineContentEnd(source, span.Start);
                if (!string.IsNullOrWhiteSpace(source.Substring(span.Start + oldMarker.Length, lineEnd - span.Start - oldMarker.Length)))
                    continue;
                edits.Add(span.Start, oldMarker.Length, newMarker, "missing-else-comment-migration");
            }
        }

        private static void CollectMissingSwitchDefault(
            string source,
            string newline,
            IReadOnlyList<CodeToken> tokens,
            EditCollector edits)
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                if (!tokens[i].Is("switch") || i + 1 >= tokens.Count || !tokens[i + 1].Is("(")) continue;
                int closeParen = FindMatchingToken(tokens, i + 1, "(", ")");
                if (closeParen < 0 || closeParen + 1 >= tokens.Count || !tokens[closeParen + 1].Is("{")) continue;
                int openBrace = closeParen + 1;
                int closeBrace = FindMatchingToken(tokens, openBrace, "{", "}");
                if (closeBrace < 0) continue;

                int depth = 0;
                bool hasDefault = false;
                for (int j = openBrace + 1; j < closeBrace; j++)
                {
                    if (tokens[j].Is("{")) depth++;
                    else if (tokens[j].Is("}")) depth--;
                    else if (depth == 0 && tokens[j].Is("default") && j + 1 < closeBrace && tokens[j + 1].Is(":"))
                    {
                        hasDefault = true;
                        break;
                    }
                }
                if (hasDefault) continue;

                int lineStart = FindLineStart(source, tokens[closeBrace].Start);
                string indent = GetLineIndent(source, tokens[closeBrace].Start);
                string addition = indent + "default:" + newline + indent + "    /* Unexpected state. */" + newline
                    + indent + "    break;" + newline;
                edits.Add(lineStart, 0, addition, "missing-switch-default");
            }
        }

        private static void CollectIntegerLiteralSuffixes(
            IReadOnlyList<CodeToken> tokens,
            EditCollector edits)
        {
            var suffixByName = new Dictionary<string, string>(StringComparer.Ordinal);
            var functionSuffixes = new List<(int BodyStart, int BodyEnd, string Suffix)>();
            var squareBracketDepth = new int[tokens.Count];
            int currentSquareBracketDepth = 0;
            for (int i = 0; i < tokens.Count; i++)
            {
                squareBracketDepth[i] = currentSquareBracketDepth;
                if (tokens[i].Is("[")) currentSquareBracketDepth++;
                else if (tokens[i].Is("]") && currentSquareBracketDepth > 0) currentSquareBracketDepth--;
            }

            for (int i = 0; i < tokens.Count; i++)
            {
                if (!TryReadIntegerTypeSuffix(tokens, i, out int typeEnd, out string suffix)) continue;
                bool pointer = false;
                for (int j = typeEnd + 1; j < tokens.Count && j <= typeEnd + 8; j++)
                {
                    CodeToken token = tokens[j];
                    if (token.Is("*") || token.Is("&"))
                    {
                        pointer = true;
                        continue;
                    }
                    if (token.Is(";") || token.Is("=") || token.Is(")") || token.Is("{")) break;
                    if (token.IsIdentifier && !IsTypeWord(token))
                    {
                        bool functionName = j + 1 < tokens.Count && tokens[j + 1].Is("(");
                        if (!pointer && functionName)
                        {
                            int parametersClose = FindMatchingToken(tokens, j + 1, "(", ")");
                            int bodyOpen = parametersClose + 1;
                            while (parametersClose >= 0 && bodyOpen < tokens.Count &&
                                   !tokens[bodyOpen].Is("{") && !tokens[bodyOpen].Is(";")) bodyOpen++;
                            if (bodyOpen < tokens.Count && tokens[bodyOpen].Is("{"))
                            {
                                int bodyClose = FindMatchingToken(tokens, bodyOpen, "{", "}");
                                if (bodyClose > bodyOpen) functionSuffixes.Add((bodyOpen, bodyClose, suffix));
                            }
                        }
                        else if (!pointer)
                        {
                            suffixByName[token.Text] = suffix;
                        }
                        break;
                    }
                }
                i = typeEnd;
            }

            for (int i = 0; i < tokens.Count; i++)
            {
                if (!TrySplitIntegerLiteral(tokens[i], out int valueLength, out int suffixLength)) continue;
                // Array extents and subscripts are index expressions, not values of the
                // surrounding integer declaration. Keep their literals unchanged.
                if (squareBracketDepth[i] > 0) continue;
                int start = FindStatementTokenStart(tokens, i);
                int end = FindStatementTokenEnd(tokens, i);
                string? desiredSuffix = FindIntegerLiteralSuffix(tokens, start, end, i, suffixByName, functionSuffixes);
                if (desiredSuffix == null) continue;
                string replacement = BuildIntegerLiteralWithSuffix(
                    tokens[i].SliceText(0, valueLength),
                    suffixLength == 0 ? "" : tokens[i].SliceText(valueLength, suffixLength),
                    desiredSuffix);
                if (tokens[i].Is(replacement)) continue;
                edits.Add(tokens[i].Start, tokens[i].End - tokens[i].Start, replacement, "integer-suffix");
            }
        }

        private static bool TrySplitIntegerLiteral(CodeToken token, out int valueLength, out int suffixLength)
        {
            valueLength = 0;
            suffixLength = 0;
            if (token.Kind != CodeTokenKind.Number) return false;
            ReadOnlySpan<char> text = token.Span;
            if (text.Length == 0 || !char.IsDigit(text[0])) return false;

            int index = 0;
            if (text.Length >= 2 && text[0] == '0' && (text[1] == 'x' || text[1] == 'X'))
            {
                index = 2;
                int digitStart = index;
                while (index < text.Length && Uri.IsHexDigit(text[index])) index++;
                if (index == digitStart) return false;
            }
            else if (text.Length >= 2 && text[0] == '0' && (text[1] == 'b' || text[1] == 'B'))
            {
                index = 2;
                int digitStart = index;
                while (index < text.Length && (text[index] == '0' || text[index] == '1')) index++;
                if (index == digitStart) return false;
            }
            else
            {
                while (index < text.Length && char.IsDigit(text[index])) index++;
            }

            valueLength = index;
            while (index < text.Length && (text[index] == 'u' || text[index] == 'U' ||
                   text[index] == 'l' || text[index] == 'L')) index++;
            if (index != text.Length) return false;
            suffixLength = index - valueLength;
            return true;
        }

        private static bool TryReadIntegerTypeSuffix(
            IReadOnlyList<CodeToken> tokens,
            int start,
            out int typeEnd,
            out string suffix)
        {
            typeEnd = start;
            suffix = "";
            CodeToken token = tokens[start];
            if (token.Is("uint8_t") || token.Is("uint16_t") || token.Is("uint32_t") || token.Is("uint64_t") ||
                token.Is("int8_t") || token.Is("int16_t") || token.Is("int32_t") || token.Is("int64_t"))
            {
                bool unsigned = token.Is("uint8_t") || token.Is("uint16_t") ||
                    token.Is("uint32_t") || token.Is("uint64_t");
                bool wide = token.Is("uint64_t") || token.Is("int64_t");
                suffix = unsigned ? (wide ? "ULL" : "U") : (wide ? "LL" : "L");
                return true;
            }
            if (token.Is("size_t"))
            {
                // 현재 C/C++ 대상은 LP64 Linux 프로젝트를 기준으로 size_t를 unsigned long으로 취급한다.
                suffix = "UL";
                return true;
            }

            bool isUnsigned = token.Is("unsigned");
            bool isSigned = token.Is("signed");
            if (isUnsigned || isSigned)
            {
                int next = start + 1;
                if (next < tokens.Count && tokens[next].Is("long"))
                {
                    typeEnd = next;
                    bool longLong = next + 1 < tokens.Count && tokens[next + 1].Is("long");
                    if (longLong) typeEnd++;
                    if (typeEnd + 1 < tokens.Count && tokens[typeEnd + 1].Is("int")) typeEnd++;
                    suffix = isUnsigned ? (longLong ? "ULL" : "UL") : (longLong ? "LL" : "L");
                    return true;
                }
                if (next < tokens.Count && (tokens[next].Is("int") || tokens[next].Is("short") || tokens[next].Is("char")))
                    typeEnd = next;
                suffix = isUnsigned ? "U" : "L";
                return true;
            }

            if (token.Is("long"))
            {
                bool longLong = start + 1 < tokens.Count && tokens[start + 1].Is("long");
                if (longLong) typeEnd++;
                if (typeEnd + 1 < tokens.Count && tokens[typeEnd + 1].Is("int")) typeEnd++;
                suffix = longLong ? "LL" : "L";
                return true;
            }
            if (token.Is("int"))
            {
                suffix = "L";
                return true;
            }
            return false;
        }

        private static string? FindIntegerLiteralSuffix(
            IReadOnlyList<CodeToken> tokens,
            int statementStart,
            int statementEnd,
            int literalIndex,
            IReadOnlyDictionary<string, string> suffixByName,
            IReadOnlyList<(int BodyStart, int BodyEnd, string Suffix)> functionSuffixes)
        {
            string? nearestSuffix = null;
            int nearestDistance = int.MaxValue;
            for (int i = statementStart; i <= statementEnd; i++)
            {
                if (!tokens[i].IsIdentifier || !suffixByName.TryGetValue(tokens[i].Text, out string? candidate)) continue;
                int distance = Math.Abs(i - literalIndex);
                if (distance < nearestDistance)
                {
                    nearestDistance = distance;
                    nearestSuffix = candidate;
                }
            }
            if (nearestSuffix != null) return nearestSuffix;

            bool returnStatement = false;
            for (int i = statementStart; i < literalIndex; i++)
            {
                if (tokens[i].Is("return"))
                {
                    returnStatement = true;
                    break;
                }
            }
            if (returnStatement)
            {
                foreach ((int bodyStart, int bodyEnd, string returnSuffix) in functionSuffixes)
                {
                    if (literalIndex > bodyStart && literalIndex < bodyEnd) return returnSuffix;
                }
            }
            return nearestSuffix;
        }

        private static string BuildIntegerLiteralWithSuffix(string value, string existingSuffix, string desiredSuffix)
        {
            bool unsigned = existingSuffix.IndexOf('u', StringComparison.OrdinalIgnoreCase) >= 0 ||
                            desiredSuffix.IndexOf('U', StringComparison.Ordinal) >= 0;
            int existingLongs = existingSuffix.Count(character => character == 'l' || character == 'L');
            int desiredLongs = desiredSuffix.Count(character => character == 'L');
            int longCount = Math.Max(existingLongs, desiredLongs);
            return value + (unsigned ? "U" : "") + new string('L', longCount);
        }

        private static void CollectSizeOfPointers(IReadOnlyList<CodeToken> tokens, EditCollector edits)
        {
            var pointers = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i + 1 < tokens.Count; i++)
            {
                if (tokens[i].Is("*") && tokens[i + 1].IsIdentifier && LooksLikePointerDeclaration(tokens, i))
                    pointers.Add(tokens[i + 1].Text);
                if (tokens[i].IsIdentifier && i + 3 < tokens.Count && tokens[i + 1].Is("=") &&
                    (tokens[i + 2].Is("malloc") || tokens[i + 2].Is("calloc") || tokens[i + 2].Is("realloc")) &&
                    tokens[i + 3].Is("("))
                    pointers.Add(tokens[i].Text);
            }

            for (int i = 0; i + 3 < tokens.Count; i++)
            {
                if (!tokens[i].Is("sizeof") || !tokens[i + 1].Is("(") || !tokens[i + 2].IsIdentifier || !tokens[i + 3].Is(")")) continue;
                if (pointers.Contains(tokens[i + 2].Text)) edits.Add(tokens[i + 2].Start, 0, "*", "sizeof-pointee");
            }
        }

        private sealed class ComparisonCandidate
        {
            internal ComparisonCandidate(
                int start,
                int end,
                int leftStart,
                int leftEnd,
                int rightStart,
                int rightEnd,
                string replacementOperator)
            {
                Start = start;
                End = end;
                LeftStart = leftStart;
                LeftEnd = leftEnd;
                RightStart = rightStart;
                RightEnd = rightEnd;
                ReplacementOperator = replacementOperator;
            }

            internal int Start { get; }
            internal int End { get; }
            internal int LeftStart { get; }
            internal int LeftEnd { get; }
            internal int RightStart { get; }
            internal int RightEnd { get; }
            internal string ReplacementOperator { get; }
            internal List<ComparisonCandidate> Children { get; } = new List<ComparisonCandidate>();
        }

        private static void CollectConstantsOnLeft(
            string source,
            IReadOnlyList<CodeToken> tokens,
            EditCollector edits)
        {
            var candidates = new List<ComparisonCandidate>();
            for (int i = 1; i + 1 < tokens.Count; i++)
            {
                CodeToken comparisonToken = tokens[i];
                if (!comparisonToken.Is("==") && !comparisonToken.Is("!=") && !comparisonToken.Is("<") &&
                    !comparisonToken.Is("<=") && !comparisonToken.Is(">") && !comparisonToken.Is(">=")) continue;
                if (!OperandRangeFinder.TryFind(tokens, i, out TokenRange left, out TokenRange right)) continue;
                if (OperandRangeFinder.ContainsUnsafeExpression(tokens, left) ||
                    OperandRangeFinder.ContainsUnsafeExpression(tokens, right)) continue;
                CodeToken leftStart = tokens[left.StartIndex];
                CodeToken leftEnd = tokens[left.EndIndex];
                CodeToken rightStart = tokens[right.StartIndex];
                CodeToken rightEnd = tokens[right.EndIndex];
                if (!OnlyWhitespaceBetween(source, leftEnd.End, tokens[i].Start) ||
                    !OnlyWhitespaceBetween(source, tokens[i].End, rightStart.Start)) continue;
                bool leftConstant = IsConstantExpression(tokens, left);
                bool rightConstant = IsConstantExpression(tokens, right);
                if (leftConstant == rightConstant) continue;

                string? replacementOperator = null;
                if ((comparisonToken.Is("==") || comparisonToken.Is("!=")) && rightConstant)
                {
                    replacementOperator = comparisonToken.Is("==") ? "==" : "!=";
                }
                else if (leftConstant)
                {
                    if (comparisonToken.Is("<")) replacementOperator = ">";
                    else if (comparisonToken.Is("<=")) replacementOperator = ">=";
                    else if (comparisonToken.Is(">")) replacementOperator = "<";
                    else if (comparisonToken.Is(">=")) replacementOperator = "<=";
                }
                if (replacementOperator == null) continue;
                if (LooksLikeTemplateArgumentList(tokens, left, i)) continue;

                candidates.Add(new ComparisonCandidate(
                    leftStart.Start,
                    rightEnd.End,
                    leftStart.Start,
                    leftEnd.End,
                    rightStart.Start,
                    rightEnd.End,
                    replacementOperator));
            }

            var roots = new List<ComparisonCandidate>();
            var stack = new Stack<ComparisonCandidate>();
            foreach (ComparisonCandidate candidate in candidates
                .OrderBy(candidate => candidate.Start)
                .ThenByDescending(candidate => candidate.End))
            {
                while (stack.Count > 0 && candidate.Start >= stack.Peek().End) stack.Pop();
                if (stack.Count > 0)
                {
                    ComparisonCandidate parent = stack.Peek();
                    if (candidate.End > parent.End) continue;
                    parent.Children.Add(candidate);
                }
                else
                {
                    roots.Add(candidate);
                }
                stack.Push(candidate);
            }

            foreach (ComparisonCandidate candidate in roots)
            {
                string replacement = BuildComparisonReplacement(source, candidate);
                edits.Add(candidate.Start, candidate.End - candidate.Start, replacement, "constant-comparison");
            }
        }

        private static string BuildComparisonReplacement(
            string source,
            ComparisonCandidate candidate)
        {
            string left = RewriteComparisonRange(source, candidate.LeftStart, candidate.LeftEnd, candidate.Children);
            string right = RewriteComparisonRange(source, candidate.RightStart, candidate.RightEnd, candidate.Children);
            return right + " " + candidate.ReplacementOperator + " " + left;
        }

        private static string RewriteComparisonRange(
            string source,
            int start,
            int end,
            IReadOnlyList<ComparisonCandidate> children)
        {
            ComparisonCandidate[] contained = children
                .Where(candidate => candidate.Start >= start && candidate.End <= end)
                .OrderBy(candidate => candidate.Start)
                .ToArray();
            if (contained.Length == 0) return source.Substring(start, end - start);

            var builder = new StringBuilder(end - start);
            int cursor = start;
            foreach (ComparisonCandidate child in contained)
            {
                if (child.Start < cursor) continue;
                builder.Append(source, cursor, child.Start - cursor);
                builder.Append(BuildComparisonReplacement(source, child));
                cursor = child.End;
            }
            builder.Append(source, cursor, end - cursor);
            return builder.ToString();
        }

        private static bool IsConstantExpression(IReadOnlyList<CodeToken> tokens, TokenRange range)
        {
            int start = range.StartIndex;
            int end = range.EndIndex;
            while (start < end && tokens[start].Is("(") &&
                   FindMatchingToken(tokens, start, "(", ")") == end)
            {
                start++;
                end--;
            }
            if (start == end) return IsConstantToken(tokens[start]);
            return start + 1 == end &&
                   (tokens[start].Is("+") || tokens[start].Is("-") || tokens[start].Is("~")) &&
                   tokens[end].Kind == CodeTokenKind.Number;
        }

        private static bool OnlyWhitespaceBetween(string source, int start, int end)
        {
            for (int i = start; i < end; i++)
                if (!char.IsWhiteSpace(source[i])) return false;
            return true;
        }

        private static bool LooksLikeTemplateArgumentList(
            IReadOnlyList<CodeToken> tokens,
            TokenRange left,
            int comparisonIndex)
        {
            if (!tokens[comparisonIndex].Is(">") || !IsConstantExpression(tokens, left)) return false;
            for (int i = left.StartIndex - 1; i >= 1; i--)
            {
                if (tokens[i].Is(";") || tokens[i].Is("{") || tokens[i].Is("}") || tokens[i].Is("(")) break;
                if (tokens[i].Is("<") && tokens[i - 1].IsIdentifier) return true;
            }
            return false;
        }

        private static bool IsConstantToken(CodeToken token) =>
            token.Kind == CodeTokenKind.Number || token.Kind == CodeTokenKind.StringLiteral ||
            token.Kind == CodeTokenKind.CharacterLiteral || token.Is("NULL") || token.Is("nullptr") ||
            token.Is("true") || token.Is("false");

        private static void CollectVariableInitializers(
            IReadOnlyList<CodeToken> tokens,
            EditCollector edits,
            bool convertInitializerTypes,
            bool addIntegerSuffix = false)
        {
            for (int semicolon = 0; semicolon < tokens.Count; semicolon++)
            {
                if (!tokens[semicolon].Is(";")) continue;
                int start = semicolon - 1;
                while (start > 0 && !tokens[start - 1].Is(";") && !tokens[start - 1].Is("{") && !tokens[start - 1].Is("}")) start--;
                if (start >= semicolon) continue;

                bool rejected = false;
                for (int i = start; i < semicolon; i++)
                {
                    CodeToken token = tokens[i];
                    if (token.Is("=") || token.Is(",") || token.Is("(") || token.Is(")") ||
                        token.Is("typedef") || token.Is("extern"))
                    {
                        rejected = true;
                        break;
                    }
                }
                if (rejected) continue;

                int arrayOpen = -1;
                for (int i = start; i < semicolon; i++)
                {
                    if (tokens[i].Is("[")) { arrayOpen = i; break; }
                }
                int nameIndex = arrayOpen >= 0 ? arrayOpen - 1 : semicolon - 1;
                if (nameIndex <= start || !tokens[nameIndex].IsIdentifier) continue;
                if (!LooksLikeVariableDeclaration(tokens, start, nameIndex)) continue;

                int firstStar = -1;
                for (int i = start; i < nameIndex; i++)
                {
                    if (tokens[i].Is("*")) { firstStar = i; break; }
                }

                string initializer;
                if (arrayOpen >= 0)
                {
                    initializer = " = {0}";
                }
                else if (firstStar >= 0)
                {
                    var castParts = new List<string>();
                    for (int i = start; i < nameIndex; i++)
                    {
                        string part = tokens[i].Text;
                        if (part == "static" || part == "register" || part == "auto") continue;
                        castParts.Add(part);
                    }
                    if (convertInitializerTypes) ConvertFixedWidthTypeParts(castParts);
                    initializer = " = (" + string.Join(" ", castParts) + ")0";
                }
                else
                {
                    string suffix = "";
                    if (addIntegerSuffix)
                    {
                        for (int i = start; i < nameIndex; i++)
                        {
                            if (!TryReadIntegerTypeSuffix(tokens, i, out _, out suffix)) continue;
                            break;
                        }
                    }
                    initializer = " = 0" + suffix;
                }
                edits.Add(tokens[semicolon].Start, 0, initializer, "variable-initialization");
            }
        }

        private static bool LooksLikeVariableDeclaration(IReadOnlyList<CodeToken> tokens, int start, int nameIndex)
        {
            int typeStart = start;
            while (typeStart < nameIndex &&
                   (tokens[typeStart].Is("static") || tokens[typeStart].Is("register") ||
                    tokens[typeStart].Is("auto") || tokens[typeStart].Is("const") ||
                    tokens[typeStart].Is("volatile"))) typeStart++;
            if (typeStart >= nameIndex) return false;

            CodeToken first = tokens[typeStart];
            if (!first.IsIdentifier || first.Is("return") || first.Is("break") || first.Is("continue") ||
                first.Is("goto") || first.Is("case") || first.Is("default") || first.Is("asm")) return false;

            for (int i = typeStart; i < nameIndex; i++)
            {
                if (!tokens[i].IsIdentifier && !tokens[i].Is("*")) return false;
            }
            return true;
        }

        private static void ConvertFixedWidthTypeParts(List<string> parts)
        {
            var converted = new List<string>(parts.Count);
            for (int i = 0; i < parts.Count; i++)
            {
                string text = parts[i];
                if (text == "unsigned" || text == "signed")
                {
                    bool unsigned = text == "unsigned";
                    if (i + 1 < parts.Count && parts[i + 1] == "char")
                    {
                        converted.Add(unsigned ? "uint8_t" : "int8_t");
                        i++;
                    }
                    else if (i + 1 < parts.Count && parts[i + 1] == "short")
                    {
                        converted.Add(unsigned ? "uint16_t" : "int16_t");
                        i++;
                        if (i + 1 < parts.Count && parts[i + 1] == "int") i++;
                    }
                    else if (i + 2 < parts.Count && parts[i + 1] == "long" && parts[i + 2] == "long")
                    {
                        converted.Add(unsigned ? "uint64_t" : "int64_t");
                        i += 2;
                        if (i + 1 < parts.Count && parts[i + 1] == "int") i++;
                    }
                    else if (i + 1 < parts.Count && parts[i + 1] == "long")
                    {
                        converted.Add(unsigned ? "uint32_t" : "int32_t");
                        i++;
                        if (i + 1 < parts.Count && parts[i + 1] == "int") i++;
                    }
                    else
                    {
                        converted.Add(unsigned ? "uint32_t" : "int32_t");
                        if (i + 1 < parts.Count && parts[i + 1] == "int") i++;
                    }
                }
                else if (text == "short")
                {
                    converted.Add("int16_t");
                    if (i + 1 < parts.Count && parts[i + 1] == "int") i++;
                }
                else if (text == "long" && i + 1 < parts.Count && parts[i + 1] == "long")
                {
                    converted.Add("int64_t");
                    i++;
                    if (i + 1 < parts.Count && parts[i + 1] == "int") i++;
                }
                else if (text == "long")
                {
                    converted.Add("int32_t");
                    if (i + 1 < parts.Count && parts[i + 1] == "int") i++;
                }
                else if (text == "int")
                {
                    converted.Add("int32_t");
                }
                else
                {
                    converted.Add(text);
                }
            }
            parts.Clear();
            parts.AddRange(converted);
        }

        private static void CollectNoFollow(IReadOnlyList<CodeToken> tokens, EditCollector edits)
        {
            for (int i = 0; i + 1 < tokens.Count; i++)
            {
                if (!tokens[i].Is("open") || !tokens[i + 1].Is("(")) continue;
                if (i > 0)
                {
                    CodeToken previous = tokens[i - 1];
                    if (previous.IsIdentifier && !previous.Is("return")) continue;
                    if (previous.Is(")") || previous.Is("]")) continue;
                }

                int close = FindMatchingToken(tokens, i + 1, "(", ")");
                if (close < 0) continue;
                var commas = new List<int>();
                int depth = 0;
                for (int j = i + 2; j < close; j++)
                {
                    if (tokens[j].Is("(") || tokens[j].Is("[") || tokens[j].Is("{")) depth++;
                    else if (tokens[j].Is(")") || tokens[j].Is("]") || tokens[j].Is("}")) depth--;
                    else if (tokens[j].Is(",") && depth == 0) commas.Add(j);
                }
                if (commas.Count == 0) continue;

                int secondStart = commas[0] + 1;
                int secondEnd = (commas.Count > 1 ? commas[1] : close) - 1;
                if (secondStart > secondEnd) continue;
                bool alreadyPresent = false;
                for (int j = secondStart; j <= secondEnd; j++)
                {
                    if (tokens[j].Is("O_NOFOLLOW")) { alreadyPresent = true; break; }
                }
                if (!alreadyPresent)
                    edits.Add(tokens[secondEnd].End, 0, " | O_NOFOLLOW", "open-no-follow");
            }
        }

        private static void CollectFixedWidthIntegerTypes(
            string source,
            string newline,
            IReadOnlyList<CodeToken> tokens,
            EditCollector edits)
        {
            bool hasTypeEdit = false;
            for (int i = 0; i < tokens.Count; i++)
            {
                int end = i;
                string? replacement = null;
                CodeToken token = tokens[i];
                if (token.Is("unsigned") || token.Is("signed"))
                {
                    bool unsigned = token.Is("unsigned");
                    if (i + 1 < tokens.Count && tokens[i + 1].Is("char")) { end = i + 1; replacement = unsigned ? "uint8_t" : "int8_t"; }
                    else if (i + 1 < tokens.Count && tokens[i + 1].Is("short"))
                    {
                        end = i + 1;
                        if (end + 1 < tokens.Count && tokens[end + 1].Is("int")) end++;
                        replacement = unsigned ? "uint16_t" : "int16_t";
                    }
                    else if (i + 2 < tokens.Count && tokens[i + 1].Is("long") && tokens[i + 2].Is("long"))
                    {
                        end = i + 2;
                        if (end + 1 < tokens.Count && tokens[end + 1].Is("int")) end++;
                        replacement = unsigned ? "uint64_t" : "int64_t";
                    }
                    else if (i + 1 < tokens.Count && tokens[i + 1].Is("long"))
                    {
                        end = i + 1;
                        if (end + 1 < tokens.Count && tokens[end + 1].Is("int")) end++;
                        replacement = unsigned ? "uint32_t" : "int32_t";
                    }
                    else if (i + 1 < tokens.Count && tokens[i + 1].Is("int")) { end = i + 1; replacement = unsigned ? "uint32_t" : "int32_t"; }
                    else { replacement = unsigned ? "uint32_t" : "int32_t"; }
                }
                else if (token.Is("short"))
                {
                    if (i > 0 && (tokens[i - 1].Is("unsigned") || tokens[i - 1].Is("signed"))) continue;
                    end = i + 1 < tokens.Count && tokens[i + 1].Is("int") ? i + 1 : i;
                    replacement = "int16_t";
                }
                else if (token.Is("long") && i + 1 < tokens.Count && tokens[i + 1].Is("long"))
                {
                    if (i > 0 && (tokens[i - 1].Is("unsigned") || tokens[i - 1].Is("signed"))) continue;
                    end = i + 1;
                    if (end + 1 < tokens.Count && tokens[end + 1].Is("int")) end++;
                    replacement = "int64_t";
                }
                else if (token.Is("long"))
                {
                    if (i > 0 && (tokens[i - 1].Is("unsigned") || tokens[i - 1].Is("signed"))) continue;
                    if (i + 1 < tokens.Count && tokens[i + 1].Is("int")) end = i + 1;
                    replacement = "int32_t";
                }
                else if (token.Is("int"))
                {
                    if (i > 0 && (tokens[i - 1].Is("unsigned") || tokens[i - 1].Is("signed") || tokens[i - 1].Is("short") || tokens[i - 1].Is("long"))) continue;
                    if (i + 2 < tokens.Count && tokens[i + 1].Is("main") && tokens[i + 2].Is("(")) continue;
                    replacement = "int32_t";
                }

                if (replacement == null) continue;
                edits.Add(tokens[i].Start, tokens[end].End - tokens[i].Start, replacement, "fixed-width-type");
                hasTypeEdit = true;
                i = end;
            }

            if (!hasTypeEdit || Regex.IsMatch(source, "(?m)^\\s*#\\s*include\\s*[<\"]stdint\\.h[>\"]")) return;
            int insertion = StdIntIncludeInsertion(source);
            edits.Add(insertion, 0, "#include <stdint.h>" + newline, "fixed-width-include");
        }

        private static void CollectWrapStatementBody(
            string source,
            string newline,
            WrapCandidate candidate,
            EditCollector edits,
            int additionalIndentLevels = 0)
        {
            CodeToken control = candidate.Control;
            CodeToken bodyStart = candidate.BodyStart;
            CodeToken bodyEnd = candidate.BodyEnd;
            int controlLine = FindLineStart(source, control.Start);
            int bodyLine = FindLineStart(source, bodyStart.Start);
            string indent = GetLineIndent(source, control.Start) + new string(' ', additionalIndentLevels * 4);
            if (controlLine != bodyLine)
            {
                int insertion = StatementInsertionPoint(source, bodyEnd.End);
                int controlLineBreak = source.IndexOf('\n', control.Start);
                int headerEnd = controlLineBreak < 0 ? bodyLine : controlLineBreak;
                if (headerEnd > 0 && source[headerEnd - 1] == '\r') headerEnd--;
                while (headerEnd > controlLine && (source[headerEnd - 1] == ' ' || source[headerEnd - 1] == '\t')) headerEnd--;
                string controlText = source.Substring(controlLine, Math.Max(0, headerEnd - controlLine));
                bool hasTrailingComment = controlText.Contains("//", StringComparison.Ordinal) ||
                                          controlText.Contains("/*", StringComparison.Ordinal);
                if (hasTrailingComment)
                {
                    edits.Add(bodyLine, 0, indent + "{" + newline, "compound-statement-open");
                    edits.Add(insertion, 0, newline + indent + "}", "compound-statement-close");
                    return;
                }
                edits.Add(headerEnd, bodyLine - headerEnd, " {" + newline, "compound-statement-open");
                edits.Add(insertion, 0, newline + indent + "}", "compound-statement-close");
                return;
            }
            edits.Add(bodyStart.Start, 0, "{" + newline + indent + "    ", "compound-statement-open");
            edits.Add(bodyEnd.End, 0, newline + indent + "}", "compound-statement-close");
        }

        private static int FindStatementEnd(IReadOnlyList<CodeToken> tokens, int start)
        {
            if (start < 0 || start >= tokens.Count) return -1;
            if (tokens[start].Is("{")) return FindMatchingToken(tokens, start, "{", "}");
            if (tokens[start].Is("if") && start + 1 < tokens.Count && tokens[start + 1].Is("("))
            {
                int close = FindMatchingToken(tokens, start + 1, "(", ")");
                if (close < 0 || close + 1 >= tokens.Count) return -1;
                int thenEnd = FindStatementEnd(tokens, close + 1);
                if (thenEnd >= 0 && thenEnd + 1 < tokens.Count && tokens[thenEnd + 1].Is("else"))
                    return FindStatementEnd(tokens, thenEnd + 2);
                return thenEnd;
            }
            if ((tokens[start].Is("for") || tokens[start].Is("while") || tokens[start].Is("switch")) &&
                start + 1 < tokens.Count && tokens[start + 1].Is("("))
            {
                int close = FindMatchingToken(tokens, start + 1, "(", ")");
                return close >= 0 && close + 1 < tokens.Count ? FindStatementEnd(tokens, close + 1) : -1;
            }
            if (tokens[start].Is("do") && start + 1 < tokens.Count)
            {
                int bodyEnd = FindStatementEnd(tokens, start + 1);
                if (bodyEnd < 0) return -1;
                int next = bodyEnd + 1;
                if (next < tokens.Count && tokens[next].Is("while") && next + 1 < tokens.Count && tokens[next + 1].Is("("))
                {
                    int close = FindMatchingToken(tokens, next + 1, "(", ")");
                    if (close >= 0 && close + 1 < tokens.Count && tokens[close + 1].Is(";")) return close + 1;
                }
                return bodyEnd;
            }

            int parens = 0;
            int brackets = 0;
            for (int i = start; i < tokens.Count; i++)
            {
                if (tokens[i].Is("(")) parens++;
                else if (tokens[i].Is(")")) parens--;
                else if (tokens[i].Is("[")) brackets++;
                else if (tokens[i].Is("]")) brackets--;
                else if (tokens[i].Is("{") && parens == 0 && brackets == 0) return FindMatchingToken(tokens, i, "{", "}");
                else if (tokens[i].Is(";") && parens == 0 && brackets == 0) return i;
            }
            return -1;
        }

        private static int FindMatchingToken(IReadOnlyList<CodeToken> tokens, int open, string opener, string closer)
        {
            int depth = 0;
            for (int i = open; i < tokens.Count; i++)
            {
                if (tokens[i].Is(opener)) depth++;
                else if (tokens[i].Is(closer) && --depth == 0) return i;
            }
            return -1;
        }

        private static int FindLineStart(string source, int position)
        {
            int line = source.LastIndexOf('\n', Math.Max(0, position - 1));
            return line < 0 ? 0 : line + 1;
        }

        private static string GetLineIndent(string source, int position)
        {
            int line = FindLineStart(source, position);
            int end = line;
            while (end < source.Length && (source[end] == ' ' || source[end] == '\t')) end++;
            return source.Substring(line, end - line);
        }

        private static int StatementInsertionPoint(string source, int tokenEnd)
        {
            int lineEnd = source.IndexOf('\n', tokenEnd);
            if (lineEnd < 0) lineEnd = source.Length;
            string suffix = source.Substring(tokenEnd, lineEnd - tokenEnd);
            if (suffix.Contains("//", StringComparison.Ordinal) || suffix.Contains("/*", StringComparison.Ordinal)) return lineEnd;
            return tokenEnd;
        }

        private static int FindStatementTokenStart(IReadOnlyList<CodeToken> tokens, int index)
        {
            int start = index;
            while (start > 0 && !tokens[start - 1].Is(";") && !tokens[start - 1].Is("{") && !tokens[start - 1].Is("}")) start--;
            return start;
        }

        private static int FindStatementTokenEnd(IReadOnlyList<CodeToken> tokens, int index)
        {
            int end = index;
            while (end + 1 < tokens.Count && !tokens[end + 1].Is(";") && !tokens[end + 1].Is("{") && !tokens[end + 1].Is("}")) end++;
            return end;
        }

        private static bool IsUnsignedTypeToken(string text)
            => text == "unsigned" || text == "size_t" || Regex.IsMatch(text, @"^uint(8|16|32|64)_t$");

        private static bool IsTypeWord(string text)
            => text == "const" || text == "volatile" || text == "static" || text == "extern" || text == "register" ||
               text == "signed" || text == "unsigned" || text == "char" || text == "short" || text == "int" ||
               text == "long" || text == "float" || text == "double" || text == "void" || text == "struct" ||
               text == "union" || text == "enum" || text == "size_t" || Regex.IsMatch(text, @"^[u]?int(8|16|32|64)_t$");

        private static bool IsTypeWord(CodeToken token) =>
            token.Is("const") || token.Is("volatile") || token.Is("static") || token.Is("extern") ||
            token.Is("register") || token.Is("signed") || token.Is("unsigned") || token.Is("char") ||
            token.Is("short") || token.Is("int") || token.Is("long") || token.Is("float") ||
            token.Is("double") || token.Is("void") || token.Is("struct") || token.Is("union") ||
            token.Is("enum") || token.Is("size_t") || token.Is("int8_t") || token.Is("int16_t") ||
            token.Is("int32_t") || token.Is("int64_t") || token.Is("uint8_t") || token.Is("uint16_t") ||
            token.Is("uint32_t") || token.Is("uint64_t");

        private static bool LooksLikePointerDeclaration(IReadOnlyList<CodeToken> tokens, int star)
        {
            if (star <= 0) return false;
            for (int i = star - 1, checkedCount = 0; i >= 0 && checkedCount < 5; i--, checkedCount++)
            {
                CodeToken token = tokens[i];
                if (token.Is(";") || token.Is("{") || token.Is("}") || token.Is("=")) return false;
                if (IsTypeWord(token)) return true;
                if (token.IsIdentifier)
                {
                    ReadOnlySpan<char> text = token.Span;
                    if (text.EndsWith("_t".AsSpan(), StringComparison.Ordinal) ||
                        (text.Length > 0 && char.IsUpper(text[0]))) return true;
                }
            }
            return false;
        }

        private static int StdIntIncludeInsertion(string source)
        {
            MatchCollection includes = Regex.Matches(source, @"(?m)^\s*#\s*include[^\r\n]*(?:\r?\n|$)");
            if (includes.Count > 0)
            {
                Match last = includes[includes.Count - 1];
                return last.Index + last.Length;
            }
            Match pragma = Regex.Match(source, @"(?m)^\s*#\s*pragma\s+once[^\r\n]*(?:\r?\n|$)");
            return pragma.Success ? pragma.Index + pragma.Length : 0;
        }

        private static void CollectLogicalParentheses(
            string source,
            IReadOnlyList<CodeToken> tokens,
            EditCollector edits)
        {
            var operandRanges = new HashSet<(int Start, int End)>();
            for (int i = 0; i + 2 < tokens.Count; i++)
            {
                CodeToken keyword = tokens[i];
                if (!keyword.Is("if") && !keyword.Is("while") && !keyword.Is("for")) continue;
                int open = i + 1;
                if (keyword.Is("if") && open < tokens.Count &&
                    (tokens[open].Is("constexpr") || tokens[open].Is("consteval")))
                    open++;
                if (open >= tokens.Count || !tokens[open].Is("(")) continue;
                int close = FindMatchingToken(tokens, open, "(", ")");
                if (close < 0) continue;

                int conditionStart = open + 1;
                int conditionEnd = close - 1;
                if (keyword.Is("for"))
                {
                    if (!TryFindForCondition(tokens, conditionStart, conditionEnd, out conditionStart, out conditionEnd))
                    {
                        i = close;
                        continue;
                    }
                }

                if (conditionStart <= conditionEnd)
                {
                    CollectLogicalOperandRanges(
                        tokens,
                        conditionStart,
                        conditionEnd,
                        operandRanges);
                }
                i = close;
            }

            foreach ((int start, int end) in operandRanges
                .OrderBy(range => tokens[range.Start].Start)
                .ThenByDescending(range => tokens[range.End].End))
            {
                edits.Add(tokens[start].Start, 0, "(", "logical-parentheses-open");
                edits.Add(tokens[end].End, 0, ")", "logical-parentheses-close");
            }
        }

        private static bool TryFindForCondition(
            IReadOnlyList<CodeToken> tokens,
            int start,
            int end,
            out int conditionStart,
            out int conditionEnd)
        {
            conditionStart = start;
            conditionEnd = end;
            var semicolons = new List<int>(2);
            int parentheses = 0;
            int brackets = 0;
            int braces = 0;
            for (int i = start; i <= end; i++)
            {
                CodeToken token = tokens[i];
                if (token.Is("(")) parentheses++;
                else if (token.Is(")")) parentheses--;
                else if (token.Is("[")) brackets++;
                else if (token.Is("]")) brackets--;
                else if (token.Is("{")) braces++;
                else if (token.Is("}")) braces--;
                else if (token.Is(";") && parentheses == 0 && brackets == 0 && braces == 0)
                    semicolons.Add(i);
            }

            if (semicolons.Count != 2) return false;
            conditionStart = semicolons[0] + 1;
            conditionEnd = semicolons[1] - 1;
            return conditionStart <= conditionEnd;
        }

        private static void CollectLogicalOperandRanges(
            IReadOnlyList<CodeToken> tokens,
            int start,
            int end,
            HashSet<(int Start, int End)> ranges)
        {
            if (start > end) return;

            // Parenthesized subexpressions and function-call arguments can contain their own
            // logical expressions. Process those independently before handling this level.
            for (int i = start; i <= end; i++)
            {
                CodeToken token = tokens[i];
                if (!token.Is("(") && !token.Is("[") && !token.Is("{")) continue;
                string openText = token.Is("(") ? "(" : token.Is("[") ? "[" : "{";
                string closeText = token.Is("(") ? ")" : token.Is("[") ? "]" : "}";
                int close = FindMatchingToken(tokens, i, openText, closeText);
                if (close < 0 || close > end) continue;
                if (i + 1 <= close - 1)
                    CollectLogicalOperandRanges(tokens, i + 1, close - 1, ranges);
                i = close;
            }

            if (HasUnsafeTopLevelLogicalContext(tokens, start, end)) return;

            List<int> orOperators = FindTopLevelLogicalOperators(tokens, start, end, "||");
            if (orOperators.Count > 0)
            {
                int pieceStart = start;
                foreach (int pieceEnd in orOperators.Concat(new[] { end + 1 }))
                {
                    int segmentEnd = pieceEnd - 1;
                    if (pieceStart <= segmentEnd)
                    {
                        List<int> andOperators = FindTopLevelLogicalOperators(tokens, pieceStart, segmentEnd, "&&");
                        if (andOperators.Count > 0)
                        {
                            CollectSameOperatorOperands(tokens, pieceStart, segmentEnd, andOperators, ranges);
                            AddLogicalOperandRange(tokens, pieceStart, segmentEnd, ranges);
                        }
                        else
                        {
                            AddLogicalOperandRange(tokens, pieceStart, segmentEnd, ranges);
                        }
                    }
                    pieceStart = pieceEnd + 1;
                }
                return;
            }

            List<int> andOnlyOperators = FindTopLevelLogicalOperators(tokens, start, end, "&&");
            if (andOnlyOperators.Count > 0)
                CollectSameOperatorOperands(tokens, start, end, andOnlyOperators, ranges);
        }

        private static void CollectSameOperatorOperands(
            IReadOnlyList<CodeToken> tokens,
            int start,
            int end,
            IReadOnlyList<int> operators,
            HashSet<(int Start, int End)> ranges)
        {
            int operandStart = start;
            foreach (int operandEnd in operators.Concat(new[] { end + 1 }))
            {
                AddLogicalOperandRange(tokens, operandStart, operandEnd - 1, ranges);
                operandStart = operandEnd + 1;
            }
        }

        private static void AddLogicalOperandRange(
            IReadOnlyList<CodeToken> tokens,
            int start,
            int end,
            HashSet<(int Start, int End)> ranges)
        {
            // A single identifier/literal is already an unambiguous logical operand.
            // Parenthesize only compound operands such as comparisons and mixed groups.
            if (start >= end || IsFullyParenthesized(tokens, start, end)) return;
            ranges.Add((start, end));
        }

        private static bool IsFullyParenthesized(IReadOnlyList<CodeToken> tokens, int start, int end)
            => start < end && tokens[start].Is("(") &&
               FindMatchingToken(tokens, start, "(", ")") == end;

        private static List<int> FindTopLevelLogicalOperators(
            IReadOnlyList<CodeToken> tokens,
            int start,
            int end,
            string logicalOperator)
        {
            var positions = new List<int>();
            int parentheses = 0;
            int brackets = 0;
            int braces = 0;
            for (int i = start; i <= end; i++)
            {
                CodeToken token = tokens[i];
                if (token.Is("(")) parentheses++;
                else if (token.Is(")")) parentheses--;
                else if (token.Is("[")) brackets++;
                else if (token.Is("]")) brackets--;
                else if (token.Is("{")) braces++;
                else if (token.Is("}")) braces--;
                else if (token.Is(logicalOperator) && parentheses == 0 && brackets == 0 && braces == 0 &&
                         (i == start || !tokens[i - 1].Is("operator")))
                    positions.Add(i);
            }
            return positions;
        }

        private static bool HasUnsafeTopLevelLogicalContext(
            IReadOnlyList<CodeToken> tokens,
            int start,
            int end)
        {
            int parentheses = 0;
            int brackets = 0;
            int braces = 0;
            for (int i = start; i <= end; i++)
            {
                CodeToken token = tokens[i];
                if (token.Is("(")) parentheses++;
                else if (token.Is(")")) parentheses--;
                else if (token.Is("[")) brackets++;
                else if (token.Is("]")) brackets--;
                else if (token.Is("{")) braces++;
                else if (token.Is("}")) braces--;
                else if (parentheses == 0 && brackets == 0 && braces == 0 &&
                         (token.Is("?") || token.Is(":") || token.Is(",") || token.Is(";") ||
                          token.Is("=") || token.Is("+=") || token.Is("-=") || token.Is("*=") ||
                          token.Is("/=") || token.Is("%=") || token.Is("&=") || token.Is("|=") ||
                          token.Is("^=") || token.Is("<<=") || token.Is(">>=")))
                    return true;
            }
            return false;
        }

        private static void CollectCommentEdits(
            string source,
            string newline,
            IReadOnlyList<LexicalSpan> spans,
            Options options,
            EditCollector edits)
        {
            int coveredUntil = -1;
            foreach (LexicalSpan span in spans)
            {
                if (span.Start < coveredUntil) continue;
                if (span.Kind == LexicalSpanKind.LineComment)
                {
                    CollectLineCommentEdit(source, newline, span, options, edits, ref coveredUntil);
                }
                else if (span.Kind == LexicalSpanKind.BlockComment)
                {
                    CollectBlockCommentEdit(source, newline, span, options, edits, ref coveredUntil);
                }
            }
        }

        private static void CollectLineCommentEdit(
            string source,
            string newline,
            LexicalSpan span,
            Options options,
            EditCollector edits,
            ref int coveredUntil)
        {
            if (source.IndexOf("// else 구문 추가", span.Start, StringComparison.Ordinal) == span.Start ||
                source.IndexOf("///< else 구문 추가", span.Start, StringComparison.Ordinal) == span.Start) return;
            int lineStart = FindLineStart(source, span.Start);
            int lineEnd = FindLineContentEnd(source, span.Start);
            int commentEnd = Math.Min(span.End, lineEnd);
            string code = source.Substring(lineStart, span.Start - lineStart);
            bool hasCodeBefore = code.Trim().Length > 0;
            int delimiterLength = ExistingLineDelimiterLength(source, span.Start, options);
            delimiterLength = Math.Min(delimiterLength, Math.Max(0, commentEnd - span.Start));
            string existingMarker = source.Substring(span.Start, delimiterLength);
            string body = source.Substring(span.Start + delimiterLength, commentEnd - span.Start - delimiterLength);
            if (options.CommentSpace) body = body.TrimStart();
            if (options.CommentCapitalize) body = CapitalizeFirstAscii(body);
            if (options.CommentPeriod) body = AddPeriod(body);
            bool isParagraphComment = !hasCodeBefore && IsPartOfLineCommentParagraph(source, lineStart, lineEnd);
            string marker = isParagraphComment && options.ParagraphDelimiter
                ? options.ParagraphDelimiterText
                : options.SingleLineDelimiter
                    ? options.SingleLineDelimiterText
                    : existingMarker;
            string rebuilt = marker + (options.CommentSpace ? " " : "") + body;

            if (options.TrailingComment && hasCodeBefore)
            {
                string indent = code.Substring(0, code.Length - code.TrimStart().Length);
                string replacement = indent + rebuilt + newline + code.TrimEnd();
                edits.Add(lineStart, lineEnd - lineStart, replacement, "trailing-line-comment");
                coveredUntil = lineEnd;
            }
            else
            {
                string original = source.Substring(span.Start, commentEnd - span.Start);
                if (!string.Equals(original, rebuilt, StringComparison.Ordinal))
                    edits.Add(span.Start, commentEnd - span.Start, rebuilt, "line-comment");
            }
        }

        private static void CollectBlockCommentEdit(
            string source,
            string newline,
            LexicalSpan span,
            Options options,
            EditCollector edits,
            ref int coveredUntil)
        {
            if (span.End - span.Start < 4) return;
            if (source[span.End - 2] != '*' || source[span.End - 1] != '/') return;
            int openerLength = ExistingBlockDelimiterLength(source, span.Start, options);
            string opener = options.MultiLineDelimiter ? options.MultiLineDelimiterText : source.Substring(span.Start, openerLength);
            bool isMultiLine = source.IndexOf('\n', span.Start, span.End - span.Start) >= 0;
            if (isMultiLine)
            {
                if (options.MultiLineDelimiter &&
                    !source.AsSpan(span.Start, openerLength).SequenceEqual(opener.AsSpan()))
                    edits.Add(span.Start, openerLength, opener, "block-comment-opener");
                return;
            }

            int closeStart = span.End - 2;
            string body = source.Substring(span.Start + openerLength, closeStart - span.Start - openerLength);
            if (options.CommentSpace) body = body.Trim();
            if (options.CommentCapitalize) body = CapitalizeFirstAscii(body);
            if (options.CommentPeriod) body = AddPeriod(body);
            string rebuilt = options.CommentSpace
                ? opener + " " + body.Trim() + " */"
                : opener + body + "*/";

            int lineStart = FindLineStart(source, span.Start);
            int lineEnd = FindLineContentEnd(source, span.Start);
            string code = source.Substring(lineStart, span.Start - lineStart);
            string suffix = source.Substring(span.End, Math.Max(0, lineEnd - span.End));
            if (options.TrailingComment && code.Trim().Length > 0 && suffix.Trim().Length == 0)
            {
                string indent = code.Substring(0, code.Length - code.TrimStart().Length);
                edits.Add(lineStart, lineEnd - lineStart, indent + rebuilt + newline + code.TrimEnd(), "trailing-block-comment");
                coveredUntil = lineEnd;
            }
            else
            {
                string original = source.Substring(span.Start, span.End - span.Start);
                if (!string.Equals(original, rebuilt, StringComparison.Ordinal))
                    edits.Add(span.Start, span.End - span.Start, rebuilt, "block-comment");
            }
        }

        private static int FindLineContentEnd(string source, int position)
        {
            int lineFeed = source.IndexOf('\n', position);
            int end = lineFeed < 0 ? source.Length : lineFeed;
            if (end > position && source[end - 1] == '\r') end--;
            return end;
        }

        private static bool IsPartOfLineCommentParagraph(string source, int lineStart, int lineEnd)
        {
            int previousEnd = lineStart;
            while (previousEnd > 0 && (source[previousEnd - 1] == '\r' || source[previousEnd - 1] == '\n'))
                previousEnd--;
            if (previousEnd > 0)
            {
                int previousStart = FindLineStart(source, previousEnd);
                if (IsStandaloneLineComment(source, previousStart, previousEnd)) return true;
            }

            int nextStart = lineEnd;
            while (nextStart < source.Length && (source[nextStart] == '\r' || source[nextStart] == '\n'))
                nextStart++;
            if (nextStart < source.Length)
            {
                int nextEnd = FindLineContentEnd(source, nextStart);
                if (IsStandaloneLineComment(source, nextStart, nextEnd)) return true;
            }

            return false;
        }

        private static bool IsStandaloneLineComment(string source, int lineStart, int lineEnd)
        {
            int contentStart = lineStart;
            while (contentStart < lineEnd && (source[contentStart] == ' ' || source[contentStart] == '\t'))
                contentStart++;
            return contentStart + 1 < lineEnd && source[contentStart] == '/' && source[contentStart + 1] == '/';
        }

        private static int ExistingLineDelimiterLength(string source, int start, Options options)
        {
            int length = 2;
            if (start + 3 < source.Length &&
                (source.AsSpan(start, 4).SequenceEqual("///<".AsSpan()) ||
                 source.AsSpan(start, 4).SequenceEqual("//!<".AsSpan()))) length = 4;
            if (start + 2 < source.Length &&
                (source.AsSpan(start, 3).SequenceEqual("///".AsSpan()) ||
                 source.AsSpan(start, 3).SequenceEqual("//!".AsSpan()))) length = Math.Max(length, 3);
            if (StartsWithAt(source, start, options.SingleLineDelimiterText))
                length = Math.Max(length, options.SingleLineDelimiterText.Length);
            if (StartsWithAt(source, start, options.ParagraphDelimiterText))
                length = Math.Max(length, options.ParagraphDelimiterText.Length);
            return length;
        }

        private static int ExistingBlockDelimiterLength(string source, int start, Options options)
        {
            int length = start + 2 < source.Length && (source[start + 2] == '*' || source[start + 2] == '!') ? 3 : 2;
            if (StartsWithAt(source, start, options.MultiLineDelimiterText))
                length = Math.Max(length, options.MultiLineDelimiterText.Length);
            return length;
        }

        private static bool StartsWithAt(string source, int start, string value) =>
            !string.IsNullOrEmpty(value) && start >= 0 && start + value.Length <= source.Length &&
            source.AsSpan(start, value.Length).SequenceEqual(value.AsSpan());

        private static string CapitalizeFirstAscii(string text)
        {
            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] >= 'a' && text[i] <= 'z') return text.Substring(0, i) + char.ToUpperInvariant(text[i]) + text.Substring(i + 1);
                if (char.IsLetterOrDigit(text[i])) break;
            }
            return text;
        }

        private static string AddPeriod(string text)
        {
            string trimmed = text.TrimEnd();
            if (trimmed.Length == 0 || ".!?;:)}]".Contains(trimmed[trimmed.Length - 1])) return text;
            return trimmed + "." + text.Substring(trimmed.Length);
        }

    }
}
