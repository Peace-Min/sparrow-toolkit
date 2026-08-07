using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;

namespace SparrowRunner.Gui
{
    public partial class HelpWindow : Window
    {
        public HelpWindow()
        {
            InitializeComponent();
        }

        private void HelpTopics_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            // XAML 로딩 중에는 왼쪽의 기본 선택이 오른쪽 뷰어 생성보다 먼저 발생할 수 있다.
            // 이때는 XAML에 선언된 시작 문서가 이미 올바른 초기 내용이므로 그대로 둔다.
            if (HelpContent == null) return;
            if (!(e.NewValue is TreeViewItem item)) return;
            ShowTopic(item.Tag as string ?? "start");
        }

        private void ShowTopic(string topic)
        {
            (string title, string[] paragraphs) = topic switch
            {
                "fix" => ("코드 자동수정", new[] { "분석 대상 솔루션, 프로젝트 또는 폴더를 선택한 뒤 설정 메뉴에서 적용 규칙을 고릅니다.", "코드 수정 결과는 출력 폴더에 복사되지 않고 선택한 원본 소스 파일에 직접 반영됩니다. 실행 후 원본 파일 또는 git diff로 변경 내용을 확인하세요." }),
                "code-rules" => ("코드 규칙", new[] { "설정 > 코드 규칙에서 적용 항목을 선택하거나 해제한 뒤 실행 > 실행 > 코드 규칙 수정 실행(F7)을 선택합니다. C#은 선택된 규칙을 사용하고 C/C++ 계열은 언어 의미가 달라 안전한 기본 규칙만 적용합니다.", "프로젝트 탐색기에서 파일을 한 번 클릭하면 미리보고, 두 번 클릭하면 여러 파일을 탭으로 고정할 수 있습니다." }),
                "comments" => ("주석·레이아웃", new[] { "설정 > 주석·레이아웃에서 주석, 공백, 문장부호, 줄 정렬 규칙을 선택한 뒤 주석·레이아웃 수정 실행(F6)을 선택합니다.", "C/C++ 계열에는 // 및 한 줄 /* ... */ 주석의 뒤 주석 이동, 공백, 마침표, 첫 영문 대문자화 기본 규칙만 적용합니다." }),
                "xls" => ("XLS 분리 (모든 언어)", new[] { "Sparrow 결과 XLS를 읽어 체커별 폴더와 Markdown 항목으로 분리합니다.", "파일 > 출력 폴더는 이 XLS 분리 결과를 저장하는 위치입니다. C, C++, C#, Java 등 XLS에 기록된 언어와 무관하며 원본 소스는 변경하지 않습니다." }),
                "language-safety" => ("지원 언어 및 작업 안전", new[] { "C#은 코드 규칙과 주석·레이아웃 규칙을 적용합니다. C, C++, H, HPP 파일은 안전한 기본 코드 규칙과 기본 주석 규칙만 적용합니다.", "코드 자동수정은 원본 소스 파일을 직접 변경합니다. 파일 > 출력 폴더는 XLS 분리 전용이므로 코드 수정 결과 파일이 그곳에 생성되지 않는 것이 정상입니다.", "XLS 분리의 입력은 Sparrow 결과 XLS 하나이며, C, C++, C#, Java 등 어떤 언어의 검출이든 체커별 폴더의 Markdown 파일로 분리합니다.", "코드 자동수정 실행 후에는 git diff로 변경을 검토한 뒤 직접 커밋하세요." }),
                "safety" => ("안전하게 작업하기", new[] { "코드 자동수정 전에는 작업 트리가 깨끗한지 확인하는 것을 권장합니다.", "실행 후 git diff와 프로젝트 빌드로 결과를 검증하세요. 규칙별 커밋을 사용하면 규칙 단위로 되돌릴 수 있습니다." }),
                "troubleshooting" => ("문제 해결 및 로그", new[] { "화면 오른쪽 위의 LOG 문서 아이콘을 누르면 현재 작업의 진행 상태와 오류를 별도 창에서 확인할 수 있습니다.", "상세 세션 로그는 사용자 LocalAppData의 SparrowRunner\\logs 폴더에 저장됩니다." }),
                _ => ("Sparrow Helper 시작하기", new[] { "Sparrow Helper는 C# 전체 규칙과 C/C++ 계열의 안전한 기본 규칙을 소스에 적용하고, Sparrow 분석 결과 XLS를 체커별 Markdown 파일로 분리하는 도구입니다.", "코드 자동수정 또는 XLS 분리 (모든 언어) 작업을 선택하세요. 창 어디에서든 F1 키를 누르면 도움말을 열 수 있습니다." })
            };

            var document = new FlowDocument
            {
                FontFamily = new System.Windows.Media.FontFamily("Segoe UI, Malgun Gothic"),
                FontSize = 14,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(25, 31, 40))
            };
            document.Blocks.Add(new Paragraph(new Run(title)) { FontSize = 24, FontWeight = FontWeights.SemiBold });
            foreach (string text in paragraphs) document.Blocks.Add(new Paragraph(new Run(text)));
            HelpContent.Document = document;
        }
    }
}
