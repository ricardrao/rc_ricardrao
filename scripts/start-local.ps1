param([int]$ApiPort = 5055, [int]$ConsolePort = 8090)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
$toolRoot = Join-Path (Split-Path $projectRoot -Parent) '.tools'
$apiRoot = Join-Path $projectRoot 'src/NotificationHub.Api'
$runtimeDirectory = Join-Path $projectRoot '.local'
New-Item -ItemType Directory -Force -Path $runtimeDirectory | Out-Null
$dotnetPath = Join-Path $toolRoot 'dotnet/dotnet.exe'
$pythonPath = (Get-Command python).Source
if (-not (Test-Path -LiteralPath $dotnetPath)) { throw 'The workspace-local .NET SDK is missing. Use README setup instructions.' }
function Test-LocalPort([int]$Port) {
    return [bool](Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue)
}
if (-not (Test-LocalPort 55432)) {
    & (Join-Path $toolRoot 'pgsql/bin/pg_ctl.exe') -D (Join-Path $toolRoot 'pgdata-notification') -l (Join-Path $toolRoot 'pg-notification.log') -o '-p 55432 -h 127.0.0.1' start
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL did not start.' }
}
if (-not (Test-LocalPort 56379)) {
    Start-Process -FilePath (Join-Path $toolRoot 'redis/redis-server.exe') -ArgumentList '--port 56379 --bind 127.0.0.1 --appendonly no' -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runtimeDirectory 'redis.log') -RedirectStandardError (Join-Path $runtimeDirectory 'redis-error.log')
}
if (-not (Test-LocalPort 8081)) {
    Start-Process -FilePath $pythonPath -ArgumentList 'examples/mock_supplier.py' -WorkingDirectory $projectRoot -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runtimeDirectory 'mock.log') -RedirectStandardError (Join-Path $runtimeDirectory 'mock-error.log')
}
# Development-only identities, scoped to the local demonstration database.
$env:ConnectionStrings__Postgres = 'Host=127.0.0.1;Port=55432;Database=notification_hub_demo;Username=postgres'
$env:ConnectionStrings__Redis = '127.0.0.1:56379'
$env:Clients__0__Id = 'business-demo'
$env:Clients__0__Key = 'local-business-demo-key-12345678'
$env:Clients__0__Role = 'business'
$env:Clients__1__Id = 'provider-demo'
$env:Clients__1__Key = 'local-provider-demo-key-12345678'
$env:Clients__1__Role = 'provider'
$env:Hub__AllowedHosts__0 = '127.0.0.1'
$env:Hub__AllowPrivateNetworks = 'true'
$env:Hub__TicketWebhookUrl = 'http://127.0.0.1:8081/tickets'
$env:Hub__TicketWebhookSecret = 'local-ticket-demo-secret-12345678'
$env:ASPNETCORE_URLS = "http://127.0.0.1:$ApiPort"
$env:HUB_URL = "http://127.0.0.1:$ApiPort"
$env:CONSOLE_PORT = "$ConsolePort"
$env:BUSINESS_API_KEY = $env:Clients__0__Key
$env:PROVIDER_API_KEY = $env:Clients__1__Key
if (-not (Test-LocalPort $ApiPort)) {
    $apiProcess = Start-Process -FilePath $dotnetPath -ArgumentList 'bin/Release/net10.0/NotificationHub.Api.dll' -WorkingDirectory $apiRoot -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runtimeDirectory 'api.log') -RedirectStandardError (Join-Path $runtimeDirectory 'api-error.log') -PassThru
    Set-Content -LiteralPath (Join-Path $runtimeDirectory 'api.pid') -Value $apiProcess.Id
}
if (-not (Test-LocalPort $ConsolePort)) {
    $consoleProcess = Start-Process -FilePath $pythonPath -ArgumentList 'examples/console_gateway.py' -WorkingDirectory $projectRoot -WindowStyle Hidden -RedirectStandardOutput (Join-Path $runtimeDirectory 'console.log') -RedirectStandardError (Join-Path $runtimeDirectory 'console-error.log') -PassThru
    Set-Content -LiteralPath (Join-Path $runtimeDirectory 'console.pid') -Value $consoleProcess.Id
}
Write-Output "Console: http://127.0.0.1:$ConsolePort"
Write-Output "API: http://127.0.0.1:$ApiPort"
