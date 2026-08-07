#requires -Version 5.1
<#
    Run-SparrowSyntaxFix.ps1 — [코드 규칙] 2단계 원콜 러너.
    [코드 규칙] 체커를 자작 Roslyn 툴 SparrowSyntaxFix로 결정론 처리:
      - nullvar              : `<타입> x = null;` / `<타입> x;` -> `var x = (<타입>)null;`(커밋명 review-needed)
      - parens               : `a && b` 등의 비교/산술 피연산자 괄호
      - objectvar-safe       : `Foo x = new Foo()` -> `var x = new Foo()`
      - foreachcast          : `foreach (T x in xs)` -> `foreach (var x in Enumerable.Cast<T>(xs))`(커밋명 review-needed)
      - obviousvar           : literal/Convert/cast initializer -> var
      - objectvar-narrowing  : 인터페이스/기반타입 var 변환(커밋명 review-needed)
      - localconst           : 지역 const -> var(커밋명 review-needed)
      - objectinitializer    : 생성 직후 연속 property 대입 -> object initializer + var(커밋명 review-needed)
      - arrayvar-safe        : T[] a = new T[] { ... } -> T[] a = { ... }
      - arrayvar-narrowing   : 배열 정적 타입 축소 var 변환(커밋명 review-needed)
      - forvar               : for(int i=0; ...) -> for(var i=0; ...) (단일 선언자·명백 초기값; opt-in, 커밋명 review-needed)
      - fieldsplit           : 다중 선언자 필드 -> 줄마다 하나(필드 한정; opt-in, 커밋명 review-needed)
      - emptystmt            : 잉여 빈문장(; ;) 제거(for(;;)/label 등 의미상 필요분 제외; opt-in, 커밋명 review-needed)
      - forhoist             : 다중 선언자 for 초기화절 분해 — 비루프 선언자를 for 앞으로 hoist(for는 단일 선언자 유지; opt-in, 커밋명 review-needed)
      ※ review-needed 단일 진실 = SparrowSyntaxFix\README.md 규칙 표의 'Commit policy' 열. 아래 $labels 와
        GUI 라벨/검토필요 카운트는 그 표를 따라간다(표를 고치면 세 곳을 함께 고칠 것).
      ※ nullcast 는 nullvar 의 legacy alias 다(같은 rewriter). -Rules 에 둘 다 줘도 1회로 접는다.
    원샷 UX: 솔루션 경로만 주면 동작(내부에서 exe 확보 -> 규칙별 실행 -> 규칙별 커밋).

    사용(원큐): 그냥 실행 -> 솔루션 경로 -> 검토필요 규칙 포함 여부(Y/N) -> 커밋 여부(Y/N)를 물어봄.
      .\Run-SparrowSyntaxFix.ps1                                                # ← 이게 원큐. 경로/검토필요 규칙/커밋 Y/N
      .\Run-SparrowSyntaxFix.ps1 -Solution C:\Work\MyApp\MyApp.sln              # 경로를 미리 줘도 됨(커밋 여부는 물음)
      .\Run-SparrowSyntaxFix.ps1 -Solution ...\MyApp.sln -Commit                # 안 물어보고 규칙별 자동 커밋
      .\Run-SparrowSyntaxFix.ps1 -Solution ...\MyApp.sln -NoCommit              # 파일만 수정, 커밋 안 함(안 물어봄)
      .\Run-SparrowSyntaxFix.ps1 -Solution ...\MyApp.sln -DryRun                # 변경 안 함, 무엇이 바뀔지만 보고
      .\Run-SparrowSyntaxFix.ps1 -Solution ...\MyApp.sln -Rules objectvar-safe,foreachcast # 일부 규칙만
      .\Run-SparrowSyntaxFix.ps1 -Solution ...\MyApp.sln -FilesFrom files.csv   # (정밀) 지정한 파일만 (파일명/경로 컬럼 CSV 또는 줄 목록)
      .\Run-SparrowSyntaxFix.ps1 -Solution ...\MyApp.sln -ExePath C:\tools\SparrowSyntaxFix.exe  # 폐쇄망: 반입 exe 지정
      .\Run-SparrowSyntaxFix.ps1 -Solution ...\MyApp.sln -Commit -VerifyCmd '"C:\...\msbuild.exe" ...\MyApp.sln /t:Build'  # 규칙별 커밋 전 컴파일 게이트(실패 규칙 revert)

    폐쇄망 참고: 이 툴은 Roslyn을 품은 컴파일 exe라, 대상 PC에 exe가 있어야 합니다. 러너는
    (1) -ExePath  (2) 스크립트 옆 publish\SparrowSyntaxFix.exe  (3) csproj + SDK 가 있으면 '항상' 증분 `dotnet build`
    (4) 그래도 없으면(빌드 실패/SDK 없음) 기존 bin\Release\net8.0\SparrowSyntaxFix.dll 폴백  순으로 확보합니다.
    빌드가 dll 폴백보다 '먼저'인 이유: 오래된 bin dll을 그대로 쓰면 소스를 고쳐도 옛 규칙이 돌아
    "안 고쳐졌다"처럼 보이는 사고가 실제로 있었습니다. 인터넷 없는 PC는 (1)/(2)로 반입 exe를 주세요.
