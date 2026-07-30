using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace SparrowRunner.Gui
{
    public static class ScopeManifestWriter
    {
        public static string WriteTemp(IReadOnlyCollection<string> selectedFiles)
        {
            string path = Path.Combine(Path.GetTempPath(), "sparrow-scope-" + Guid.NewGuid().ToString("N") + ".csv");
            Write(path, selectedFiles);
            return path;
        }

        /// <summary>
        /// XLS 범위 트리(Track C)용 manifest: 항목을 <b>xls 가 적어 둔 그대로</b> 기록한다(Path.GetFullPath 로
        /// 로컬 경로화하지 않는다 — 그 순간 다른 PC 의 경로가 이 PC 기준으로 변질된다). 익스포터는 이 문자열을
        /// 같은 xls 의 경로와 그대로 대조하므로(Tier 0) 매칭이 100% 정확하고 언어와 무관하다.
        /// </summary>
        public static string WriteTempRaw(IReadOnlyCollection<string> xlsPaths)
        {
            string path = Path.Combine(Path.GetTempPath(), "sparrow-xls-scope-" + Guid.NewGuid().ToString("N") + ".csv");
            var sb = new StringBuilder();
            sb.AppendLine("파일명");
            foreach (string entry in xlsPaths
                         .Where(p => !string.IsNullOrWhiteSpace(p))
                         .Select(p => p.Trim())
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append('"').Append(entry.Replace("\"", "\"\"")).AppendLine("\"");
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        public static void Write(string path, IReadOnlyCollection<string> selectedFiles)
        {
            var sb = new StringBuilder();
            sb.AppendLine("파일명");
            foreach (string file in selectedFiles
                         .Select(Path.GetFullPath)
                         .Distinct(StringComparer.OrdinalIgnoreCase)
                         .OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                sb.Append('"').Append(file.Replace("\"", "\"\"")).AppendLine("\"");
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }
    }
}
