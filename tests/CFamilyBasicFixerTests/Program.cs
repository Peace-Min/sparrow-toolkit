using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using SparrowRunner.Gui;

internal static class Program
{
    private static readonly List<string> Failures = new List<string>();

    private static int Main()
    {
        RunCase(
            "existing logical/comment rules",
            "int main() { //hello\r\n    int value = 1; /*block note*/\r\n    if (ready && valid) return 1;\r\n}\r\n",
            new CFamilyBasicFixer.Options
            {
                LogicalParentheses = true,
                TrailingComment = true,
                CommentSpace = true,
                CommentPeriod = true,
                CommentCapitalize = true,
            },
            "// Hello.\r\nint main() {\r\n    /* Block note. */\r\n    int value = 1;\r\n    if ((ready && valid)) return 1;\r\n}\r\n");

        RunCase(
            "compound statements",
            "void work(void)\n{\n    if (ready)\n        run();\n    else\n        stop();\n    for (int i = 0; i < 2; i++) tick();\n    while (busy) wait();\n    do step(); while (active);\n}\n",
            new CFamilyBasicFixer.Options { CompoundStatements = true },
            "void work(void)\n{\n    if (ready)\n    {\n        run();\n    }\n    else\n    {\n        stop();\n    }\n    for (int i = 0; i < 2; i++) { tick(); }\n    while (busy) { wait(); }\n    do { step(); } while (active);\n}\n");

        RunCase(
            "mixed logical precedence",
            "void work(void)\n{\n    if (ready && valid || forced) run();\n}\n",
            new CFamilyBasicFixer.Options { LogicalParentheses = true },
            "void work(void)\n{\n    if ((ready && valid) || forced) run();\n}\n");

        RunCase(
            "else-if final else",
            "void work(void)\n{\n    if (state == READY)\n    {\n        start();\n    }\n    else if (state == STOPPED)\n    {\n        stop();\n    }\n}\n",
            new CFamilyBasicFixer.Options { FinalElse = true },
            "void work(void)\n{\n    if (state == READY)\n    {\n        start();\n    }\n    else if (state == STOPPED)\n    {\n        stop();\n    }\n    else\n    {\n        asm(\"nop\");\n    }\n}\n");

        RunCase(
            "standalone if missing else",
            "void work(void)\n{\n    if (ready)\n    {\n        run();\n    }\n}\n",
            new CFamilyBasicFixer.Options { MissingElse = true },
            "void work(void)\n{\n    if (ready)\n    {\n        run();\n    }\n    else\n    {\n        asm(\"nop\");\n    }\n}\n");

        RunCase(
            "final-else rule ignores standalone if",
            "void work(void)\n{\n    if (ready)\n    {\n        run();\n    }\n}\n",
            new CFamilyBasicFixer.Options { FinalElse = true },
            "void work(void)\n{\n    if (ready)\n    {\n        run();\n    }\n}\n",
            expectInitialChange: false);

        RunCase(
            "missing-else rule ignores else-if chain",
            "void work(void)\n{\n    if (state == READY)\n    {\n        start();\n    }\n    else if (state == STOPPED)\n    {\n        stop();\n    }\n}\n",
            new CFamilyBasicFixer.Options { MissingElse = true },
            "void work(void)\n{\n    if (state == READY)\n    {\n        start();\n    }\n    else if (state == STOPPED)\n    {\n        stop();\n    }\n}\n",
            expectInitialChange: false);

        RunCase(
            "switch default with break",
            "void work(int state)\n{\n    switch (state)\n    {\n    case 1:\n        start();\n        break;\n    }\n}\n",
            new CFamilyBasicFixer.Options { SwitchDefault = true },
            "void work(int state)\n{\n    switch (state)\n    {\n    case 1:\n        start();\n        break;\n    default:\n        /* Unexpected state. */\n        asm(\"nop\");\n        break;\n    }\n}\n");

        RunCase(
            "unsigned suffix",
            "void work(uint32_t mask)\n{\n    uint32_t flag = 1 << mask;\n    if (mask == 0)\n    {\n        flag = 2L;\n    }\n}\n",
            new CFamilyBasicFixer.Options { UnsignedSuffix = true },
            "void work(uint32_t mask)\n{\n    uint32_t flag = 1U << mask;\n    if (mask == 0U)\n    {\n        flag = 2UL;\n    }\n}\n");

        RunCase(
            "ignored return value",
            "void work(void)\n{\n    log_flush();\n    asm(\"nop\");\n    value = read_value();\n    (void)already_marked();\n}\n",
            new CFamilyBasicFixer.Options { IgnoredReturn = true },
            "void work(void)\n{\n    (void)log_flush();\n    asm(\"nop\");\n    value = read_value();\n    (void)already_marked();\n}\n");

        RunCase(
            "sizeof pointee",
            "void work(size_t count)\n{\n    char *buffer;\n    buffer = malloc(count * sizeof(buffer));\n}\n",
            new CFamilyBasicFixer.Options { SizeOfPointee = true },
            "void work(size_t count)\n{\n    char *buffer;\n    buffer = malloc(count * sizeof(*buffer));\n}\n");

        RunCase(
            "fixed width integer types",
            "unsigned int count;\nint result;\nshort small;\nunsigned long long total;\nint main(void)\n{\n    int local = 0;\n    return local;\n}\n",
            new CFamilyBasicFixer.Options { FixedWidthTypes = true },
            "#include <stdint.h>\nuint32_t count;\nint32_t result;\nint16_t small;\nuint64_t total;\nint main(void)\n{\n    int32_t local = 0;\n    return local;\n}\n");

        RunCase(
            "existing switch default remains unchanged",
            "void work(int state)\n{\n    switch (state)\n    {\n    default:\n        break;\n    }\n}\n",
            new CFamilyBasicFixer.Options { SwitchDefault = true },
            "void work(int state)\n{\n    switch (state)\n    {\n    default:\n        break;\n    }\n}\n",
            expectInitialChange: false);

        RunCase(
            "all C family code rules together",
            "#include <stdlib.h>\nvoid log_flush(void);\nvoid start(void);\nunsigned int count = 1;\nchar *buffer;\nvoid work(int state, int ready)\n{\n    if (ready && count > 0)\n        log_flush();\n    switch (state)\n    {\n    case 1:\n        start();\n        break;\n    }\n    buffer = malloc(count * sizeof(buffer));\n}\n",
            new CFamilyBasicFixer.Options
            {
                CompoundStatements = true,
                FinalElse = true,
                MissingElse = true,
                SwitchDefault = true,
                LogicalParentheses = true,
                UnsignedSuffix = true,
                IgnoredReturn = true,
                SizeOfPointee = true,
                FixedWidthTypes = true,
            },
            "#include <stdlib.h>\n#include <stdint.h>\nvoid log_flush(void);\nvoid start(void);\nuint32_t count = 1U;\nchar *buffer;\nvoid work(int32_t state, int32_t ready)\n{\n    if ((ready && count > 0U))\n    {\n        (void)log_flush();\n    }\n    else\n    {\n        asm(\"nop\");\n    }\n    switch (state)\n    {\n    case 1:\n        (void)start();\n        break;\n    default:\n        /* Unexpected state. */\n        asm(\"nop\");\n        break;\n    }\n    buffer = malloc(count * sizeof(*buffer));\n}\n",
            verifyCWithCompiler: true);

        RunCase(
            "compound statements preserve dangling else",
            "void work(void)\n{\n    if (outer) if (inner) run(); else stop();\n}\n",
            new CFamilyBasicFixer.Options { CompoundStatements = true },
            "void work(void)\n{\n    if (outer) { if (inner) { run(); } else { stop(); } }\n}\n");

        RunCase(
            "comments strings and macros are not rewritten",
            "#define CHECK(x) if (x) run()\nconst char *text = \"if (x) run();\";\nvoid work(void)\n{\n    // if (comment) run();\n    if (ready) run();\n}\n",
            new CFamilyBasicFixer.Options { CompoundStatements = true },
            "#define CHECK(x) if (x) run()\nconst char *text = \"if (x) run();\";\nvoid work(void)\n{\n    // if (comment) run();\n    if (ready) { run(); }\n}\n");

        RunCase(
            "C++ source remains valid",
            "void log_flush();\nvoid work(int state, bool ready)\n{\n    if (ready) log_flush();\n    switch (state)\n    {\n    case 1:\n        break;\n    }\n}\n",
            new CFamilyBasicFixer.Options
            {
                CompoundStatements = true,
                FinalElse = true,
                MissingElse = true,
                SwitchDefault = true,
                IgnoredReturn = true,
                FixedWidthTypes = true,
            },
            "#include <stdint.h>\nvoid log_flush();\nvoid work(int32_t state, bool ready)\n{\n    if (ready) { (void)log_flush(); }\n    else\n    {\n        asm(\"nop\");\n    }\n    switch (state)\n    {\n    case 1:\n        break;\n    default:\n        /* Unexpected state. */\n        asm(\"nop\");\n        break;\n    }\n}\n",
            verifyCppWithCompiler: true);

        if (Failures.Count > 0)
        {
            Console.Error.WriteLine("CFamilyBasicFixer FAIL (" + Failures.Count + ")");
            foreach (string failure in Failures) Console.Error.WriteLine(failure);
            return 1;
        }

        Console.WriteLine("CFamilyBasicFixer PASS (17 cases)");
        return 0;
    }

    private static void RunCase(
        string name,
        string input,
        CFamilyBasicFixer.Options options,
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
            int changed = CFamilyBasicFixer.Apply(new[] { file }, options, CancellationToken.None, _ => { });
            string actual = File.ReadAllText(file);
            if (changed != (expectInitialChange ? 1 : 0) || !string.Equals(actual, expected, StringComparison.Ordinal))
            {
                Failures.Add("--- " + name + " ---\nchanged=" + changed + "\nEXPECTED:\n" + expected + "\nACTUAL:\n" + actual);
                return;
            }

            if (verifyCWithCompiler) VerifyWithCompiler(name, file, "gcc", "-std=gnu11");
            if (verifyCppWithCompiler) VerifyWithCompiler(name, file, "g++", "-std=gnu++17");

            int secondChanged = CFamilyBasicFixer.Apply(new[] { file }, options, CancellationToken.None, _ => { });
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
