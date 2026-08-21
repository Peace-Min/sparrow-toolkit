using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using SparrowClang.Protocol;

namespace Sparrow.ClangAnalyzer
{
    internal static class AstAnalyzer
    {
        private static readonly HashSet<string> LogicalOperators =
            new HashSet<string>(new[] { "&&", "||" }, StringComparer.Ordinal);
        private static readonly HashSet<string> TransparentExpressionKinds =
            new HashSet<string>(new[]
            {
                "ImplicitCastExpr", "ExprWithCleanups", "CXXBindTemporaryExpr",
                "MaterializeTemporaryExpr", "FullExpr", "ConstantExpr",
            }, StringComparer.Ordinal);

        internal static async Task<ClangFileAnalysis> AnalyzeAsync(
            string clangPath,
            string clangVersion,
            ClangAnalysisRequest request,
            ClangFileRequest fileRequest,
            CancellationToken token)
        {
            string fullPath;
            try { fullPath = Path.GetFullPath(fileRequest.Path); }
            catch (Exception error)
            {
                return Failed(fileRequest.Path, "잘못된 소스 파일 경로: " + error.Message);
            }
            if (!File.Exists(fullPath)) return Failed(fullPath, "소스 파일이 존재하지 않습니다.");
            if (!Utf8Source.TryLoad(fullPath, out Utf8Source? source, out string? encodingError))
                return Failed(fullPath, encodingError ?? "Clang AST가 지원하지 않는 인코딩입니다.");

            ResolvedCompilationCommand command = CompilationCommandResolver.Resolve(fullPath, request.ProjectRoot);
            var startInfo = new ProcessStartInfo(clangPath)
            {
                WorkingDirectory = command.WorkingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            foreach (string argument in command.Arguments) startInfo.ArgumentList.Add(argument);
            foreach (string argument in request.AdditionalArguments) startInfo.ArgumentList.Add(argument);
            foreach (string argument in fileRequest.AdditionalArguments) startInfo.ArgumentList.Add(argument);
            startInfo.ArgumentList.Add("-fsyntax-only");
            startInfo.ArgumentList.Add("-Wno-everything");
            startInfo.ArgumentList.Add("-Xclang");
            startInfo.ArgumentList.Add("-ast-dump=json");
            startInfo.ArgumentList.Add(fullPath);

            var analysis = new ClangFileAnalysis
            {
                Path = fullPath,
                UsedCompilationDatabase = command.UsedDatabase,
                CompilationDatabasePath = command.DatabasePath,
                WorkingDirectory = command.WorkingDirectory,
                Fingerprint = CreateFingerprint(clangVersion, command, request, fileRequest),
            };

            try
            {
                using Process process = Process.Start(startInfo) ??
                    throw new InvalidOperationException("clang AST 프로세스를 시작하지 못했습니다.");
                Task<string> outputTask = process.StandardOutput.ReadToEndAsync();
                Task<string> errorTask = process.StandardError.ReadToEndAsync();
                try
                {
                    await process.WaitForExitAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                    throw;
                }
                string output = await outputTask.ConfigureAwait(false);
                string diagnosticText = await errorTask.ConfigureAwait(false);
                AddDiagnostics(analysis.Diagnostics, diagnosticText);
                if (process.ExitCode != 0)
                {
                    analysis.Diagnostics.Insert(0, "clang AST 파싱 실패(exit " + process.ExitCode + ")");
                    return analysis;
                }
                if (string.IsNullOrWhiteSpace(output))
                {
                    analysis.Diagnostics.Add("clang AST 출력이 비어 있습니다.");
                    return analysis;
                }

                using JsonDocument ast = JsonDocument.Parse(output, new JsonDocumentOptions
                {
                    MaxDepth = 512,
                    AllowTrailingCommas = false,
                });
                var walker = new AstWalker(fullPath, command.WorkingDirectory, source!);
                walker.Walk(ast.RootElement);
                analysis.FunctionCount = walker.FunctionCount;
                analysis.VariableCount = walker.VariableCount;
                analysis.CallCount = walker.CallCount;
                analysis.LogicalExpressionCount = walker.LogicalExpressionCount;
                analysis.LogicalEdits = walker.Edits
                    .OrderBy(edit => edit.Start)
                    .ThenBy(edit => edit.Replacement == "(" ? 0 : 1)
                    .ToList();
                analysis.Diagnostics.AddRange(walker.Diagnostics);
                analysis.Success = true;
                return analysis;
            }
            catch (OperationCanceledException)
            {
                analysis.Diagnostics.Add("clang AST 분석 시간이 초과되었거나 취소되었습니다.");
                return analysis;
            }
            catch (JsonException error)
            {
                analysis.Diagnostics.Add("clang AST JSON 해석 실패: " + error.Message);
                return analysis;
            }
            catch (IOException error)
            {
                analysis.Diagnostics.Add("clang AST 실행 실패: " + error.Message);
                return analysis;
            }
            catch (InvalidOperationException error)
            {
                analysis.Diagnostics.Add("clang AST 실행 실패: " + error.Message);
                return analysis;
            }
        }

        private static ClangFileAnalysis Failed(string path, string message) => new ClangFileAnalysis
        {
            Path = path,
            Diagnostics = new List<string> { message },
        };

        private static void AddDiagnostics(List<string> target, string diagnostics)
        {
            foreach (string line in diagnostics.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Take(50))
                target.Add(line.Trim());
        }

        private static string CreateFingerprint(
            string clangVersion,
            ResolvedCompilationCommand command,
            ClangAnalysisRequest request,
            ClangFileRequest file)
        {
            string content = "sparrow-clang-ast-v1\n" + clangVersion + "\n" + command.WorkingDirectory + "\n" +
                string.Join("\n", command.Arguments) + "\n" + string.Join("\n", request.AdditionalArguments) + "\n" +
                string.Join("\n", file.AdditionalArguments);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        }

        private sealed class AstWalker
        {
            private readonly string _mainFile;
            private readonly string _workingDirectory;
            private readonly Utf8Source _source;
            private readonly HashSet<(int Start, int End)> _parenthesizedRanges = new HashSet<(int, int)>();

            internal AstWalker(string mainFile, string workingDirectory, Utf8Source source)
            {
                _mainFile = Path.GetFullPath(mainFile);
                _workingDirectory = workingDirectory;
                _source = source;
            }

            internal int FunctionCount { get; private set; }
            internal int VariableCount { get; private set; }
            internal int CallCount { get; private set; }
            internal int LogicalExpressionCount { get; private set; }
            internal List<ClangTextEdit> Edits { get; } = new List<ClangTextEdit>();
            internal List<string> Diagnostics { get; } = new List<string>();

            internal void Walk(JsonElement root) => Visit(
                root,
                inheritedMainFile: false,
                insideControlCondition: false,
                insideBinaryTree: false);

            private void Visit(
                JsonElement node,
                bool inheritedMainFile,
                bool insideControlCondition,
                bool insideBinaryTree)
            {
                if (node.ValueKind != JsonValueKind.Object) return;
                bool isMainFile = ResolveMainFile(node, inheritedMainFile);
                string kind = GetString(node, "kind");
                if (isMainFile)
                {
                    if (kind is "FunctionDecl" or "CXXMethodDecl" or "CXXConstructorDecl" or "CXXDestructorDecl") FunctionCount++;
                    else if (kind is "VarDecl" or "ParmVarDecl" or "FieldDecl") VariableCount++;
                    else if (kind is "CallExpr" or "CXXMemberCallExpr" or "CXXOperatorCallExpr") CallCount++;
                }

                bool binary = insideControlCondition && isMainFile && kind == "BinaryOperator";
                if (binary)
                {
                    if (LogicalOperators.Contains(GetString(node, "opcode"))) LogicalExpressionCount++;
                    if (!insideBinaryTree) CollectPrecedenceEdits(node);
                }

                if (!node.TryGetProperty("inner", out JsonElement inner) || inner.ValueKind != JsonValueKind.Array) return;
                int childIndex = 0;
                foreach (JsonElement child in inner.EnumerateArray())
                {
                    bool childInsideControlCondition = IsControlStatement(kind)
                        ? IsControlConditionChild(node, kind, childIndex)
                        : insideControlCondition;
                    Visit(child, isMainFile, childInsideControlCondition, insideBinaryTree || binary);
                    childIndex++;
                }
            }

            private static bool IsControlStatement(string kind) =>
                kind is "IfStmt" or "WhileStmt" or "DoStmt" or "ForStmt";

            private static bool IsControlConditionChild(JsonElement node, string kind, int childIndex)
            {
                if (kind == "ForStmt")
                {
                    // Clang keeps fixed raw slots for init, condition variable,
                    // condition expression, increment and body. Missing clauses
                    // remain empty objects, so the condition is always slot 2.
                    return childIndex == 2;
                }
                if (kind == "DoStmt") return childIndex == 1;
                if (kind == "WhileStmt")
                    return childIndex == (GetBoolean(node, "hasVar") ? 1 : 0);

                if (GetBoolean(node, "isConsteval")) return false;

                int conditionIndex = (GetBoolean(node, "hasInit") ? 1 : 0) +
                    (GetBoolean(node, "hasVar") ? 1 : 0);
                return childIndex == conditionIndex;
            }

            private static bool GetBoolean(JsonElement node, string name) =>
                node.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

            private void CollectPrecedenceEdits(JsonElement node)
            {
                JsonElement core = Unwrap(node, out _);
                if (GetString(core, "kind") != "BinaryOperator")
                {
                    foreach (JsonElement child in Children(core)) CollectPrecedenceEdits(child);
                    return;
                }

                string parentOperation = GetString(core, "opcode");
                int parentPrecedence = GetBinaryPrecedence(parentOperation);

                foreach (JsonElement child in Children(core))
                {
                    JsonElement childCore = Unwrap(child, out bool explicitlyParenthesized);
                    string childKind = GetString(childCore, "kind");
                    string childOperation = GetString(childCore, "opcode");
                    if (!explicitlyParenthesized && childKind == "BinaryOperator")
                    {
                        int childPrecedence = GetBinaryPrecedence(childOperation);
                        if (parentPrecedence >= 0 && childPrecedence >= 0 && parentPrecedence != childPrecedence)
                            AddParentheses(childCore, "clang-operator-precedence");
                    }
                    CollectPrecedenceEdits(childCore);
                }
            }

            private static int GetBinaryPrecedence(string operation) => operation switch
            {
                "*" or "/" or "%" => 12,
                "+" or "-" => 11,
                "<<" or ">>" => 10,
                "<" or "<=" or ">" or ">=" => 9,
                "==" or "!=" => 8,
                "&" => 7,
                "^" => 6,
                "|" => 5,
                "&&" => 4,
                "||" => 3,
                "=" or "*=" or "/=" or "%=" or "+=" or "-=" or "<<=" or ">>=" or "&=" or "^=" or "|=" => 2,
                "," => 1,
                _ => -1,
            };

            private void AddParentheses(JsonElement node, string rule)
            {
                if (!TryGetByteRange(node, out int byteStart, out int byteEnd) || byteStart >= byteEnd) return;
                if (!_source.TryMapRange(byteStart, byteEnd, out int start, out int end))
                {
                    Diagnostics.Add("AST 소스 범위를 UTF-16 위치로 변환하지 못해 편집을 건너뜁니다: " + byteStart + ".." + byteEnd);
                    return;
                }
                if (!_parenthesizedRanges.Add((start, end))) return;
                Edits.Add(new ClangTextEdit { Start = start, Length = 0, Replacement = "(", Rule = rule + "-open" });
                Edits.Add(new ClangTextEdit { Start = end, Length = 0, Replacement = ")", Rule = rule + "-close" });
            }

            private bool ResolveMainFile(JsonElement node, bool inherited)
            {
                if (TryGetLocationFile(node, out string? file, out bool includedFrom))
                {
                    if (includedFrom) return false;
                    if (!string.IsNullOrWhiteSpace(file))
                    {
                        try
                        {
                            string full = Path.GetFullPath(Path.IsPathRooted(file) ? file : Path.Combine(_workingDirectory, file));
                            return string.Equals(full, _mainFile, StringComparison.OrdinalIgnoreCase);
                        }
                        catch (ArgumentException) { return false; }
                    }
                }
                return inherited;
            }

            private static bool TryGetLocationFile(JsonElement node, out string? file, out bool includedFrom)
            {
                file = null;
                includedFrom = false;
                if (node.TryGetProperty("loc", out JsonElement loc) && loc.ValueKind == JsonValueKind.Object)
                {
                    if (loc.TryGetProperty("includedFrom", out _)) includedFrom = true;
                    if (loc.TryGetProperty("file", out JsonElement value)) file = value.GetString();
                }
                if (file == null && node.TryGetProperty("range", out JsonElement range) &&
                    range.TryGetProperty("begin", out JsonElement begin) && begin.ValueKind == JsonValueKind.Object)
                {
                    if (begin.TryGetProperty("includedFrom", out _)) includedFrom = true;
                    if (begin.TryGetProperty("file", out JsonElement value)) file = value.GetString();
                }
                return file != null || includedFrom;
            }

            private static bool TryGetByteRange(JsonElement node, out int start, out int end)
            {
                start = 0;
                end = 0;
                if (!node.TryGetProperty("range", out JsonElement range) ||
                    !range.TryGetProperty("begin", out JsonElement begin) ||
                    !range.TryGetProperty("end", out JsonElement finish)) return false;
                if (begin.TryGetProperty("spellingLoc", out _) || begin.TryGetProperty("expansionLoc", out _) ||
                    finish.TryGetProperty("spellingLoc", out _) || finish.TryGetProperty("expansionLoc", out _)) return false;
                if (!begin.TryGetProperty("offset", out JsonElement startValue) ||
                    !finish.TryGetProperty("offset", out JsonElement endValue)) return false;
                int tokenLength = finish.TryGetProperty("tokLen", out JsonElement lengthValue) ? lengthValue.GetInt32() : 0;
                start = startValue.GetInt32();
                end = endValue.GetInt32() + Math.Max(0, tokenLength);
                return true;
            }

            private static JsonElement Unwrap(JsonElement node, out bool explicitlyParenthesized)
            {
                explicitlyParenthesized = false;
                JsonElement current = node;
                while (current.ValueKind == JsonValueKind.Object)
                {
                    string kind = GetString(current, "kind");
                    if (kind == "ParenExpr") explicitlyParenthesized = true;
                    else if (!TransparentExpressionKinds.Contains(kind)) break;
                    JsonElement first = Children(current).FirstOrDefault();
                    if (first.ValueKind == JsonValueKind.Undefined) break;
                    current = first;
                }
                return current;
            }

            private static IEnumerable<JsonElement> Children(JsonElement node)
            {
                if (!node.TryGetProperty("inner", out JsonElement inner) || inner.ValueKind != JsonValueKind.Array)
                    yield break;
                foreach (JsonElement child in inner.EnumerateArray())
                    if (child.ValueKind == JsonValueKind.Object && child.TryGetProperty("kind", out _)) yield return child;
            }

            private static string GetString(JsonElement node, string name) =>
                node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out JsonElement value)
                    ? value.GetString() ?? ""
                    : "";
        }

