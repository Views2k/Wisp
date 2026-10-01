#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DotNet,
    [Parameter(Mandatory)][string]$InnoCompiler,
    [Parameter(Mandatory)][string]$Python,
    [Parameter(Mandatory)][ValidatePattern('^[a-z0-9][a-z0-9-]{5,79}$')][string]$DiagnosticBuildId,
    [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9 ._-]{1,80}$')][string]$DiagnosticBuildLabel
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-PrivateBuildIdentity {
    param([string]$ProjectText, [string]$ExpectedId, [string]$ExpectedLabel)
    [xml]$projectXml = $ProjectText
    foreach ($pair in @(
        @('WispDiagnosticBuildId', $ExpectedId),
        @('WispDiagnosticBuildLabel', $ExpectedLabel))) {
        $nodes = @($projectXml.SelectNodes("/Project/ItemGroup/AssemblyMetadata[@Include='$($pair[0])']"))
        if ($nodes.Count -ne 1 -or $nodes[0].GetAttribute('Value') -cne $pair[1] -or
            [string]::IsNullOrWhiteSpace($pair[1])) {
            throw 'Private packaging requires the exact explicit diagnostic ID and label.'
        }
    }
}

function Assert-PrivatePayloadFiles {
    param([string]$Directory)
    foreach ($name in @('Wisp.Updater.exe', 'Wisp.exe', 'Wisp.dll', 'Wisp.Core.dll',
        'Wisp.Telemetry.dll', 'Wisp.Update.dll', 'Wisp.NativeRenderer.dll', 'Wisp.Recorder.exe',
        'Wisp.deps.json', 'Wisp.runtimeconfig.json', 'hostfxr.dll', 'hostpolicy.dll',
        'coreclr.dll', 'PresentationNative_cor3.dll', 'wpfgfx_cor3.dll')) {
        $path = Join-Path $Directory $name
        if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or
            (Get-Item -LiteralPath $path).Length -le 0 -or
            ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The private payload is missing a regular nonempty $name."
        }
    }
    $runtime = (Get-Content -LiteralPath (Join-Path $Directory 'Wisp.runtimeconfig.json') -Raw |
        ConvertFrom-Json).runtimeOptions
    if ($null -ne $runtime.PSObject.Properties['frameworks'] -or
        $null -ne $runtime.PSObject.Properties['framework'] -or
        $null -eq $runtime.PSObject.Properties['includedFrameworks'] -or
        @($runtime.includedFrameworks | Where-Object name -eq 'Microsoft.NETCore.App').Count -ne 1 -or
        @($runtime.includedFrameworks | Where-Object name -eq 'Microsoft.WindowsDesktop.App').Count -ne 1) {
        throw 'The private application must include its own .NET and Windows Desktop runtimes.'
    }
}

function Replace-PrivateDirective {
    param([string]$Text, [string]$Old, [string]$New)
    if ([regex]::Matches($Text, [regex]::Escape($Old)).Count -ne 1) {
        throw 'The canonical Inno script changed; review the private packaging adaptation.'
    }
    return $Text.Replace($Old, $New)
}

function Get-PrivateCompilerSourcePath {
    param([Parameter(Mandatory)][string]$Directory)
    if (-not ('WispPrivateCompilerPath' -as [type])) {
        Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
using System.Text;
public static class WispPrivateCompilerPath {
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern uint GetShortPathName(string path, StringBuilder result, uint capacity);
}
'@
    }
    $buffer = [Text.StringBuilder]::new(32768)
    $count = [WispPrivateCompilerPath]::GetShortPathName($Directory, $buffer, [uint32]$buffer.Capacity)
    $compilerPath = if ($count -gt 0 -and $count -lt $buffer.Capacity) { $buffer.ToString() } else { $Directory }
    foreach ($file in Get-PrivateRegularFiles $Directory) {
        $relative = [IO.Path]::GetRelativePath($Directory, $file.FullName)
        $alias = Join-Path $compilerPath $relative
        if ($alias.Length -ge 260) { throw 'The installer source path is too long; use a shorter checkout path.' }
        if ((Get-FileHash -LiteralPath $alias -Algorithm SHA256).Hash -cne
            (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) {
            throw 'The compiler source alias differs from the validated payload.'
        }
    }
    return $compilerPath
}

function Get-PrivateRegularFiles {
    param([Parameter(Mandatory)][string]$Directory)
    $root = Get-Item -LiteralPath $Directory -Force
    if (-not $root.PSIsContainer -or ($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The decoder payload directory must be a regular directory.'
    }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($root.FullName)
    while ($pending.Count -gt 0) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Decoder payloads cannot contain reparse points.'
            }
            if ($item.PSIsContainer) { $pending.Push($item.FullName) }
            else { $item }
        }
    }
}

