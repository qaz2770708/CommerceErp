param([Parameter(Mandatory)][string]$Configuration)
$ErrorActionPreference = 'Stop'
$configurationPath = (Resolve-Path -LiteralPath $Configuration).Path
$server = Join-Path (Split-Path -Parent $PSScriptRoot) 'app/server/CommerceErp.Api.exe'
if (-not (Test-Path -LiteralPath $server)) { throw '先运行 build.ps1 生成服务端。' }
& $server --server $configurationPath
if ($LASTEXITCODE -ne 0) { throw "服务退出，代码 $LASTEXITCODE。" }
