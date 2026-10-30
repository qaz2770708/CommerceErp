#Requires -RunAsAdministrator
param([Parameter(Mandatory)][string]$Configuration)
$ErrorActionPreference = 'Stop'
$configurationPath = (Resolve-Path -LiteralPath $Configuration).Path
$server = (Resolve-Path -LiteralPath (Join-Path (Split-Path -Parent $PSScriptRoot) 'app/server/CommerceErp.Api.exe')).Path
if ($server.Contains('"') -or $configurationPath.Contains('"')) { throw '路径不允许包含引号。' }
$config = Get-Content -LiteralPath $configurationPath -Raw | ConvertFrom-Json
if (-not $config.tokenFile) { throw 'Windows 服务须在配置中指定 tokenFile。先按部署说明配置仅服务账户可读的凭证文件。' }
if (Get-Service -Name CommerceErp -ErrorAction SilentlyContinue) { throw 'CommerceErp 服务已存在，未覆盖。' }
# Runs with a low-privilege account. Grant this account only the documented paths before starting.
$identity = [PSCredential]::new('NT AUTHORITY\LocalService', [Security.SecureString]::new())
New-Service -Name CommerceErp -DisplayName 'Commerce ERP Service' -BinaryPathName ('"' + $server + '" --server "' + $configurationPath + '"') -StartupType Manual -Credential $identity
Write-Output '已注册但尚未启动。配置 LocalService 文件权限和数据库权限后，执行 Start-Service CommerceErp。'
