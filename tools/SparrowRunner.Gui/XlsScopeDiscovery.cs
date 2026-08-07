using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SparrowXlsExport.Core;

namespace SparrowRunner.Gui
{
    /// <summary>
    /// [XLS 분리] 범위 트리의 결과. 트리는 <b>로컬 소스를 스캔해서 만든 것이 아니라</b> xls 가 스스로
    /// 보고한 검출 경로(<see cref="SparrowExporter.ListPaths"/>)로 만든다. 그래서
    ///   * 프로젝트 경로 입력이 필요 없고(입력은 xls 하나),
    ///   * 어떤 언어(C/C++/C#/Java...)의 결과든 트리가 만들어지고,
    ///   * 선택을 xls 자기 경로 그대로 익스포터에 되먹이므로 크로스-PC 경로 불일치가 원리적으로 없다.
    /// </summary>
    public sealed class XlsScope
    {
        public XlsScope(IReadOnlyList<SourceScopeNode> roots, int totalFiles, int totalItems, string commonPrefix)
        {
            Roots = roots;
            TotalFiles = totalFiles;
            TotalItems = totalItems;
            CommonPrefix = commonPrefix ?? "";
        }

        public IReadOnlyList<SourceScopeNode> Roots { get; }

        /// <summary>
        /// 트리에서 접어 낸 <b>공통 접두 경로</b>(표시 전용, 없으면 빈 문자열). 실 xls 는 모든 경로가
        /// <c>D:\Work\...\release\2026-07-10\</c> 처럼 긴 상위 폴더를 공유해서, 그대로 트리로 만들면 자식이 하나뿐인
        /// 노드를 6번 파고들어야 실제 프로젝트 폴더가 나온다. 그 체인을 트리에서 빼고 이 문자열로 돌려 화면 상단에
        /// 한 줄로 보여 준다. <b>선택/매칭은 여전히 리프의 xls 원본 절대경로 전체</b>를 쓴다(Tier 0 계약 불변).
        /// </summary>
        public string CommonPrefix { get; }

        /// <summary>트리의 리프(=xls 가 보고한 서로 다른 경로) 개수.</summary>
        public int TotalFiles { get; }

        /// <summary>전체 검출 건수(리프 건수의 합).</summary>
        public int TotalItems { get; }

        /// <summary>체크된 리프의 <b>xls 원본 경로</b>(manifest 에 그대로 쓴다).</summary>
        public IReadOnlyList<string> SelectedPaths =>
            Roots.SelectMany(r => r.EnumerateSelectedFiles())
                 .Distinct(StringComparer.OrdinalIgnoreCase)
                 .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                 .ToList();

        /// <summary>체크된 리프가 가진 검출 건수 합(요약 "선택 N개 파일 · M건").</summary>
        public int SelectedItems => SumSelected(Roots);

        public int SelectedFileCount => SelectedPaths.Count;

        private static int SumSelected(IEnumerable<SourceScopeNode> nodes)
        {
            int sum = 0;
            foreach (SourceScopeNode node in nodes)
            {
                if (node.IsFile)
                {
                    if (node.IsChecked == true) sum += node.ItemCount;
                    continue;
                }
                sum += SumSelected(node.Children);
            }
            return sum;
        }
    }

    /// <summary>xls 검출 경로 목록 → 디렉토리 트리(폴더 노드 + 파일 리프). 파일도 폴더도 스캔하지 않는다.</summary>
    public static class XlsScopeDiscovery
    {
        // 이 깊이보다 얕은 폴더 노드는 펼친 상태로 시작한다(열자마자 내용이 보이도록. 깊은 트리는 접어 둔다).
        private const int ExpandDepthLimit = 2;

        public static Task<XlsScope> DiscoverAsync(string xlsPath, CancellationToken cancellationToken)
        {
            return Task.Run(() =>
            {
                // ListPaths 는 어떤 파일도 쓰지 않는다(무작성 census). 트리 생성까지 백그라운드에서 끝낸다.
                IReadOnlyList<XlsPathEntry> entries = SparrowExporter.ListPaths(xlsPath);
                cancellationToken.ThrowIfCancellationRequested();
                return Build(entries);
            }, cancellationToken);
        }