function Assert-PrivateLosslessPayload {
    param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$ManifestPath)
    $root = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    # Check every ancestor/file before validating or following nested payload paths.
    $regular = @{}
    foreach ($file in Get-PrivateRegularFiles $root) {
        $relative = $file.FullName.Substring($root.Length + 1).Replace('\', '/')
        $regular[$relative] = $file
    }
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    $native = @($manifest.nativeFiles)
    if ($native.Count -ne 24 -or @($native | Where-Object path -like 'plugins/*').Count -ne 22) {
        throw 'The selected decoder bundle must contain exactly22 plugins and2 core libraries.'
    }
    $expected = @{}
    foreach ($entry in $native) {
        $relative = [string]$entry.path
        if ($relative -cnotmatch '^(libvlc\.dll|libvlccore\.dll|plugins/[a-z_]+/lib[a-z0-9_]+_plugin\.dll)$' -or
            $expected.ContainsKey("libvlc/win-x64/$relative")) { throw 'Invalid or duplicate decoder manifest path.' }
        $expected["libvlc/win-x64/$relative"] = $entry
    }
    if (-not $expected.ContainsKey('libvlc/win-x64/libvlc.dll') -or -not $expected.ContainsKey('libvlc/win-x64/libvlccore.dll')) {
        throw 'The decoder manifest must name both core libraries.'
    }
    $actualNative = @($regular.Keys | Where-Object { $_.StartsWith('libvlc/', [StringComparison]::OrdinalIgnoreCase) })
    if ($actualNative.Count -ne $expected.Count -or @($actualNative | Where-Object { -not $expected.ContainsKey($_) }).Count -ne 0) {
        throw 'The published decoder tree differs from the selected native allowlist.'
    }
    $notices = @($manifest.noticeFiles)
    if ($notices.Count -eq 0 -or $notices.Count -gt 128) { throw 'The decoder notice manifest is missing or unbounded.' }
    foreach ($entry in $notices) {
        $relative = [string]$entry.path
        if ($relative -cnotmatch '^[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*$' -or
            @($relative.Split('/') | Where-Object { $_ -eq '.' -or $_ -eq '..' }).Count -ne 0 -or
            $expected.ContainsKey("Licenses/LibVLCThirdParty/$relative")) { throw 'Invalid or duplicate decoder notice path.' }
        $expected["Licenses/LibVLCThirdParty/$relative"] = $entry
    }
    $hashes = @{}
    foreach ($relative in $expected.Keys) {
        $entry = $expected[$relative]
        if (-not $regular.ContainsKey($relative) -or $entry.bytes -le 0 -or $regular[$relative].Length -ne $entry.bytes -or
            [string]$entry.sha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'A required decoder file or notice is missing or has an invalid size.' }
        $hash = (Get-FileHash -LiteralPath $regular[$relative].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -cne $entry.sha256) { throw 'A published decoder file or notice differs from its provenance manifest.' }
        $hashes[$relative] = $hash
    }
    foreach ($relative in @('LibVLCSharp.dll', 'LICENSE.txt', 'THIRD-PARTY-NOTICES.md',
        'Licenses/LGPL-2.1.txt', 'Licenses/NVIDIA-nvEncodeAPI-MIT.txt', 'Licenses/libvlc-3.0.24-source-manifest.json')) {
        if (-not $regular.ContainsKey($relative) -or $regular[$relative].Length -le 0) {
            throw 'The decoder managed library, license or provenance file is missing.'
        }
        $hashes[$relative] = (Get-FileHash -LiteralPath $regular[$relative].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    if ($hashes['Licenses/libvlc-3.0.24-source-manifest.json'] -cne
        (Get-FileHash -LiteralPath $ManifestPath -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw 'The published decoder manifest differs from the reviewed source.'
    }
    return $hashes
}

function Assert-PrivateTestOutputDirectory {
    param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$RepositoryRoot)
    $repositoryRootPath = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/')
    $target = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    $allowed = @('tests/Wisp.Core.Tests/bin/Release/net8.0-windows', 'tests/Wisp.Telemetry.Tests/bin/Release/net8.0',
        'tests/Wisp.App.Tests/bin/Release/net8.0-windows', 'tests/Wisp.Update.Tests/bin/Release/net8.0-windows',
        'tests/Wisp.Updater.Tests/bin/Release/net8.0-windows', 'tools/Wisp.UiReview/bin/Release/net8.0-windows') |
        ForEach-Object { [IO.Path]::GetFullPath((Join-Path $repositoryRootPath $_)).TrimEnd('\', '/') }
    if ($target -notin $allowed) { throw 'Private component copies are limited to the six known generated test outputs.' }
    for ($ancestor = $target; ; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
        $item = Get-Item -LiteralPath $ancestor -Force
        if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'A private test output ancestor is not a regular directory.'
        }
        if ([string]::Equals($ancestor, $repositoryRootPath, [StringComparison]::OrdinalIgnoreCase)) { break }
        if (-not $ancestor.StartsWith($repositoryRootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'A private test output resolved outside the repository.'
        }
    }
    # Includes existing DLL/EXE/PDB destinations and every nested entry. Complete
    # this pass for every output before the first product component is copied.
    $null = @(Get-PrivateRegularFiles $target)
}

function Sync-PrivateLosslessTestPayload {
    param([Parameter(Mandatory)][string]$PublishDirectory, [Parameter(Mandatory)][string]$TestDirectory,
        [Parameter(Mandatory)][string]$RepositoryRoot, [Parameter(Mandatory)][hashtable]$ExpectedHashes)
    $repositoryRootPath = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd('\', '/')
    $target = [IO.Path]::GetFullPath($TestDirectory).TrimEnd('\', '/')
    $allowed = @('tests/Wisp.App.Tests/bin/Release/net8.0-windows', 'tools/Wisp.UiReview/bin/Release/net8.0-windows') |
        ForEach-Object { [IO.Path]::GetFullPath((Join-Path $repositoryRootPath $_)).TrimEnd('\', '/') }
    if ($target -notin $allowed) { throw 'Decoder synchronization is limited to the two known generated test outputs.' }
    # Do not let any existing generated-output ancestor redirect writes or removal.
    for ($ancestor = $target; ; $ancestor = [IO.Path]::GetDirectoryName($ancestor)) {
        $item = Get-Item -LiteralPath $ancestor -Force
        if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'A test output ancestor is not a regular directory.'
        }
        if ([string]::Equals($ancestor, $repositoryRootPath, [StringComparison]::OrdinalIgnoreCase)) { break }
        if (-not $ancestor.StartsWith($repositoryRootPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The test output resolved outside the repository.'
        }
    }
    # Inspect the complete destination before mutation. Remove only extra files
    # in this generated decoder tree; keep unrelated assemblies, data and licenses.
    $existing = @(Get-PrivateRegularFiles $target)
    $decoderRoot = Join-Path $target 'libvlc'
    $decoderPrefix = [IO.Path]::GetFullPath($decoderRoot).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    foreach ($file in $existing) {
        if (-not $file.FullName.StartsWith($decoderPrefix, [StringComparison]::OrdinalIgnoreCase)) { continue }
        $relative = $file.FullName.Substring($target.Length + 1).Replace('\', '/')
        if (-not $ExpectedHashes.ContainsKey($relative)) { Remove-Item -LiteralPath $file.FullName -Force }
    }
    foreach ($relative in $ExpectedHashes.Keys) {
        $source = Join-Path $PublishDirectory $relative
        $destination = [IO.Path]::GetFullPath((Join-Path $target $relative))
        if (-not $destination.StartsWith($target + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'A decoder synchronization path escaped its test output.'
        }
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
        [IO.File]::Copy($source, $destination, $true)
        if ((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -cne $ExpectedHashes[$relative]) {
            throw 'The test decoder payload differs from the fresh published files.'
        }
    }
}

$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$canonical = Join-Path $PSScriptRoot 'Build-Installer.ps1'
$canonicalHash = (Get-FileHash -LiteralPath $canonical -Algorithm SHA256).Hash
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($canonical, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -ne 0) { throw 'Canonical packaging helpers could not be parsed.' }
# Import only shared validators and archive functions, never the public build body or its identity gate.
foreach ($name in @('Resolve-Executable', 'Get-RepositorySourceState', 'Assert-ReleasePath',
    'Assert-NativeRendererLibrary', 'Assert-InstallerExecutable', 'Invoke-UpdaterGuardSmoke',
    'Assert-SinglePassedTestResult', 'Invoke-InstallerRuntimeValidation',
    'Initialize-ArchiveSupport', 'Assert-InstallerArchive', 'New-InstallerArchive')) {
    $definitions = @($ast.FindAll({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -ceq $name
    }, $false))
    if ($definitions.Count -ne 1) { throw "Required packaging helper is unavailable: $name" }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}

$dotnetExecutable = Resolve-Executable $DotNet @() @()
$innoExecutable = Resolve-Executable $InnoCompiler @() @()
$pythonExecutable = Resolve-Executable $Python @() @()
$gitExecutable = Resolve-Executable 'git' @() @('git.exe')
$source = Get-RepositorySourceState $repository $gitExecutable -AllowDirty
$project = Join-Path $repository 'src/Wisp.App/Wisp.App.csproj'
$updaterProject = Join-Path $repository 'src/Wisp.Updater/Wisp.Updater.csproj'
$projectText = [IO.File]::ReadAllText($project)
Assert-PrivateBuildIdentity $projectText $DiagnosticBuildId $DiagnosticBuildLabel
[xml]$projectXml = $projectText
$version = [string]$projectXml.SelectSingleNode('/Project/PropertyGroup/Version').InnerText
if ($version -cnotmatch '^\d+\.\d+\.\d+$') { throw 'Invalid private candidate version.' }
$candidateRoot = Assert-ReleasePath (Join-Path $repository 'artifacts/private-candidates') $repository
$stageDirectory = Assert-ReleasePath (Join-Path $candidateRoot ([guid]::NewGuid().ToString('N'))) $repository
[IO.Directory]::CreateDirectory($stageDirectory) | Out-Null
$publishDirectory = Join-Path $stageDirectory 'payload'
$updaterPublishDirectory = Join-Path $stageDirectory 'updater-publish'
$solution = Join-Path $repository 'Wisp.sln'
$appTestsProject = Join-Path $repository 'tests/Wisp.App.Tests/Wisp.App.Tests.csproj'
$updateTestsProject = Join-Path $repository 'tests/Wisp.Update.Tests/Wisp.Update.Tests.csproj'
$updaterTestsProject = Join-Path $repository 'tests/Wisp.Updater.Tests/Wisp.Updater.Tests.csproj'
$uiReviewProject = Join-Path $repository 'tools/Wisp.UiReview/Wisp.UiReview.csproj'
$testResults = Join-Path $stageDirectory 'test-results'
$allocationTests = @(
    'Wisp.App.Tests.TachRendererDiagnosticsTests.DisabledRendererDoesNotAllocateOrCreateCapture',
    'Wisp.App.Tests.TachRendererDiagnosticsTests.EnabledUncontendedProducerHasNoPerEventAllocations',
    'Wisp.App.Tests.TachDiagnosticsTests.EnabledNeedleRecordingReportsOfflineCostWithoutATimingThreshold',
    'Wisp.App.Tests.TachDiagnosticsTests.DisabledRecordCallsCreateNoHistoryOrPerCallAllocations'
)
$nonAllocationFilter = ($allocationTests | ForEach-Object { "FullyQualifiedName!=$_" }) -join '&'
$env:DOTNET_CLI_HOME = Join-Path $repository 'work/dotnet-home'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:PYTHONDONTWRITEBYTECODE = '1'
Push-Location $repository
try {
    & $dotnetExecutable restore $solution --locked-mode --disable-parallel -p:NuGetAudit=true -p:NuGetAuditMode=all -m:1
    if ($LASTEXITCODE -ne 0) { throw 'Private candidate locked restore failed.' }
    & $dotnetExecutable format $solution --verify-no-changes --no-restore --verbosity minimal
    if ($LASTEXITCODE -ne 0) { throw 'Private candidate formatting failed.' }
    & $dotnetExecutable build $solution --configuration Release --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Private candidate test-host build failed.' }
    & $pythonExecutable -m unittest discover -s (Join-Path $repository 'tools/tests') -p 'test_*.py' -v
    if ($LASTEXITCODE -ne 0) { throw 'Private candidate Python tests failed.' }
    & $dotnetExecutable build $uiReviewProject --configuration Release --no-restore --nologo --disable-build-servers -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Private candidate UI review build failed.' }
    foreach ($runtimeProject in @($project, $updaterProject)) {
        & $dotnetExecutable restore $runtimeProject --runtime win-x64 --locked-mode --disable-parallel `
            -p:NuGetAudit=true -p:NuGetAuditMode=all -m:1
        if ($LASTEXITCODE -ne 0) { throw 'Private candidate runtime restore failed.' }
    }
    & $dotnetExecutable publish $project --configuration Release --runtime win-x64 --self-contained true `
        --no-restore --output $publishDirectory -p:PublishSingleFile=false -p:PublishTrimmed=false `
        --disable-build-servers -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Private application publish failed.' }
    Assert-InstallerExecutable (Join-Path $publishDirectory 'Wisp.exe') $version 'Wisp' 'Wisp' $version
    Assert-NativeRendererLibrary (Join-Path $publishDirectory 'Wisp.NativeRenderer.dll')
    $decoderManifest = Join-Path $repository 'LICENSES/libvlc-3.0.24-source-manifest.json'
    $decoderHashes = Assert-PrivateLosslessPayload $publishDirectory $decoderManifest
    # Validate the exact RID-published components, not a separately compiled copy.
    # Test hosts keep their own dependencies/runtime configuration; only existing
    # Wisp product components are replaced before --no-build test execution.
    $componentNames = @('Wisp.dll', 'Wisp.Core.dll', 'Wisp.Telemetry.dll', 'Wisp.Update.dll', 'Wisp.NativeRenderer.dll', 'Wisp.Recorder.exe')
    $testHostDirectories = @(
        'tests/Wisp.Core.Tests/bin/Release/net8.0-windows',
        'tests/Wisp.Telemetry.Tests/bin/Release/net8.0',
        'tests/Wisp.App.Tests/bin/Release/net8.0-windows',
        'tests/Wisp.Update.Tests/bin/Release/net8.0-windows',
        'tests/Wisp.Updater.Tests/bin/Release/net8.0-windows',
        'tools/Wisp.UiReview/bin/Release/net8.0-windows')
    foreach ($directory in $testHostDirectories) {
        Assert-PrivateTestOutputDirectory (Join-Path $repository $directory) $repository
    }
    foreach ($directory in $testHostDirectories) {
        foreach ($name in $componentNames) {
            $destination = Join-Path $repository "$directory/$name"
            if (Test-Path -LiteralPath $destination -PathType Leaf) {
                [IO.File]::Copy((Join-Path $publishDirectory $name), $destination, $true)
                $symbols = [IO.Path]::ChangeExtension($name, '.pdb')
                if (Test-Path -LiteralPath (Join-Path $publishDirectory $symbols) -PathType Leaf) {
                    [IO.File]::Copy((Join-Path $publishDirectory $symbols), (Join-Path $repository "$directory/$symbols"), $true)
                }
            }
        }
    }
    foreach ($directory in @('tests/Wisp.App.Tests/bin/Release/net8.0-windows', 'tools/Wisp.UiReview/bin/Release/net8.0-windows')) {
        Sync-PrivateLosslessTestPayload $publishDirectory (Join-Path $repository $directory) $repository $decoderHashes
        $null = Assert-PrivateLosslessPayload (Join-Path $repository $directory) $decoderManifest
    }
    & $dotnetExecutable test $solution --configuration Release --no-build --no-restore --nologo --filter $nonAllocationFilter `
        --logger trx --results-directory $testResults --disable-build-servers -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Private candidate full test suite failed.' }
    foreach ($allocationTest in $allocationTests) {
        $allocationResults = Join-Path $testResults $allocationTest.Split('.')[-1]
        & $dotnetExecutable test $appTestsProject --configuration Release --no-build --no-restore --nologo `
            --filter "FullyQualifiedName=$allocationTest" --logger 'trx;LogFileName=allocation.trx' `
            --results-directory $allocationResults --disable-build-servers -m:1 -p:UseSharedCompilation=false
        if ($LASTEXITCODE -ne 0) { throw 'Private candidate isolated allocation check failed.' }
        Assert-SinglePassedTestResult (Join-Path $allocationResults 'allocation.trx') 'Private allocation check' $allocationTest
    }
    foreach ($name in @('Wisp.dll', 'Wisp.Core.dll', 'Wisp.Telemetry.dll', 'Wisp.Update.dll', 'Wisp.NativeRenderer.dll', 'Wisp.Recorder.exe')) {
        $publishedHash = (Get-FileHash -LiteralPath (Join-Path $publishDirectory $name) -Algorithm SHA256).Hash
        foreach ($testedDirectory in @('tests/Wisp.App.Tests/bin/Release/net8.0-windows', 'tools/Wisp.UiReview/bin/Release/net8.0-windows')) {
            if ($publishedHash -cne (Get-FileHash -LiteralPath (Join-Path $repository "$testedDirectory/$name") -Algorithm SHA256).Hash) {
                throw "Published $name differs from the tested and reviewed assembly."
            }
        }
    }
    foreach ($directory in @($publishDirectory, (Join-Path $repository 'tests/Wisp.App.Tests/bin/Release/net8.0-windows'),
        (Join-Path $repository 'tools/Wisp.UiReview/bin/Release/net8.0-windows'))) {
        $verified = Assert-PrivateLosslessPayload $directory $decoderManifest
        foreach ($relative in $decoderHashes.Keys) {
            if ($verified[$relative] -cne $decoderHashes[$relative]) { throw 'The decoder payload changed during private validation.' }
        }
    }
    & $dotnetExecutable publish $updaterProject --configuration Release --runtime win-x64 --self-contained true `
        --no-restore --output $updaterPublishDirectory -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:PublishTrimmed=true -p:TrimMode=full -p:EnableCompressionInSingleFile=true -p:DebugType=None `
        --disable-build-servers -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Private updater publish failed.' }
    $updaterPath = Join-Path $updaterPublishDirectory 'Wisp.Updater.exe'
    Assert-InstallerExecutable $updaterPath $version 'Wisp' 'Wisp Update Helper' $version
    Invoke-UpdaterGuardSmoke $updaterPath $stageDirectory
    $bundledUpdater = Join-Path $publishDirectory 'Wisp.Updater.exe'
    [IO.File]::Copy($updaterPath, $bundledUpdater, $false)
    Assert-InstallerExecutable $bundledUpdater $version 'Wisp' 'Wisp Update Helper' $version
    if ((Get-FileHash -LiteralPath $updaterPath -Algorithm SHA256).Hash -cne
        (Get-FileHash -LiteralPath $bundledUpdater -Algorithm SHA256).Hash) { throw 'Bundled updater hash mismatch.' }
    Assert-PrivatePayloadFiles $publishDirectory
    [IO.File]::Copy((Join-Path $PSScriptRoot 'PrivateShiftCaptureGuide.txt'),
        (Join-Path $publishDirectory 'PrivateShiftCaptureGuide.txt'), $false)

    $innoPath = Join-Path $repository 'installer/Wisp.iss'
    $inno = [IO.File]::ReadAllText($innoPath)
    $innoHash = (Get-FileHash -LiteralPath $innoPath -Algorithm SHA256).Hash
    $inno = Replace-PrivateDirective $inno '#define MyAppDisplayVersion MyAppVersion' ('#define MyAppDisplayVersion "' + $DiagnosticBuildLabel + ' (private)"')
    $outputVersion = "$version-$DiagnosticBuildId"
    $inno = Replace-PrivateDirective $inno '#define MyAppOutputVersion MyAppVersion' ('#define MyAppOutputVersion "' + $outputVersion + '"')
    $compilerSourceDirectory = Get-PrivateCompilerSourcePath $publishDirectory
    $inno = Replace-PrivateDirective $inno 'Source: "..\artifacts\publish\*"' ('Source: "' + (Join-Path $compilerSourceDirectory '*') + '"')
    $inno = Replace-PrivateDirective $inno 'SetupIconFile=..\src\Wisp.App\Assets\Wisp.ico' ('SetupIconFile=' + (Join-Path $repository 'src/Wisp.App/Assets/Wisp.ico'))
    $privateInno = Join-Path $stageDirectory 'Wisp.Private.iss'
    [IO.File]::WriteAllText($privateInno, $inno, [Text.UTF8Encoding]::new($false))
    $payload = @{}
    foreach ($file in Get-ChildItem -LiteralPath $publishDirectory -Recurse -File) {
        $payload[[IO.Path]::GetRelativePath($publishDirectory, $file.FullName)] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    & $innoExecutable "/O$stageDirectory" $privateInno
    if ($LASTEXITCODE -ne 0) { throw 'Private Inno compilation failed.' }
    foreach ($name in $payload.Keys) {
        if ($payload[$name] -cne (Get-FileHash -LiteralPath (Join-Path $publishDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant()) {
            throw 'Private payload changed during compilation.'
        }
    }
    if ($canonicalHash -cne (Get-FileHash -LiteralPath $canonical -Algorithm SHA256).Hash -or
        $innoHash -cne (Get-FileHash -LiteralPath $innoPath -Algorithm SHA256).Hash) { throw 'Canonical packaging source changed.' }
    $setupPath = Join-Path $stageDirectory "Wisp-Setup-$outputVersion.exe"
    Assert-InstallerExecutable $setupPath $version 'Wisp' 'Wisp installer' "$version.0"
    Invoke-InstallerRuntimeValidation $dotnetExecutable $setupPath $version $updateTestsProject $updaterTestsProject
    $hash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($setupPath + '.sha256'), "$hash *$([IO.Path]::GetFileName($setupPath))$([Environment]::NewLine)", [Text.UTF8Encoding]::new($false))
    $archivePath = [IO.Path]::ChangeExtension($setupPath, '.zip')
    New-InstallerArchive $setupPath $archivePath $hash
    $archiveHash = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText(($archivePath + '.sha256'), "$archiveHash *$([IO.Path]::GetFileName($archivePath))$([Environment]::NewLine)", [Text.UTF8Encoding]::new($false))
    $report = [ordered]@{ kind = 'private-diagnostic-installer'; version = $version; diagnosticBuildId = $DiagnosticBuildId;
        diagnosticBuildLabel = $DiagnosticBuildLabel; sourceRevision = $source.Revision; sourceDirty = $source.IsDirty;
        createdUtc = [DateTime]::UtcNow.ToString('o'); installerSha256 = $hash; archiveSha256 = $archiveHash; payload = $payload;
        validation = 'Full solution, four isolated allocation checks, Python tests, updater guard, installer runtime identity and ZIP checksum validation passed. Live gameplay timing is not certified.' }
    [IO.File]::WriteAllText((Join-Path $stageDirectory 'PRIVATE-PACKAGE-CHECKS.json'), ($report | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    Write-Output "Private test installer: $setupPath"
    Write-Output "Private test archive: $archivePath"
}
finally { Pop-Location }
