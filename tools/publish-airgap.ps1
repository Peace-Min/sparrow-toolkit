#requires -Version 5.1
<#
    publish-airgap.ps1 — 폐쇄망(오프라인/air-gapped) 반입 번들 빌더.

    인터넷 + .NET SDK가 있는 PC에서 한 번 실행해, sparrow-toolkit 도구 4종을 미리 발행(publish)한다.
    발행 산출물을 skill 폴더째 폐쇄망 PC로 복사하면, 대상 PC에 .NET SDK나 NuGet 복원 없이도
    GUI/러너가 그대로 돈다(= `dotnet run`/`dotnet build`가 필요 없어짐).

    발행 대상(각각 자기 프로젝트 폴더의 publish\ 아래):
      1) _internal\SparrowSyntaxFix   -> _internal\SparrowSyntaxFix\publish\SparrowSyntaxFix.exe
                                         (Run-SparrowSyntaxFix.ps1 의 fallback이 이 경로를 찾음)
      2) _internal\SparrowCommentFix  -> _internal\SparrowCommentFix\publish\SparrowCommentFix.exe
                                         (Run-SparrowCommentFix.ps1 의 fallback이 이 경로를 찾음)
      3) _internal\SparrowXlsExport   -> _internal\SparrowXlsExport\publish\SparrowXlsExport.exe  (XLS 분리 CLI)
      4) SparrowRunner.Gui            -> SparrowRunner.Gui\publish\SparrowRunner.Gui.exe          (통합 WPF GUI)

    기본은 self-contained(win-x64): 대상 PC에 .NET 런타임이 아예 없어도 됨(런타임 동봉).
    -FrameworkDependent: 산출물 크기를 줄이는 대신 대상 PC에 .NET 8 런타임이 필요.
      - GUI: .NET 8 Desktop Runtime (WPF)
      - CLI 3종: .NET 8 Runtime

    사용:
      .\publish-airgap.ps1                 # self-contained win-x64 로 4종 발행(권장, 폐쇄망 무설치)
      .\publish-airgap.ps1 -FrameworkDependent   # 크기 축소(대상 PC에 .NET 8 런타임 필요)
      .\publish-airgap.ps1 -DryRun         # 무엇을 어디에 발행할지만 출력하고 종료(빌드 안 함)

    산출물(publish\)은 머신마다 생성되는 것이라 커밋하지 않는다(.gitignore 로 제외).
#>
param(
    [switch]$FrameworkDependent,
    [switch]$DryRun,
    [string]$Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'

# 콘솔 인코딩(한글 출력). 실패해도 본동작을 막지 않는다.
try {
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [Console]::OutputEncoding = $utf8NoBom
    $OutputEncoding = $utf8NoBom
}
catch {
}

# $PSScriptRoot 가 비어 있는 호출 경로도 방어
$scriptDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }

$selfContained = -not $FrameworkDependent
$scMode = if ($selfContained) { 'true' } else { 'false' }

# 발행 대상 4종. Exe = 발행 후 존재 확인용 실행 파일명(CLI는 러너 fallback 검증에 쓰임).
$projects = @(
    [pscustomobject]@{ Name = 'SparrowSyntaxFix';  Kind = 'CLI(코드 규칙)';    Csproj = (Join-Path $scriptDir '_internal\SparrowSyntaxFix\SparrowSyntaxFix.csproj');   OutDir = (Join-Path $scriptDir '_internal\SparrowSyntaxFix\publish');   Exe = 'SparrowSyntaxFix.exe' }
    [pscustomobject]@{ Name = 'SparrowCommentFix'; Kind = 'CLI(주석·레이아웃)'; Csproj = (Join-Path $scriptDir '_internal\SparrowCommentFix\SparrowCommentFix.csproj'); OutDir = (Join-Path $scriptDir '_internal\SparrowCommentFix\publish'); Exe = 'SparrowCommentFix.exe' }
    [pscustomobject]@{ Name = 'SparrowXlsExport';  Kind = 'CLI(XLS 분리)';     Csproj = (Join-Path $scriptDir '_internal\SparrowXlsExport\SparrowXlsExport.csproj');   OutDir = (Join-Path $scriptDir '_internal\SparrowXlsExport\publish');   Exe = 'SparrowXlsExport.exe' }
    [pscustomobject]@{ Name = 'SparrowRunner.Gui'; Kind = 'WPF GUI';            Csproj = (Join-Path $scriptDir 'SparrowRunner.Gui\SparrowRunner.Gui.csproj');            OutDir = (Join-Path $scriptDir 'SparrowRunner.Gui\publish');           Exe = 'SparrowRunner.Gui.exe' }
)