        /// <summary>경로 목록으로 디렉토리 트리를 만든다. 리프의 FullPath = xls 원본 경로 문자열(변형 없음).</summary>
        public static XlsScope Build(IReadOnlyList<XlsPathEntry>? entries)
        {
            var rootDir = new DirBuilder("", "");
            int totalFiles = 0;
            int totalItems = 0;

            foreach (XlsPathEntry entry in entries ?? Array.Empty<XlsPathEntry>())
            {
                string[] segments = SplitSegments(entry.Path);
                if (segments.Length == 0) continue;

                DirBuilder dir = rootDir;
                for (int i = 0; i < segments.Length - 1; i++)
                {
                    dir = dir.Child(segments[i], JoinSegments(segments, i + 1));
                }

                string leafName = entry.FileName.Length > 0 ? entry.FileName : segments[segments.Length - 1];
                dir.Files.Add(new LeafBuilder(leafName, entry.Path, entry.Count));
                totalFiles++;
                totalItems += entry.Count;
            }

            // 공통 접두 접기(표시 전용): 파일이 없고 하위 폴더가 정확히 하나뿐인 최상위 체인은 정보가 0이므로
            // 트리에서 빼고 CommonPrefix 로 돌린다. 트리 루트는 "실제 분기점"(ModuleA/ModuleB/src …)부터 시작한다.
            // 리프의 FullPath 는 건드리지 않는다 — 익스포터에 되먹이는 문자열은 xls 원본 경로 그대로다.
            DirBuilder start = rootDir;
            string commonPrefix = "";
            while (start.Files.Count == 0 && start.DirCount == 1)
            {
                DirBuilder only = start.Dirs.First();
                commonPrefix = only.Path;
                start = only;
            }

            var roots = new List<SourceScopeNode>();
            foreach (SourceScopeNode node in Materialize(start, parent: null, depth: 0)) roots.Add(node);
            foreach (SourceScopeNode root in roots) AggregateCounts(root);
            return new XlsScope(roots, totalFiles, totalItems, commonPrefix);
        }

        // DirBuilder 트리를 화면용 노드로 옮긴다. 폴더 먼저(이름 사전순), 그 다음 파일(이름 사전순) = 결정적 순서.
        private static IEnumerable<SourceScopeNode> Materialize(DirBuilder dir, SourceScopeNode? parent, int depth)
        {
            foreach (DirBuilder child in dir.Dirs.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
            {
                var node = new SourceScopeNode(child.Name, child.Path, isFile: false, parent, initialChecked: false)
                {
                    IsExpanded = depth < ExpandDepthLimit,
                };
                foreach (SourceScopeNode grandChild in Materialize(child, node, depth + 1))
                {
                    node.Children.Add(grandChild);
                }
                yield return node;
            }

            foreach (LeafBuilder file in dir.Files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                                                 .ThenBy(f => f.Path, StringComparer.OrdinalIgnoreCase))
            {
                yield return new SourceScopeNode(file.Name, file.Path, isFile: true, parent, initialChecked: false)
                {
                    ItemCount = file.Count,
                };
            }
        }

        // 폴더 노드의 ItemCount = 하위 리프 건수 합(폴더에 "하위 합계"를 표시하기 위해).
        private static int AggregateCounts(SourceScopeNode node)
        {
            if (node.IsFile) return node.ItemCount;
            int sum = 0;
            foreach (SourceScopeNode child in node.Children) sum += AggregateCounts(child);
            node.ItemCount = sum;
            return sum;
        }

        private static string[] SplitSegments(string path)
        {
            return (path ?? "")
                .Replace('/', '\\')
                .Split('\\')
                .Select(s => s.Trim())
                .Where(s => s.Length > 0)
                .ToArray();
        }

        // 폴더 노드의 FullPath 는 표시/툴팁 용도의 접두 경로다(매칭에는 리프의 xls 원본 경로만 쓴다).
        private static string JoinSegments(string[] segments, int count)
        {
            return string.Join("\\", segments.Take(count));
        }

        private sealed class DirBuilder
        {
            private readonly Dictionary<string, DirBuilder> _dirs =
                new Dictionary<string, DirBuilder>(StringComparer.OrdinalIgnoreCase);

            public DirBuilder(string name, string path)
            {
                Name = name;
                Path = path;
            }

            public string Name { get; }
            public string Path { get; }
            public IEnumerable<DirBuilder> Dirs => _dirs.Values;
            public int DirCount => _dirs.Count;
            public List<LeafBuilder> Files { get; } = new List<LeafBuilder>();

            public DirBuilder Child(string name, string path)
            {
                if (!_dirs.TryGetValue(name, out DirBuilder? child))
                {
                    child = new DirBuilder(name, path);
                    _dirs[name] = child;
                }
                return child;
            }
        }

        private sealed class LeafBuilder
        {
            public LeafBuilder(string name, string path, int count)
            {
                Name = name;
                Path = path;
                Count = count;
            }

            public string Name { get; }
            public string Path { get; }
            public int Count { get; }
        }
    }
}
