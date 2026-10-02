function Assert-RecorderExecutable {
    param([Parameter(Mandatory)][string]$Path)
    $file = Get-Item -LiteralPath $Path -Force
    if ($file.PSIsContainer -or ($file.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The recorder must be a regular executable file.'
    }
    $reader = [IO.BinaryReader]::new([IO.File]::OpenRead($Path))
    try {
        if ($reader.BaseStream.Length -lt 64 -or $reader.ReadUInt16() -ne 0x5A4D) {
            throw 'The recorder has an invalid executable header.'
        }
        $reader.BaseStream.Position = 0x3C
        $offset = $reader.ReadUInt32()
        if ($offset -lt 64 -or $offset -gt $reader.BaseStream.Length - 24) {
            throw 'The recorder has invalid PE header bounds.'
        }
        $reader.BaseStream.Position = $offset
        if ($reader.ReadUInt32() -ne 0x00004550 -or $reader.ReadUInt16() -ne 0x8664 -or $reader.ReadUInt16() -eq 0) {
            throw 'The recorder must be an AMD64 executable.'
        }
        $reader.BaseStream.Position = $offset + 20
        $optionalSize = $reader.ReadUInt16()
        $flags = $reader.ReadUInt16()
        if (($flags -band 0x2002) -ne 0x0002 -or $optionalSize -lt 112 -or
            $offset + 24 + $optionalSize -gt $reader.BaseStream.Length -or $reader.ReadUInt16() -ne 0x020B) {
            throw 'The recorder must be a complete PE32+ executable, not a DLL.'
        }
    }
    finally { $reader.Dispose() }
}

function Get-ClipRegularFiles {
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

function Assert-VlcPayload {
    param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$ManifestPath)
    $root = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    # Check every ancestor/file before validating or following nested payload paths.
    $regular = @{}
    foreach ($file in Get-ClipRegularFiles $root) {
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

function Assert-MpvPayload {
    param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$ManifestPath,
        [Parameter(Mandatory)][string]$DependencyManifestPath)
    $root = [IO.Path]::GetFullPath($Directory).TrimEnd('\', '/')
    $regular = @{}
    foreach ($file in Get-ClipRegularFiles $root) {
        $regular[$file.FullName.Substring($root.Length + 1).Replace('\', '/')] = $file
    }
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
    $dependency = Get-Content -LiteralPath $DependencyManifestPath -Raw | ConvertFrom-Json
    if ($null -eq $manifest.PSObject.Properties['sourceClosure'] -or
        $manifest.sourceClosure.status -cne 'complete' -or $manifest.sourceClosure.distributionReady -ne $true) {
        throw 'mpv packaging requires the completed reviewed source and license companion.'
    }
    $native = @($manifest.nativeFiles)
    if ($manifest.schemaVersion -ne 1 -or $dependency.schemaVersion -ne 1 -or $native.Count -ne 1 -or
        $native[0].path -cne 'libmpv-2.dll' -or $dependency.binary.entry -cne 'libmpv-2.dll' -or
        $native[0].bytes -ne $dependency.binary.bytes -or $native[0].sha256 -cne $dependency.binary.sha256) {
        throw 'The mpv source and dependency manifests must identify the same single runtime.'
    }
    $expected = @{ 'libmpv/win-x64/libmpv-2.dll' = $native[0] }
    $actualNative = @($regular.Keys | Where-Object { $_.StartsWith('libmpv/', [StringComparison]::OrdinalIgnoreCase) })
    if ($actualNative.Count -ne 1 -or $actualNative[0] -cne 'libmpv/win-x64/libmpv-2.dll') {
        throw 'The published mpv tree differs from the selected runtime.'
    }
    $notices = @($manifest.noticeFiles)
    if ($notices.Count -eq 0 -or $notices.Count -gt 256) { throw 'The mpv notice manifest is missing or unbounded.' }
    foreach ($entry in $notices) {
        $relative = [string]$entry.path
        if ($relative -cnotmatch '^(LGPL-3\.0\.txt|GPL-3\.0\.txt|libmpv-thirdparty/[A-Za-z0-9_.-]+(/[A-Za-z0-9_.-]+)*)$' -or
            @($relative.Split('/') | Where-Object { $_ -eq '.' -or $_ -eq '..' }).Count -ne 0 -or
            $expected.ContainsKey("Licenses/$relative")) { throw 'Invalid or duplicate mpv notice path.' }
        $expected["Licenses/$relative"] = $entry
    }
    foreach ($required in @('Licenses/LGPL-3.0.txt', 'Licenses/GPL-3.0.txt')) {
        if (-not $expected.ContainsKey($required)) { throw 'The mpv manifest must include both required license texts.' }
    }
    $actualNotices = @($regular.Keys | Where-Object { $_.StartsWith('Licenses/libmpv-thirdparty/', [StringComparison]::OrdinalIgnoreCase) })
    if (@($actualNotices | Where-Object { -not $expected.ContainsKey($_) }).Count -ne 0) {
        throw 'The published mpv notices differ from the reviewed manifest.'
    }
    $hashes = @{}
    foreach ($relative in $expected.Keys) {
        $entry = $expected[$relative]
        if (-not $regular.ContainsKey($relative) -or $entry.bytes -le 0 -or $regular[$relative].Length -ne $entry.bytes -or
            [string]$entry.sha256 -cnotmatch '^[a-f0-9]{64}$') { throw 'An mpv runtime file or notice is missing or has an invalid size.' }
        $hash = (Get-FileHash -LiteralPath $regular[$relative].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -cne $entry.sha256) { throw 'An mpv runtime file or notice differs from its provenance manifest.' }
        $hashes[$relative] = $hash
    }
    foreach ($pair in @(@('Licenses/libmpv-source-manifest.json', $ManifestPath), @('Licenses/mpv-dependency.json', $DependencyManifestPath))) {
        if (-not $regular.ContainsKey($pair[0])) { throw 'An mpv provenance manifest is missing.' }
        $hash = (Get-FileHash -LiteralPath $regular[$pair[0]].FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($hash -cne (Get-FileHash -LiteralPath $pair[1] -Algorithm SHA256).Hash.ToLowerInvariant()) {
            throw 'A published mpv manifest differs from the reviewed source.'
        }
        $hashes[$pair[0]] = $hash
    }
    return $hashes
}

function Assert-ClipDecoders {
    param([Parameter(Mandatory)][string]$Directory, [Parameter(Mandatory)][string]$VlcManifestPath,
        [Parameter(Mandatory)][string]$MpvManifestPath, [Parameter(Mandatory)][string]$MpvDependencyPath)
    $hashes = Assert-VlcPayload $Directory $VlcManifestPath
    $mpv = Assert-MpvPayload $Directory $MpvManifestPath $MpvDependencyPath
    foreach ($relative in $mpv.Keys) {
        if ($hashes.ContainsKey($relative)) { throw 'Decoder manifests contain a duplicate payload path.' }
        $hashes[$relative] = $mpv[$relative]
    }
    return $hashes
}

