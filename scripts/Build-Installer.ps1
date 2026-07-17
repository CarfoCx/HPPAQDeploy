[CmdletBinding()]
param(
    [string]$Version,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\HPPAQDeploy.App\HPPAQDeploy.App.csproj'
$installerScript = Join-Path $repoRoot 'installer\HPPAQDeploy.iss'
$artifactRoot = Join-Path $repoRoot 'artifacts'
$publishDir = Join-Path $artifactRoot 'publish\win-x64'
$outputDir = Join-Path $artifactRoot 'installer'

if ([string]::IsNullOrWhiteSpace($Version)) {
    [xml]$project = Get-Content -LiteralPath $projectPath
    $Version = [string]($project.Project.PropertyGroup.Version | Select-Object -First 1)
}

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must use Major.Minor.Patch format; received '$Version'."
}

$resolvedArtifactRoot = [System.IO.Path]::GetFullPath($artifactRoot)
$resolvedRepoRoot = [System.IO.Path]::GetFullPath($repoRoot)
if (-not $resolvedArtifactRoot.StartsWith($resolvedRepoRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Artifact directory resolved outside the repository: $resolvedArtifactRoot"
}

if (Test-Path -LiteralPath $publishDir) {
    Remove-Item -LiteralPath $publishDir -Recurse -Force
}
if (Test-Path -LiteralPath $outputDir) {
    Remove-Item -LiteralPath $outputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $publishDir, $outputDir -Force | Out-Null

& dotnet publish $projectPath `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=false `
    -p:DebugType=None `
    -p:DebugSymbols=false `
    -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}

$requiredFiles = @(
    (Join-Path $publishDir 'HPPAQDeploy.exe'),
    (Join-Path $publishDir 'hp-hpia-5.3.4.exe'),
    (Join-Path $publishDir 'Agent\HPPAQDeploy.Agent.exe')
)
foreach ($requiredFile in $requiredFiles) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Publish output is incomplete; missing $requiredFile"
    }
}

$isccCandidates = @(
    $env:INNO_ISCC_PATH,
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno\ISCC.exe'),
    (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 7\ISCC.exe'),
    (Join-Path ${env:ProgramFiles} 'Inno Setup 7\ISCC.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe')
) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

$isccPath = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
if (-not $isccPath) {
    throw 'Inno Setup compiler (ISCC.exe) was not found. Install Inno Setup or set INNO_ISCC_PATH.'
}

& $isccPath "/DAppVersion=$Version" "/DSourceDir=$publishDir" "/DOutputDir=$outputDir" $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE."
}

$installerPath = Join-Path $outputDir "HPPAQDeploy-Setup-v$Version-win-x64.exe"
if (-not (Test-Path -LiteralPath $installerPath -PathType Leaf)) {
    throw "Installer was not produced at $installerPath"
}

$hash = Get-FileHash -LiteralPath $installerPath -Algorithm SHA256
$checksumPath = "$installerPath.sha256"
"$($hash.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($installerPath))" |
    Set-Content -LiteralPath $checksumPath -Encoding ascii

Write-Host "Installer: $installerPath"
Write-Host "SHA-256:  $($hash.Hash.ToLowerInvariant())"
Write-Host "Checksum: $checksumPath"