#>
param(
    [string]$Solution,
    [string[]]$Rules = @('objectvar-safe', 'obviousvar', 'arrayvar-safe', 'parens'),
    [switch]$Commit,
    [switch]$NoCommit,
    [switch]$DryRun,
    [string]$FilesFrom,
    [string]$ExePath,
    [string]$LogDir,
    # 규칙별 커밋 앞 컴파일 게이트(선택). 예: '"C:\...\msbuild.exe" C:\Work\MyApp\MyApp.sln /t:Build'
    # 주면 각 규칙 edits 후·git 커밋 전 이 명령을 실행. 비정상 종료(exit!=0) 시 그 규칙의 미커밋 *.cs edits를
    # `git checkout -- *.cs`로 되돌리고(커밋 skip) '[GATE] rule <r> reverted' 로그 후 다음 규칙으로 진행.
    # 게이트를 통과한 규칙만 커밋된다. (-Commit 과 함께일 때만 의미 있음 — revert 기준선이 직전 규칙 커밋이므로.)
    # 안 주면 게이트 없음: -Commit이면 "커밋 후 전체 빌드 필수" 안내만 1줄 출력(동작은 종전과 동일).
    [string]$VerifyCmd
)

trap {
    $message = if ($_.Exception) { $_.Exception.Message } else { ($_ | Out-String).Trim() }
    Write-Host ""
    Write-Host "[FATAL] Run-SparrowSyntaxFix 중단: $message" -ForegroundColor Red
    $lp = Get-Variable -Name logPath -Scope 0 -ErrorAction SilentlyContinue
    if ($lp -and $lp.Value) { Write-Host "로그: $($lp.Value)" }
    $inputRedirected = $false
    try { $inputRedirected = [Console]::IsInputRedirected } catch { $inputRedirected = $false }
    if ([Environment]::UserInteractive -and -not $inputRedirected) {
        [void](Read-Host "오류로 중단되었습니다. 내용을 확인한 뒤 Enter를 누르면 닫습니다")
    }
    exit 1
}

try {
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [Console]::InputEncoding = $utf8NoBom
    [Console]::OutputEncoding = $utf8NoBom
    $OutputEncoding = $utf8NoBom
}
catch {
    # 콘솔 인코딩 설정 실패는 러너 본동작을 막지 않는다.
}

$ErrorActionPreference = 'Stop'
$rulesExplicit = $PSBoundParameters.ContainsKey('Rules')

# $PSScriptRoot가 일부 호출에서 비어 있을 수 있어 본문에서 스크립트 폴더 해석
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }

# 원큐 UX: 인자 없이 실행하면 솔루션 경로를 물어봄(그다음 커밋 여부도 물어봄). 붙여넣기 따옴표 자동 제거.
if (-not $Solution) {
    $Solution = Read-Host "정리할 솔루션(.sln) 파일 또는 소스 폴더 경로를 입력하세요"
}
if ($Solution) { $Solution = $Solution.Trim().Trim('"').Trim("'").Trim() }
if (-not $Solution) { throw "경로가 비었습니다. 솔루션(.sln) 또는 소스 폴더 경로가 필요합니다." }

