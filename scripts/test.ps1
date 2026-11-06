param([switch]$Integration)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$installedDotnet = Get-Command dotnet -ErrorAction SilentlyContinue
$dotnetPath = if ($installedDotnet) { $installedDotnet.Source } else { Join-Path (Split-Path $projectRoot -Parent) '.tools/dotnet/dotnet.exe' }
if (-not (Test-Path -LiteralPath $dotnetPath)) { throw 'Install the .NET 10 SDK and place dotnet on PATH.' }
if ($Integration -and (-not $env:TEST_POSTGRES -or -not $env:TEST_REDIS)) {
    throw 'Integration mode requires TEST_POSTGRES and TEST_REDIS.'
}
Push-Location $projectRoot
try {
    $testArguments = @('test', 'NotificationHub.sln', '-c', 'Release', '--logger', 'trx', '--results-directory', 'TestResults')
    if (-not $Integration) { $testArguments += @('--filter', 'Category!=Integration') }
    & $dotnetPath @testArguments
    if ($LASTEXITCODE -ne 0) { throw "Tests failed with exit code $LASTEXITCODE." }
} finally { Pop-Location }
