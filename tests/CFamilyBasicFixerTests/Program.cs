using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using SparrowCFamilyCommentFix;
using SparrowCFamilyPipeline;
using SparrowCFamilySyntaxFix;

internal static class Program
{
    private static readonly List<string> Failures = new List<string>();

    private static int Main()
    {
        RunCase(
            "existing logical/comment rules",
            "int main() { //hello\r\n    int value = 1; /*block note*/\r\n    if (ready && valid) return 1;\r\n}\r\n",
            new CFamilyFixEngine.Options
            {
                LogicalParentheses = true,
                TrailingComment = true,
                CommentSpace = true,
                CommentPeriod = true,
                CommentCapitalize = true,
            },
            "// Hello.\r\nint main() {\r\n    /* Block note. */\r\n    int value = 1;\r\n    if (ready && valid) return 1;\r\n}\r\n");

        RunCase(
            "comment delimiter rules",
            "// standalone\nint value = 0; // trailing\n// paragraph one\n// paragraph two\n/* one line */\n/*\n * multiple lines\n */\nconst char *text = \"// not a comment\";\n",
            new CFamilyFixEngine.Options
            {
                SingleLineDelimiter = true,
                SingleLineDelimiterText = "///<",
                MultiLineDelimiter = true,
                MultiLineDelimiterText = "/**",
                ParagraphDelimiter = true,
                ParagraphDelimiterText = "//!",
            },
            "///< standalone\nint value = 0; ///< trailing\n//! paragraph one\n//! paragraph two\n/** one line */\n/**\n * multiple lines\n */\nconst char *text = \"// not a comment\";\n");

        RunCase(
            "custom comment delimiter values",
            "int value = 0; // trailing\n// paragraph one\n// paragraph two\n/* block\n * body\n */\n",
            new CFamilyFixEngine.Options
            {
                SingleLineDelimiter = true,
                SingleLineDelimiterText = "//!<",
                MultiLineDelimiter = true,
                MultiLineDelimiterText = "/***",
                ParagraphDelimiter = true,
                ParagraphDelimiterText = "///",
            },
            "int value = 0; //!< trailing\n/// paragraph one\n/// paragraph two\n/*** block\n * body\n */\n");

        RunCase(
            "single standalone line is not a paragraph",
            "// standalone\nint value = 0;\n// paragraph one\n// paragraph two\n",
            new CFamilyFixEngine.Options
            {
                ParagraphDelimiter = true,
                ParagraphDelimiterText = "//!",
            },
            "// standalone\nint value = 0;\n//! paragraph one\n//! paragraph two\n");

        RunCase(
            "compound statements",
            "void work(void)\n{\n    if (ready)\n        run();\n    else\n        stop();\n    for (int i = 0; i < 2; i++) tick();\n    while (busy) wait();\n    do step(); while (active);\n}\n",
            new CFamilyFixEngine.Options { CompoundStatements = true },
            "void work(void)\n{\n    if (ready) {\n        run();\n    }\n    else {\n        stop();\n    }\n    for (int i = 0; i < 2; i++) {\n        tick();\n    }\n    while (busy) {\n        wait();\n    }\n    do {\n        step();\n    } while (active);\n}\n");

        RunCase(
            "mixed logical precedence",
            "void work(void)\n{\n    if (ready && valid || forced) run();\n}\n",
            new CFamilyFixEngine.Options { LogicalParentheses = true },
            "void work(void)\n{\n    if ((ready && valid) || forced) run();\n}\n");

        RunCase(
            "pure logical operators",
            "void work(void)\n{\n    if (ready || forced) run();\n    while (ready && valid && active) tick();\n}\n",
            new CFamilyFixEngine.Options { LogicalParentheses = true },
            "void work(void)\n{\n    if (ready || forced) run();\n    while (ready && valid && active) tick();\n}\n",
            expectInitialChange: false);

        RunCase(
            "nested logical expressions and function calls",
            "void work(void)\n{\n    if (ready && (valid || check(left && right))) run();\n}\n",
            new CFamilyFixEngine.Options { LogicalParentheses = true },
            "void work(void)\n{\n    if (ready && (valid || (check(left && right)))) run();\n}\n");

        RunCase(
            "for loop condition only",
            "void work(int count)\n{\n    for (int i = 0; i < count && ready; i++) tick();\n}\n",
            new CFamilyFixEngine.Options { LogicalParentheses = true },
            "void work(int count)\n{\n    for (int i = 0; (i < count) && ready; i++) tick();\n}\n");

        RunCase(
            "logical operands preserve comments and literals",
            "void work(const char *text)\n{\n    if (strcmp(text, \"a && b\") == 0 /* mode */ || ready) run();\n}\n",
            new CFamilyFixEngine.Options { LogicalParentheses = true },
            "void work(const char *text)\n{\n    if ((strcmp(text, \"a && b\") == 0) /* mode */ || ready) run();\n}\n");

        RunCase(
            "unsafe top-level logical contexts are skipped",
            "void work(void)\n{\n    if (ready && valid ? first : second) run();\n    if (assigned = ready && valid) run();\n}\n",
            new CFamilyFixEngine.Options { LogicalParentheses = true },
            "void work(void)\n{\n    if (ready && valid ? first : second) run();\n    if (assigned = ready && valid) run();\n}\n",
            expectInitialChange: false);

        RunCase(
            "else-if final else",
            "void work(void)\n{\n    if (state == READY)\n    {\n        start();\n    }\n    else if (state == STOPPED)\n    {\n        stop();\n    }\n}\n",
            new CFamilyFixEngine.Options
            {
                MissingElse = true,
                CommentSpace = true,
                CommentPeriod = true,
                CommentCapitalize = true,
            },
            "void work(void)\n{\n    if (state == READY) {\n        start();\n    }\n    else if (state == STOPPED) {\n        stop();\n    }\n    else {\n        // else 구문 추가\n    }\n}\n");

        RunCase(
            "standalone if missing else",
            "void work(void)\n{\n    if (ready)\n    {\n        run();\n    }\n}\n",
            new CFamilyFixEngine.Options { MissingElse = true },
            "void work(void)\n{\n    if (ready) {\n        run();\n    }\n    else {\n        // else 구문 추가\n    }\n}\n");

        RunCase(
            "legacy missing else comment migration",
            "void work(void)\n{\n    if (ready) {\n        run();\n    }\n    else {\n        ///< else 구문 추가\n    }\n}\n",
            new CFamilyFixEngine.Options { MissingElse = true },
            "void work(void)\n{\n    if (ready) {\n        run();\n    }\n    else {\n        // else 구문 추가\n    }\n}\n");

        RunCase(
            "switch default with break",
            "void work(int state)\n{\n    switch (state)\n    {\n    case 1:\n        start();\n        break;\n    }\n}\n",
            new CFamilyFixEngine.Options { SwitchDefault = true },
            "void work(int state)\n{\n    switch (state) {\n    case 1:\n        start();\n        break;\n    default:\n        /* Unexpected state. */\n        break;\n    }\n}\n");

        RunCase(
            "integer suffix by declared type",
            "int returns_int(void)\n{\n    return 12;\n}\nlong long returns_long_long(void)\n{\n    return 13;\n}\nvoid work(uint32_t mask)\n{\n    unsigned int ui = 1;\n    unsigned long ul = 2;\n    unsigned long long ull = 3;\n    int si = 4;\n    long sl = 5;\n    signed long ssl = 6;\n    long long sll = 7;\n    signed long long ssll = 8;\n    uint64_t u64 = 9;\n    int64_t s64 = 10;\n    size_t length = 14;\n    if (mask == 0)\n    {\n        ui = 11L;\n    }\n}\n",
            new CFamilyFixEngine.Options { UnsignedSuffix = true },
            "int returns_int(void)\n{\n    return 12L;\n}\nlong long returns_long_long(void)\n{\n    return 13LL;\n}\nvoid work(uint32_t mask)\n{\n    unsigned int ui = 1U;\n    unsigned long ul = 2UL;\n    unsigned long long ull = 3ULL;\n    int si = 4L;\n    long sl = 5L;\n    signed long ssl = 6L;\n    long long sll = 7LL;\n    signed long long ssll = 8LL;\n    uint64_t u64 = 9ULL;\n    int64_t s64 = 10LL;\n    size_t length = 14UL;\n    if (mask == 0U)\n    {\n        ui = 11UL;\n    }\n}\n");

        RunCase(
            "sizeof pointee",
            "void work(size_t count)\n{\n    char *buffer;\n    buffer = malloc(count * sizeof(buffer));\n}\n",
            new CFamilyFixEngine.Options { SizeOfPointee = true },
            "void work(size_t count)\n{\n    char *buffer;\n    buffer = malloc(count * sizeof(*buffer));\n}\n");

        RunCase(
            "fixed width integer types",
            "unsigned int count;\nint result;\nshort small;\nunsigned long long total;\nint main(void)\n{\n    int local = 0;\n    return local;\n}\n",
            new CFamilyFixEngine.Options { FixedWidthTypes = true },
            "#include <stdint.h>\nuint32_t count;\nint32_t result;\nint16_t small;\nuint64_t total;\nint main(void)\n{\n    int32_t local = 0;\n    return local;\n}\n");

        RunCase(
            "constants on left in comparisons",
            "void work(int status, int count)\n{\n    if (status == 0 || count < 10) stop();\n    if (status != NULL) run();\n    if (10 > count) wait();\n}\n",
            new CFamilyFixEngine.Options { ConstantOnLeft = true },
            "void work(int status, int count)\n{\n    if (0 == status || count < 10) stop();\n    if (NULL != status) run();\n    if (count < 10) wait();\n}\n");

        RunCase(
            "constant comparisons with compound operands",
            "void work(const char *text, int index, int pos, Node *node)\n{\n    if (strcmp(text, \"ok\") == 0 || strcmp(text, \"yes\") != 0) run();\n    if ((index + 1) % 16 == 0) tick();\n    if (values[pos] == 0) clear();\n    if (node->value != 0) keep();\n    if (10 > get_count()) wait();\n    if (100 <= total + offset) retry();\n    if (check(index == 0) == 1) nested();\n}\n",
            new CFamilyFixEngine.Options { ConstantOnLeft = true },
            "void work(const char *text, int index, int pos, Node *node)\n{\n    if (0 == strcmp(text, \"ok\") || 0 != strcmp(text, \"yes\")) run();\n    if (0 == (index + 1) % 16) tick();\n    if (0 == values[pos]) clear();\n    if (0 != node->value) keep();\n    if (get_count() < 10) wait();\n    if (total + offset >= 100) retry();\n    if (1 == check(0 == index)) nested();\n}\n");

        RunCase(
            "unsafe constant comparison expressions are skipped",
            "Array<int, 10> values;\nvoid work(int value, int left, int right)\n{\n    if ((value = read()) == 0) assigned();\n    if ((value ? left : right) == 0) ternary();\n    if ((left, right) == 0) comma();\n    if (value /* keep */ == 0) commented();\n}\n",
            new CFamilyFixEngine.Options { ConstantOnLeft = true },
            "Array<int, 10> values;\nvoid work(int value, int left, int right)\n{\n    if ((value = read()) == 0) assigned();\n    if ((value ? left : right) == 0) ternary();\n    if ((left, right) == 0) comma();\n    if (value /* keep */ == 0) commented();\n}\n",
            expectInitialChange: false);

        RunCase(
            "Crypto AES compound comparison patterns",
            "void work(const char *text, size_t index, size_t length)\n{\n    if (strcmp(text, \"cbc\") == 0) run();\n    if ((index + 1) % 16 == 0) line();\n    if (length % 16 != 0) tail();\n}\n",
            new CFamilyFixEngine.Options
            {
                UnsignedSuffix = true,
                ConstantOnLeft = true,
            },
            "void work(const char *text, size_t index, size_t length)\n{\n    if (0 == strcmp(text, \"cbc\")) run();\n    if (0UL == (index + 1UL) % 16UL) line();\n    if (0UL != length % 16UL) tail();\n}\n");

        RunCase(
            "variable initialization by declaration kind",
            "void work(void)\n{\n    int32_t count;\n    int32_t values[4];\n    int32_t *pointer;\n}\n",
            new CFamilyFixEngine.Options { VariableInitialization = true },
            "void work(void)\n{\n    int32_t count = 0;\n    int32_t values[4] = {0};\n    int32_t *pointer = (int32_t *)0;\n}\n");

        RunCase(
            "open adds no-follow flag",
            "void work(void)\n{\n    int32_t fd = open(\"open.txt\", O_RDWR);\n}\n",
            new CFamilyFixEngine.Options { FileNoFollow = true },
            "void work(void)\n{\n    int32_t fd = open(\"open.txt\", O_RDWR | O_NOFOLLOW);\n}\n");

        RunCase(
            "existing switch default uses same-line brace",
            "void work(int state)\n{\n    switch (state)\n    {\n    default:\n        break;\n    }\n}\n",
            new CFamilyFixEngine.Options { SwitchDefault = true },
            "void work(int state)\n{\n    switch (state) {\n    default:\n        break;\n    }\n}\n");

        RunCase(
            "all C family code rules together",
            "#include <stdlib.h>\nvoid log_flush(void);\nvoid start(void);\nunsigned int count = 1;\nchar *buffer;\nvoid work(int state, int ready)\n{\n    if (ready && count > 0)\n        log_flush();\n    switch (state)\n    {\n    case 1:\n        start();\n        break;\n    }\n    buffer = malloc(count * sizeof(buffer));\n}\n",
            new CFamilyFixEngine.Options
            {
                CompoundStatements = true,
                MissingElse = true,
                SwitchDefault = true,
                LogicalParentheses = true,
                UnsignedSuffix = true,
                SizeOfPointee = true,
                FixedWidthTypes = true,
                ConstantOnLeft = true,
            },
            "#include <stdlib.h>\n#include <stdint.h>\nvoid log_flush(void);\nvoid start(void);\nuint32_t count = 1U;\nchar *buffer;\nvoid work(int32_t state, int32_t ready)\n{\n    if (ready && (count > 0U)) {\n        log_flush();\n    }\n    else {\n        // else 구문 추가\n    }\n    switch (state) {\n    case 1:\n        start();\n        break;\n    default:\n        /* Unexpected state. */\n        break;\n    }\n    buffer = malloc(count * sizeof(*buffer));\n}\n",
            verifyCWithCompiler: true);

        RunCase(
            "compound statements preserve dangling else",
            "void work(void)\n{\n    if (outer) if (inner) run(); else stop();\n}\n",
            new CFamilyFixEngine.Options { CompoundStatements = true },
            "void work(void)\n{\n    if (outer) {\n        if (inner) {\n            run();\n        }\n        else {\n            stop();\n        }\n    }\n}\n");

        RunCase(
            "comments strings and macros are not rewritten",
            "#define CHECK(x) if (x) run()\nconst char *text = \"if (x) run();\";\nvoid work(void)\n{\n    // if (comment) run();\n    if (ready) run();\n}\n",
            new CFamilyFixEngine.Options { CompoundStatements = true },
            "#define CHECK(x) if (x) run()\nconst char *text = \"if (x) run();\";\nvoid work(void)\n{\n    // if (comment) run();\n    if (ready) {\n        run();\n    }\n}\n");

        RunCase(
            "C++ raw strings are protected",
            "const char *text = R\"tag(if (raw && text) run(); // text)tag\";\n//outside\nvoid work(void)\n{\n    if (ready && valid) run();\n}\n",
            new CFamilyFixEngine.Options
            {
                CompoundStatements = true,
                LogicalParentheses = true,
                CommentSpace = true,
                ParagraphDelimiter = true,
            },
            "const char *text = R\"tag(if (raw && text) run(); // text)tag\";\n// outside\nvoid work(void)\n{\n    if (ready && valid) {\n        run();\n    }\n}\n");

        RunCase(
            "C++ source remains valid",
            "void log_flush();\nvoid work(int state, bool ready)\n{\n    if (ready) log_flush();\n    switch (state)\n    {\n    case 1:\n        break;\n    }\n}\n",
            new CFamilyFixEngine.Options
            {
                CompoundStatements = true,
                MissingElse = true,
                SwitchDefault = true,
                FixedWidthTypes = true,
            },
            "#include <stdint.h>\nvoid log_flush();\nvoid work(int32_t state, bool ready)\n{\n    if (ready) {\n        log_flush();\n    }\n    else {\n        // else 구문 추가\n    }\n    switch (state) {\n    case 1:\n        break;\n    default:\n        /* Unexpected state. */\n        break;\n    }\n}\n",
            verifyCppWithCompiler: true);

        RunTokenizationBudgetCase();
        RunNestedCompoundTokenizationCase();
        RunNestedComparisonCase();
        RunAllRulesLoopCase();
        RunClangAstLoopCase();
        RunParallelCacheCase();
        RunUtf16BomCase();
        RunMixedNewLineCase();
        RunCommentDelimiterRoundTripCase();

        if (Failures.Count > 0)
        {
            Console.Error.WriteLine("SparrowCFamily engines FAIL (" + Failures.Count + ")");
            foreach (string failure in Failures) Console.Error.WriteLine(failure);
            return 1;
        }

        Console.WriteLine("SparrowCFamily engines PASS (39 cases)");
        return 0;
    }

