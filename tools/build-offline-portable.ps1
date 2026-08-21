#requires -Version 5.1
<#
  Build a private/offline, self-contained Windows x64 portable folder and ZIP.

  Typical use (internet/build PC with .NET 8 SDK + approved LLVM 20.1.0 installed):
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build-offline-portable.ps1

  Re-stage already published self-contained outputs without rebuilding:
    powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\build-offline-portable.ps1 -SkipPublish

  Generated output is under out\ and is ignored by Git.  No commit or push is performed.
#>
param(
    [string]$Runtime = 'win-x64',
    [string]$OutputRoot,
    [switch]$SkipPublish,
    [switch]$SkipZip,
    [switch]$SkipZipExtractionTest,
    [switch]$NoPrune,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

function Assert-ChildPath {
    param([string]$Parent, [string]$Child)
    $parentFull = [System.IO.Path]::GetFullPath($Parent).TrimEnd('\') + '\'
    $childFull = [System.IO.Path]::GetFullPath($Child).TrimEnd('\') + '\'
    if (-not $childFull.StartsWith($parentFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "안전하지 않은 출력 경로입니다. '$Child'는 '$Parent' 아래여야 합니다."
    }
}

function Ensure-Directory {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path -PathType Container)) {
        [void](New-Item -ItemType Directory -Path $Path -Force)
    }
}

function Copy-RequiredFile {
    param([string]$Source, [string]$Destination)
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "복사할 필수 파일이 없습니다: $Source"
    }
    Ensure-Directory (Split-Path -Parent $Destination)
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Copy-PublishTree {
    param([string]$Source, [string]$Destination, [bool]$Prune)
    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "publish 폴더가 없습니다: $Source"
    }
    $sourceFull = [System.IO.Path]::GetFullPath($Source).TrimEnd('\')
    Ensure-Directory $Destination
    foreach ($file in Get-ChildItem -LiteralPath $sourceFull -File -Recurse) {
        $relative = $file.FullName.Substring($sourceFull.Length + 1)
        if ($Prune) {
            if ($file.Extension -ieq '.pdb') { continue }
            if ($relative -match '^runtimes\\([^\\]+)\\') {
                $rid = $matches[1]
                if ($rid -notin @('win', 'win-x64')) { continue }
            }
        }
        $target = Join-Path $Destination $relative
        Ensure-Directory (Split-Path -Parent $target)
        Copy-Item -LiteralPath $file.FullName -Destination $target -Force
    }
}

function Copy-NuGetNotices {
    param([string[]]$AssetsFiles, [string]$Destination)
    Ensure-Directory $Destination
    $packages = @{}
    foreach ($assetsFile in $AssetsFiles) {
        if (-not (Test-Path -LiteralPath $assetsFile -PathType Leaf)) { continue }
        $assets = Get-Content -LiteralPath $assetsFile -Raw | ConvertFrom-Json
        $packageFolders = @($assets.packageFolders.PSObject.Properties | ForEach-Object { $_.Name })
        foreach ($libraryProperty in $assets.libraries.PSObject.Properties) {
            $library = $libraryProperty.Value
            if ($library.type -ne 'package') { continue }
            $key = $libraryProperty.Name
            if (-not $packages.ContainsKey($key)) {
                $packages[$key] = [pscustomobject]@{
                    Key = $key
                    RelativePath = [string]$library.path
                    Folders = $packageFolders
                }
            }
        }
    }

    $manifest = New-Object System.Collections.Generic.List[string]
    $manifest.Add('NuGet package license manifest')
    $manifest.Add('==============================')
    $manifest.Add('Generated from project.assets.json. Review upstream terms before external distribution.')
    $manifest.Add('')

    foreach ($entry in @($packages.Values | Sort-Object Key)) {
        $packageRoot = $null
        foreach ($folder in $entry.Folders) {
            $candidate = Join-Path $folder $entry.RelativePath
            if (Test-Path -LiteralPath $candidate -PathType Container) {
                $packageRoot = $candidate
                break
            }
        }
        if (-not $packageRoot) {
            $manifest.Add("$($entry.Key) | restored package folder not found")
            continue
        }

        $safeName = ($entry.Key -replace '[\\/:*?""<>|]', '_')
        $packageDestination = Join-Path $Destination $safeName
        $licenseFiles = @(Get-ChildItem -LiteralPath $packageRoot -File -Recurse | Where-Object {
            $_.Name -match '(?i)^(license|licence|notice|copying|third[-_. ]?party|osmfeula)(\..*)?$'
        })
        foreach ($licenseFile in $licenseFiles) {
            $relative = $licenseFile.FullName.Substring($packageRoot.TrimEnd('\').Length + 1)
            $target = Join-Path $packageDestination $relative
            Ensure-Directory (Split-Path -Parent $target)
            Copy-Item -LiteralPath $licenseFile.FullName -Destination $target -Force
        }

        $nuspec = Get-ChildItem -LiteralPath $packageRoot -Filter '*.nuspec' -File | Select-Object -First 1
        $licenseText = 'not declared in nuspec'
        $projectUrl = ''
        if ($nuspec) {
            [xml]$nuspecXml = Get-Content -LiteralPath $nuspec.FullName -Raw
            $licenseNode = $nuspecXml.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='license']")
            $licenseUrlNode = $nuspecXml.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='licenseUrl']")
            $projectUrlNode = $nuspecXml.SelectSingleNode("//*[local-name()='metadata']/*[local-name()='projectUrl']")
            if ($licenseNode) {
                $kind = if ($licenseNode.type) { [string]$licenseNode.type } else { 'value' }
                $licenseText = "$kind=$($licenseNode.InnerText)"
            }
            elseif ($licenseUrlNode) { $licenseText = "url=$($licenseUrlNode.InnerText)" }
            if ($projectUrlNode) { $projectUrl = [string]$projectUrlNode.InnerText }
        }
        $manifest.Add("$($entry.Key) | $licenseText | $projectUrl | copied notice files=$($licenseFiles.Count)")
    }
    [System.IO.File]::WriteAllLines((Join-Path $Destination 'PACKAGE-MANIFEST.txt'), $manifest, (New-Object System.Text.UTF8Encoding($false)))
}

$toolsDir = if ($PSScriptRoot) { $PSScriptRoot } else { Split-Path -Parent $MyInvocation.MyCommand.Path }
$repoRoot = Split-Path -Parent $toolsDir
if (-not $OutputRoot) { $OutputRoot = Join-Path $repoRoot 'out' }
$outputRootFull = [System.IO.Path]::GetFullPath($OutputRoot)
$bundleName = "SparrowHelper-Portable-$Runtime"
$stageRoot = Join-Path $outputRootFull $bundleName
$zipPath = Join-Path $outputRootFull "$bundleName.zip"
Assert-ChildPath -Parent $repoRoot -Child $outputRootFull
Assert-ChildPath -Parent $outputRootFull -Child $stageRoot

if ($Runtime -ne 'win-x64') {
    throw '현재 Portable 검증/경량화 정책은 win-x64만 지원합니다.'
}

Write-Host '==================== Sparrow Helper Offline Portable ===================='
Write-Host "Repo       : $repoRoot"
Write-Host "Stage      : $stageRoot"
Write-Host "ZIP        : $zipPath"
Write-Host "Publish    : $(-not $SkipPublish)"
Write-Host "Prune PDB/other RID assets: $(-not $NoPrune)"

if ($DryRun) {
    Write-Host '[DryRun] 파일 변경 없이 종료합니다.'
    exit 0
}

if (-not $SkipPublish) {
    $publisher = Join-Path $toolsDir 'publish-airgap.ps1'
    $powershell = Join-Path $PSHOME 'powershell.exe'
    if (-not (Test-Path -LiteralPath $powershell)) { $powershell = 'powershell.exe' }
    & $powershell -NoProfile -ExecutionPolicy Bypass -File $publisher -Runtime $Runtime
    if ($LASTEXITCODE -ne 0) { throw "self-contained publish 실패(exit=$LASTEXITCODE)" }
}

$guiPublish = Join-Path $toolsDir 'SparrowRunner.Gui\publish'
$syntaxRoot = Join-Path $toolsDir '_internal\SparrowSyntaxFix'
$commentRoot = Join-Path $toolsDir '_internal\SparrowCommentFix'
$xlsRoot = Join-Path $toolsDir '_internal\SparrowXlsExport'

if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}
if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
$zipHashPath = "$zipPath.sha256.txt"
if (Test-Path -LiteralPath $zipHashPath) {
    Remove-Item -LiteralPath $zipHashPath -Force
}
Ensure-Directory $stageRoot

Copy-RequiredFile (Join-Path $repoRoot 'SKILL.md') (Join-Path $stageRoot 'SKILL.md')
Copy-RequiredFile (Join-Path $repoRoot 'LICENSE') (Join-Path $stageRoot 'LICENSE')
Copy-RequiredFile (Join-Path $toolsDir 'offline\README-OFFLINE.txt') (Join-Path $stageRoot 'README-OFFLINE.txt')
Copy-RequiredFile (Join-Path $toolsDir 'offline\Start-SparrowHelper.cmd') (Join-Path $stageRoot 'Start-SparrowHelper.cmd')
Copy-RequiredFile (Join-Path $toolsDir 'offline\Verify-SparrowHelper.cmd') (Join-Path $stageRoot 'Verify-SparrowHelper.cmd')
Copy-RequiredFile (Join-Path $toolsDir 'Run-SparrowRunnerGui.cmd') (Join-Path $stageRoot 'tools\Run-SparrowRunnerGui.cmd')
Copy-RequiredFile (Join-Path $toolsDir 'Run-SparrowAll.cmd') (Join-Path $stageRoot 'tools\Run-SparrowAll.cmd')
Copy-RequiredFile (Join-Path $toolsDir 'Run-SparrowAll.ps1') (Join-Path $stageRoot 'tools\Run-SparrowAll.ps1')
Copy-RequiredFile (Join-Path $toolsDir 'verify-offline-portable.ps1') (Join-Path $stageRoot 'tools\verify-offline-portable.ps1')
Copy-RequiredFile (Join-Path $syntaxRoot 'Run-SparrowSyntaxFix.ps1') (Join-Path $stageRoot 'tools\_internal\SparrowSyntaxFix\Run-SparrowSyntaxFix.ps1')
Copy-RequiredFile (Join-Path $syntaxRoot 'Run-SparrowSyntaxFix.cmd') (Join-Path $stageRoot 'tools\_internal\SparrowSyntaxFix\Run-SparrowSyntaxFix.cmd')
Copy-RequiredFile (Join-Path $commentRoot 'Run-SparrowCommentFix.ps1') (Join-Path $stageRoot 'tools\_internal\SparrowCommentFix\Run-SparrowCommentFix.ps1')
Copy-RequiredFile (Join-Path $commentRoot 'Run-SparrowCommentFix.cmd') (Join-Path $stageRoot 'tools\_internal\SparrowCommentFix\Run-SparrowCommentFix.cmd')

$prune = -not $NoPrune
Copy-PublishTree $guiPublish (Join-Path $stageRoot 'tools\SparrowRunner.Gui\publish') $prune
Copy-PublishTree (Join-Path $syntaxRoot 'publish') (Join-Path $stageRoot 'tools\_internal\SparrowSyntaxFix\publish') $prune
Copy-PublishTree (Join-Path $commentRoot 'publish') (Join-Path $stageRoot 'tools\_internal\SparrowCommentFix\publish') $prune
Copy-PublishTree (Join-Path $xlsRoot 'publish') (Join-Path $stageRoot 'tools\_internal\SparrowXlsExport\publish') $prune

$licensesDir = Join-Path $stageRoot 'licenses'
Ensure-Directory $licensesDir
Copy-RequiredFile (Join-Path $toolsDir 'offline\THIRD-PARTY-NOTICES.txt') (Join-Path $licensesDir 'SPARROW-THIRD-PARTY-NOTICES.txt')
$assetsFiles = @(
    (Join-Path $toolsDir 'SparrowRunner.Gui\obj\project.assets.json'),
    (Join-Path $syntaxRoot 'obj\project.assets.json'),
    (Join-Path $commentRoot 'obj\project.assets.json'),
    (Join-Path $xlsRoot 'obj\project.assets.json')
)
Copy-NuGetNotices -AssetsFiles $assetsFiles -Destination (Join-Path $licensesDir 'nuget')

$commit = 'not-a-git-worktree'
$dirty = 'unknown'
if (Test-Path -LiteralPath (Join-Path $repoRoot '.git')) {
    $commit = (& git -C $repoRoot rev-parse HEAD 2>$null)
    $dirtyText = (& git -C $repoRoot status --porcelain 2>$null)
    $dirty = if ($dirtyText) { 'true' } else { 'false' }
}
$buildInfo = @(
    "Bundle=$bundleName",
    "BuiltUtc=$([DateTime]::UtcNow.ToString('o'))",
    "SourceCommit=$commit",
    "SourceWorktreeDirty=$dirty",
    'Target=Windows x64',
    'DotNetMode=self-contained',
    'Clang=20.1.0'
)
[System.IO.File]::WriteAllLines((Join-Path $stageRoot 'BUILD-INFO.txt'), $buildInfo, (New-Object System.Text.UTF8Encoding($false)))

$verifier = Join-Path $toolsDir 'verify-offline-portable.ps1'
& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $verifier -BundleRoot $stageRoot -SkipManifest
if ($LASTEXITCODE -ne 0) { throw "staging 검증 실패(exit=$LASTEXITCODE)" }

$hashLines = Get-ChildItem -LiteralPath $stageRoot -File -Recurse |
    Where-Object { $_.Name -ne 'SHA256SUMS.txt' } |
    Sort-Object FullName |
    ForEach-Object {
        $relative = $_.FullName.Substring($stageRoot.TrimEnd('\').Length + 1).Replace('\', '/')
        $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        "$hash  $relative"
    }
[System.IO.File]::WriteAllLines((Join-Path $stageRoot 'SHA256SUMS.txt'), $hashLines, (New-Object System.Text.UTF8Encoding($false)))

& powershell.exe -NoProfile -ExecutionPolicy Bypass -File $verifier -BundleRoot $stageRoot
if ($LASTEXITCODE -ne 0) { throw "manifest 검증 실패(exit=$LASTEXITCODE)" }

if (-not $SkipZip) {
    Write-Host 'ZIP 압축 중...'
    Compress-Archive -LiteralPath $stageRoot -DestinationPath $zipPath -CompressionLevel Optimal
    $zipHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    [System.IO.File]::WriteAllText($zipHashPath, "$zipHash  $([System.IO.Path]::GetFileName($zipPath))`r`n", (New-Object System.Text.UTF8Encoding($false)))

    if (-not $SkipZipExtractionTest) {
        $testParent = Join-Path ([System.IO.Path]::GetTempPath()) ("Sparrow 압축 검증 " + [Guid]::NewGuid().ToString('N'))
        try {
            Ensure-Directory $testParent
            Expand-Archive -LiteralPath $zipPath -DestinationPath $testParent -Force
            $extractedRoot = Join-Path $testParent $bundleName
            & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $extractedRoot 'tools\verify-offline-portable.ps1') -BundleRoot $extractedRoot
            if ($LASTEXITCODE -ne 0) { throw "ZIP 재압축 해제 검증 실패(exit=$LASTEXITCODE)" }
        }
        finally {
            if (Test-Path -LiteralPath $testParent) { Remove-Item -LiteralPath $testParent -Recurse -Force }
        }
    }
}

$stageSize = (Get-ChildItem -LiteralPath $stageRoot -File -Recurse | Measure-Object -Property Length -Sum).Sum
Write-Host '==================== 완료 ====================' -ForegroundColor Green
Write-Host ("Portable folder: {0} ({1:N2} MiB)" -f $stageRoot, ($stageSize / 1MB))
if (-not $SkipZip) {
    $zipSize = (Get-Item -LiteralPath $zipPath).Length
    Write-Host ("Portable ZIP   : {0} ({1:N2} MiB)" -f $zipPath, ($zipSize / 1MB))
    Write-Host "ZIP SHA-256   : $zipHash"
}
Write-Host '커밋/푸시는 수행하지 않았습니다.'
exit 0
