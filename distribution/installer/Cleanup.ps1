[CmdletBinding()]
param(
    [ValidateSet('Capture', 'Install', 'Downloads')][string]$Mode,
    [string]$InstallDirectory,
    [string]$InstalledVersion,
    [string]$ManifestPath,
    [string]$PlanPath,
    [string]$SetupPath,
    [ValidateSet('CurrentUser', 'AllUsers')][string]$InstallationMode = 'CurrentUser',
    [string]$ResultPath,
    [switch]$FunctionsOnly
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
$script:ManifestName = 'DiskLens.install-manifest.json'
$script:UninstallKey = 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{4C340A6D-C624-4E52-91AA-D17D26830485}_is1'
$script:PayloadNames = @('DiskLens.App.exe', 'LICENSE', '使用说明.md', 'UserGuide.en.md', 'NOTICE.md', 'THIRD-PARTY-NOTICES.txt')
$script:Events = [Collections.Generic.List[object]]::new()

function Add-CleanupEvent([string]$Action, [string]$Path, [string]$Reason) {
    $script:Events.Add([ordered]@{ action = $Action; path = $Path; reason = $Reason })
}

function Get-NormalPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { throw 'Empty path.' }
    return [IO.Path]::GetFullPath($Path).TrimEnd('\', '/')
}

function Test-SamePath([string]$Left, [string]$Right) {
    return [string]::Equals((Get-NormalPath $Left), (Get-NormalPath $Right), [StringComparison]::OrdinalIgnoreCase)
}

function Test-PlainPath([string]$Path) {
    # Reject junctions/symlinks anywhere in the existing ancestor chain.
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { return $false }
        }
        $parent = [IO.Directory]::GetParent($cursor)
        if ($null -eq $parent) { break }
        $cursor = $parent.FullName
    }
    return $true
}

function Test-InstallRoot([string]$Path) {
    try {
        $full = Get-NormalPath $Path
        if ($full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\', '/')) { return $false }
        foreach ($base in @([Environment]::GetFolderPath('ApplicationData'), [Environment]::GetFolderPath('LocalApplicationData'))) {
            if ($base -and (Test-SamePath $full (Join-Path $base 'DiskLens'))) { return $false }
        }
        if ($env:DISKLENS_DATA_DIRECTORY -and (Test-SamePath $full $env:DISKLENS_DATA_DIRECTORY)) { return $false }
        return (Test-Path -LiteralPath $full -PathType Container) -and (Test-PlainPath $full)
    } catch { return $false }
}

function Get-Hash([string]$Path) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }

function ConvertTo-FourPartVersion([version]$Version) {
    return [version]::new($Version.Major, $Version.Minor, [Math]::Max(0, $Version.Build), [Math]::Max(0, $Version.Revision))
}

function Test-ManagedName([string]$Name) {
    # A component manifest may never nominate settings, credentials, caches or arbitrary paths.
    return $script:PayloadNames -contains $Name -or $Name -match '^DiskLens\.[a-zA-Z0-9_.-]+\.(dll|pdb|deps\.json|runtimeconfig\.json)$'
}

function Read-ComponentManifest([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf) -or !(Test-PlainPath $Path)) { return $null }
    $info = Get-Item -LiteralPath $Path
    if ($info.Length -gt 131072) { return $null }
    try {
        $data = [IO.File]::ReadAllText($Path) | ConvertFrom-Json
        if ($data.schemaVersion -ne 1 -or $data.product -ne 'DiskLens' -or $data.appId -ne '{4C340A6D-C624-4E52-91AA-D17D26830485}') { return $null }
        $null = [version]$data.version
        if (@($data.files).Count -gt 100) { return $null }
        foreach ($file in $data.files) {
            if (!(Test-ManagedName $file.name) -or $file.sha256 -notmatch '^[0-9a-f]{64}$') { return $null }
        }
        return $data
    } catch { return $null }
}