$modeText = if ($selfContained) { "self-contained ($Runtime, 런타임 동봉 - 대상 PC 무설치)" } else { "framework-dependent ($Runtime, 대상 PC에 .NET 8 런타임 필요)" }

Write-Host "==================== sparrow-toolkit 폐쇄망 반입 발행 ===================="
Write-Host "모드      : $modeText"
Write-Host "발행 대상 : $($projects.Count)개 프로젝트"
Write-Host ""

# --- DryRun: 계획만 출력하고 종료(빌드 안 함, exit 0) ---
if ($DryRun) {
    Write-Host "[DryRun] 아래 명령을 실행할 예정입니다(실제 빌드는 하지 않음):"
    Write-Host ""
    foreach ($p in $projects) {
        Write-Host ("  [{0}] {1}" -f $p.Kind, $p.Name)
        Write-Host ("     csproj : {0}" -f $p.Csproj)
        Write-Host ("     ->     : {0}" -f $p.OutDir)
        Write-Host ("     cmd    : dotnet publish `"{0}`" -c Release -r {1} --self-contained {2} /p:PublishSingleFile=false -o `"{3}`"" -f $p.Csproj, $Runtime, $scMode, $p.OutDir)
        if (-not (Test-Path -LiteralPath $p.Csproj)) {
            Write-Warning ("     (주의) csproj 없음: {0}" -f $p.Csproj)
        }
        Write-Host ""
    }
    $dn = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($dn) { Write-Host "dotnet    : 발견됨 ($($dn.Source))" }
    else { Write-Warning "dotnet    : 이 PC에서 dotnet SDK를 찾지 못했습니다. 실제 발행은 SDK가 있는 인터넷 PC에서 하세요." }
    Write-Host ""
    Write-Host "[DryRun] 종료(exit 0). 실제 발행하려면 -DryRun 없이 다시 실행하세요."
    exit 0
}

# --- 실제 발행: dotnet 필수 ---
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    throw "dotnet SDK를 찾을 수 없습니다. 인터넷 + .NET 8 이상 SDK가 있는 PC에서 실행하세요(폐쇄망 대상 PC가 아니라 발행 PC에서)."
}
Write-Host "dotnet    : $($dotnet.Source)"
Write-Host ""

