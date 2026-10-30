param([Parameter(Mandatory)][string]$Database, [Parameter(Mandatory)][string]$Report)
$ErrorActionPreference = 'Stop'
if (-not $env:ERP_SQL_TEST_CONNECTION) { throw '请在本机设置 ERP_SQL_TEST_CONNECTION，指向已创建的专用空测试库；连接必须验证证书。' }
$reportPath = [IO.Path]::GetFullPath($Report)
if (Test-Path -LiteralPath $reportPath) { throw '报告文件已存在，请使用新路径。' }
$root = Split-Path -Parent $PSScriptRoot
& dotnet run --project (Join-Path $PSScriptRoot 'CommerceErp.SqlServerTests') -c Release -- --sql $root $reportPath $Database
if ($LASTEXITCODE -ne 0) { throw 'SQL Server 实机验收未通过。保留报告和快照，目标测试库不会自动删除。' }