    private static void RunTokenizationBudgetCase()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sparrow-c-family-token-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "many-statements.c");
        try
        {
            var source = new StringBuilder("void run(void);\nvoid work(void)\n{\n");
            for (int i = 0; i < 500; i++) source.Append("    if (ready").Append(i).Append(") run();\n");
            source.Append("}\n");
            File.WriteAllText(file, source.ToString(), new UTF8Encoding(false));

            CFamilyPipelineEngine.Result result = CFamilyPipelineEngine.Apply(new[] { file }, new CFamilyPipelineEngine.Options
            {
                Syntax = new CFamilySyntaxFixEngine.Options { CompoundStatements = true },
                EnableIncrementalCache = false,
                MaxDegreeOfParallelism = 1,
            }, CancellationToken.None, _ => { });

            if (result.ChangedFiles != 1 || result.TokenizationPasses > 3)
                Failures.Add("--- tokenization budget ---\nchanged=" + result.ChangedFiles +
                    "\ntokenizationPasses=" + result.TokenizationPasses + " (expected <= 3)");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static void RunNestedCompoundTokenizationCase()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sparrow-c-family-nested-compound-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "nested.c");
        try
        {
            var source = new StringBuilder("void run(void);\nvoid work(void)\n{\n    ");
            for (int i = 0; i < 64; i++) source.Append("if (ready").Append(i).Append(") ");
            source.Append("run();\n}\n");
            File.WriteAllText(file, source.ToString(), new UTF8Encoding(false));

            CFamilyPipelineEngine.Result result = CFamilyPipelineEngine.Apply(new[] { file }, new CFamilyPipelineEngine.Options
            {
                Syntax = new CFamilySyntaxFixEngine.Options { CompoundStatements = true },
                EnableIncrementalCache = false,
                MaxDegreeOfParallelism = 1,
            }, CancellationToken.None, _ => { });
            int secondChanged = CFamilyPipelineEngine.Apply(new[] { file }, new CFamilyPipelineEngine.Options
            {
                Syntax = new CFamilySyntaxFixEngine.Options { CompoundStatements = true },
                EnableIncrementalCache = false,
                MaxDegreeOfParallelism = 1,
            }, CancellationToken.None, _ => { }).ChangedFiles;
            Console.WriteLine("Nested compound: tokenizations=" + result.TokenizationPasses +
                ", edits=" + result.AppliedEdits + ", elapsed=" + result.ElapsedMilliseconds + "ms");

            if (result.ChangedFiles != 1 || result.TokenizationPasses > 2 || secondChanged != 0)
                Failures.Add("--- nested compound tokenization ---\nchanged=" + result.ChangedFiles +
                    "\ntokenizationPasses=" + result.TokenizationPasses + " (expected <= 2)" +
                    "\nsecondChanged=" + secondChanged);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static void RunNestedComparisonCase()
    {
        const string input = "void work(int index)\n{\n    if (check(check(index == 0) == 1) == 2) run();\n}\n";
        const string expected = "void work(int index)\n{\n    if (2 == check(1 == check(0 == index))) run();\n}\n";
        string dir = Path.Combine(Path.GetTempPath(), "sparrow-c-family-nested-comparison-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "nested-comparison.c");
        try
        {
            File.WriteAllText(file, input, new UTF8Encoding(false));
            var options = new CFamilyPipelineEngine.Options
            {
                Syntax = new CFamilySyntaxFixEngine.Options { ConstantOnLeft = true },
                EnableIncrementalCache = false,
                MaxDegreeOfParallelism = 1,
            };
            CFamilyPipelineEngine.Result first = CFamilyPipelineEngine.Apply(
                new[] { file }, options, CancellationToken.None, _ => { });
            string actual = File.ReadAllText(file);
            CFamilyPipelineEngine.Result second = CFamilyPipelineEngine.Apply(
                new[] { file }, options, CancellationToken.None, _ => { });
            Console.WriteLine("Nested comparison: tokenizations=" + first.TokenizationPasses +
                ", edits=" + first.AppliedEdits + ", elapsed=" + first.ElapsedMilliseconds + "ms");
            if (first.ChangedFiles != 1 || first.TokenizationPasses > 1 || second.ChangedFiles != 0 ||
                !string.Equals(actual, expected, StringComparison.Ordinal))
            {
                Failures.Add("--- nested comparison single pass ---\nchanged=" + first.ChangedFiles +
                    "\ntokenizationPasses=" + first.TokenizationPasses + " (expected <= 1)" +
                    "\nsecondChanged=" + second.ChangedFiles + "\nEXPECTED:\n" + expected + "\nACTUAL:\n" + actual);
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static void RunAllRulesLoopCase()
    {
        string? validationDirectory = FindValidationDirectory();
        if (validationDirectory == null)
        {
            Failures.Add("--- all-rules loop ---\nvalidation fixtures not found");
            return;
        }

        string dir = Path.Combine(Path.GetTempPath(), "sparrow-c-family-all-rules-loop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string[] files =
            {
                Path.Combine(dir, "c-all-rules.c"),
                Path.Combine(dir, "cpp-all-rules.cpp"),
            };
            File.Copy(Path.Combine(validationDirectory, "c-all-rules.c"), files[0]);
            File.Copy(Path.Combine(validationDirectory, "cpp-all-rules.cpp"), files[1]);
            var options = CreateAllRulesPipelineOptions();
            CFamilyPipelineEngine.Result first = CFamilyPipelineEngine.Apply(
                files, options, CancellationToken.None, _ => { });
            string[] stable = files.Select(File.ReadAllText).ToArray();
            for (int iteration = 0; iteration < 20; iteration++)
            {
                CFamilyPipelineEngine.Result repeated = CFamilyPipelineEngine.Apply(
                    files, options, CancellationToken.None, _ => { });
                if (repeated.ChangedFiles != 0 || !files.Select(File.ReadAllText).SequenceEqual(stable, StringComparer.Ordinal))
                {
                    Failures.Add("--- all-rules loop ---\niteration=" + (iteration + 1) +
                        "\nchanged=" + repeated.ChangedFiles);
                    break;
                }
            }

            Console.WriteLine("All-rules loop: files=2, iterations=20, tokenizations=" + first.TokenizationPasses +
                ", edits=" + first.AppliedEdits + ", elapsed=" + first.ElapsedMilliseconds + "ms");
            if (first.TokenizationPasses > 8)
                Failures.Add("--- all-rules tokenization budget ---\npasses=" + first.TokenizationPasses + " (expected <= 8 for two files)");
            VerifyWithCompiler("all-rules loop C", files[0], "gcc", "-std=gnu11");
            VerifyWithCompiler("all-rules loop C++", files[1], "g++", "-std=gnu++17");
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static CFamilyPipelineEngine.Options CreateAllRulesPipelineOptions() => new CFamilyPipelineEngine.Options
    {
        Syntax = new CFamilySyntaxFixEngine.Options
        {
            CompoundStatements = true,
            MissingElse = true,
            SwitchDefault = true,
            LogicalParentheses = true,
            UnsignedSuffix = true,
            SizeOfPointee = true,
            FixedWidthTypes = true,
            ConstantOnLeft = true,
            VariableInitialization = true,
            FileNoFollow = true,
        },
        Comment = new CFamilyCommentFixEngine.Options
        {
            TrailingComment = true,
            CommentSpace = true,
            CommentPeriod = true,
            CommentCapitalize = true,
            SingleLineDelimiter = true,
            MultiLineDelimiter = true,
            ParagraphDelimiter = true,
        },
        EnableIncrementalCache = false,
        MaxDegreeOfParallelism = 1,
    };

    private static string? FindValidationDirectory()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory != null)
            {
                string candidate = Path.Combine(directory.FullName, "tests", "CFamilyRuleValidation");
                if (File.Exists(Path.Combine(candidate, "c-all-rules.c")) &&
                    File.Exists(Path.Combine(candidate, "cpp-all-rules.cpp"))) return candidate;
                directory = directory.Parent;
            }
        }
        return null;
    }

    private static void RunClangAstLoopCase()
    {
        string? repositoryRoot = FindRepositoryRoot();
        string analyzer = Environment.GetEnvironmentVariable("SPARROW_TEST_CLANG_ANALYZER") ??
            (repositoryRoot == null ? "" : Path.Combine(
                repositoryRoot, "tools", "_internal", "Sparrow.ClangAnalyzer",
                "bin", "Release", "net8.0", "Sparrow.ClangAnalyzer.exe"));
        string clang = Environment.GetEnvironmentVariable("SPARROW_TEST_CLANG") ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LLVM", "bin", "clang.exe");
        if (!File.Exists(analyzer) || !File.Exists(clang))
        {
            Console.WriteLine("Clang AST loop: SKIP (analyzer or clang not installed)");
            return;
        }

        string dir = Path.Combine(Path.GetTempPath(), "sparrow-clang-ast-loop-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            const string source =
                "// 한글 오프셋 검증\n" +
                "void work(int count, int ready, int forced)\n" +
                "{\n" +
                "    int untouched = count < 10 && ready;\n" +
                "    for (int index = 0; index < count && ready; index++) { count += index; }\n" +
                "    for (int index = count < 10 && ready; index < count && ready; index += count < 10 && ready)\n" +
                "    {\n" +
                "        count = count < 10 && ready;\n" +
                "    }\n" +
                "    for (int index = 0; index < count + ready * forced | forced && ready; index++) { count += index; }\n" +
                "    if (count > 0 && ready || forced) { count++; }\n" +
                "}\n";
            const string expected =
                "// 한글 오프셋 검증\n" +
                "void work(int count, int ready, int forced)\n" +
                "{\n" +
                "    int untouched = count < 10 && ready;\n" +
                "    for (int index = 0; (index < count) && ready; index++) { count += index; }\n" +
                "    for (int index = count < 10 && ready; (index < count) && ready; index += count < 10 && ready)\n" +
                "    {\n" +
                "        count = count < 10 && ready;\n" +
                "    }\n" +
                "    for (int index = 0; ((index < (count + (ready * forced))) | forced) && ready; index++) { count += index; }\n" +
                "    if (((count > 0) && ready) || forced) { count++; }\n" +
                "}\n";
            string cFile = Path.Combine(dir, "ast-loop.c");
            string cppFile = Path.Combine(dir, "ast-loop.cpp");
            File.WriteAllText(cFile, source, new UTF8Encoding(false));
            File.WriteAllText(cppFile, source, new UTF8Encoding(false));
            string database = JsonSerializer.Serialize(new object[]
            {
                new { directory = dir, arguments = new[] { clang, "-x", "c", "-std=gnu11", cFile }, file = cFile },
                new { directory = dir, arguments = new[] { clang, "-x", "c++", "-std=gnu++17", cppFile }, file = cppFile },
            });
            File.WriteAllText(Path.Combine(dir, "compile_commands.json"), database, new UTF8Encoding(false));

            var options = new CFamilyPipelineEngine.Options
            {
                Syntax = new CFamilySyntaxFixEngine.Options { LogicalParentheses = true },
                EnableIncrementalCache = false,
                EnableClangAst = true,
                ClangAnalyzerPath = analyzer,
                ClangExecutablePath = clang,
                ProjectRoot = dir,
                MaxDegreeOfParallelism = 1,
            };
            string[] files = { cFile, cppFile };
            var logs = new List<string>();
            CFamilyPipelineEngine.Result first = CFamilyPipelineEngine.Apply(
                files, options, CancellationToken.None, logs.Add);
            bool stable = true;
            for (int iteration = 0; iteration < 20; iteration++)
            {
                CFamilyPipelineEngine.Result repeated = CFamilyPipelineEngine.Apply(
                    files, options, CancellationToken.None, logs.Add);
                if (repeated.ChangedFiles != 0 || repeated.ClangAnalyzedFiles != 2 || repeated.ClangFallbackFiles != 0)
                {
                    stable = false;
                    Failures.Add("--- clang AST loop ---\niteration=" + (iteration + 1) +
                        "\nchanged=" + repeated.ChangedFiles +
                        "\nanalyzed=" + repeated.ClangAnalyzedFiles +
                        "\nfallback=" + repeated.ClangFallbackFiles);
                    break;
                }
            }

            string fallbackFile = Path.Combine(dir, "ast-fallback.c");
            File.WriteAllText(
                fallbackFile,
                "#include <sparrow_header_that_does_not_exist.h>\n" +
                "void fallback(int ready, int valid, int forced) { if (ready && valid || forced) { } }\n",
                new UTF8Encoding(false));
            CFamilyPipelineEngine.Result fallback = CFamilyPipelineEngine.Apply(
                new[] { fallbackFile }, options, CancellationToken.None, logs.Add);
            string fallbackText = File.ReadAllText(fallbackFile);

            string bomFile = Path.Combine(dir, "ast-bom.c");
            File.WriteAllText(bomFile, source, new UTF8Encoding(true));
            CFamilyPipelineEngine.Result bom = CFamilyPipelineEngine.Apply(
                new[] { bomFile }, options, CancellationToken.None, logs.Add);
            string bomText = File.ReadAllText(bomFile);

            string actualC = File.ReadAllText(cFile);
            string actualCpp = File.ReadAllText(cppFile);
            Console.WriteLine("Clang AST loop: files=2, iterations=20, analyzed=" + first.ClangAnalyzedFiles +
                ", fallback=" + first.ClangFallbackFiles + ", edits=" + first.AppliedEdits);
            if (first.ChangedFiles != 2 || first.ClangAnalyzedFiles != 2 || first.ClangFallbackFiles != 0 ||
                !stable || !string.Equals(actualC, expected, StringComparison.Ordinal) ||
                !string.Equals(actualCpp, expected, StringComparison.Ordinal) ||
                fallback.ClangAnalyzedFiles != 0 || fallback.ClangFallbackFiles != 1 || fallback.ChangedFiles != 1 ||
                !fallbackText.Contains("if ((ready && valid) || forced)", StringComparison.Ordinal) ||
                bom.ClangAnalyzedFiles != 1 || bom.ClangFallbackFiles != 0 || bom.ChangedFiles != 1 ||
                !string.Equals(bomText, expected, StringComparison.Ordinal))
            {
                Failures.Add("--- clang AST initial result ---\nchanged=" + first.ChangedFiles +
                    "\nanalyzed=" + first.ClangAnalyzedFiles + "\nfallback=" + first.ClangFallbackFiles +
                    "\nEXPECTED:\n" + expected + "\nACTUAL C:\n" + actualC + "\nACTUAL C++:\n" + actualCpp +
                    "\nFALLBACK: analyzed=" + fallback.ClangAnalyzedFiles + " fallback=" + fallback.ClangFallbackFiles +
                    " changed=" + fallback.ChangedFiles + "\n" + fallbackText +
                    "\nUTF8 BOM: analyzed=" + bom.ClangAnalyzedFiles + " fallback=" + bom.ClangFallbackFiles +
                    " changed=" + bom.ChangedFiles + "\n" + bomText +
                    "\nLOG:\n" + string.Join("\n", logs.Take(12)));
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static string? FindRepositoryRoot()
    {
        foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var directory = new DirectoryInfo(start);
            while (directory != null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "LICENSE")) &&
                    Directory.Exists(Path.Combine(directory.FullName, "tools", "_internal")))
                    return directory.FullName;
                directory = directory.Parent;
            }
        }
        return null;
    }

    private static void RunParallelCacheCase()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sparrow-c-family-cache-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var files = new List<string>();
            for (int i = 0; i < 8; i++)
            {
                string file = Path.Combine(dir, "file" + i + ".c");
                File.WriteAllText(file, "void run(void);\nvoid work(void)\n{\n    if (ready) run();\n}\n", new UTF8Encoding(false));
                files.Add(file);
            }
            var options = new CFamilyPipelineEngine.Options
            {
                Syntax = new CFamilySyntaxFixEngine.Options { CompoundStatements = true },
                EnableIncrementalCache = true,
                MaxDegreeOfParallelism = 4,
            };

            CFamilyPipelineEngine.Result first = CFamilyPipelineEngine.Apply(files, options, CancellationToken.None, _ => { });
            CFamilyPipelineEngine.Result second = CFamilyPipelineEngine.Apply(files, options, CancellationToken.None, _ => { });
            File.AppendAllText(files[0], "void added(void) { if (ready) run(); }\n", new UTF8Encoding(false));
            CFamilyPipelineEngine.Result third = CFamilyPipelineEngine.Apply(files, options, CancellationToken.None, _ => { });

            if (first.ChangedFiles != 8 || second.ChangedFiles != 0 || second.CacheSkippedFiles != 8 ||
                third.ChangedFiles != 1 || third.CacheSkippedFiles != 7)
            {
                Failures.Add("--- parallel incremental cache ---\nfirstChanged=" + first.ChangedFiles +
                    "\nsecondChanged=" + second.ChangedFiles + "\nsecondSkipped=" + second.CacheSkippedFiles +
                    "\nthirdChanged=" + third.ChangedFiles + "\nthirdSkipped=" + third.CacheSkippedFiles);
            }
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static void RunUtf16BomCase()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sparrow-c-family-utf16-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "utf16.c");
        try
        {
            var encoding = new UnicodeEncoding(bigEndian: false, byteOrderMark: true, throwOnInvalidBytes: true);
            File.WriteAllText(file, "int value = 0; // note\r\n", encoding);
            CFamilyPipelineEngine.Apply(new[] { file }, new CFamilyPipelineEngine.Options
            {
                Comment = new CFamilyCommentFixEngine.Options
                {
                    SingleLineDelimiter = true,
                    SingleLineDelimiterText = "///<",
                },
                EnableIncrementalCache = false,
                MaxDegreeOfParallelism = 1,
            }, CancellationToken.None, _ => { });

            byte[] bytes = File.ReadAllBytes(file);
            bool hasBom = bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE;
            string actual = encoding.GetString(bytes, hasBom ? 2 : 0, bytes.Length - (hasBom ? 2 : 0));
            if (!hasBom || actual != "int value = 0; ///< note\r\n")
                Failures.Add("--- UTF-16 BOM round-trip ---\nhasBom=" + hasBom + "\nACTUAL:\n" + actual);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static void RunMixedNewLineCase()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sparrow-c-family-newlines-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "mixed.c");
        try
        {
            const string input = "// one\r\n// two\n// three\r\n";
            const string expected = "//! one\r\n//! two\n//! three\r\n";
            File.WriteAllText(file, input, new UTF8Encoding(false));
            CFamilyPipelineEngine.Apply(new[] { file }, new CFamilyPipelineEngine.Options
            {
                Comment = new CFamilyCommentFixEngine.Options
                {
                    ParagraphDelimiter = true,
                    ParagraphDelimiterText = "//!",
                },
                EnableIncrementalCache = false,
                MaxDegreeOfParallelism = 1,
            }, CancellationToken.None, _ => { });

            string actual = File.ReadAllText(file);
            if (!string.Equals(expected, actual, StringComparison.Ordinal))
                Failures.Add("--- mixed newlines ---\nEXPECTED:\n" + expected + "\nACTUAL:\n" + actual);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static void RunCommentDelimiterRoundTripCase()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sparrow-c-family-comment-round-trip-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "comments.c");
        const string input = "// standalone\nint value = 0;\n// paragraph one\n// paragraph two\n/* block */\n";
        const string defaultsExpected = "///< standalone\nint value = 0;\n//! paragraph one\n//! paragraph two\n/** block */\n";
        try
        {
            File.WriteAllText(file, input, new UTF8Encoding(false));
            var defaults = new CFamilyFixEngine.Options
            {
                CommentSpace = true,
                SingleLineDelimiter = true,
                SingleLineDelimiterText = "///<",
                MultiLineDelimiter = true,
                MultiLineDelimiterText = "/**",
                ParagraphDelimiter = true,
                ParagraphDelimiterText = "//!",
            };
            var shortened = new CFamilyFixEngine.Options
            {
                CommentSpace = true,
                SingleLineDelimiter = true,
                SingleLineDelimiterText = "///",
                MultiLineDelimiter = true,
                MultiLineDelimiterText = "/*",
                ParagraphDelimiter = true,
                ParagraphDelimiterText = "//",
            };
            var bothSlash = new CFamilyFixEngine.Options
            {
                CommentSpace = true,
                SingleLineDelimiter = true,
                SingleLineDelimiterText = "//",
                MultiLineDelimiter = true,
                MultiLineDelimiterText = "/*",
                ParagraphDelimiter = true,
                ParagraphDelimiterText = "//",
            };

            CFamilyFixEngine.Apply(new[] { file }, defaults, CancellationToken.None, _ => { });
            CFamilyFixEngine.Apply(new[] { file }, shortened, CancellationToken.None, _ => { });
            CFamilyFixEngine.Apply(new[] { file }, defaults, CancellationToken.None, _ => { });
            CFamilyFixEngine.Apply(new[] { file }, bothSlash, CancellationToken.None, _ => { });
            CFamilyFixEngine.Apply(new[] { file }, defaults, CancellationToken.None, _ => { });

            string actual = File.ReadAllText(file);
            if (!string.Equals(defaultsExpected, actual, StringComparison.Ordinal))
                Failures.Add("--- comment delimiter round trip ---\nEXPECTED:\n" + defaultsExpected + "\nACTUAL:\n" + actual);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static void RunCase(
        string name,
        string input,
        CFamilyFixEngine.Options options,
        string expected,
        bool expectInitialChange = true,
        bool verifyCWithCompiler = false,
        bool verifyCppWithCompiler = false)
    {
        string dir = Path.Combine(Path.GetTempPath(), "sparrow-c-family-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, verifyCppWithCompiler ? "sample.cpp" : "sample.c");
        try
        {
            File.WriteAllText(file, input, new UTF8Encoding(false));
            int changed = CFamilyFixEngine.Apply(new[] { file }, options, CancellationToken.None, _ => { });
            string actual = File.ReadAllText(file);
            if (changed != (expectInitialChange ? 1 : 0) || !string.Equals(actual, expected, StringComparison.Ordinal))
            {
                Failures.Add("--- " + name + " ---\nchanged=" + changed + "\nEXPECTED:\n" + expected + "\nACTUAL:\n" + actual);
                return;
            }

            if (verifyCWithCompiler) VerifyWithCompiler(name, file, "gcc", "-std=gnu11");
            if (verifyCppWithCompiler) VerifyWithCompiler(name, file, "g++", "-std=gnu++17");

            int secondChanged = CFamilyFixEngine.Apply(new[] { file }, options, CancellationToken.None, _ => { });
            string secondActual = File.ReadAllText(file);
            if (secondChanged != 0 || !string.Equals(secondActual, expected, StringComparison.Ordinal))
                Failures.Add("--- " + name + " (idempotency) ---\nchanged=" + secondChanged + "\nACTUAL:\n" + secondActual);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    private static void VerifyWithCompiler(string name, string file, string compiler, string standard)
    {
        try
        {
            var start = new ProcessStartInfo(compiler)
            {
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(standard);
            start.ArgumentList.Add("-fsyntax-only");
            start.ArgumentList.Add(file);
            using Process? process = Process.Start(start);
            if (process == null) return;
            string stderr = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode != 0) Failures.Add("--- " + name + " (C syntax) ---\n" + stderr);
        }
        catch
        {
            // A local C compiler is optional; exact-output tests still run everywhere.
        }
    }
}

/// <summary>두 공개 엔진을 순서대로 검증하기 위한 테스트 전용 어댑터입니다.</summary>
internal static class CFamilyFixEngine
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
    }

    internal static int Apply(IEnumerable<string> files, Options options, CancellationToken token, Action<string> log)
    {
        string[] paths = new List<string>(files).ToArray();
        var syntaxOptions = new CFamilySyntaxFixEngine.Options
        {
            CompoundStatements = options.CompoundStatements,
            MissingElse = options.MissingElse,
            SwitchDefault = options.SwitchDefault,
            LogicalParentheses = options.LogicalParentheses,
            UnsignedSuffix = options.UnsignedSuffix,
            SizeOfPointee = options.SizeOfPointee,
            FixedWidthTypes = options.FixedWidthTypes,
            ConstantOnLeft = options.ConstantOnLeft,
            VariableInitialization = options.VariableInitialization,
            FileNoFollow = options.FileNoFollow,
        };
        var commentOptions = new CFamilyCommentFixEngine.Options
        {
            TrailingComment = options.TrailingComment,
            CommentSpace = options.CommentSpace,
            CommentPeriod = options.CommentPeriod,
            CommentCapitalize = options.CommentCapitalize,
            SingleLineDelimiter = options.SingleLineDelimiter,
            SingleLineDelimiterText = options.SingleLineDelimiterText,
            MultiLineDelimiter = options.MultiLineDelimiter,
            MultiLineDelimiterText = options.MultiLineDelimiterText,
            ParagraphDelimiter = options.ParagraphDelimiter,
            ParagraphDelimiterText = options.ParagraphDelimiterText,
        };
        CFamilyPipelineEngine.Result result = CFamilyPipelineEngine.Apply(paths, new CFamilyPipelineEngine.Options
        {
            Syntax = syntaxOptions,
            Comment = commentOptions,
            EnableIncrementalCache = false,
            MaxDegreeOfParallelism = 1,
        }, token, log);
        return result.ChangedFiles;
    }
}
