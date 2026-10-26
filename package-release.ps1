param([switch]$UseExistingBuild)
$ErrorActionPreference = 'Stop'
if (-not $UseExistingBuild) { & (Join-Path $PSScriptRoot 'build.ps1') -RunTests }

$exe = Join-Path $PSScriptRoot 'dist\Win11AssistantBridge.exe'
$report = Join-Path $PSScriptRoot 'dist\test-results.txt'
$receiptPath = Join-Path $PSScriptRoot 'dist\build-verification.json'
$receipt = Get-Content -LiteralPath $receiptPath -Raw -Encoding utf8 | ConvertFrom-Json
if ((Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash -ne $receipt.sha256) { throw 'Executable changed since its test run. Run build.ps1 -RunTests first.' }
if ((Get-FileHash -LiteralPath $report -Algorithm SHA256).Hash -ne $receipt.reportSha256) { throw 'Test report changed since its verification receipt.' }
if ($receipt.testsFailed -ne 0 -or $receipt.testsPassed -lt 30) { throw 'The required tests did not pass.' }
$version = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
if ($version -ne $receipt.version -or $version -notmatch '^\d+\.\d+\.\d+\.0$') { throw 'Invalid or mismatched release version.' }
$shortVersion = $version -replace '\.0$', ''
$releaseRoot = Join-Path $PSScriptRoot ('release\' + $shortVersion)
$validation = Join-Path $PSScriptRoot ('validation\' + $shortVersion)
New-Item -ItemType Directory -Path $releaseRoot,$validation -Force | Out-Null

$releaseExe = Join-Path $releaseRoot ('Win11Updater-' + $shortVersion + '.exe')
Copy-Item -LiteralPath $exe -Destination $releaseExe -Force
Copy-Item -LiteralPath $report -Destination (Join-Path $validation 'self-tests.txt') -Force
Copy-Item -LiteralPath $receiptPath -Destination (Join-Path $validation 'build-verification.json') -Force
$guide = Join-Path $releaseRoot 'הוראות-הפעלה.txt'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'docs\QUICKSTART.he.txt') -Destination $guide -Force
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'LICENSE') -Destination (Join-Path $releaseRoot 'LICENSE.txt') -Force

$sourceArchive = Join-Path $releaseRoot ('Win11Updater-' + $shortVersion + '-source.zip')
$sourceItems = @(
    (Join-Path $PSScriptRoot 'src'), (Join-Path $PSScriptRoot 'docs'),
    (Join-Path $PSScriptRoot 'build.ps1'), (Join-Path $PSScriptRoot 'package-release.ps1'),
    (Join-Path $PSScriptRoot 'README.md'), (Join-Path $PSScriptRoot 'LICENSE'),
    (Join-Path $PSScriptRoot '.gitignore'), (Join-Path $PSScriptRoot '.gitattributes'),
    (Join-Path $PSScriptRoot 'CONTRIBUTING.md'), (Join-Path $PSScriptRoot '.github'),
    (Join-Path $PSScriptRoot 'validation')
)
Compress-Archive -LiteralPath $sourceItems -DestinationPath $sourceArchive -Force

$manifest = [ordered]@{
    version = $version
    createdUtc = [DateTime]::UtcNow.ToString('o')
    executable = [IO.Path]::GetFileName($releaseExe)
    executableSha256 = (Get-FileHash -LiteralPath $releaseExe -Algorithm SHA256).Hash
    sourceArchive = [IO.Path]::GetFileName($sourceArchive)
    sourceSha256 = (Get-FileHash -LiteralPath $sourceArchive -Algorithm SHA256).Hash
    selfTestsPassed = $receipt.testsPassed
    signatureStatus = [string](Get-AuthenticodeSignature -LiteralPath $releaseExe).Status
    microsoftAssistantBundled = $false
    osUpgradeTestedByAgent = $false
    notes = 'Component-tested release; unsupported-hardware success is device-dependent. Microsoft Assistant is downloaded and verified at runtime.'
}
$manifestPath = Join-Path $releaseRoot 'release-manifest.json'
$manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding utf8
Get-FileHash -LiteralPath $releaseExe,$sourceArchive,$guide,$manifestPath -Algorithm SHA256 |
    ForEach-Object { $_.Hash + '  ' + [IO.Path]::GetFileName($_.Path) } |
    Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') -Encoding utf8
Get-ChildItem -LiteralPath $releaseRoot | Select-Object Name,Length
Write-Output ('Release directory: ' + $releaseRoot)