# 규칙 -> 커밋 라벨 (검수 가능한 단위로 규칙별 커밋)
#
# 라벨이 '검토필요:' 로 시작하면 커밋 접두가 'sparrow(rule)! ' 가 된다(아래 커밋 단계). 그래서 이 표의
# '검토필요:' 유무 = SparrowSyntaxFix\README.md 규칙 표의 review-needed 여부와 반드시 일치해야 한다.
# (어긋나면 "검토필요 커밋만 revert" 작업에서 위험 규칙 커밋이 통째로 누락된다 — 실제로 forvar/fieldsplit/
#  emptystmt 가 그렇게 빠져 있었다.) 키는 canonical 규칙명만 쓴다: nullcast 는 nullvar 로 정규화되어
# 여기까지 오지 않는다.
$labels = [ordered]@{
    nullvar               = '검토필요: 명시 지역변수 typed null 초기화 (SparrowSyntaxFix)'
    parens                = '괄호 명확화 일괄 (&&/|| 피연산자) (SparrowSyntaxFix)'
    'objectvar-safe'      = '객체 생성 명시 타입 var 변환 일괄 (SparrowSyntaxFix)'
    foreachcast           = '검토필요: foreach Cast<T> 기반 var 변환 (SparrowSyntaxFix)'
    obviousvar            = '명확한 지역변수 var 변환 일괄 (SparrowSyntaxFix)'
    'objectvar-narrowing' = '검토필요: 정적 타입 축소 var 변환 (SparrowSyntaxFix)'
    localconst            = '검토필요: 지역 const var 전환 (SparrowSyntaxFix)'
    objectinitializer     = '검토필요: 연속 대입 object initializer 통합 (SparrowSyntaxFix)'
    'arrayvar-safe'       = '배열 선언 문법 간소화 일괄 (SparrowSyntaxFix)'
    'arrayvar-narrowing'  = '검토필요: 배열 정적 타입 축소 var 변환 (SparrowSyntaxFix)'
    forvar                = '검토필요: for 초기화절 명시 타입 var 변환 (SparrowSyntaxFix)'
    fieldsplit            = '검토필요: 다중 선언자 필드 줄분리 (SparrowSyntaxFix)'
    emptystmt             = '검토필요: 잉여 빈문장(; ;) 제거 (SparrowSyntaxFix)'
    forhoist              = '검토필요: 다중 선언자 for 초기화절 hoist 분해 (SparrowSyntaxFix)'
}

if (-not $rulesExplicit -and [Environment]::UserInteractive) {
    $optionalRules = @(
        @{ Key = 'foreachcast'; Prompt = 'foreach Cast<T> 기반 var 변환(foreachcast)을 포함할까요? (Y=포함 / N=제외)' },
        @{ Key = 'objectinitializer'; Prompt = '연속 대입 object initializer 통합(objectinitializer)을 포함할까요? (Y=포함 / N=제외)' },
        @{ Key = 'nullvar'; Prompt = '명시 지역변수 typed null 초기화(nullvar)를 포함할까요? (Y=포함 / N=제외)' },
        @{ Key = 'objectvar-narrowing'; Prompt = '정적 타입 축소 var 변환(objectvar-narrowing)을 포함할까요? (Y=포함 / N=제외)' },
        @{ Key = 'localconst'; Prompt = '지역 const var 전환(localconst)을 포함할까요? (Y=포함 / N=제외)' },
        @{ Key = 'arrayvar-narrowing'; Prompt = '배열 정적 타입 축소 var 변환(arrayvar-narrowing)을 포함할까요? (Y=포함 / N=제외)' },
        @{ Key = 'forvar'; Prompt = 'for 초기화절 var 변환(forvar)을 포함할까요? (Y=포함 / N=제외)' },
        @{ Key = 'fieldsplit'; Prompt = '다중 선언자 필드 줄분리(fieldsplit)를 포함할까요? (Y=포함 / N=제외)' },
        @{ Key = 'emptystmt'; Prompt = '잉여 빈문장 제거(emptystmt)를 포함할까요? (Y=포함 / N=제외)' },
        @{ Key = 'forhoist'; Prompt = '다중 선언자 for 초기화절 hoist 분해(forhoist)를 포함할까요? (Y=포함 / N=제외)' }
    )
    foreach ($rule in $optionalRules) {
        $ans = Read-Host $rule.Prompt
        if ($ans -match '^\s*(y|yes|예|ㅛ)\s*$') {
            $Rules += $rule.Key
            Write-Host "-> $($rule.Key) 포함"
        }
        else {
            Write-Host "-> $($rule.Key) 제외"
        }
    }
}