function Test-AppBinary([string]$Directory, [version]$MaximumVersion) {
    $app = Join-Path $Directory 'DiskLens.App.exe'
    if (!(Test-InstallRoot $Directory) -or !(Test-Path -LiteralPath $app -PathType Leaf) -or !(Test-PlainPath $app)) { return $false }
    try {
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($app)
        return $info.ProductName.Trim() -eq 'DiskLens.App' -and $info.CompanyName.Trim() -eq 'DiskLens.App' -and
            ([version]$info.FileVersion.Trim()) -le (ConvertTo-FourPartVersion $MaximumVersion)
    } catch { return $false }
}

function Get-RegisteredInstallation([string]$Scope) {
    $hive = [Microsoft.Win32.RegistryHive]::CurrentUser
    if ($Scope -eq 'AllUsers') { $hive = [Microsoft.Win32.RegistryHive]::LocalMachine }
    $base = [Microsoft.Win32.RegistryKey]::OpenBaseKey($hive, [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $key = $base.OpenSubKey($script:UninstallKey)
        if ($null -eq $key) { return $null }
        try { return [pscustomobject]@{ directory = [string]$key.GetValue('InstallLocation'); uninstaller = [string]$key.GetValue('UninstallString') } }
        finally { $key.Dispose() }
    } finally { $base.Dispose() }
}

function New-CleanupPlan([string]$CurrentDirectory, [string]$Scope, $CurrentManifest) {
    $registered = Get-RegisteredInstallation $Scope
    $roots = @($CurrentDirectory)
    if ($null -ne $registered -and $registered.directory) { $roots += $registered.directory }
    $plans = @()
    $seen = @{}
    foreach ($root in $roots) {
        if (!(Test-AppBinary $root ([version]$CurrentManifest.version))) { continue }
        $root = Get-NormalPath $root
        if ($seen.ContainsKey($root)) { continue }
        $seen[$root] = $true
        $oldManifestPath = Join-Path $root $script:ManifestName
        $old = Read-ComponentManifest $oldManifestPath
        $files = @()
        if ($null -ne $old -and ([version]$old.version) -le ([version]$CurrentManifest.version)) {
            $files = @($old.files)
        } else {
            # Installers before this feature did not carry a component manifest.
            # Only exact hashes from known official packages can be migrated.
            foreach ($file in $CurrentManifest.legacyFiles) {
                if (!(Test-ManagedName $file.name) -or $file.sha256 -notmatch '^[0-9a-f]{64}$') { continue }
                $path = Join-Path $root $file.name
                if ((Test-Path -LiteralPath $path -PathType Leaf) -and (Test-PlainPath $path) -and (Get-Hash $path) -eq $file.sha256) { $files += $file }
            }
        }
        $uninstallFiles = @()
        if ($null -ne $registered -and (Test-SamePath $root $registered.directory)) {
            $uninstaller = $registered.uninstaller.Trim('"')
            if ($uninstaller -and (Test-SamePath (Split-Path -Parent $uninstaller) $root) -and
                [IO.Path]::GetFileName($uninstaller) -match '^unins\d{3}\.exe$' -and (Test-PlainPath $uninstaller) -and (Test-Path -LiteralPath $uninstaller -PathType Leaf)) {
                $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($uninstaller)
                if ($info.CompanyName.Trim() -eq 'kxdyh' -and $info.FileDescription.Trim() -eq 'Setup/Uninstall') {
                    foreach ($extension in @('.exe', '.dat', '.msg')) {
                        $path = [IO.Path]::ChangeExtension($uninstaller, $extension)
                        if ((Test-Path -LiteralPath $path -PathType Leaf) -and (Test-PlainPath $path)) {
                            $uninstallFiles += [pscustomobject]@{name = [IO.Path]::GetFileName($path); sha256 = Get-Hash $path}
                        }
                    }
                }
            }
        }
        $plans += [pscustomobject]@{ directory = $root; files = @($files); uninstallFiles = @($uninstallFiles); manifestHash = $(if ($null -ne $old) { Get-Hash $oldManifestPath } else { '' }) }
    }
    return [pscustomobject]@{ schemaVersion = 1; product = 'DiskLens'; destination = Get-NormalPath $CurrentDirectory; roots = @($plans) }
}

function Remove-MatchingFile([string]$Root, $Entry, [switch]$Uninstaller) {
    $name = [string]$Entry.name
    $allowed = Test-ManagedName $name
    if ($Uninstaller) { $allowed = $name -match '^unins\d{3}\.(exe|dat|msg)$' }
    if (!$allowed -or $Entry.sha256 -notmatch '^[0-9a-f]{64}$') { return }
    $path = Join-Path $Root $name
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { return }
    try {
        if (!(Test-PlainPath $path) -or (Get-Hash $path) -ne $Entry.sha256) {
            Add-CleanupEvent 'kept' $path 'Changed or redirected file.'
            return
        }
        Remove-Item -LiteralPath $path -Force -ErrorAction Stop
        Add-CleanupEvent 'deleted' $path 'Verified obsolete application component.'
    } catch { Add-CleanupEvent 'kept' $path 'File is in use or cannot be removed.' }
}

function Complete-InstallCleanup($Plan, $CurrentManifest, [string]$Scope) {
    $destination = [string]$Plan.destination
    if ($Plan.schemaVersion -ne 1 -or $Plan.product -ne 'DiskLens' -or
        !(Test-AppBinary $destination ([version]$CurrentManifest.version))) { throw 'Invalid cleanup plan destination.' }
    $appEntry = @($CurrentManifest.files | Where-Object { $_.name -eq 'DiskLens.App.exe' })
    if ($appEntry.Count -ne 1 -or (Get-Hash (Join-Path $destination 'DiskLens.App.exe')) -ne $appEntry[0].sha256) {
        throw 'The new application has not been installed successfully.'
    }
    $registered = Get-RegisteredInstallation $Scope
    foreach ($root in $Plan.roots) {
        $directory = [string]$root.directory
        if (!(Test-InstallRoot $directory)) { continue }
        $sameDirectory = Test-SamePath $directory $destination
        if (!$sameDirectory -and !(Test-AppBinary $directory ([version]$CurrentManifest.version))) { continue }
        foreach ($entry in $root.files) {
            if ($sameDirectory -and @($CurrentManifest.files.name) -contains $entry.name) { continue }
            Remove-MatchingFile $directory $entry
        }
        if (!$sameDirectory) {
            # Never run an older uninstaller after an upgrade. Only its exact,
            # captured files may be removed after the new registration exists.
            if ($null -ne $registered -and $registered.directory -and (Test-SamePath $registered.directory $destination)) {
                foreach ($entry in $root.uninstallFiles) { Remove-MatchingFile $directory $entry -Uninstaller }
            }
            $manifest = Join-Path $directory $script:ManifestName
            if ($root.manifestHash -match '^[0-9a-f]{64}$' -and (Test-Path -LiteralPath $manifest -PathType Leaf) -and
                (Test-PlainPath $manifest) -and (Get-Hash $manifest) -eq $root.manifestHash) {
                Remove-Item -LiteralPath $manifest -Force
                Add-CleanupEvent 'deleted' $manifest 'Obsolete component manifest.'
            }
            # Remove the old directory only if it is now empty; never recurse.
            if (@(Get-ChildItem -LiteralPath $directory -Force).Count -eq 0) { [IO.Directory]::Delete($directory) }
        }
    }
}

function Test-OldInstaller([string]$Path, [version]$NewVersion, [string]$ActiveSetup) {
    try {
        if ((Test-SamePath $Path $ActiveSetup) -or !(Test-PlainPath $Path)) { return $false }
        $name = [IO.Path]::GetFileName($Path)
        if ($name -notmatch '^DiskLens-Setup-(\d+\.\d+\.\d+)-x64(?: \(\d+\))?\.exe$') { return $false }
        $nameVersion = [version]($Matches[1] + '.0')
        $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
        return $info.CompanyName.Trim() -eq 'kxdyh' -and $info.FileDescription.Trim() -eq 'DiskLens installer' -and
            ([version]$info.FileVersion.Trim()) -eq $nameVersion -and $nameVersion -lt (ConvertTo-FourPartVersion $NewVersion)
    } catch { return $false }
}

function Get-DownloadsDirectory {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders')
    try { $path = $(if ($null -ne $key) { [string]$key.GetValue('{374DE290-123F-4565-9164-39C4925E467B}') }) }
    finally { if ($null -ne $key) { $key.Dispose() } }
    if ($path) { return [Environment]::ExpandEnvironmentVariables($path) }
    return Join-Path ([Environment]::GetFolderPath('UserProfile')) 'Downloads'
}

function Clear-OldDownloads([string[]]$Directories, [version]$NewVersion, [string]$ActiveSetup) {
    $seen = @{}
    foreach ($directory in $Directories) {
        if (!$directory -or !(Test-Path -LiteralPath $directory -PathType Container) -or !(Test-PlainPath $directory)) { continue }
        $directory = Get-NormalPath $directory
        if ($seen.ContainsKey($directory)) { continue }
        $seen[$directory] = $true
        # Top-level files only: browser download folders are not application trees.
        foreach ($file in (Get-ChildItem -LiteralPath $directory -File -Filter 'DiskLens-Setup-*.exe' -Force)) {
            if (!(Test-OldInstaller $file.FullName $NewVersion $ActiveSetup)) { continue }
            try {
                # Recheck immediately before removal; do not schedule on reboot.
                if (Test-OldInstaller $file.FullName $NewVersion $ActiveSetup) {
                    Remove-Item -LiteralPath $file.FullName -Force -ErrorAction Stop
                    Add-CleanupEvent 'deleted' $file.FullName 'Older DiskLens installer.'
                }
            } catch { Add-CleanupEvent 'kept' $file.FullName 'File is in use or cannot be removed.' }
        }
    }
}

if ($FunctionsOnly) { return }
try {
    if ($Mode -eq 'Downloads') {
        Clear-OldDownloads @((Get-DownloadsDirectory), (Split-Path -Parent $SetupPath)) ([version]($InstalledVersion + '.0')) $SetupPath
    } else {
        $manifest = Read-ComponentManifest $ManifestPath
        if ($null -eq $manifest) { throw 'Invalid embedded component manifest.' }
        if ($Mode -eq 'Capture') {
            $plan = New-CleanupPlan $InstallDirectory $InstallationMode $manifest
            [IO.File]::WriteAllText($PlanPath, ($plan | ConvertTo-Json -Depth 12), [Text.UTF8Encoding]::new($false))
        } elseif ($Mode -eq 'Install') {
            if (!(Test-Path -LiteralPath $PlanPath -PathType Leaf)) { throw 'No pre-install cleanup plan.' }
            $plan = [IO.File]::ReadAllText($PlanPath) | ConvertFrom-Json
            if (!(Test-SamePath $InstallDirectory $plan.destination)) { throw 'Cleanup destination changed.' }
            Complete-InstallCleanup $plan $manifest $InstallationMode
        } else { throw 'Cleanup mode is required.' }
    }
} catch { Add-CleanupEvent 'skipped' '' $_.Exception.Message }
if ($ResultPath) {
    try { [IO.File]::WriteAllText($ResultPath, (ConvertTo-Json -InputObject @($script:Events.ToArray()) -Depth 5), [Text.UTF8Encoding]::new($false)) }
    catch { } # Cleanup must not make a successful application installation fail.
}
