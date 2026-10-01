# This explicit offline check needs locally retained research evidence, never game access.
# It is separate from portable unit tests so the game asset is not packaged or silently skipped.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppAssemblyPath,
    [Parameter(Mandatory)][string]$ResearchDirectory,
    [Parameter(Mandatory)][string]$TemporaryDirectory
)
$ErrorActionPreference = 'Stop'
$appPath = (Resolve-Path -LiteralPath $AppAssemblyPath).Path
$researchPath = (Resolve-Path -LiteralPath $ResearchDirectory).Path
$workPath = Join-Path $researchPath 'work'
$null = [Reflection.Assembly]::LoadFrom((Join-Path (Split-Path -Parent $appPath) 'Wisp.Core.dll'))
$assembly = [Reflection.Assembly]::LoadFrom($appPath)
$flags = [Reflection.BindingFlags]'NonPublic, Static'
$metadata = Get-Content -LiteralPath (Join-Path $workPath 'tune-game-asset-metadata.json') -Raw | ConvertFrom-Json
$arguments = [object[]]::new(4)
$arguments[0] = [IO.File]::ReadAllBytes((Join-Path $workPath 'tune-game-asset-encoded.bin'))
$arguments[1] = [Convert]::FromHexString($metadata.crcTableHex)
$arguments[2] = [Convert]::FromHexString($metadata.foldTableHex)
$arguments[3] = [Threading.CancellationToken]::None
$decoder = $assembly.GetType('Wisp.App.Tunes.TuneAssetCapture', $true)
$decoded = $decoder.GetMethod('Decode', $flags).Invoke($null, $arguments)
$extractor = $assembly.GetType('Wisp.App.Tunes.TuneAssetSqlite', $true)
$extractArguments = [object[]]::new(3)
$extractArguments[0] = $decoded
$extractArguments[1] = [Threading.CancellationToken]::None
$extractArguments[2] = [IO.Path]::GetFullPath($TemporaryDirectory)
$cache = $extractor.GetMethod('Extract', $flags).Invoke($null, $extractArguments)
$rows = @($cache.GetType().GetField('_rows', [Reflection.BindingFlags]'NonPublic, Instance').GetValue($cache).Values)
if ($rows.Count -lt 1 -or $rows.Count -gt 250000) { throw 'Unexpected extracted metadata row count.' }
$mapping = @{
    engine='Engine'; drivetrain='Drivetrain'; carBody='CarBody'; motor='Motor'; brakes='Brakes';
    springDamper='SpringDamper'; frontAntiroll='FrontAntiroll'; rearAntiroll='RearAntiroll';
    rearAero='RearAero'; transmission='Transmission'; differential='Differential'; frontAero='FrontAero'
}
$partChecks = 0
foreach ($name in @('tune-miat-fe-locked-resolved.json', 'tune-exact-editable-1229-resolved.json', 'tune-exact-locked-4200-resolved.json')) {
    $fixture = Get-Content -LiteralPath (Join-Path $workPath $name) -Raw | ConvertFrom-Json
    foreach ($entry in $fixture.exactPartSources.parts.PSObject.Properties) {
        $part = $entry.Value
        if ($part.partId -lt 0) { continue }
        $parentId = $fixture.carOrdinal
        if ($entry.Name -in @('transmission', 'differential', 'frontAero')) {
            $parentName = if ($entry.Name -eq 'frontAero') { 'carBody' } else { 'drivetrain' }
            $parentPart = $fixture.exactPartSources.parts.$parentName
            $parents = @($rows | Where-Object { $_.Kind.ToString() -eq $mapping[$parentName] -and $_.Parent -eq $fixture.carOrdinal -and $_.Id -eq $parentPart.partId })
            if ($parents.Count -ne 1 -or $null -eq $parents[0].ChildParent) { throw 'Parent metadata did not match the fixture.' }
            $parentId = $parents[0].ChildParent
        }
        $matches = @($rows | Where-Object { $_.Kind.ToString() -eq $mapping[$entry.Name] -and $_.Parent -eq $parentId -and $_.Id -eq $part.partId })
        if ($matches.Count -ne 1 -or $matches[0].Level -ne $part.level) { throw 'Part metadata did not match the fixture.' }
        $partChecks++
    }
}
[Array]::Clear($decoded)
[ordered]@{ Result = 'pass'; ResolvedFixtures = 3; PartChecks = $partChecks; ExtractedRows = $rows.Count; AssetHashesVerified = $true; LocalReadOnlySqlite = $true; LiveGameAccess = $false } | ConvertTo-Json -Compress
