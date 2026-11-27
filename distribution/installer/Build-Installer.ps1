[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$PublishDirectory,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [string]$Version = '1.1.5',
    [string]$IconFile = (Join-Path $PSScriptRoot '..\..\src\DiskLens.App\Resources\Brand\DiskLens.ico'),
    [string]$CompilerPath = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$payload = (Resolve-Path -LiteralPath $PublishDirectory).Path
$null = New-Item -ItemType Directory -Path $OutputDirectory -Force
$output = (Resolve-Path -LiteralPath $OutputDirectory).Path
$icon = (Resolve-Path -LiteralPath $IconFile).Path
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Version must contain three numeric components.' }
$application = Join-Path $payload 'DiskLens.App.exe'
if ([version]([Diagnostics.FileVersionInfo]::GetVersionInfo($application).FileVersion.Trim()) -ne [version]($Version + '.0')) {
    throw 'Application version does not match installer version.'
}
$files = @()
foreach ($name in @('DiskLens.App.exe', 'LICENSE', '使用说明.md', 'UserGuide.en.md', 'NOTICE.md', 'THIRD-PARTY-NOTICES.txt')) {
    $path = Join-Path $payload $name
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing payload: $name" }
    $files += [ordered]@{ name = $name; sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() }
}
$legacy = @([IO.File]::ReadAllText((Join-Path $PSScriptRoot 'legacy-components.json')) | ConvertFrom-Json)
$manifest = [ordered]@{
    schemaVersion = 1; product = 'DiskLens'; appId = '{4C340A6D-C624-4E52-91AA-D17D26830485}'
    version = $Version; files = $files; legacyFiles = $legacy
}
[IO.File]::WriteAllText((Join-Path $payload 'DiskLens.install-manifest.json'), ($manifest | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
& $CompilerPath "/DAppVersion=$Version" "/DPublishDir=$payload" "/DOutputPath=$output" "/DIconFile=$icon" (Join-Path $PSScriptRoot 'DiskLens.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed: $LASTEXITCODE" }
$installer = Get-Item -LiteralPath (Join-Path $output "DiskLens-Setup-$Version-x64.exe")
$hash = (Get-FileHash -LiteralPath $installer.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
$metadata = [ordered]@{
    version = $Version; filename = $installer.Name; bytes = $installer.Length; sha256 = $hash
    fileVersion = $installer.VersionInfo.FileVersion.Trim(); features = @('automatic-old-version-cleanup', 'independent-recycle-worker-processes', 'native-windows-shell-ui')
    applicationSha256 = $files[0].sha256
}
[IO.File]::WriteAllText((Join-Path $output 'release-artifact.json'), ($metadata | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText((Join-Path $output 'SHA256SUMS.txt'), "$hash  $($installer.Name)`n", [Text.UTF8Encoding]::new($false))
$metadata | ConvertTo-Json -Depth 5
