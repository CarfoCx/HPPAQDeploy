param(
    [ValidateSet('Patch', 'Minor', 'Major')]
    [string]$Part = 'Patch'
)

$ErrorActionPreference = 'Stop'

$projectPath = Join-Path $PSScriptRoot '..\src\HPPAQDeploy.App\HPPAQDeploy.App.csproj'
[xml]$project = Get-Content -LiteralPath $projectPath
$versionNode = $project.Project.PropertyGroup.Version | Select-Object -First 1

if (-not $versionNode) {
    throw 'Version element not found in HPPAQDeploy.App.csproj.'
}

$parts = $versionNode.Split('.')
if ($parts.Count -ne 3) {
    throw "Expected semantic version in Major.Minor.Patch format, got '$versionNode'."
}

$major = [int]$parts[0]
$minor = [int]$parts[1]
$patch = [int]$parts[2]

switch ($Part) {
    'Patch' {
        $patch++
        if ($patch -gt 99) {
            $patch = 0
            $minor++
        }
    }
    'Minor' {
        $minor++
        $patch = 0
    }
    'Major' {
        $major++
        $minor = 0
        $patch = 0
    }
}

if ($major -gt 99 -or $minor -gt 99 -or $patch -gt 99) {
    throw 'Version parts must stay between 0 and 99.'
}

$newVersion = "$major.$minor.$patch"
$project.Project.PropertyGroup.Version = $newVersion
$project.Save((Resolve-Path -LiteralPath $projectPath).Path)

Write-Host "HPPAQDeploy version bumped to $newVersion"
