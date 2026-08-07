#requires -Version 5.1
<#
    coretests-run.ps1 — tools\_internal\SparrowXlsExport.Core\CoreTests 를 validate 게이트에 태우는 얇은 러너.

    CoreTests 자체는 .NET 콘솔 하네스다. Core 출력 계약을 단정한다:
      항목 md 필드표 / 출력 레이아웃(= 체커폴더 + md, 그 외 부산물 0) / --files-from 스코프 필터 /
      크로스PC 상대꼬리 매칭 / Core.Run == 콘솔 파싱 바이트 동일.
    "[XLS 분리] 부산물 0" 의 가장 강한 단정이 여기 있는데 호출자가 하나도 없어서 PR 게이트 밖에 있었다 → 편입.

    합성 픽스처만 쓴다(--fixtures-only). 실 xls 를 인자로 넘기면 CoreTests 가 그 경로를 stdout 에 찍으므로
    (사내 파일명 유출) 여기서는 절대 넘기지 않는다.

    .NET SDK 가 없으면 SKIP(실패 아님). 종료 코드: 0=통과, 1=실패.
    직접 실행: tests\coretests-run.ps1
    validate:  validate.ps1 -IncludeCoreTests   (또는 -All)
#>
param([string]$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path)

$ErrorActionPreference = 'Stop'

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Host "dotnet SDK not found; skipping CoreTests."
    $global:SparrowTestSkip = "dotnet SDK 없음"
    return
}

$proj = Join-Path $RepositoryRoot 'tools\_internal\SparrowXlsExport.Core\CoreTests\CoreTests.csproj'
if (-not (Test-Path -LiteralPath $proj)) { throw "missing project: $proj" }
$dll = Join-Path $RepositoryRoot 'tools\_internal\SparrowXlsExport.Core\CoreTests\bin\Release\net8.0\CoreTests.dll'

Write-Host "  building CoreTests (Release)..."
$prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
try {
    & $dotnet.Source build $proj -c Release --nologo -v q 2>&1 | Out-Null
    $buildExit = $LASTEXITCODE
}
finally { $ErrorActionPreference = $prev }
if ($buildExit -ne 0) { throw "CoreTests build failed (exit $buildExit)" }
if (-not (Test-Path -LiteralPath $dll)) { throw "build produced no dll: $dll" }

$prev = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
try {
    & $dotnet.Source $dll '--fixtures-only'
    $runExit = $LASTEXITCODE
}
finally { $ErrorActionPreference = $prev }

# validate.ps1 신호 규약: 성공은 반드시 exit 0, 실패는 exit 1 (또는 throw).
if ($runExit -ne 0) {
    Write-Host ("CoreTests FAILED (exit {0})." -f $runExit)
    exit 1
}
Write-Host "CoreTests passed."
exit 0
