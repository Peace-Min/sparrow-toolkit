// Minimal RFC 4180-ish CSV reader for --files-from. Matches how SparrowXlsExport quotes its index.csv:
// a field is quoted only when it contains a comma/quote/newline, and an embedded quote is doubled ("").
// A single-column newline-delimited list is parsed as one field per line. Strips a leading UTF-8 BOM.

using System.Collections.Generic;
using System.Text;

namespace SparrowSyntaxFix
{
    internal static class Csv
    {
        public static List<string[]> Parse(string text)
        {
            if (text.Length > 0 && text[0] == '\uFEFF') text = text.Substring(1);

            var rows = new List<string[]>();
            var fields = new List<string>();
            var sb = new StringBuilder();
            bool inQuotes = false;
            bool rowHasContent = false;

            void EndField()
            {
                fields.Add(sb.ToString());
                sb.Clear();
            }

            void EndRow()
            {
                EndField();
                if (rowHasContent) rows.Add(fields.ToArray());   // skip fully-blank lines
                fields.Clear();
                rowHasContent = false;
            }

            int i = 0;
            while (i < text.Length)
            {
                char c = text[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i += 2; continue; }
                        inQuotes = false; i++; continue;
                    }
                    sb.Append(c); rowHasContent = true; i++; continue;
                }

                if (c == '"') { inQuotes = true; rowHasContent = true; i++; continue; }
                if (c == ',') { EndField(); i++; continue; }
                if (c == '\r') { i++; continue; }
                if (c == '\n') { EndRow(); i++; continue; }

                sb.Append(c);
                if (!char.IsWhiteSpace(c)) rowHasContent = true;
                i++;
            }

            // Flush a final row that has no trailing newline.
            if (sb.Length > 0 || fields.Count > 0 || rowHasContent) EndRow();
            return rows;
        }
    }
}
