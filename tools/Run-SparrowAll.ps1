#requires -Version 5.1
<#
    원큐 러너 — Track A(SparrowSyntaxFix, Roslyn) + Track B(SparrowCommentFix)를 한 번의 명령으로 순차 실행.

    두 서브러너는 각각 git 하드닝(대상 repo에 gc.auto 0 자동 설정 + 커밋 락 재시도)을 이미 내장하므로,
    기본 옵션으로 그냥 돌려도 Windows "Unlink of file ...pack-*.idx failed" 에러에 멈추지 않는다.

    기본 규칙셋은 이 스킬이 결정론으로 처리 가능한 Track A/B 전 규칙(신규 opt-in forvar/fieldsplit/emptystmt/
    forhoist/blockpromote 포함)이다. 검토필요(review-needed) 규칙은 커밋 메시지에 '!' 로 표시된다.
    (참고: 예전 dotnet-format 기반 러너는 MyApp x64(레거시 비-SDK 프로젝트)에서 무력이라 삭제했고, 지금은 Roslyn 기반 SparrowSyntaxFix 러너를 쓴다.)

    사용:
      .\Run-SparrowAll.ps1 -Solution ...\MyApp.sln -Commit     # A→B 전 규칙, 규칙별 자동 커밋
      .\Run-SparrowAll.ps1 -Solution ...\MyApp.sln -NoCommit   # A→B 전 규칙, 파일만 수정
      .\Run-SparrowAll.ps1 -Solution ...\src -DryRun            # 변경 미리보기(파일 안 건드림)
      .\Run-SparrowAll.ps1 -Solution ...\src                    # 적용만(커밋 여부는 각 러너가 물음)
      # 규칙 좁히기:
      .\Run-SparrowAll.ps1 -Solution ... -SyntaxRules forhoist,forvar -CommentRules blockpromote -Commit
      # 규칙별 커밋 전 컴파일 게이트(-Commit 조합): 각 규칙 edits 후·커밋 전 이 명령을 돌려 실패하면 그 규칙만 revert:
      .\Run-SparrowAll.ps1 -Solution ...\MyApp.sln -Commit -VerifyCmd '"C:\Program Files\...\msbuild.exe" C:\Work\MyApp\MyApp.sln /t:Build'
#>
param(
    [string]$Solution,
    [switch]$Commit,
    [switch]$NoCommit,
    [switch]$DryRun,
    # Track A(SparrowSyntaxFix) 규칙 — 안전 기본(obviousvar/objectvar-safe/parens/foreachcast) + opt-in(forvar/fieldsplit/emptystmt/forhoist)
    [string]$SyntaxRules = 'obviousvar,objectvar-safe,parens,foreachcast,forvar,fieldsplit,emptystmt,forhoist',
    # Track B(SparrowCommentFix) 규칙 — 전 규칙(continuation 깊이정규화·blockpromote opt-in 포함)
    [string]$CommentRules = 'flatten,trailing,space,period,capitalize,memberblank,onestatement,onedeclaration,continuation,linqalign,blockpromote',
    # 규칙별 커밋 전 컴파일 게이트 명령(선택). 예: '"C:\path\msbuild.exe" C:\Work\MyApp\MyApp.sln /t:Build'
    # 주면 두 서브러너에 그대로 전달 — 각 규칙 edits 후·커밋 전 실행, 비정상 종료 시 그 규칙 edits를 revert하고
    # 커밋을 건너뛴다(-Commit 조합 시). 안 주면 게이트 없음(커밋 후 전체 빌드로 반드시 확인).
    [string]$VerifyCmd
)

trap {
    $message = if ($_.Exception) { $_.Exception.Message } else { ($_ | Out-String).Trim() }
    Write-Host ""
    Write-Host "[FATAL] Run-SparrowAll 중단: $message" -ForegroundColor Red
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
$here = $PSScriptRoot
$syntaxRunner  = Join-Path $here '_internal\SparrowSyntaxFix\Run-SparrowSyntaxFix.ps1'
$commentRunner = Join-Path $here '_internal\SparrowCommentFix\Run-SparrowCommentFix.ps1'
foreach ($p in @($syntaxRunner, $commentRunner)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "서브러너를 찾을 수 없습니다: $p" }
}

# -Commit / -DryRun / -VerifyCmd 를 서브러너로 전달(둘 다 없으면 각 러너가 커밋 여부를 물음).
$pass = @{}
if ($Commit) { $pass['Commit'] = $true }
if ($NoCommit) { $pass['NoCommit'] = $true }
if ($DryRun) { $pass['DryRun'] = $true }
if ($VerifyCmd) { $pass['VerifyCmd'] = $VerifyCmd }

$syntaxList  = @($SyntaxRules  -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$commentList = @($CommentRules -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })

# 서브러너 종료코드를 각각 붙잡아 전파한다(예전엔 통째로 삼켜 항상 0으로 끝나 실패가 묻혔다).
# 첫 트랙이 실패해도 두 번째 트랙은 계속 실행하고(둘 다 보고), 마지막에 하나라도 실패면 nonzero로 종료.
$failedTracks = @()
$firstFailExit = 0

Write-Host "==================== 원큐: TRACK A (SparrowSyntaxFix) ===================="
Write-Host "  규칙: $($syntaxList -join ',')"
& $syntaxRunner -Solution $Solution -Rules $syntaxList @pass
$trackAExit = $LASTEXITCODE
if ($null -eq $trackAExit) { $trackAExit = 0 }
Write-Host "  [Track A 종료코드] $trackAExit"
if ($trackAExit -ne 0) {
    $failedTracks += "A(SparrowSyntaxFix) exit=$trackAExit"
    if ($firstFailExit -eq 0) { $firstFailExit = $trackAExit }
}

Write-Host ""
Write-Host "==================== 원큐: TRACK B (SparrowCommentFix) ===================="
Write-Host "  규칙: $($commentList -join ',')"
& $commentRunner -Solution $Solution -Rules $commentList @pass
$trackBExit = $LASTEXITCODE
if ($null -eq $trackBExit) { $trackBExit = 0 }
Write-Host "  [Track B 종료코드] $trackBExit"
if ($trackBExit -ne 0) {
    $failedTracks += "B(SparrowCommentFix) exit=$trackBExit"
    if ($firstFailExit -eq 0) { $firstFailExit = $trackBExit }
}

Write-Host ""
if ($failedTracks.Count -gt 0) {
    Write-Host "==================== 원큐 실패 ====================" -ForegroundColor Red
    Write-Host "실패 트랙: $($failedTracks -join ' / ')" -ForegroundColor Red
    Write-Host "해당 트랙의 로그(Run-Sparrow*.log)를 확인하세요. (원큐 종료코드 $firstFailExit)"
    exit $firstFailExit
}

Write-Host "원큐 완료 (A→B). 다음(필수): (1) 빌드 통과 확인  (2) Sparrow 재분석으로 검출 감소 실측."
Write-Host "참고: Track C(로직) 작업 후에는 이 원큐를 한 번 더 돌려 C가 새로 만든 형식 검출을 정리하세요."
