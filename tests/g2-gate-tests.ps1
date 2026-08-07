#requires -Version 5.1
<#
    g2-gate-tests.ps1 — G2 게이트(`tools\Compare-Sparrow.ps1`) 시나리오 검증.

    gen-xls --scenarios 로 합성 xls 3쌍을 만들어 게이트 의미론을 검사한다:
      1) 라인시프트  : 건수 그대로/감소 → 가짜 신규 없이 PASS
      2) 진짜 회귀   : (체커,전체경로) 건수 증가 → FAIL + 증가쌍을 전체경로로 나열
      3) 스캔 위생   : before/after 경로 집합 불일치 → 기본은 경고만(PASS), -StrictScope 는 FAIL

    (Track C 판정 패키징 레이어를 걷어내면서 그 픽스처 검증 스크립트의 G2 구간만 분리해 옮긴 것.)
    .NET SDK 가 필요하다. 없으면 SKIP(실패 아님).
    각 검사 PASS/FAIL 출력, 하나라도 실패면 exit 1.
#>
param([string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path)

$ErrorActionPreference = 'Stop'

$compare = Join-Path $RepositoryRoot 'tools\Compare-Sparrow.ps1'
$genProj = Join-Path $RepositoryRoot 'tests\e2e-lab\gen-xls\gen-xls.csproj'
$parserProj = Join-Path $RepositoryRoot 'tools\_internal\SparrowXlsExport\SparrowXlsExport.csproj'
$parserExe = Join-Path $RepositoryRoot 'tools\_internal\SparrowXlsExport\bin\Release\net8.0\SparrowXlsExport.exe'

$script:fails = 0
function Assert($cond, $msg) {
    if ($cond) { Write-Host "  PASS  $msg" } else { Write-Host "  FAIL  $msg"; $script:fails++ }
}

Write-Host "== G2 게이트 시나리오 검증 =="
Assert (Test-Path -LiteralPath $compare) "Compare-Sparrow.ps1 존재: $compare"
Assert (Test-Path -LiteralPath $genProj) "gen-xls.csproj 존재: $genProj"

$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    Write-Host "  SKIP  dotnet SDK 없음 — G2 시나리오 검사를 건너뜁니다."
    # 스킵 신호는 위 Test-Path 단정이 전부 통과했을 때만 올린다. 하나라도 깨졌으면 그건 스킵이 아니라
    # 진짜 실패이므로 exit 1 로만 나가야 한다(스킵 마커가 붙으면 부모가 실패를 스킵으로 삼킨다).
    if ($script:fails -eq 0) { $global:SparrowTestSkip = "dotnet SDK 없음"; exit 0 } else { exit 1 }
}

$work = Join-Path ([System.IO.Path]::GetTempPath()) ('g2-gate-' + [System.IO.Path]::GetRandomFileName())
New-Item -ItemType Directory -Force -Path $work | Out-Null

$prevEap = $ErrorActionPreference
$ErrorActionPreference = 'Continue'
try {
    # 파서는 항상 소스에서 재빌드한 bin exe 사용(오래된 publish exe가 경로 컬럼을 모를 수 있음).
    & $dotnetCmd.Source build $parserProj -c Release --nologo -v q 2>&1 | Out-Null
    Assert (Test-Path -LiteralPath $parserExe) "SparrowXlsExport.exe 빌드됨: $parserExe"

    $scenDir = Join-Path $work 'g2-scenarios'
    & $dotnetCmd.Source run -c Release --project $genProj -- $scenDir --scenarios 2>&1 | Out-Null
    Assert ($LASTEXITCODE -eq 0) "gen-xls --scenarios exit 0"
    Assert (Test-Path -LiteralPath (Join-Path $scenDir 'lineshift-before.xls')) "시나리오 xls 생성됨"

    function Invoke-Compare([string[]]$CmpArgs) {
        $o = & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $compare @CmpArgs -Exe $parserExe 2>&1 | Out-String
        return [pscustomobject]@{ Exit = $LASTEXITCODE; Out = $o }
    }

    # 1) 라인 시프트: 해소 1건 + 라인만 밀린 1건 → 가짜 신규 없이 PASS.
    $r1 = Invoke-Compare @('-Before', (Join-Path $scenDir 'lineshift-before.xls'), '-After', (Join-Path $scenDir 'lineshift-after.xls'))
    Assert ($r1.Exit -eq 0) "G2 라인시프트: PASS(exit 0) — 라인 이동은 신규 아님"
    Assert ($r1.Out -match '결과: PASS') "G2 라인시프트: '결과: PASS' 출력"

    # 2) 진짜 회귀: (체커,전체경로) 건수 증가 → FAIL + 증가쌍(전체경로) 나열.
    $r2 = Invoke-Compare @('-Before', (Join-Path $scenDir 'regress-before.xls'), '-After', (Join-Path $scenDir 'regress-after.xls'))
    Assert ($r2.Exit -eq 1) "G2 진짜회귀: FAIL(exit 1)"
    Assert ($r2.Out -match 'src/App/Fresh\.cs') "G2 진짜회귀: 증가쌍이 전체경로로 나열됨"

    # 3) 스캔 스코프 불일치: 기본은 경고+판정미변경(PASS), -StrictScope 는 FAIL.
    $r3 = Invoke-Compare @('-Before', (Join-Path $scenDir 'scope-before.xls'), '-After', (Join-Path $scenDir 'scope-after.xls'))
    Assert ($r3.Exit -eq 0) "G2 스코프불일치: 기본 PASS(경고만)"
    # 한글 match 는 중첩 powershell 캡처의 콘솔 코드페이지에 따라 깨질 수 있어 ASCII 배너(####…)도 인정.
    Assert (($r3.Out -match '스캔 위생 경고') -or ($r3.Out -match '#{40,}')) "G2 스코프불일치: 위생 경고 출력"
    $r4 = Invoke-Compare @('-Before', (Join-Path $scenDir 'scope-before.xls'), '-After', (Join-Path $scenDir 'scope-after.xls'), '-StrictScope')
    Assert ($r4.Exit -eq 1) "G2 스코프불일치: -StrictScope 는 FAIL(exit 1)"
}
finally {
    $ErrorActionPreference = $prevEap
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
if ($script:fails -eq 0) { Write-Host "== G2 게이트 전체 PASS =="; exit 0 }
else { Write-Host ("== G2 게이트 실패 {0} 건 ==" -f $script:fails); exit 1 }