        private sealed class Utf8Source
        {
            private readonly byte[] _bytes;
            private readonly string _text;
            private readonly int _clangByteOffset;

            private Utf8Source(byte[] bytes, string text, int clangByteOffset)
            {
                _bytes = bytes;
                _text = text;
                _clangByteOffset = clangByteOffset;
            }

            internal static bool TryLoad(string path, out Utf8Source? source, out string? error)
            {
                source = null;
                error = null;
                try
                {
                    byte[] raw = File.ReadAllBytes(path);
                    int offset = raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF ? 3 : 0;
                    byte[] content = offset == 0 ? raw : raw.Skip(offset).ToArray();
                    var utf8 = new UTF8Encoding(false, true);
                    string text = utf8.GetString(content);
                    // Clang source offsets are measured from the beginning of the
                    // physical file and therefore include an UTF-8 BOM. The core
                    // transformation works on decoded text without the BOM.
                    source = new Utf8Source(content, text, offset);
                    return true;
                }
                catch (DecoderFallbackException)
                {
                    error = "현재 Clang AST 연동은 UTF-8/ASCII 소스만 지원합니다. 토큰 엔진으로 폴백합니다.";
                    return false;
                }
                catch (IOException exception)
                {
                    error = "소스 파일 읽기 실패: " + exception.Message;
                    return false;
                }
            }

            internal bool TryMapRange(int byteStart, int byteEnd, out int start, out int end)
            {
                start = 0;
                end = 0;
                byteStart -= _clangByteOffset;
                byteEnd -= _clangByteOffset;
                if (byteStart < 0 || byteEnd < byteStart || byteEnd > _bytes.Length) return false;
                try
                {
                    var utf8 = new UTF8Encoding(false, true);
                    start = utf8.GetCharCount(_bytes, 0, byteStart);
                    end = utf8.GetCharCount(_bytes, 0, byteEnd);
                    return start <= end && end <= _text.Length;
                }
                catch (DecoderFallbackException) { return false; }
            }
        }
    }
}