$results = @()
foreach ($p in $projects) {
    Write-Host "-------------------------------------------------------------------------------"
    Write-Host ("발행: [{0}] {1}" -f $p.Kind, $p.Name)
    Write-Host ("  csproj : {0}" -f $p.Csproj)
    Write-Host ("  ->     : {0}" -f $p.OutDir)

    if (-not (Test-Path -LiteralPath $p.Csproj)) {
        Write-Warning ("  실패: csproj 없음 -> {0}" -f $p.Csproj)
        $results += [pscustomobject]@{ Name = $p.Name; Ok = $false; Reason = 'csproj 없음'; ExePath = $null; Bytes = 0 }
        continue
    }

    # 이전 발행 잔여물 제거(부분 실패 산출물이 성공처럼 보이는 것 방지)
    if (Test-Path -LiteralPath $p.OutDir) {
        Remove-Item -LiteralPath $p.OutDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    # 네이티브(dotnet) 호출: stderr가 EAP=Stop+2>&1 에서 종료오류로 throw되는 것을 막기 위해 Continue로 격리.
    $prevEap = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & dotnet publish $p.Csproj -c Release -r $Runtime --self-contained $scMode /p:PublishSingleFile=false -o $p.OutDir --nologo 2>&1 |
            ForEach-Object { Write-Host "  | $_" }
        $publishExit = $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $prevEap }

    $exePath = Join-Path $p.OutDir $p.Exe
    if ($publishExit -eq 0 -and (Test-Path -LiteralPath $exePath)) {
        $bytes = (Get-Item -LiteralPath $exePath).Length
        Write-Host ("  성공: {0} ({1:N0} bytes)" -f $exePath, $bytes)
        $results += [pscustomobject]@{ Name = $p.Name; Ok = $true; Reason = $null; ExePath = $exePath; Bytes = $bytes }
    }
    elseif ($publishExit -eq 0) {
        Write-Warning ("  실패: publish는 exit 0이나 예상 실행파일이 없음 -> {0}" -f $exePath)
        $results += [pscustomobject]@{ Name = $p.Name; Ok = $false; Reason = "exe 미생성(exit 0)"; ExePath = $exePath; Bytes = 0 }
    }
    else {
        Write-Warning ("  실패: dotnet publish exit={0} ({1})" -f $publishExit, $p.Name)
        $results += [pscustomobject]@{ Name = $p.Name; Ok = $false; Reason = "publish exit=$publishExit"; ExePath = $exePath; Bytes = 0 }
    }
    Write-Host ""
}

# --- 요약 ---
$failed = @($results | Where-Object { -not $_.Ok })
$okCount = @($results | Where-Object { $_.Ok }).Count

Write-Host "==================== 발행 요약 ===================="
foreach ($r in $results) {
    if ($r.Ok) { Write-Host ("  [OK]   {0,-20} {1:N0} bytes  {2}" -f $r.Name, $r.Bytes, $r.ExePath) }
    else { Write-Host ("  [FAIL] {0,-20} {1}" -f $r.Name, $r.Reason) }
}
Write-Host ("  성공 {0} / {1}" -f $okCount, $results.Count)
Write-Host ""

if ($failed.Count -gt 0) {
    Write-Host ("일부 프로젝트 발행 실패 -> 위 로그 확인. 폐쇄망 반입 전에 반드시 {0}종 모두 성공시켜야 합니다." -f $projects.Count) -ForegroundColor Red
    exit 1
}

# --- 반입 체크리스트(성공 시) ---
Write-Host "==================== 폐쇄망 반입 체크리스트 ===================="
Write-Host "1) 복사할 것: 이 레포(sparrow-toolkit) 폴더 트리 전체"
Write-Host ("   - 방금 생성된 publish\ 산출물 {0}곳:" -f $projects.Count)
foreach ($p in $projects) { Write-Host ("       {0}" -f $p.OutDir) }
Write-Host "   - (선행 문서 불필요: [XLS 분리] 익스포터는 Sparrow xls 하나만 입력으로 받습니다)"
Write-Host "   - tools\Run-SparrowRunnerGui.cmd, tools\Run-SparrowAll.cmd, tools\_internal\...\Run-*.ps1 (러너/진입점)"
Write-Host ""
if ($selfContained) {
    Write-Host "2) 대상 PC 런타임: 불필요. self-contained 발행이라 .NET SDK/런타임 설치가 필요 없습니다."
}
else {
    Write-Host "2) 대상 PC 런타임(필수 - framework-dependent 로 발행함):"
    Write-Host "   - GUI(SparrowRunner.Gui): .NET 8 Desktop Runtime (WPF)"
    Write-Host "   - CLI 3종: .NET 8 Runtime"
}
Write-Host ""
Write-Host "3) 폐쇄망에서 실행:"
Write-Host "   - GUI    : tools\Run-SparrowRunnerGui.cmd  (publish\SparrowRunner.Gui.exe 를 자동 사용)"
Write-Host "   - 러너는 publish\SparrowSyntaxFix.exe / publish\SparrowCommentFix.exe 를 자동으로 집어 씁니다"
Write-Host "     (dotnet build/복원 불필요). Windows 기본 powershell.exe 만 있으면 됩니다."
Write-Host ""
Write-Host "발행 완료."
exit 0