# 허용 입력 = canonical 규칙 14종 + legacy alias. alias 는 엔진(Program.cs TryParseRules)과 동일하게
# canonical 로 접는다: nullcast 와 nullvar 는 같은 rewriter(SyntaxRule.NullVar)라, 둘 다 주면 같은 변환을
# 두 번 돌려 '동일 메시지 커밋 2개'가 생겼다. 정규화 후 첫 등장만 남겨 1회로 접는다(사용자가 준 순서는 유지 —
# 규칙 실행/커밋 순서가 곧 롤백 단위 순서이므로 임의로 재정렬하지 않는다).
$ruleAliases = @{ nullcast = 'nullvar' }
$canonicalRules = @('nullvar', 'parens', 'objectvar-safe', 'foreachcast', 'obviousvar', 'objectvar-narrowing', 'localconst', 'objectinitializer', 'arrayvar-safe', 'arrayvar-narrowing', 'forvar', 'fieldsplit', 'emptystmt', 'forhoist')
$acceptedRules = @($canonicalRules + $ruleAliases.Keys)
$Rules = @($Rules | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$invalidRules = @($Rules | Where-Object { $acceptedRules -notcontains $_ })
if ($invalidRules.Count -gt 0) {
    throw "지원하지 않는 규칙: $($invalidRules -join ', ') / 허용: $($acceptedRules -join ', ')"
}
$normalizedRules = New-Object System.Collections.Generic.List[string]
$foldedAliases = @()
foreach ($r in $Rules) {
    # 엔진(Program.cs)과 같은 판정: 소문자로 접고 alias 를 canonical 로 바꾼다.
    $c = if ($ruleAliases.ContainsKey($r)) { $ruleAliases[$r] } else { $r.ToLowerInvariant() }
    if ($c -ne $r) { $foldedAliases += ("{0} -> {1}" -f $r, $c) }
    if (-not $normalizedRules.Contains($c)) { [void]$normalizedRules.Add($c) }
}
if ($foldedAliases.Count -gt 0) { Write-Host ("규칙 alias 정규화: {0}" -f ($foldedAliases -join ', ')) }
if ($normalizedRules.Count -lt $Rules.Count) {
    Write-Host ("규칙 중복 제거: {0}개 지정 -> {1}개 실행 ({2})" -f $Rules.Count, $normalizedRules.Count, ($normalizedRules -join ','))
}
$Rules = @($normalizedRules.ToArray())

# 0) preflight
if (-not (Test-Path -LiteralPath $Solution)) { throw "솔루션/경로 없음: $Solution" }
$slnFull = (Resolve-Path -LiteralPath $Solution).Path
# .sln 파일이면 그 폴더, 폴더면 그대로 = 소스 루트(툴이 .cs 재귀 + 생성/백업 제외)
$root = if (Test-Path -LiteralPath $slnFull -PathType Leaf) { Split-Path -Parent $slnFull } else { $slnFull }

# 실행 로그
if (-not $LogDir) { $LogDir = (Get-Location).Path }
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$logPath = Join-Path $LogDir ("Run-SparrowSyntaxFix.$stamp.log")
"Run-SparrowSyntaxFix | root=$root | rules=$($Rules -join ',') | dryrun=$([bool]$DryRun) | commit=$([bool]$Commit) | nocommit=$([bool]$NoCommit) | time=$stamp" | Out-File -LiteralPath $logPath -Encoding utf8
Write-Host "실행 로그(전체): $logPath"
Write-Host "소스 루트      : $root"

# 1) 툴 바이너리 확보: ExePath > publish exe > (소스 있으면) 항상 증분 빌드 > 기존 dll(폐쇄망 fallback)
#    ★ 중요: 소스(csproj)가 있으면 항상 재빌드한다. 오래된 bin\Release\dll을 그대로 쓰면 pull 후에도 옛 규칙이
#    돌아 "안 고쳐졌다"처럼 보이기 때문(과거 실제 발생). 증분 빌드는 최신이면 ~수초로 no-op에 가깝다.
function Resolve-Tool {
    if ($ExePath) {
        if (-not (Test-Path -LiteralPath $ExePath)) { throw "-ExePath 없음: $ExePath" }
        $p = (Resolve-Path -LiteralPath $ExePath).Path
        return @{ kind = $(if ($p -match '\.dll$') { 'dll' } else { 'exe' }); path = $p }
    }
    $pubExe = Join-Path $scriptDir 'publish\SparrowSyntaxFix.exe'
    if (Test-Path -LiteralPath $pubExe) { return @{ kind = 'exe'; path = $pubExe } }

    $dll = Join-Path $scriptDir 'bin\Release\net8.0\SparrowSyntaxFix.dll'
    $csproj = Join-Path $scriptDir 'SparrowSyntaxFix.csproj'

    # 소스 + SDK가 있으면 항상 증분 빌드로 dll을 최신 소스와 일치시킨다.
    if ((Test-Path -LiteralPath $csproj) -and (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Host "소스에서 빌드(증분, 최신 규칙 보장): dotnet build -c Release"
        Write-Host "  (첫 빌드는 NuGet 복원 포함 — 아래 진행이 흐릅니다. 인터넷 없는 PC면 Ctrl+C 후 -ExePath 로 반입 exe 지정.)"
        # 빌드는 네이티브(dotnet) 호출 — stderr가 EAP=Stop+2>&1에서 종료오류로 throw되는 것을 막기 위해 Continue로 격리.
        # 출력은 삼키지 않고 한 줄씩 콘솔+로그로 흘려 "멈춘 것처럼 보임"을 방지.
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try {
            & dotnet build $csproj -c Release --nologo -v minimal 2>&1 | ForEach-Object {
                Write-Host "  | $_"
                Add-Content -LiteralPath $logPath -Value $_
            }
            $buildExit = $LASTEXITCODE
        }
        finally { $ErrorActionPreference = $prevEap }
        if ($buildExit -eq 0 -and (Test-Path -LiteralPath $dll)) {
            Write-Host "빌드 완료(최신): $dll"
            return @{ kind = 'dll'; path = $dll }
        }
        if (Test-Path -LiteralPath $dll) {
            Write-Warning "빌드 실패(exit=$buildExit) — 기존 dll을 사용합니다(최신 소스와 다를 수 있음!). 로그: $logPath"
            return @{ kind = 'dll'; path = $dll }
        }
        throw "빌드 실패/미완(exit=$buildExit) + 기존 dll 없음. 인터넷 PC에서 발행한 exe를 -ExePath 로 지정하세요. 로그: $logPath"
    }

    # SDK/소스 없음(폐쇄망 등): 기존 빌드 dll이라도 사용
    if (Test-Path -LiteralPath $dll) {
        Write-Warning "SDK/소스가 없어 기존 빌드 dll을 사용합니다(최신 여부 미검증): $dll"
        return @{ kind = 'dll'; path = $dll }
    }
    throw "실행할 exe/dll이 없고 빌드도 불가합니다(csproj/SDK 없음). 인터넷 PC에서 발행한 exe를 -ExePath 로 지정하세요."
}
$tool = Resolve-Tool
Write-Host "툴            : $($tool.path)"

# 작업트리 오염 경고(자동수정 diff 격리를 위해). native(git) stderr가 EAP=Stop에서 throw되는 것을 막기
# 위해 이 구간만 Continue. git 없음/비-git 폴더(exit!=0)면 조용히 건너뜀(경고는 편의 기능일 뿐).
if (-not $DryRun) {
    $ErrorActionPreference = 'Continue'
    $dirty = @(& git -C $root status --porcelain 2>$null)
    $gitCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($gitCode -eq 0) {
        # 커밋마다 git 자동 gc(재패킹)가 .git pack의 .idx를 unlink하려다 백신/인덱서와 충돌해
        # "Unlink of file ...pack-*.idx failed. Should I try again?" 가 나는 것을 원천 차단.
        # 대상 repo 로컬 설정(1회), 다른 repo엔 영향 없음.
        & git -C $root config gc.auto 0 2>&1 | Out-Null
        & git -C $root config gc.autoDetach false 2>&1 | Out-Null
        & git -C $root config core.fscache true 2>&1 | Out-Null
        if ($dirty.Count -gt 0) {
            Write-Warning "작업트리에 미커밋 변경이 있습니다($($dirty.Count)개). 자동수정 diff와 섞일 수 있으니 깨끗한 상태에서 권장."
        }
    }
}

# git 커밋 하드닝: add/commit을 일시 락(.idx unlink 실패·index.lock 등)에 자동 재시도로 감쌈.
# 반환: 'committed' | 'nochange' | 'failed'. 실패해도 러너는 계속 진행(다음 규칙 처리).
function Read-FilesFromValues {
    param([Parameter(Mandatory)][string]$Path)
    $preferredColumns = @('경로', '파일명', 'path', 'filepath', 'file', 'fullpath')
    $lines = @(Get-Content -LiteralPath $Path -Encoding UTF8)
    $first = @($lines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -First 1)
    if ($first.Count -gt 0) {
        $firstText = [string]$first[0]
        $isKnownHeader = $false
        foreach ($name in $preferredColumns) {
            if ($firstText.Trim() -ieq $name) { $isKnownHeader = $true; break }
        }
        if (-not $isKnownHeader -and $firstText.IndexOf(',') -lt 0) {
            foreach ($line in $lines) {
                $value = ([string]$line).Trim().Trim('"')
                if (-not [string]::IsNullOrWhiteSpace($value)) { $value }
            }
            return
        }
    }

    $rows = @(Import-Csv -LiteralPath $Path -Encoding UTF8)
    foreach ($row in $rows) {
        $props = @($row.PSObject.Properties)
        if ($props.Count -eq 0) { continue }
        $prop = $null
        foreach ($name in $preferredColumns) {
            $prop = $props | Where-Object { $_.Name -ieq $name -and -not [string]::IsNullOrWhiteSpace([string]$_.Value) } | Select-Object -First 1
            if ($prop) { break }
        }
        if (-not $prop) { $prop = $props[0] }
        $value = [string]$prop.Value
        if (-not [string]::IsNullOrWhiteSpace($value)) { $value.Trim() }
    }
}

function New-GitPathspecFile {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$FilesFromPath)
    $rootFull = [System.IO.Path]::GetFullPath($Root).TrimEnd('\', '/') + [System.IO.Path]::DirectorySeparatorChar
    $paths = New-Object System.Collections.Generic.List[string]
    foreach ($entry in Read-FilesFromValues -Path $FilesFromPath) {
        $full = if ([System.IO.Path]::IsPathRooted($entry)) {
            [System.IO.Path]::GetFullPath($entry)
        }
        else {
            [System.IO.Path]::GetFullPath((Join-Path $Root $entry))
        }
        if (-not $full.EndsWith('.cs', [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        if (-not $full.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) { continue }
        $rel = $full.Substring($rootFull.Length).Replace('\', '/')
        if ($rel) { $paths.Add($rel) }
    }
    $paths = @($paths | Sort-Object -Unique)
    if ($paths.Count -eq 0) { return $null }
    $pathspec = Join-Path $env:TEMP ("SparrowSyntaxFix.git-pathspec.$stamp.$PID")
    [System.IO.File]::WriteAllText($pathspec, (($paths -join [char]0) + [char]0), $utf8NoBom)
    return $pathspec
}

function Get-PathspecEntries {
    param([Parameter(Mandatory)][string]$PathspecFile)
    [System.IO.File]::ReadAllText($PathspecFile, $utf8NoBom).Split([char]0) | Where-Object { $_ }
}

function Test-GitTargetChanged {
    param([Parameter(Mandatory)][string]$Root, [string]$PathspecFile)
    if ($PathspecFile) {
        $wanted = New-Object 'System.Collections.Generic.HashSet[string]' ([System.StringComparer]::OrdinalIgnoreCase)
        foreach ($rel in Get-PathspecEntries -PathspecFile $PathspecFile) { [void]$wanted.Add($rel.Replace('\', '/')) }
        $changed = @(& git -C $Root diff --name-only 2>$null)
        foreach ($path in $changed) {
            if ($wanted.Contains(([string]$path).Replace('\', '/'))) { return $true }
        }
        return $false
    }
    $csDirty = @(& git -C $Root status --porcelain -- '*.cs') | Where-Object { $_ }
    return $csDirty.Count -gt 0
}

function Backup-GitTargets {
    param([Parameter(Mandatory)][string]$Root, [string]$PathspecFile)
    if (-not $PathspecFile) { return $null }
    $backup = Join-Path $env:TEMP ("SparrowSyntaxFix.backup.$stamp.$PID." + [guid]::NewGuid().ToString('N'))
    foreach ($rel in Get-PathspecEntries -PathspecFile $PathspecFile) {
        $src = Join-Path $Root ($rel.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        if (-not (Test-Path -LiteralPath $src -PathType Leaf)) { continue }
        $dst = Join-Path $backup ($rel.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        $parent = Split-Path -Parent $dst
        if ($parent) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        Copy-Item -LiteralPath $src -Destination $dst -Force
    }
    return $backup
}

function Restore-GitTargets {
    param([Parameter(Mandatory)][string]$Root, [string]$PathspecFile, [string]$BackupDir)
    if ($PathspecFile -and $BackupDir) {
        foreach ($rel in Get-PathspecEntries -PathspecFile $PathspecFile) {
            $src = Join-Path $BackupDir ($rel.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
            if (-not (Test-Path -LiteralPath $src -PathType Leaf)) { continue }
            $dst = Join-Path $Root ($rel.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
            Copy-Item -LiteralPath $src -Destination $dst -Force
        }
    }
    else {
        & git -C $Root checkout -- '*.cs' 2>&1 | Out-Null
    }
}

function Invoke-GitCommitStep {
    param([Parameter(Mandatory)][string]$Root, [Parameter(Mandatory)][string]$Message, [string]$PathspecFile)
    if ($PathspecFile) {
        if (-not (Test-GitTargetChanged -Root $Root -PathspecFile $PathspecFile)) { return 'nochange' }
        for ($attempt = 1; $attempt -le 5; $attempt++) {
            & git -C $Root commit -q -m $Message --only --pathspec-from-file=$PathspecFile --pathspec-file-nul 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) { return 'committed' }
            if (-not (Test-GitTargetChanged -Root $Root -PathspecFile $PathspecFile)) { return 'committed' }
            Start-Sleep -Milliseconds (400 * $attempt)
            $lock = Join-Path $Root '.git\index.lock'
            if (Test-Path -LiteralPath $lock) { Remove-Item -LiteralPath $lock -Force -ErrorAction SilentlyContinue }
        }
        return 'failed'
    }

    & git -C $Root add -- '*.cs' 2>&1 | Out-Null
    & git -C $Root diff --cached --quiet
    if ($LASTEXITCODE -eq 0) { return 'nochange' }
    for ($attempt = 1; $attempt -le 5; $attempt++) {
        & git -C $Root commit -q -m $Message 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) { return 'committed' }
        # 커밋이 실제로는 성공(스테이징 소진)했는지 확인 - 그러면 성공 처리.
        & git -C $Root diff --cached --quiet
        if ($LASTEXITCODE -eq 0) { return 'committed' }
        # 전형적 일시 락 - 점증 백오프 후 재시도. 혹시 남은 index.lock 은 정리.
        Start-Sleep -Milliseconds (400 * $attempt)
        $lock = Join-Path $Root '.git\index.lock'
        if (Test-Path -LiteralPath $lock) { Remove-Item -LiteralPath $lock -Force -ErrorAction SilentlyContinue }
    }
    return 'failed'
}

# 컴파일 게이트: $VerifyCmd(문자열 명령)를 실행하고 종료코드를 반환. 0=통과. 출력/종료코드는 로그로 흘림.
# 명령 예: '"C:\...\msbuild.exe" C:\Work\MyApp\MyApp.sln /t:Build' 또는 'powershell -Command "exit 1"'.
# 네이티브 exe가 아닌 순수 cmdlet만 실행되면 $LASTEXITCODE가 안 바뀌므로 null->0(통과)로 간주한다.
function Invoke-VerifyGate {
    param([Parameter(Mandatory)][string]$Cmd, [Parameter(Mandatory)][string]$LogFile)
    Add-Content -LiteralPath $LogFile -Value ("`n---------- [GATE] verify: $Cmd ----------")
    $global:LASTEXITCODE = 0
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        $gout = Invoke-Expression $Cmd 2>&1
        $gexit = $LASTEXITCODE
    }
    catch {
        $gout = ($_ | Out-String)
        $gexit = 1
    }
    finally { $ErrorActionPreference = $prevEap }
    if ($null -eq $gexit) { $gexit = 0 }
    Add-Content -LiteralPath $LogFile -Value (($gout | Out-String))
    Add-Content -LiteralPath $LogFile -Value ("[GATE] exit=$gexit")
    return $gexit
}

# 1b) -Commit/-DryRun 둘 다 없으면 물어봄(플래그 빼먹는 실수 방지). 비대화형은 안 물어보고 커밋 안 함.
if (-not $Commit -and -not $DryRun -and -not $NoCommit) {
    if ([Environment]::UserInteractive) {
        $ans = Read-Host "규칙별로 커밋할까요? (Y=규칙별 자동 커밋 / N=파일만 수정, 커밋 안 함)"
        if ($ans -match '^\s*(y|yes|예|ㅛ)\s*$') { $Commit = $true; Write-Host "-> 규칙별 커밋 진행" }
        else { Write-Host "-> 파일만 수정(커밋 안 함). 나중에 -Commit으로 재실행 가능." }
    }
    else {
        Write-Host "(비대화형: -Commit 미지정 -> 커밋 안 함)"
    }
}
elseif ($NoCommit) {
    Write-Host "-> 파일만 수정(커밋 안 함). (-NoCommit)"
}

# 1c) 컴파일 게이트 안내. -Commit인데 -VerifyCmd가 없으면 게이트가 없다는 걸 분명히 알린다(커밋 후 전체 빌드 필수).
$gateActive = ($Commit -and $VerifyCmd)
if ($Commit -and -not $VerifyCmd) {
    Write-Host "빌드 게이트 없음 — 커밋 후 반드시 전체 빌드로 확인 (규칙별 컴파일 게이트는 -VerifyCmd 로 활성화)."
}
elseif ($gateActive) {
    Write-Host "빌드 게이트 활성: 규칙별 커밋 전 검증 실행 -> $VerifyCmd  (실패 규칙은 edits revert 후 커밋 skip)"
}
elseif ($VerifyCmd -and -not $Commit) {
    Write-Host "참고: -VerifyCmd는 -Commit과 함께일 때만 게이트로 동작합니다(revert 기준선이 직전 규칙 커밋). 이번 실행은 커밋을 안 하므로 게이트 미적용."
}

# 2) 규칙별 실행 — native(dotnet/git) stderr가 EAP=Stop에서 throw되는 것을 막기 위해 이 구간은 Continue.
$ErrorActionPreference = 'Continue'
$failed = $false
$grand = 0
$gateReverted = 0
$gitPathspecFile = $null
if ($FilesFrom) { $gitPathspecFile = New-GitPathspecFile -Root $root -FilesFromPath $FilesFrom }
if ($FilesFrom -and -not $gitPathspecFile) { throw "-FilesFrom에 소스 루트 아래 .cs 파일이 없습니다: $FilesFrom" }
foreach ($r in $Rules) {
    $backupDir = if ($gateActive -and -not $DryRun) { Backup-GitTargets -Root $root -PathspecFile $gitPathspecFile } else { $null }
    if ($FilesFrom) {
        $toolArgs = @('--rules', $r, '--root', $root, '--files-from', $FilesFrom)
    }
    else {
        $toolArgs = @($root, '--rules', $r, '--root', $root)
    }
    if ($DryRun) { $toolArgs += '--dry-run' }

    if ($tool.kind -eq 'dll') { $out = & dotnet $tool.path @toolArgs 2>&1 }
    else { $out = & $tool.path @toolArgs 2>&1 }
    $code = $LASTEXITCODE
    $text = ($out | Out-String)

    Add-Content -LiteralPath $logPath -Value ("`n========== $r | exit=$code ==========")
    Add-Content -LiteralPath $logPath -Value $text

    $nChanged = [regex]::Match($text, 'files changed:\s*(\d+)').Groups[1].Value
    $nEdits = [regex]::Match($text, [regex]::Escape($r) + ' edits:\s*(\d+)').Groups[1].Value
    if ($nEdits) { $grand += [int]$nEdits }

    Write-Host ""
    Write-Host "=== $r  | exit=$code ==="
    Write-Host "  변경 파일 : $(if ($nChanged) { $nChanged } else { '? (로그 확인)' })"
    Write-Host "  수정 건수 : $(if ($nEdits) { $nEdits } else { '? (로그 확인)' })"

    if ($code -eq 2) { Write-Warning "  사용법 오류(exit 2) - 로그 확인."; $failed = $true; break }
    if ($code -ne 0) { Write-Warning "  실패(exit $code) - 로그 확인."; $failed = $true; break }
    if ($DryRun) { Write-Host "  결과      : [dry-run] 파일 변경 안 함"; continue }

    if ($Commit) {
        # 컴파일 게이트: 커밋 앞에서 $VerifyCmd 실행. 실패하면 이 규칙의 미커밋 *.cs edits를 revert하고 커밋 skip.
        # (revert pathspec는 커밋의 git add와 동일한 '*.cs' — 대상 루트 아래 추적 .cs만 직전 커밋 상태로 되돌림.)
        # 이 규칙이 실제로 .cs를 안 바꿨으면(no-op) 느린 빌드를 낭비하지 않도록 게이트를 건너뛴다(커밋도 nochange 처리).
        $hasRuleChanges = $false
        if ($nChanged -and [int]$nChanged -gt 0) { $hasRuleChanges = $true }
        elseif ($nEdits -and [int]$nEdits -gt 0) { $hasRuleChanges = $true }
        elseif (Test-GitTargetChanged -Root $root -PathspecFile $gitPathspecFile) { $hasRuleChanges = $true }
        if ($gateActive -and $hasRuleChanges) {
            $gexit = Invoke-VerifyGate -Cmd $VerifyCmd -LogFile $logPath
            if ($gexit -ne 0) {
                Restore-GitTargets -Root $root -PathspecFile $gitPathspecFile -BackupDir $backupDir
                Write-Host "  [GATE] rule $r reverted: verify failed(exit $gexit)"
                Add-Content -LiteralPath $logPath -Value "[GATE] rule $r reverted: verify failed(exit $gexit)"
                $gateReverted++
                if ($backupDir) { Remove-Item -LiteralPath $backupDir -Recurse -Force -ErrorAction SilentlyContinue }
                continue
            }
            Write-Host "  게이트    : 통과(exit 0) -> 커밋 진행"
        }
        $prefix = if ($labels[$r] -like '검토필요:*') { 'sparrow(rule)! ' } else { 'sparrow(rule): ' }
        $res = Invoke-GitCommitStep -Root $root -Message "$prefix$($labels[$r])" -PathspecFile $gitPathspecFile
        switch ($res) {
            'committed' { Write-Host "  커밋      : $prefix$($labels[$r])" }
            'nochange'  { Write-Host "  커밋      : 변경 없음 -> 건너뜀 (이 규칙에서 바뀐 .cs 없음)" }
            'failed'    { Write-Warning "  커밋 실패(git 락 5회 재시도 후에도) - 파일 수정은 유지됨. 나중에 수동 커밋 가능." }
        }
    }
    elseif ($NoCommit) { Write-Host "  커밋      : -NoCommit -> 커밋 안 함 (파일만 수정됨)" }
    else { Write-Host "  커밋      : -Commit 미지정 -> 커밋 안 함 (파일만 수정됨)" }
    if ($backupDir) { Remove-Item -LiteralPath $backupDir -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host ""
if (-not $DryRun) { Write-Host "총 수정 건수(적용된 규칙 합): $grand" }
if ($gateActive -and $gateReverted -gt 0) { Write-Host "게이트 revert(검증 실패로 되돌리고 커밋 skip한 규칙): $gateReverted" }
if ($failed) { Write-Host "일부 규칙 미완 -> 로그 확인." }
Write-Host "전체 로그: $logPath"
Write-Host "다음(필수): (1) 빌드 통과 확인  (2) 스패로우 재분석으로 해당 체커 건수 감소 확인 (Roslyn 경계 != Sparrow 경계)."
if ($failed) { exit 1 }
