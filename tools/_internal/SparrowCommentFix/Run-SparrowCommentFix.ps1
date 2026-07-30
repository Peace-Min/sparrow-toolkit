#requires -Version 5.1
<#
    Run-SparrowCommentFix.ps1 — Track B 주석 픽스 원콜 러너.
    자작 Roslyn 툴 SparrowCommentFix로 주석 trivia 규칙을 결정론 처리:
      - flatten : `/** @brief x */` -> `// X.` 라인별 평탄화
      - trailing: `code; //ABC` -> `// ABC.` + `code;`
      - space  : `//x` -> `// x`  (FORMATTING.COMMENT.MISSING_SPACE_AFTER_DELIMITER)
      - period : 주석 문장 끝 마침표 보정                     (FORMATTING.COMMENT.MISSING_PERIOD)
      - memberblank    : 메소드/프로퍼티 선언 사이 빈 줄 보정
      - onestatement   : 한 줄 여러 구문 분리
      - onedeclaration : 한 줄 여러 선언 분리
      - continuation   : 여러 줄 문장 continuation 들여쓰기 보정
      - linqalign      : LINQ query clause 정렬
    Track A/B 원샷 UX: 솔루션/폴더 경로만 주면 동작(내부에서 exe 확보
    -> 규칙별 실행 -> 규칙별 커밋). -Commit/-DryRun 둘 다 없으면 커밋 여부를 물음. 단, SparrowCommentFix는
    디렉터리를 받지 않으므로(개별 .cs 경로 또는 --files-from CSV만) 러너가 PowerShell에서 .cs 재귀
    수집 + 생성/백업 파일 제외를 직접 수행한 뒤, 대상 전체경로를 임시 --files-from CSV로 툴에 넘긴다.

    사용:
      .\Run-SparrowCommentFix.ps1 -Solution C:\Work\MyApp\MyApp.sln          # 적용. flatten/layout 포함 여부와 커밋 여부를 물음
      .\Run-SparrowCommentFix.ps1 -Solution ...\MyApp.sln -Commit            # 규칙별 git 커밋(안 물어봄)
      .\Run-SparrowCommentFix.ps1 -Solution ...\MyApp.sln -NoCommit          # 파일만 수정, 커밋 안 함(안 물어봄)
      .\Run-SparrowCommentFix.ps1 -Solution C:\Work\MyApp -DryRun            # 변경 안 함, 무엇이 바뀔지만 보고
      .\Run-SparrowCommentFix.ps1 -Solution ...\MyApp.sln -Rules period      # 일부 규칙만
      .\Run-SparrowCommentFix.ps1 -Solution ...\MyApp.sln -FilesFrom files.csv   # (정밀) 자동 글롭 대신 준 CSV/목록 사용
      .\Run-SparrowCommentFix.ps1 -Solution ...\MyApp -IncludeGenerated      # 생성/백업 파일도 포함(기본 제외)
      .\Run-SparrowCommentFix.ps1 -Solution ...\MyApp.sln -ExePath C:\tools\SparrowCommentFix.exe  # 폐쇄망: 반입 exe 지정
      .\Run-SparrowCommentFix.ps1 -Solution ...\MyApp.sln -Commit -VerifyCmd '"C:\...\msbuild.exe" ...\MyApp.sln /t:Build'  # 규칙별 커밋 전 컴파일 게이트(실패 규칙 revert)

    폐쇄망 참고: 이 툴은 Roslyn을 품은 컴파일 exe라, 대상 PC에 exe가 있어야 합니다. 러너는
    (1) -ExePath  (2) 스크립트 옆 publish\SparrowCommentFix.exe  (3) bin\Release\net8.0\SparrowCommentFix.dll
    (4) 없으면 `dotnet build`(패키지 복원 가능할 때)  순으로 확보합니다. 인터넷 없는 PC는 (1)/(2)로 반입 exe를 주세요.
