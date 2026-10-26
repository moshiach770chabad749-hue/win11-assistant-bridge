param([switch]$RunTests)
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'Required: Windows x64 with .NET Framework 4.8 compiler.' }
$outputDir = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' | ForEach-Object FullName)
$exe = Join-Path $outputDir 'Win11AssistantBridge.exe'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /utf8output /warnaserror+ /codepage:65001 "/out:$exe" "/win32manifest:$PSScriptRoot\src\app.manifest" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Runtime.Serialization.dll /reference:System.Net.Http.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll $sourceFiles
if ($LASTEXITCODE -ne 0) { throw "Compilation failed: $LASTEXITCODE" }
Get-FileHash -LiteralPath $exe -Algorithm SHA256 | Format-List
if ($RunTests) {
    $report = Join-Path $outputDir 'test-results.txt'
    $testProcess = Start-Process -FilePath $exe -ArgumentList @('--self-test', ('"' + $report + '"')) -WindowStyle Hidden -Wait -PassThru
    Get-Content -LiteralPath $report -Encoding utf8
    if ($testProcess.ExitCode -ne 0) { throw 'Self-tests failed.' }
    $summary = [regex]::Match((Get-Content -LiteralPath $report -Raw -Encoding utf8), 'TOTAL tests=(\d+) failed=0 UTC=')
    if (-not $summary.Success) { throw 'A successful self-test summary was not found.' }
    $receipt = [ordered]@{
        version = (Get-Item -LiteralPath $exe).VersionInfo.FileVersion
        executable = [IO.Path]::GetFileName($exe)
        sha256 = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
        reportSha256 = (Get-FileHash -LiteralPath $report -Algorithm SHA256).Hash
        testsPassed = [int]$summary.Groups[1].Value
        testsFailed = 0
        verifiedUtc = [DateTime]::UtcNow.ToString('o')
        osUpgradeTestedByAgent = $false
    }
    $receipt | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputDir 'build-verification.json') -Encoding utf8
}
