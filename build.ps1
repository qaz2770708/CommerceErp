param([switch]$SkipTests)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot
$env:DOTNET_CLI_HOME = Join-Path ([System.IO.Path]::GetTempPath()) 'CommerceErp.DotnetHome'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_NOLOGO = 'true'
function Invoke-Dotnet { & dotnet @args; if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" } }
Invoke-Dotnet build CommerceErp.slnx -c Release --nologo
if (-not $SkipTests) {
    Invoke-Dotnet run --project tests/CommerceErp.Tests -c Release --no-build -- (Join-Path $PSScriptRoot 'verification/unit-results.json')
    Invoke-Dotnet run --project tests/CommerceErp.BusinessTests -c Release --no-build -- (Join-Path $PSScriptRoot 'verification/business-results.json')
    Invoke-Dotnet run --project tests/CommerceErp.WarehouseTests -c Release --no-build -- (Join-Path $PSScriptRoot 'verification/warehouse-results.json')
    Invoke-Dotnet run --project tests/CommerceErp.AfterSaleTests -c Release --no-build -- (Join-Path $PSScriptRoot 'verification/after-sale-results.json')
    Invoke-Dotnet run --project tests/CommerceErp.FinanceTests -c Release --no-build -- (Join-Path $PSScriptRoot 'verification/finance-results.json')
    $serviceEvidence = Join-Path ([IO.Path]::GetTempPath()) ('CommerceErp.ServiceTests-' + [Guid]::NewGuid().ToString('N'))
    Invoke-Dotnet run --project tests/CommerceErp.ServiceTests -c Release --no-build -- $serviceEvidence $PSScriptRoot
    Copy-Item -LiteralPath (Join-Path $serviceEvidence 'service-results.json') -Destination 'verification/service-results.json' -Force
    $accountEvidence = Join-Path ([IO.Path]::GetTempPath()) ('CommerceErp.AccountTests-' + [Guid]::NewGuid().ToString('N'))
    Invoke-Dotnet run --project tests/CommerceErp.AccountTests -c Release --no-build -- $accountEvidence $PSScriptRoot
    Copy-Item -LiteralPath (Join-Path $accountEvidence 'account-results.json') -Destination 'verification/account-results.json' -Force
    $scopeEvidence = Join-Path ([IO.Path]::GetTempPath()) ('CommerceErp.ScopeTests-' + [Guid]::NewGuid().ToString('N'))
    Invoke-Dotnet run --project tests/CommerceErp.ScopeTests -c Release --no-build -- $scopeEvidence $PSScriptRoot
    Copy-Item -LiteralPath (Join-Path $scopeEvidence 'scope-results.json') -Destination 'verification/scope-results.json' -Force
    Invoke-Dotnet run --project tests/CommerceErp.InventoryDocumentTests -c Release --no-build -- (Join-Path $PSScriptRoot 'verification/inventory-document-results.json') $PSScriptRoot
    Invoke-Dotnet run --project tests/CommerceErp.MasterTests -c Release --no-build -- (Join-Path $PSScriptRoot 'verification/master-results.json') $PSScriptRoot
    Invoke-Dotnet run --project tests/CommerceErp.TaskTests -c Release --no-build -- (Join-Path $PSScriptRoot 'verification/task-results.json')
    $capacityEvidence = Join-Path ([IO.Path]::GetTempPath()) ('CommerceErp.CapacityTests-' + [Guid]::NewGuid().ToString('N'))
    Invoke-Dotnet run --project tests/CommerceErp.CapacityTests -c Release --no-build -- $capacityEvidence $PSScriptRoot
    Copy-Item -LiteralPath (Join-Path $capacityEvidence 'capacity-results.json') -Destination 'verification/capacity-results.json' -Force
    Copy-Item -LiteralPath (Join-Path $capacityEvidence 'capacity-report.json') -Destination 'verification/capacity-report.json' -Force
    Invoke-Dotnet run --project tests/CommerceErp.SqlServerTests -c Release --no-build -- --schema $PSScriptRoot (Join-Path $PSScriptRoot 'verification/sql-schema-results.json')
}
Invoke-Dotnet publish src/CommerceErp.Desktop -c Release --no-restore --self-contained false -o app --nologo
Invoke-Dotnet publish src/CommerceErp.Api -c Release --no-restore --self-contained false -o app/server --nologo
Copy-Item -LiteralPath 'lib/sqlite/APACHE-2.0.txt','lib/sqlite/MIT-Microsoft.Data.Sqlite.txt','lib/sqlite/THIRD-PARTY-NOTICES.txt' -Destination 'app/server' -Force
Invoke-Dotnet publish src/CommerceErp.DatabaseTool -c Release --no-restore --self-contained false -o app/tools --nologo
Write-Output 'Ready: app/CommerceErp.Desktop.exe'