#>
param(
    [string]$Solution,      # .sln / .csproj / 폴더 경로 (소스 루트)
    [string[]]$Rules = @('trailing', 'space', 'period', 'capitalize'),
    [switch]$Commit,
    [switch]$NoCommit,
    [switch]$DryRun,
    [string]$FilesFrom,          # (정밀) 파일 목록 CSV/줄 목록을 주면 자동 글롭 대신 그걸 사용
    [switch]$IncludeGenerated,   # 기본 off: 생성/백업 파일 제외. on이면 전부 포함
    [string]$ExePath,            # 폐쇄망: 반입 exe/dll 지정
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
    Write-Host "[FATAL] Run-SparrowCommentFix 중단: $message" -ForegroundColor Red
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

if (-not $Solution) {
    $Solution = Read-Host "정리할 솔루션(.sln) 파일 또는 소스 폴더 경로를 입력하세요"
}
if ($Solution) { $Solution = $Solution.Trim().Trim('"').Trim("'").Trim() }
if (-not $Solution) { throw "경로가 비었습니다. 솔루션(.sln) 또는 소스 폴더 경로가 필요합니다." }

# 규칙 -> 커밋 라벨 (검수 가능한 단위로 규칙별 커밋; Track A/B 동일 방식)
$labels = [ordered]@{
    flatten = '블록 주석 라인별 평탄화'
    trailing = '트레일링 주석 독립 줄 이동 및 보정'
    space  = '주석 구분자 뒤 공백 일괄 (//x -> // x)'
    period = '주석 끝 마침표 일괄'
    capitalize = '주석 첫 글자 대문자 일괄'
    memberblank = '멤버 선언 사이 빈 줄 일괄'
    onestatement = '한 줄 여러 구문 분리'
    onedeclaration = '한 줄 여러 선언 분리'
    continuation = '여러 줄 문장 들여쓰기 보정'
    linqalign = 'LINQ 쿼리 절 정렬'
    blockpromote = '검토필요: 인라인 블록주석 상단 승격'
}

if (-not $rulesExplicit -and [Environment]::UserInteractive) {
    $ansFlatten = Read-Host "Doxygen/XML 문서 주석 평탄화(flatten)를 포함할까요? (Y=포함 / N=기본 주석 규칙만)"
    if ($ansFlatten -match '^\s*(y|yes|예|ㅛ)\s*$') {
        $Rules = @('flatten') + @($Rules)
        Write-Host "-> flatten 포함"
    }
    else {
        Write-Host "-> flatten 제외"
    }

    $ansLayout = Read-Host "layout 계열(memberblank/onedeclaration/onestatement/linqalign/continuation)을 포함할까요? (Y=포함 / N=기본 주석 규칙만)"
    if ($ansLayout -match '^\s*(y|yes|예|ㅛ)\s*$') {
        $Rules += @('onedeclaration', 'onestatement', 'memberblank', 'linqalign', 'continuation')
        Write-Host "-> layout 계열 포함"
    }
    else {
        Write-Host "-> layout 계열 제외"
    }

    $ansBlockpromote = Read-Host "인라인 /* */ 블록주석 상단 승격(blockpromote)을 포함할까요? (Y=포함 / N=제외)"
    if ($ansBlockpromote -match '^\s*(y|yes|예|ㅛ)\s*$') {
        $Rules += @('blockpromote')
        Write-Host "-> blockpromote 포함"
    }
    else {
        Write-Host "-> blockpromote 제외"
    }
}

$canonicalRules = @('flatten', 'trailing', 'blockpromote', 'space', 'period', 'capitalize', 'onedeclaration', 'onestatement', 'memberblank', 'linqalign', 'continuation')
$selectedRules = @($Rules | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$invalidRules = @($selectedRules | Where-Object { $canonicalRules -notcontains $_ })
if ($invalidRules.Count -gt 0) {
    throw "지원하지 않는 규칙: $($invalidRules -join ', ') / 허용: $($canonicalRules -join ', ')"
}
$Rules = @($canonicalRules | Where-Object { $selectedRules -contains $_ })

# 0) preflight
if (-not (Test-Path -LiteralPath $Solution)) { throw "솔루션/경로 없음: $Solution" }
$slnFull = (Resolve-Path -LiteralPath $Solution).Path
# .sln/.csproj 파일이면 그 폴더, 폴더면 그대로 = 소스 루트(러너가 .cs 재귀 + 생성/백업 제외)
$root = if (Test-Path -LiteralPath $slnFull -PathType Leaf) { Split-Path -Parent $slnFull } else { $slnFull }

# 실행 로그
if (-not $LogDir) { $LogDir = (Get-Location).Path }
$stamp = (Get-Date).ToString('yyyyMMdd-HHmmss')
$logPath = Join-Path $LogDir ("Run-SparrowCommentFix.$stamp.log")
"Run-SparrowCommentFix | root=$root | rules=$($Rules -join ',') | dryrun=$([bool]$DryRun) | commit=$([bool]$Commit) | nocommit=$([bool]$NoCommit) | includeGenerated=$([bool]$IncludeGenerated) | time=$stamp" | Out-File -LiteralPath $logPath -Encoding utf8
Write-Host "실행 로그(전체): $logPath"
Write-Host "소스 루트      : $root"

# 1) 툴 바이너리 확보: ExePath > publish exe > (소스 있으면) 항상 증분 빌드 > 기존 dll(폐쇄망 fallback)
#    ★ 중요: 소스(csproj)가 있으면 항상 재빌드한다. 오래된 bin\Release\dll을 그대로 쓰면 pull 후 새 규칙
#    (flatten/trailing/layout 등)을 dll이 몰라 'unknown rule'로 죽는다(실제 발생). 증분 빌드는 최신이면 ~수초.
function Resolve-Tool {
    if ($ExePath) {
        if (-not (Test-Path -LiteralPath $ExePath)) { throw "-ExePath 없음: $ExePath" }
        $p = (Resolve-Path -LiteralPath $ExePath).Path
        return @{ kind = $(if ($p -match '\.dll$') { 'dll' } else { 'exe' }); path = $p }
    }
    $pubExe = Join-Path $scriptDir 'publish\SparrowCommentFix.exe'
    if (Test-Path -LiteralPath $pubExe) { return @{ kind = 'exe'; path = $pubExe } }

    $dll = Join-Path $scriptDir 'bin\Release\net8.0\SparrowCommentFix.dll'
    $csproj = Join-Path $scriptDir 'SparrowCommentFix.csproj'

    # 소스 + SDK가 있으면 항상 증분 빌드로 dll을 최신 소스와 일치시킨다.
    if ((Test-Path -LiteralPath $csproj) -and (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Write-Host "소스에서 빌드(증분, 최신 규칙 보장): dotnet build -c Release"
        Write-Host "  (첫 빌드는 NuGet 복원 포함 — 아래 진행이 흐릅니다. 인터넷 없는 PC면 Ctrl+C 후 -ExePath 로 반입 exe 지정.)"
        # 빌드는 네이티브(dotnet) 호출 — stderr가 EAP=Stop+2>&1에서 종료오류로 throw되는 것을 막기 위해 Continue로 격리.
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
    param([Parameter(Mandatory = $true)][string]$Path)
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
    param([Parameter(Mandatory = $true)][string]$Root, [Parameter(Mandatory = $true)][string]$FilesFromPath)
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
    $pathspec = Join-Path $env:TEMP ("SparrowCommentFix.git-pathspec.$stamp.$PID")
    [System.IO.File]::WriteAllText($pathspec, (($paths -join [char]0) + [char]0), $utf8NoBom)
    return $pathspec
}

function Get-PathspecEntries {
    param([Parameter(Mandatory = $true)][string]$PathspecFile)
    [System.IO.File]::ReadAllText($PathspecFile, $utf8NoBom).Split([char]0) | Where-Object { $_ }
}

function Test-GitTargetChanged {
    param([Parameter(Mandatory = $true)][string]$Root, [string]$PathspecFile)
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
    param([Parameter(Mandatory = $true)][string]$Root, [string]$PathspecFile)
    if (-not $PathspecFile) { return $null }
    $backup = Join-Path $env:TEMP ("SparrowCommentFix.backup.$stamp.$PID." + [guid]::NewGuid().ToString('N'))
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
    param([Parameter(Mandatory = $true)][string]$Root, [string]$PathspecFile, [string]$BackupDir)
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
    param([Parameter(Mandatory = $true)][string]$Root, [Parameter(Mandatory = $true)][string]$Message, [string]$PathspecFile)
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
        & git -C $Root diff --cached --quiet
        if ($LASTEXITCODE -eq 0) { return 'committed' }
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
    param([Parameter(Mandatory = $true)][string]$Cmd, [Parameter(Mandatory = $true)][string]$LogFile)
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

# 2) 대상 .cs 파일 목록 구성.
#    SparrowCommentFix는 디렉터리를 받지 않으므로 러너가 여기서 재귀 수집 + 생성/백업 제외를 하고,
#    대상 전체경로를 임시 --files-from CSV로 넘긴다. (-FilesFrom을 주면 자동 글롭을 건너뛰고 그 CSV를 그대로 사용.)

# 생성/백업(자동생성) 파일 판별: 경로에 \obj\ \bin\ 세그먼트가 있거나, 파일명이 생성물 패턴이거나,
# 이름에 복사본/TemporaryGeneratedFile/GeneratedInternalTypeHelper가 들어가면 제외. (대소문자 무시)
function Test-GeneratedOrBackup {
    param([Parameter(Mandatory = $true)][System.IO.FileInfo]$File)
    $lowerPath = $File.FullName.ToLowerInvariant()
    if ($lowerPath -like '*\obj\*') { return $true }
    if ($lowerPath -like '*\bin\*') { return $true }
    $lowerName = $File.Name.ToLowerInvariant()
    if ($lowerName -like '*.g.cs') { return $true }
    if ($lowerName -like '*.g.i.cs') { return $true }
    if ($lowerName -like '*.designer.cs') { return $true }
    if ($lowerName -eq 'assemblyinfo.cs') { return $true }
    if ($lowerName -like '*복사본*') { return $true }
    if ($lowerName -like '*temporarygeneratedfile*') { return $true }
    if ($lowerName -like '*generatedinternaltypehelper*') { return $true }
    return $false
}

$tmpCsv = $null
try {
    if ($FilesFrom) {
        # 정밀 모드: 자동 글롭 없이 준 CSV를 그대로 툴에 전달
        if (-not (Test-Path -LiteralPath $FilesFrom)) { throw "-FilesFrom 없음: $FilesFrom" }
        $csvForTool = (Resolve-Path -LiteralPath $FilesFrom).Path
        Write-Host "정밀 모드      : $csvForTool"
    }
    else {
        # 자동 글롭: 소스 루트 아래 모든 *.cs 재귀 수집 후 생성/백업 제외
        $allCs = @(Get-ChildItem -LiteralPath $root -Recurse -File -Filter *.cs -ErrorAction SilentlyContinue)
        $totalCount = $allCs.Count

        if ($IncludeGenerated) {
            $targets = @($allCs | ForEach-Object { $_.FullName })
            $excludedCount = 0
        }
        else {
            $kept = @($allCs | Where-Object { -not (Test-GeneratedOrBackup -File $_) })
            $targets = @($kept | ForEach-Object { $_.FullName })
            $excludedCount = $totalCount - $targets.Count
        }
        $targetCount = @($targets).Count

        Write-Host ""
        Write-Host "발견 .cs       : $totalCount"
        Write-Host "제외(생성/백업): $excludedCount$(if ($IncludeGenerated) { '  (-IncludeGenerated: 제외 안 함)' })"
        Write-Host "대상           : $targetCount"
        Add-Content -LiteralPath $logPath -Value ("scan | total=$totalCount | excluded=$excludedCount | targeted=$targetCount")

        if ($targetCount -eq 0) {
            Write-Host ""
            Write-Host "대상 .cs 0개 -> 할 일 없음(종료)."
            Write-Host "전체 로그: $logPath"
            exit 0
        }

        # 대상 전체경로를 임시 CSV(--files-from)로 기록. 헤더 1줄 '파일명' + 경로마다 따옴표 감싼 1줄.
        # CSV 이스케이프: 경로 안의 " 는 "" 로. (툴은 BOM 인지 -> UTF-8 no BOM으로 충분)
        $sb = New-Object System.Text.StringBuilder
        [void]$sb.AppendLine('파일명')
        foreach ($t in $targets) {
            [void]$sb.AppendLine('"' + ($t -replace '"', '""') + '"')
        }
        $tmpCsv = Join-Path $env:TEMP ("SparrowCommentFix.files.$stamp.$PID.csv")
        [System.IO.File]::WriteAllText($tmpCsv, $sb.ToString(), (New-Object System.Text.UTF8Encoding($false)))
        $csvForTool = $tmpCsv
        Add-Content -LiteralPath $logPath -Value ("files-from(temp)=$tmpCsv")
    }

    # 3) 규칙별 실행(+ -Commit이면 규칙별 커밋) — Track A/B 동일 패턴.
    #    native(dotnet/git) stderr가 EAP=Stop에서 throw되는 것을 막기 위해 이 구간은 Continue.
    $ErrorActionPreference = 'Continue'
    $failed = $false
    $grand = 0
    $gateReverted = 0
    $gitPathspecFile = $null
    if ($FilesFrom) { $gitPathspecFile = New-GitPathspecFile -Root $root -FilesFromPath $FilesFrom }
    else { $gitPathspecFile = New-GitPathspecFile -Root $root -FilesFromPath $csvForTool }
    if (-not $gitPathspecFile) { throw "대상 소스 루트 아래 .cs pathspec 생성에 실패했습니다." }

    foreach ($r in $Rules) {
        $backupDir = if ($gateActive -and -not $DryRun) { Backup-GitTargets -Root $root -PathspecFile $gitPathspecFile } else { $null }
        $toolArgs = @('--files-from', $csvForTool, '--rules', $r, '--root', $root)
        if ($DryRun) { $toolArgs += '--dry-run' }
        if ($IncludeGenerated) { $toolArgs += '--include-generated' }

        if ($tool.kind -eq 'dll') { $out = & dotnet $tool.path @toolArgs 2>&1 }
        else { $out = & $tool.path @toolArgs 2>&1 }
        $code = $LASTEXITCODE
        $text = ($out | Out-String)

        Add-Content -LiteralPath $logPath -Value ("`n========== $r | exit=$code ==========")
        Add-Content -LiteralPath $logPath -Value $text

        $nChanged = [regex]::Match($text, 'files changed:\s*(\d+)').Groups[1].Value
        $nEdits = [regex]::Match($text, 'rule ' + [regex]::Escape($r) + ':\s*(\d+)').Groups[1].Value
        if ($nEdits) { $grand += [int]$nEdits }

        Write-Host ""
        Write-Host "=== $r  | exit=$code ==="
        Write-Host "  변경 파일 : $(if ($nChanged) { $nChanged } else { '? (로그 확인)' })"
        Write-Host "  수정 건수 : $(if ($nEdits) { $nEdits } else { '0' })"

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
            $res = Invoke-GitCommitStep -Root $root -Message "sparrow(B): $($labels[$r]) (SparrowCommentFix)" -PathspecFile $gitPathspecFile
            switch ($res) {
                'committed' { Write-Host "  커밋      : sparrow(B): $($labels[$r])" }
                'nochange'  { Write-Host "  커밋      : 변경 없음 -> 건너뜀 (이 규칙에서 바뀐 .cs 없음)" }
                'failed'    { Write-Warning "  커밋 실패(git 락 5회 재시도 후에도) - 파일 수정은 유지됨. 나중에 수동 커밋 가능." }
            }
        }
        elseif ($NoCommit) { Write-Host "  커밋      : -NoCommit -> 커밋 안 함 (파일만 수정됨)" }
        else { Write-Host "  커밋      : -Commit 미지정 -> 커밋 안 함 (파일만 수정됨)" }
        if ($backupDir) { Remove-Item -LiteralPath $backupDir -Recurse -Force -ErrorAction SilentlyContinue }
    }
    $ErrorActionPreference = 'Stop'

    Write-Host ""
    if (-not $DryRun) { Write-Host "총 수정 건수(적용된 규칙 합): $grand" }
    if ($gateActive -and $gateReverted -gt 0) { Write-Host "게이트 revert(검증 실패로 되돌리고 커밋 skip한 규칙): $gateReverted" }
    if ($failed) { Write-Host "일부 규칙 미완 -> 로그 확인." }
}
finally {
    if ($tmpCsv -and (Test-Path -LiteralPath $tmpCsv)) {
        Remove-Item -LiteralPath $tmpCsv -Force -ErrorAction SilentlyContinue
    }
}

Write-Host ""
Write-Host "전체 로그: $logPath"
Write-Host "다음(필수): (1) 빌드 통과 확인  (2) 스패로우 재분석으로 해당 체커 건수 감소 확인 (Roslyn 경계 != Sparrow 경계)."
if ($failed) { exit 1 }
