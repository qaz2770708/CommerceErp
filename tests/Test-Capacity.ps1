param(
    [Parameter(Mandatory)][string]$ReportDirectory,
    [ValidateRange(100,5000)][int]$Orders = 200,
    [ValidateRange(1,64)][int]$Concurrency = 8,
    [string]$SqlDatabase
)
$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$reportPath = [IO.Path]::GetFullPath($ReportDirectory)
if (Test-Path -LiteralPath $reportPath) { throw '容量测试目录必须是全新目录。' }
$env:DOTNET_CLI_HOME = Join-Path ([IO.Path]::GetTempPath()) 'CommerceErp.DotnetHome'
if ($SqlDatabase -and [string]::IsNullOrWhiteSpace($env:ERP_SQL_TEST_CONNECTION)) { throw 'SQL 测试须在本机设置 ERP_SQL_TEST_CONNECTION，目标须为专用空测试库。' }
$projectPath = Join-Path $project 'tests/CommerceErp.CapacityTests'
& dotnet build $projectPath -c Release --nologo
if ($LASTEXITCODE -ne 0) { throw '容量测试编译失败。' }
$arguments = @('run','--project',$projectPath,'-c','Release','--no-build','--',$reportPath,$project,'--orders',$Orders.ToString(),'--concurrency',$Concurrency.ToString())
if ($SqlDatabase) { $arguments += @('--sql',$SqlDatabase) }
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw '容量测试失败；检查报告与退出原因。' }
