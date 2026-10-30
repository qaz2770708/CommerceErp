param([Parameter(Mandatory)][string]$Report)
$ErrorActionPreference = 'Stop'
$reportPath = [IO.Path]::GetFullPath($Report)
if (Test-Path -LiteralPath $reportPath) { throw '报告文件必须是新文件。' }
$project = Split-Path -Parent $PSScriptRoot
$runtimeStatus = 'Missing'; $desktopRuntime = $false; $webRuntime = $false
if (Get-Command dotnet -ErrorAction SilentlyContinue) {
    $runtimes = @(& dotnet --list-runtimes 2>$null)
    if ($LASTEXITCODE -eq 0) {
        $runtimeStatus = 'Detected'
        $desktopRuntime = [bool]($runtimes -match '^Microsoft.WindowsDesktop.App 10\.')
        $webRuntime = [bool]($runtimes -match '^Microsoft.AspNetCore.App 10\.')
    }
}
$serviceStatus = 'NotInstalled'
try { $service = Get-Service -Name CommerceErp -ErrorAction Stop; $serviceStatus = $service.Status.ToString() } catch { }
$sqlConfigured = -not [string]::IsNullOrWhiteSpace($env:ERP_SQL_TEST_CONNECTION)
$sqlIdentityStatus = if ($sqlConfigured) { 'ToolUnavailable' } else { 'NotConfigured' }
$sqlSchema = $null
$tool = Join-Path $project 'app/tools/CommerceErp.DatabaseTool.exe'
if ($sqlConfigured -and (Test-Path -LiteralPath $tool)) {
    # Read-only identity probe. Never echo the connection string or raw tool error.
    $processInfo = [Diagnostics.ProcessStartInfo]::new($tool)
    $processInfo.UseShellExecute = $false; $processInfo.CreateNoWindow = $true
    $processInfo.RedirectStandardOutput = $true; $processInfo.RedirectStandardError = $true
    $processInfo.ArgumentList.Add('identity')
    $processInfo.Environment['ERP_SQL_CONNECTION'] = $env:ERP_SQL_TEST_CONNECTION
    $probe = $null
    try {
        $probe = [Diagnostics.Process]::Start($processInfo)
        $stdout = $probe.StandardOutput.ReadToEndAsync(); $stderr = $probe.StandardError.ReadToEndAsync()
        if (-not $probe.WaitForExit(20000)) { $probe.Kill($true); $probe.WaitForExit(); $sqlIdentityStatus = 'Timeout' }
        elseif ($probe.ExitCode -eq 0) {
            $identity = $stdout.GetAwaiter().GetResult() | ConvertFrom-Json
            $sqlSchema = $identity.schema
            $sqlIdentityStatus = if ($sqlSchema -eq 10) { 'Schema10Detected' } else { 'UpgradeRequired' }
        } else { $sqlIdentityStatus = 'ConnectionOrIdentityRejected' }
        $null = $stderr.GetAwaiter().GetResult()
    } catch { $sqlIdentityStatus = 'ProbeFailed' }
    finally { if ($probe) { $probe.Dispose() } }
}
$reportData = [ordered]@{
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    readOnly = $true
    runtime = $runtimeStatus
    desktopRuntime10 = $desktopRuntime
    aspNetRuntime10 = $webRuntime
    windowsServiceStatus = $serviceStatus
    sqlTestConnectionConfigured = $sqlConfigured
    sqlIdentityProbe = $sqlIdentityStatus
    sqlSchema = $sqlSchema
    sqlIntegration = 'NotExecutedByPreflight'
    sqlUpgradeValidation = 'NotExecutedByPreflight'
    lanHttpsValidation = 'NotExecutedByPreflight'
    serviceAccountValidation = 'NotExecutedByPreflight'
    productionCapacityAccepted = $false
}
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($reportPath)) | Out-Null
$stream = [IO.File]::Open($reportPath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
try { $bytes = [Text.Encoding]::UTF8.GetBytes(($reportData | ConvertTo-Json)); $stream.Write($bytes) }
finally { $stream.Dispose() }
Write-Output ('只读检查已保存：' + $reportPath)
