using System;
using System.IO;
using System.Text;
using System.Threading;
using SparrowRunner.Gui;

internal static class Program
{
    private static int Main()
    {
        string dir = Path.Combine(Path.GetTempPath(), "sparrow-c-family-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "sample.c");
        try
        {
            File.WriteAllText(file, "int main() { //hello\r\n    int value = 1; /*block note*/\r\n    if (ready && valid) return 1;\r\n}\r\n", new UTF8Encoding(false));
            var options = new CFamilyBasicFixer.Options
            {
                LogicalParentheses = true,
                TrailingComment = true,
                CommentSpace = true,
                CommentPeriod = true,
                CommentCapitalize = true,
            };
            int changed = CFamilyBasicFixer.Apply(new[] { file }, options, CancellationToken.None, _ => { });
            string actual = File.ReadAllText(file);
            string expected = "// Hello.\r\nint main() {\r\n    /* Block note. */\r\n    int value = 1;\r\n    if ((ready && valid)) return 1;\r\n}\r\n";
            if (changed != 1 || !string.Equals(actual, expected, StringComparison.Ordinal))
            {
                Console.Error.WriteLine("CFamilyBasicFixer FAIL\n--- actual ---\n" + actual);
                return 1;
            }
            Console.WriteLine("CFamilyBasicFixer PASS");
            return 0;
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
