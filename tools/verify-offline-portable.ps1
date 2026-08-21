#requires -Version 5.1
param(
    [Parameter(Mandatory = $true)]
    [string]$BundleRoot,
    [switch]$SkipManifest,
    [switch]$SkipClangExecution
)

$ErrorActionPreference = 'Stop'

try {
    $resolvedRoot = (Resolve-Path -LiteralPath $BundleRoot).Path
    $root = [System.IO.Path]::GetFullPath($resolvedRoot).TrimEnd('\')

    $required = @(
        'SKILL.md',
        'LICENSE',
        'README-OFFLINE.txt',
        'Start-SparrowHelper.cmd',
        'tools\Run-SparrowRunnerGui.cmd',
        'tools\SparrowRunner.Gui\publish\SparrowRunner.Gui.exe',
        'tools\SparrowRunner.Gui\publish\coreclr.dll',
        'tools\SparrowRunner.Gui\publish\hostfxr.dll',
        'tools\SparrowRunner.Gui\publish\PresentationFramework.dll',
        'tools\SparrowRunner.Gui\publish\wpfgfx_cor3.dll',
        'tools\SparrowRunner.Gui\publish\clang\clang.exe',
        'tools\SparrowRunner.Gui\publish\clang\clang-format.exe',
        'tools\SparrowRunner.Gui\publish\clang-analyzer\Sparrow.ClangAnalyzer.exe',
        'tools\SparrowRunner.Gui\publish\clang-analyzer\coreclr.dll',
        'tools\SparrowRunner.Gui\publish\lib\clang\20\include\stddef.h',
        'tools\SparrowRunner.Gui\publish\licenses\LLVM-LICENSE.txt',
        'tools\SparrowRunner.Gui\publish\licenses\THIRD-PARTY-NOTICES.txt',
        'tools\_internal\SparrowSyntaxFix\Run-SparrowSyntaxFix.ps1',
        'tools\_internal\SparrowSyntaxFix\publish\SparrowSyntaxFix.exe',
        'tools\_internal\SparrowSyntaxFix\publish\coreclr.dll',
        'tools\_internal\SparrowCommentFix\Run-SparrowCommentFix.ps1',
        'tools\_internal\SparrowCommentFix\publish\SparrowCommentFix.exe',
        'tools\_internal\SparrowCommentFix\publish\coreclr.dll',
        'tools\_internal\SparrowXlsExport\publish\SparrowXlsExport.exe',
        'tools\_internal\SparrowXlsExport\publish\coreclr.dll',
        'licenses\SPARROW-THIRD-PARTY-NOTICES.txt'
    )

    $missing = @()
    foreach ($relative in $required) {
        if (-not (Test-Path -LiteralPath (Join-Path $root $relative) -PathType Leaf)) {
            $missing += $relative
        }
    }
    if ($missing.Count -gt 0) {
        throw "필수 Portable 파일이 없습니다:`n - $($missing -join "`n - ")"
    }

    $unexpectedRuntimes = @()
    Get-ChildItem -LiteralPath $root -Directory -Recurse -ErrorAction SilentlyContinue |
        Where-Object { $_.Parent -and $_.Parent.Name -eq 'runtimes' -and $_.Name -notin @('win', 'win-x64') } |
        ForEach-Object { $unexpectedRuntimes += $_.FullName.Substring($root.Length + 1) }
    if ($unexpectedRuntimes.Count -gt 0) {
        throw "Windows x64 Portable에 불필요한 RID 폴더가 포함되었습니다: $($unexpectedRuntimes -join ', ')"
    }

    if (-not $SkipClangExecution) {
        $clangPath = Join-Path $root 'tools\SparrowRunner.Gui\publish\clang\clang.exe'
        $clangOutput = & $clangPath --version 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "번들 Clang 실행 실패(exit=$LASTEXITCODE): $($clangOutput -join ' ')"
        }
        $clangText = ($clangOutput -join "`n")
        if ($clangText -notmatch 'clang version 20\.1\.0') {
            throw "승인된 Clang 20.1.0이 아닙니다: $($clangOutput | Select-Object -First 1)"
        }

        $approvedClangHash = 'F9BD9C90FE5AFEA4929721D80700BD989B9852D1E18D341ADCE8992A71C2F31E'
        $approvedFormatHash = 'B2DA772E065439062A23BA51AB3A34510803FA0196A1DC871DD32280699A55DF'
        $actualClangHash = (Get-FileHash -LiteralPath $clangPath -Algorithm SHA256).Hash
        $formatPath = Join-Path $root 'tools\SparrowRunner.Gui\publish\clang\clang-format.exe'
        $actualFormatHash = (Get-FileHash -LiteralPath $formatPath -Algorithm SHA256).Hash
        if ($actualClangHash -ne $approvedClangHash -or $actualFormatHash -ne $approvedFormatHash) {
            throw '번들 Clang/clang-format SHA-256이 승인된 LLVM 20.1.0 값과 다릅니다.'
        }
    }

    $manifestPath = Join-Path $root 'SHA256SUMS.txt'
    if (-not $SkipManifest) {
        if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
            throw 'SHA256SUMS.txt가 없습니다.'
        }
        $bad = @()
        foreach ($line in [System.IO.File]::ReadAllLines($manifestPath)) {
            if ([string]::IsNullOrWhiteSpace($line)) { continue }
            if ($line -notmatch '^([0-9A-Fa-f]{64})  (.+)$') {
                $bad += "형식 오류: $line"
                continue
            }
            $expected = $matches[1].ToUpperInvariant()
            $relative = $matches[2].Replace('/', '\')
            $file = Join-Path $root $relative
            if (-not (Test-Path -LiteralPath $file -PathType Leaf)) {
                $bad += "누락: $relative"
                continue
            }
            $actual = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
            if ($actual -ne $expected) { $bad += "해시 불일치: $relative" }
        }
        if ($bad.Count -gt 0) {
            throw "Portable manifest 검증 실패:`n - $($bad -join "`n - ")"
        }
    }

    $size = (Get-ChildItem -LiteralPath $root -File -Recurse | Measure-Object -Property Length -Sum).Sum
    $count = (Get-ChildItem -LiteralPath $root -File -Recurse | Measure-Object).Count
    Write-Host ("[OK] Portable 검증 완료: {0} files, {1:N2} MiB" -f $count, ($size / 1MB)) -ForegroundColor Green
    Write-Host "     $root"
    exit 0
}
catch {
    Write-Host "[FAIL] Portable 검증 실패: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
