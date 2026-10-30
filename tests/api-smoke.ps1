param([string]$ApiDirectory, [string]$EvidenceDirectory, [string]$DataDirectory)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
if (-not $ApiDirectory) { $ApiDirectory = Join-Path $projectRoot 'src/CommerceErp.Api/bin/Release/net10.0' }
if (-not $EvidenceDirectory) { $EvidenceDirectory = Join-Path $projectRoot 'verification' }
if (-not $DataDirectory) { $DataDirectory = Join-Path ([IO.Path]::GetTempPath()) ('CommerceErp.ApiTests-' + [Guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Force -Path $EvidenceDirectory, $DataDirectory | Out-Null
Add-Type -AssemblyName System.Net.Http
$script:results = [Collections.Generic.List[object]]::new()
$script:hostProcess = $null
$script:client = $null
$script:token = ''
$script:accountToken = ''
function Start-TestHost {
    $script:token = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = 'dotnet'
    $start.Arguments = '"' + (Join-Path $ApiDirectory 'CommerceErp.Api.dll') + '"'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.EnvironmentVariables['ERP_SESSION_TOKEN'] = $script:token
    $start.EnvironmentVariables['ERP_DATA_DIR'] = $DataDirectory
    $start.EnvironmentVariables['ASPNETCORE_ENVIRONMENT'] = 'Production'
    $script:hostProcess = [Diagnostics.Process]::Start($start)
    $lineTask = $script:hostProcess.StandardOutput.ReadLineAsync()
    if (-not $lineTask.Wait(15000)) { throw 'API startup timeout' }
    $line = $lineTask.Result
    if (-not $line -or -not $line.StartsWith('ERP_READY|')) { throw 'API startup failed' }
    $script:address = $line.Substring(10)
    if (([Uri]$script:address).Host -ne '127.0.0.1') { throw 'API must listen only on loopback' }
    $script:client = [Net.Http.HttpClient]::new()
    $script:client.BaseAddress = [Uri]$script:address
    $script:client.Timeout = [TimeSpan]::FromSeconds(15)
    $script:accountToken = ''
    $status = (Request 'GET' '/api/security/status').Body | ConvertFrom-Json
    if ($status.setupRequired) { $null = Request 'POST' '/api/security/setup' '{"username":"http-admin","displayName":"HTTP Tests","password":"HttpFixture-2026!"}' }
    $login = Request 'POST' '/api/security/login' '{"username":"http-admin","password":"HttpFixture-2026!"}'
    if ($login.Status -ne 200) { throw 'Test login failed' }
    $script:accountToken = ($login.Body | ConvertFrom-Json).token
}
function Request([string]$method, [string]$path, [string]$json = '', [bool]$authenticated = $true) {
    $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::new($method), $path)
    if ($authenticated) { $request.Headers.Add('X-Erp-Session', $script:token); if ($script:accountToken) { $request.Headers.Add('Authorization', 'Bearer ' + $script:accountToken) } }
    if ($json -ne '') { $request.Content = [Net.Http.StringContent]::new($json, [Text.Encoding]::UTF8, 'application/json') }
    $response = $script:client.SendAsync($request).GetAwaiter().GetResult()
    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
    $result = [pscustomobject]@{ Status = [int]$response.StatusCode; Body = $body }
    $response.Dispose(); $request.Dispose()
    return $result
}
function Stop-TestHost {
    if ($script:hostProcess -and -not $script:hostProcess.HasExited) {
        try { $null = Request 'POST' '/api/shutdown' } catch { }
        if (-not $script:hostProcess.WaitForExit(5000)) { $script:hostProcess.Kill() }
    }
    if ($script:client) { $script:client.Dispose() }
    if ($script:hostProcess) { $script:hostProcess.Dispose() }
}
function Check([string]$name, [scriptblock]$action) {
    try { & $action; $script:results.Add([pscustomobject]@{ name = $name; passed = $true }); Write-Output "PASS $name" }
    catch { $script:results.Add([pscustomobject]@{ name = $name; passed = $false; error = $_.Exception.Message }); Write-Output "FAIL $name / $($_.Exception.Message)" }
}
function Ensure([bool]$condition, [string]$message = 'Unexpected result') { if (-not $condition) { throw $message } }
function WarehousePost([string]$path, [hashtable]$body) {
    $body.operationId = [Guid]::NewGuid().ToString('N'); $body.actor = 'HTTP test operator'
    $r = Request 'POST' $path ($body | ConvertTo-Json -Depth 25)
    Ensure ($r.Status -eq 200) $r.Body
    return ($r.Body | ConvertFrom-Json)
}
function ShipmentDetail { return ((Request 'GET' ('/api/warehouse/shipments/' + $script:shipmentId)).Body | ConvertFrom-Json) }
function AfterDetail { return ((Request 'GET' ('/api/after-sales/cases/' + $script:afterId)).Body | ConvertFrom-Json) }
try {
    Start-TestHost
    Check 'Requests without session token return 401' { Ensure ((Request 'GET' '/api/bootstrap' '' $false).Status -eq 401) }
    Check 'Bootstrap explicitly disables live connections' {
        $r = Request 'GET' '/api/bootstrap'; Ensure ($r.Status -eq 200); $script:bootstrap = $r.Body | ConvertFrom-Json
        Ensure (-not $script:bootstrap.liveConnectionsEnabled)
    }
    Check 'Unknown fixture returns 404' { Ensure ((Request 'GET' '/api/samples/does-not-exist').Status -eq 404) }
    Check 'Malformed JSON returns 400' { Ensure ((Request 'POST' '/api/validate' '{oops').Status -eq 400) }
    Check 'Oversized input returns 413' { Ensure ((Request 'POST' '/api/validate' (' ' * 300000)).Status -eq 413) }
    Check 'Six fixtures produce expected pass/block decisions' {
        $sampleDirectory = Join-Path $projectRoot 'samples'
        New-Item -ItemType Directory -Force -Path $sampleDirectory | Out-Null
        foreach ($sample in $script:bootstrap.samples) {
            $r = Request 'GET' ('/api/samples/' + $sample.id); Ensure ($r.Status -eq 200)
            [IO.File]::WriteAllText((Join-Path $sampleDirectory ($sample.id + '.json')), $r.Body)
            $result = Request 'POST' '/api/validate' $r.Body; Ensure ($result.Status -eq 200)
            $report = $result.Body | ConvertFrom-Json
            $expected = $sample.id -in @('douyin-valid','taobao-bundle','jd-long-id')
            Ensure ($report.passed -eq $expected) $sample.id
        }
    }
    Check 'Unknown fields are rejected at HTTP boundary' {
        $json = (Request 'GET' '/api/samples/douyin-valid').Body.Replace('"schemaVersion": 1','"unexpected": true, "schemaVersion": 1')
        Ensure ((Request 'POST' '/api/validate' $json).Status -eq 400)
    }
    Check 'Missing mapping can be repaired through API' {
        $mapping = @{ tenantId='demo-enterprise'; platform='Pinduoduo'; shopId='demo-pdd-01'; platformProductId='product-100'; platformSkuId='variant-new'; internalSkuId='sku-shirt'; unitMultiplier=1; version=0 }
        $script:mappingRequest = @{ mapping=$mapping; expectedVersion=0 } | ConvertTo-Json -Depth 10
        Ensure ((Request 'PUT' '/api/mappings' $script:mappingRequest).Status -eq 200)
        $json = (Request 'GET' '/api/samples/pdd-unmapped').Body
        $report = (Request 'POST' '/api/validate' $json).Body | ConvertFrom-Json
        Ensure $report.passed
    }
    Check 'Duplicate mapping insert returns 409' { Ensure ((Request 'PUT' '/api/mappings' $script:mappingRequest).Status -eq 409) }
    Check 'Verified readiness requires evidence' {
        $current = $script:bootstrap.readiness[0]
        $current.items[0].status = 'Verified'; $current.items[0].evidence = ''
        $body = @{ expectedRevision=$current.revision; items=$current.items } | ConvertTo-Json -Depth 10
        Ensure ((Request 'PUT' ('/api/readiness/' + $current.platform) $body).Status -eq 400)
    }
    Check 'Reports and mappings survive service restart' {
        Stop-TestHost; Start-TestHost
        $reports = (Request 'GET' '/api/reports').Body | ConvertFrom-Json
        Ensure ($reports.Count -eq 7)
        $data = (Request 'GET' '/api/bootstrap').Body | ConvertFrom-Json
        Ensure (@($data.catalog.mappings | Where-Object { $_.platformSkuId -eq 'variant-new' }).Count -eq 1)
    }
    Check 'Import and automatic review reserve local stock' {
        $script:order = (Request 'GET' '/api/samples/douyin-valid').Body | ConvertFrom-Json
        $script:import = @{ operationId=[Guid]::NewGuid().ToString('N'); order=$script:order; autoReview=$true }
        $script:importJson = $script:import | ConvertTo-Json -Depth 25
        $r = Request 'POST' '/api/orders' $script:importJson; Ensure ($r.Status -eq 200) $r.Body
        $script:accepted = $r.Body | ConvertFrom-Json; Ensure ($script:accepted.state -eq 'Approved')
        $rows = (Request 'GET' '/api/inventory').Body | ConvertFrom-Json
        Ensure (@($rows | Where-Object { $_.reserved -eq 1 -and $_.onHand -eq 100 -and $_.available -eq 99 }).Count -eq 2)
    }
    Check 'Replayed operation returns original result without another reservation' {
        $r = Request 'POST' '/api/orders' $script:importJson; Ensure ($r.Status -eq 200)
        $result = $r.Body | ConvertFrom-Json; Ensure ($result.replayed -and $result.orderId -eq $script:accepted.orderId)
    }
    Check 'Business duplicate with a new operation ID is deduplicated' {
        $script:import.operationId = [Guid]::NewGuid().ToString('N')
        $r = Request 'POST' '/api/orders' ($script:import | ConvertTo-Json -Depth 25)
        Ensure ($r.Status -eq 200 -and ($r.Body | ConvertFrom-Json).replayed)
        Ensure (((Request 'GET' '/api/orders').Body | ConvertFrom-Json).total -eq 1)
    }
    Check 'Changed body under same operation ID returns 409' {
        $altered = $script:importJson | ConvertFrom-Json; $altered.order.externalOrderId = 'different-order'
        Ensure ((Request 'POST' '/api/orders' ($altered | ConvertTo-Json -Depth 25)).Status -eq 409)
    }
    Check 'Order detail includes immutable mapping snapshot and reservations' {
        $r = Request 'GET' ('/api/orders/' + $script:accepted.orderId); Ensure ($r.Status -eq 200)
        $detail = $r.Body | ConvertFrom-Json
        Ensure ($detail.reservations.Count -eq 2 -and $detail.history.Count -eq 2 -and $detail.normalized.lines[0].mappingVersion -eq 1)
    }
    Check 'Invalid versions and invalid command bodies do not mutate orders' {
        $path = '/api/orders/' + $script:accepted.orderId + '/cancel'
        Ensure ((Request 'POST' $path (@{operationId=[Guid]::NewGuid().ToString('N');expectedVersion=0;reason='stale'} | ConvertTo-Json)).Status -eq 409)
        Ensure ((Request 'POST' '/api/orders' '{}').Status -eq 400)
        Ensure ((Request 'POST' '/api/orders' 'null').Status -eq 400)
        Ensure ((Request 'POST' '/api/orders' (@{operationId=[Guid]::NewGuid().ToString('N');order=$null} | ConvertTo-Json)).Status -eq 400)
    }
    Check 'Cancellation releases once even after service restart' {
        $script:cancelPath = '/api/orders/' + $script:accepted.orderId + '/cancel'
        $script:cancelJson = @{operationId=[Guid]::NewGuid().ToString('N');expectedVersion=$script:accepted.version;reason='HTTP cancel test'} | ConvertTo-Json
        Ensure ((Request 'POST' $script:cancelPath $script:cancelJson).Status -eq 200)
        Stop-TestHost; Start-TestHost
        $r = Request 'POST' $script:cancelPath $script:cancelJson; Ensure ($r.Status -eq 200 -and ($r.Body | ConvertFrom-Json).replayed)
        $rows = (Request 'GET' '/api/inventory').Body | ConvertFrom-Json; Ensure (@($rows | Where-Object { $_.reserved -ne 0 }).Count -eq 0)
        $r = Request 'POST' '/api/orders' $script:importJson
        Ensure (($r.Body | ConvertFrom-Json).replayed)
        $current = (Request 'GET' ('/api/orders/' + $script:accepted.orderId)).Body | ConvertFrom-Json; Ensure ($current.summary.state -eq 'Cancelled')
    }
    Check 'Receive is exact and retry across restart does not duplicate stock' {
        $script:receiveJson = @{operationId=[Guid]::NewGuid().ToString('N');quantity=10.125;reason='HTTP receive test'} | ConvertTo-Json
        Ensure ((Request 'POST' '/api/inventory/sku-shirt/receive' $script:receiveJson).Status -eq 200)
        Stop-TestHost; Start-TestHost
        $r = Request 'POST' '/api/inventory/sku-shirt/receive' $script:receiveJson; Ensure ($r.Status -eq 200 -and ($r.Body | ConvertFrom-Json).replayed)
        $script:stock = @((Request 'GET' '/api/inventory').Body | ConvertFrom-Json | Where-Object { $_.skuId -eq 'sku-shirt' })[0]
        Ensure ($script:stock.onHand -eq 110.125)
    }
    Check 'Stock policy changes ATP and stale policy returns 409' {
        $json = @{operationId=[Guid]::NewGuid().ToString('N');expectedVersion=$script:stock.version;frozen=1.125;safety=2;reason='HTTP policy test'} | ConvertTo-Json
        Ensure ((Request 'POST' '/api/inventory/sku-shirt/policy' $json).Status -eq 200)
        $old = $json | ConvertFrom-Json; $old.operationId = [Guid]::NewGuid().ToString('N')
        Ensure ((Request 'POST' '/api/inventory/sku-shirt/policy' ($old | ConvertTo-Json)).Status -eq 409)
        $row = @((Request 'GET' '/api/inventory').Body | ConvertFrom-Json | Where-Object { $_.skuId -eq 'sku-shirt' })[0]; Ensure ($row.available -eq 107)
    }
    Check 'Rejected order can be corrected then explicitly re-reviewed' {
        $source = (Request 'GET' '/api/samples/jd-amount-error').Body | ConvertFrom-Json
        $r = Request 'POST' '/api/orders' (@{operationId=[Guid]::NewGuid().ToString('N');order=$source;autoReview=$true} | ConvertTo-Json -Depth 25)
        Ensure ($r.Status -eq 200); $order = $r.Body | ConvertFrom-Json; Ensure ($order.state -eq 'Exception')
        $source.paidAmount = 111
        $r = Request 'POST' ('/api/orders/' + $order.orderId + '/correct') (@{operationId=[Guid]::NewGuid().ToString('N');expectedVersion=$order.version;order=$source;reason='fix test amount'} | ConvertTo-Json -Depth 25)
        Ensure ($r.Status -eq 200); $fixed = $r.Body | ConvertFrom-Json; Ensure ($fixed.state -eq 'PendingReview')
        $r = Request 'POST' ('/api/orders/' + $order.orderId + '/review') (@{operationId=[Guid]::NewGuid().ToString('N');expectedVersion=$fixed.version} | ConvertTo-Json)
        Ensure ($r.Status -eq 200 -and ($r.Body | ConvertFrom-Json).state -eq 'Approved')
    }
    Check 'Pagination filtering and invalid query validation work over HTTP' {
        $r = Request 'GET' '/api/orders?page=1&pageSize=1&state=Approved'; Ensure ($r.Status -eq 200)
        $page = $r.Body | ConvertFrom-Json; Ensure ($page.total -eq 1 -and $page.items.Count -eq 1)
        Ensure ((Request 'GET' '/api/orders?pageSize=0').Status -eq 400)
        Ensure ((Request 'GET' '/api/orders?state=UnknownState').Status -eq 400)
    }
    Check 'Inventory ledger and reservation consistency pass after all mutations' {
        $r = Request 'GET' '/api/business/consistency'; Ensure ($r.Status -eq 200 -and ($r.Body | ConvertFrom-Json).passed)
        $ledger = (Request 'GET' '/api/inventory/ledger?sku=sku-shirt').Body | ConvertFrom-Json
        Ensure (@($ledger | Where-Object { $_.action -eq 'Release' }).Count -eq 1)
        $dashboard = (Request 'GET' '/api/business/dashboard').Body | ConvertFrom-Json
        Ensure ($dashboard.totalOrders -eq 2 -and $dashboard.pendingOutbox -gt 0)
    }
    Check 'Warehouse routes require session and reject invalid query values' {
        Ensure ((Request 'GET' '/api/warehouse/shipments' '' $false).Status -eq 401)
        Ensure ((Request 'GET' '/api/warehouse/shipments?pageSize=0').Status -eq 400)
        Ensure ((Request 'GET' '/api/warehouse/shipments?state=Invalid').Status -eq 400)
    }
    Check 'Save shipping address and allocate all order lines over HTTP' {
        $candidates = (Request 'GET' '/api/warehouse/orders').Body | ConvertFrom-Json
        $script:warehouseOrderId = $candidates[0].orderId
        $path = '/api/warehouse/orders/' + $script:warehouseOrderId
        $p = (Request 'GET' $path).Body | ConvertFrom-Json
        $null = WarehousePost ($path + '/address') @{expectedVersion=$p.order.version;address=@{recipient='Synthetic recipient';phone='00000000000';province='Test';city='Test';district='Test';street='Test street 100';postalCode=''};allowMerge=$true;reason='HTTP address test'}
        $p = (Request 'GET' $path).Body | ConvertFrom-Json
        Ensure ($p.profile.allowMerge -and $p.lines.Count -eq 2)
        $lines = @($p.lines | ForEach-Object { @{orderId=$script:warehouseOrderId;externalLineId=$_.externalLineId;quantity=$_.remaining;expectedOrderVersion=$p.order.version} })
        $r = WarehousePost '/api/warehouse/shipments' @{lines=$lines;reason='HTTP allocate test'}
        $script:shipmentId = $r.id; Ensure ($r.state -eq 'Draft')
    }
    Check 'Stale warehouse writes and address changes on allocated orders are blocked' {
        $r = Request 'POST' ('/api/warehouse/shipments/' + $script:shipmentId + '/pick') (@{operationId=[Guid]::NewGuid().ToString('N');expectedVersion=0;actor='HTTP'} | ConvertTo-Json)
        Ensure ($r.Status -eq 409)
        $p = (Request 'GET' ('/api/warehouse/orders/' + $script:warehouseOrderId)).Body | ConvertFrom-Json
        $r = Request 'POST' ('/api/warehouse/orders/' + $script:warehouseOrderId + '/address') (@{operationId=[Guid]::NewGuid().ToString('N');expectedVersion=$p.order.version;address=$p.profile.address;allowMerge=$true;actor='HTTP';reason='change'} | ConvertTo-Json -Depth 10)
        Ensure ($r.Status -eq 409)
    }
    Check 'Split and merge preserve original lines and replacement history over HTTP' {
        $d = ShipmentDetail; $oldId = $script:shipmentId
        $split = WarehousePost ('/api/warehouse/shipments/' + $oldId + '/split') @{expectedVersion=$d.summary.version;lines=@(@{lineId=$d.lines[0].id;quantity=$d.lines[0].quantity});reason='HTTP split'}
        Ensure ($split.relatedIds.Count -eq 2)
        Ensure ((ShipmentDetail).summary.state -eq 'Superseded')
        $pairs = @($split.relatedIds | ForEach-Object { $d = (Request 'GET' ('/api/warehouse/shipments/' + $_)).Body | ConvertFrom-Json; @{id=$_;version=$d.summary.version} })
        $merged = WarehousePost '/api/warehouse/merge' @{shipments=$pairs;reason='HTTP merge'}
        $script:shipmentId=$merged.id; Ensure ((ShipmentDetail).lines.Count -eq 2)
    }
    Check 'Barcode registration and wrong scans enforce physical SKU identity' {
        $null = WarehousePost '/api/warehouse/barcodes' @{expectedVersion=0;barcode='690HTTP0001';skuId='sku-shirt';reason='HTTP barcode'}
        $null = WarehousePost ('/api/warehouse/shipments/' + $script:shipmentId + '/pick') @{expectedVersion=(ShipmentDetail).summary.version}
        $r = Request 'POST' ('/api/warehouse/shipments/' + $script:shipmentId + '/scan') (@{operationId=[Guid]::NewGuid().ToString('N');expectedVersion=(ShipmentDetail).summary.version;barcode='MISSING';quantity=1;actor='HTTP'} | ConvertTo-Json)
        Ensure ($r.Status -eq 400)
        Ensure (@((Request 'GET' '/api/warehouse/barcodes').Body | ConvertFrom-Json | Where-Object { $_.barcode -eq '690HTTP0001' }).Count -eq 1)
    }
    Check 'Missing scans block packing and replayed scans do not count twice' {
        $basePath = '/api/warehouse/shipments/' + $script:shipmentId
        $r = Request 'POST' ($basePath + '/pack') (@{operationId=[Guid]::NewGuid().ToString('N');expectedVersion=(ShipmentDetail).summary.version;actor='HTTP'} | ConvertTo-Json)
        Ensure ($r.Status -eq 400)
        foreach ($check in (ShipmentDetail).checks) {
            $barcode = if ($check.skuId -eq 'sku-shirt') {'690HTTP0001'} else {$check.skuCode}
            $json = @{operationId=[Guid]::NewGuid().ToString('N');expectedVersion=(ShipmentDetail).summary.version;barcode=$barcode;quantity=$check.required;actor='HTTP'} | ConvertTo-Json
            Ensure ((Request 'POST' ($basePath + '/scan') $json).Status -eq 200)
            $r = Request 'POST' ($basePath + '/scan') $json
            Ensure ($r.Status -eq 200 -and ($r.Body | ConvertFrom-Json).replayed)
        }
        $r = WarehousePost ($basePath + '/pack') @{expectedVersion=(ShipmentDetail).summary.version}; Ensure ($r.state -eq 'Packed')
    }
    Check 'Dispatch requires a recorded waybill' {
        $basePath = '/api/warehouse/shipments/' + $script:shipmentId
        $r = Request 'POST' ($basePath + '/dispatch') (@{operationId=[Guid]::NewGuid().ToString('N');expectedVersion=(ShipmentDetail).summary.version;actor='HTTP';reason='test'} | ConvertTo-Json)
        Ensure ($r.Status -eq 400)
        $null = WarehousePost ($basePath + '/waybill') @{expectedVersion=(ShipmentDetail).summary.version;carrier='TEST';trackingNumber='TESTHTTP123456';reason='HTTP waybill'}
    }
    Check 'Dispatch consumes stock once and its response survives API restart' {
        $script:dispatchPath = '/api/warehouse/shipments/' + $script:shipmentId + '/dispatch'
        $script:dispatchJson = @{operationId=[Guid]::NewGuid().ToString('N');expectedVersion=(ShipmentDetail).summary.version;actor='HTTP';reason='HTTP dispatch'} | ConvertTo-Json
        $r = Request 'POST' $script:dispatchPath $script:dispatchJson; Ensure ($r.Status -eq 200 -and ($r.Body | ConvertFrom-Json).state -eq 'Shipped')
        Stop-TestHost; Start-TestHost
        $r = Request 'POST' $script:dispatchPath $script:dispatchJson; Ensure ($r.Status -eq 200 -and ($r.Body | ConvertFrom-Json).replayed)
        $rows = (Request 'GET' '/api/inventory').Body | ConvertFrom-Json
        Ensure (@($rows | Where-Object { $_.reserved -ne 0 }).Count -eq 0)
        Ensure (@($rows | Where-Object { $_.skuId -eq 'sku-shirt' })[0].onHand -eq 109.125)
    }
    Check 'Order fulfillment and consumed reservations are visible after dispatch' {
        $d = (Request 'GET' ('/api/orders/' + $script:warehouseOrderId)).Body | ConvertFrom-Json
        Ensure ($d.summary.shippingState -eq 'Complete' -and $d.summary.state -eq 'Approved')
        Ensure (@($d.reservations | Where-Object { $_.state -eq 'Consumed' -and $_.quantity -eq 0 -and $_.consumed -eq 1 }).Count -eq 2)
    }
    Check 'Shipped packages cannot be voided or directly cancelled' {
        $d = (Request 'GET' ('/api/orders/' + $script:warehouseOrderId)).Body | ConvertFrom-Json
        $r = Request 'POST' ('/api/orders/' + $script:warehouseOrderId + '/cancel') (@{operationId=[Guid]::NewGuid().ToString('N');expectedVersion=$d.summary.version;reason='cancel shipped'} | ConvertTo-Json)
        Ensure ($r.Status -eq 409)
        $r = Request 'POST' ('/api/warehouse/shipments/' + $script:shipmentId + '/void') (@{operationId=[Guid]::NewGuid().ToString('N');expectedVersion=(ShipmentDetail).summary.version;actor='HTTP';reason='void shipped'} | ConvertTo-Json)
        Ensure ($r.Status -eq 409)
    }
    Check 'Background retry reports unavailable connector instead of false platform success' {
        $jobs = (Request 'GET' '/api/warehouse/deliveries').Body | ConvertFrom-Json; Ensure ($jobs.Count -eq 1 -and $jobs[0].state -eq 'Blocked')
        $null = WarehousePost ('/api/warehouse/deliveries/' + $jobs[0].id + '/retry') @{expectedVersion=$jobs[0].version;reason='HTTP retry'}
        for ($i=0;$i -lt 40;$i++) {
            $jobs = (Request 'GET' '/api/warehouse/deliveries').Body | ConvertFrom-Json
            if ($jobs[0].state -eq 'Blocked' -and $jobs[0].attempts -gt 0) { break }
            Start-Sleep -Milliseconds 100
        }
        Ensure ($jobs[0].state -eq 'Blocked' -and $jobs[0].receipt -eq '' -and $jobs[0].attempts -gt 0)
    }
    Check 'Warehouse and stock audits agree after HTTP fulfillment' {
        $audit = (Request 'GET' '/api/warehouse/audit').Body | ConvertFrom-Json
        Ensure ($audit.passed -and $audit.dispatched -eq 1 -and $audit.shipments -eq 4)
        Ensure (((Request 'GET' '/api/business/consistency').Body | ConvertFrom-Json).passed)
        $page = (Request 'GET' '/api/warehouse/shipments?state=Shipped&pageSize=1').Body | ConvertFrom-Json
        Ensure ($page.total -eq 1 -and $page.items.Count -eq 1)
    }
    Check 'After-sales routes require session and validate input at HTTP boundary' {
        Ensure ((Request 'GET' '/api/after-sales/cases' '' $false).Status -eq 401)
        Ensure ((Request 'GET' '/api/after-sales/cases?state=Invalid').Status -eq 400)
        Ensure ((Request 'POST' '/api/after-sales/cases' '{}').Status -eq 400)
        Ensure ((Request 'POST' '/api/after-sales/cases' 'null').Status -eq 400)
        Ensure ((Request 'GET' '/api/after-sales/cases?pageSize=0').Status -eq 400)
    }
    Check 'Create return claim from shipped lines and expose active hold on order' {
        $p = (Request 'GET' ('/api/after-sales/orders/' + $script:warehouseOrderId)).Body | ConvertFrom-Json
        Ensure ($p.lines.Count -eq 2 -and -not $p.order.canCancelUnshipped)
        $script:afterRequest = @{operationId=[Guid]::NewGuid().ToString('N');orderId=$script:warehouseOrderId;expectedOrderVersion=$p.order.version;sourceCaseNumber='HTTP-RETURN-001';kind='ReturnRefund';refundAmount=$p.order.paidAmount;lines=@($p.lines | ForEach-Object { @{shipmentLineId=$_.shipmentLineId;quantity=$_.remaining} });actor='HTTP';reason='HTTP return claim'}
        $r = Request 'POST' '/api/after-sales/cases' ($script:afterRequest | ConvertTo-Json -Depth 15); Ensure ($r.Status -eq 200) $r.Body
        $script:afterId = ($r.Body | ConvertFrom-Json).id
        Ensure (((Request 'GET' ('/api/orders/' + $script:warehouseOrderId)).Body | ConvertFrom-Json).summary.activeAfterSales -eq 1)
        Ensure ((AfterDetail).components.Count -eq 2)
    }
    Check 'Source case identity deduplicates across operation keys and rejects changed claims' {
        $script:afterRequest.operationId=[Guid]::NewGuid().ToString('N')
        $r = Request 'POST' '/api/after-sales/cases' ($script:afterRequest | ConvertTo-Json -Depth 15); Ensure ($r.Status -eq 200 -and ($r.Body | ConvertFrom-Json).replayed)
        $changed = $script:afterRequest.Clone(); $changed.refundAmount=1; $changed.operationId=[Guid]::NewGuid().ToString('N')
        Ensure ((Request 'POST' '/api/after-sales/cases' ($changed | ConvertTo-Json -Depth 15)).Status -eq 409)
    }
    Check 'Approval and inspection enforce case state and original component quantities' {
        $d = AfterDetail
        $script:receiptRequest = @{operationId=[Guid]::NewGuid().ToString('N');expectedVersion=$d.summary.version;receiptNumber='RCV-HTTP-001';returnTracking='TEST-HTTP-RETURN';lines=@($d.components | ForEach-Object { @{caseLineId=$_.caseLineId;skuId=$_.skuId;goodQuantity=$(if ($_.skuId -eq 'sku-shirt') {1} else {0});damagedQuantity=$(if ($_.skuId -eq 'sku-mug') {1} else {0})} });actor='HTTP';reason='HTTP inspection'}
        $script:inspectPath = '/api/after-sales/cases/' + $script:afterId + '/inspect'
        Ensure ((Request 'POST' $script:inspectPath ($script:receiptRequest | ConvertTo-Json -Depth 15)).Status -eq 409)
        $null = WarehousePost ('/api/after-sales/cases/' + $script:afterId + '/approve') @{expectedVersion=$d.summary.version;reason='approve return'}
        $script:receiptRequest.expectedVersion=(AfterDetail).summary.version
        $r = Request 'POST' $script:inspectPath ($script:receiptRequest | ConvertTo-Json -Depth 15); Ensure ($r.Status -eq 200) $r.Body
        Ensure (@((AfterDetail).components | Where-Object { $_.remaining -ne 0 }).Count -eq 0)
    }
    Check 'Good returns increase main stock and damaged returns remain isolated' {
        $stock = (Request 'GET' '/api/inventory').Body | ConvertFrom-Json
        Ensure (@($stock | Where-Object { $_.skuId -eq 'sku-shirt' })[0].onHand -eq 110.125)
        $mug = @($stock | Where-Object { $_.skuId -eq 'sku-mug' })[0]; Ensure ($mug.onHand -eq 99 -and $mug.quarantined -eq 1 -and $mug.available -eq 99)
        $isolated = (Request 'GET' '/api/after-sales/quarantine/ledger').Body | ConvertFrom-Json; Ensure ($isolated.Count -eq 1 -and $isolated[0].quantity -eq 1)
    }
    Check 'Return receipt business key survives restart and rejects excess return' {
        Stop-TestHost; Start-TestHost
        $script:receiptRequest.operationId=[Guid]::NewGuid().ToString('N')
        $r = Request 'POST' $script:inspectPath ($script:receiptRequest | ConvertTo-Json -Depth 15); Ensure ($r.Status -eq 200 -and ($r.Body | ConvertFrom-Json).replayed)
        $excess = $script:receiptRequest.Clone(); $excess.receiptNumber='RCV-EXCESS'; $excess.operationId=[Guid]::NewGuid().ToString('N'); $excess.expectedVersion=(AfterDetail).summary.version
        Ensure ((Request 'POST' $script:inspectPath ($excess | ConvertTo-Json -Depth 15)).Status -eq 409)
        Ensure ((AfterDetail).receipts.Count -eq 1)
    }
    Check 'Unknown refund status retains claim and blocks withdrawal or closure' {
        $d = AfterDetail; $script:refundPath='/api/after-sales/cases/' + $script:afterId + '/refund'
        $null = WarehousePost $script:refundPath @{expectedVersion=$d.summary.version;eventKey='HTTP-REFUND-UNKNOWN';status='Unknown';amount=$d.summary.refundAmount;refundReference='';evidence='Synthetic pending lookup';observedAt=[DateTimeOffset]::UtcNow.ToString('O');confirmLocalEvidence=$false;reason='unknown refund'}
        Ensure ((AfterDetail).summary.refundState -eq 'Unknown')
        foreach ($verb in @('withdraw','close')) {
            $r = Request 'POST' ('/api/after-sales/cases/' + $script:afterId + '/' + $verb) (@{operationId=[Guid]::NewGuid().ToString('N');expectedVersion=(AfterDetail).summary.version;actor='HTTP';reason='blocked transition'} | ConvertTo-Json)
            Ensure ($r.Status -eq 409)
        }
    }
    Check 'Refund evidence requires explicit local source acknowledgement' {
        $d=AfterDetail; $script:refundRequest=@{operationId=[Guid]::NewGuid().ToString('N');expectedVersion=$d.summary.version;eventKey='HTTP-REFUND-EVIDENCE';status='EvidenceRecorded';amount=$d.summary.refundAmount;refundReference='TEST-REFUND-HTTP-001';evidence='Synthetic local evidence, not platform verified';observedAt=[DateTimeOffset]::UtcNow.ToString('O');confirmLocalEvidence=$false;actor='HTTP';reason='record local evidence'}
        Ensure ((Request 'POST' $script:refundPath ($script:refundRequest | ConvertTo-Json)).Status -eq 400)
        $script:refundRequest.confirmLocalEvidence=$true; $script:refundRequest.operationId=[Guid]::NewGuid().ToString('N')
        $r=Request 'POST' $script:refundPath ($script:refundRequest | ConvertTo-Json); Ensure ($r.Status -eq 200) $r.Body
        Ensure ((AfterDetail).summary.refundState -eq 'EvidenceRecorded')
    }
    Check 'Refund event identity prevents duplicates and overwrites' {
        $script:refundRequest.operationId=[Guid]::NewGuid().ToString('N'); $r=Request 'POST' $script:refundPath ($script:refundRequest | ConvertTo-Json)
        Ensure ($r.Status -eq 200 -and ($r.Body | ConvertFrom-Json).replayed)
        $changed=$script:refundRequest.Clone(); $changed.evidence='changed'; $changed.operationId=[Guid]::NewGuid().ToString('N')
        Ensure ((Request 'POST' $script:refundPath ($changed | ConvertTo-Json)).Status -eq 409)
        Ensure ((AfterDetail).refundObservations.Count -eq 2)
    }
    Check 'Local closure preserves received goods and blocks new receipt batches' {
        $null = WarehousePost ('/api/after-sales/cases/' + $script:afterId + '/close') @{expectedVersion=(AfterDetail).summary.version;reason='close with local evidence'}
        Ensure ((AfterDetail).summary.state -eq 'Closed')
        $new=$script:receiptRequest.Clone(); $new.receiptNumber='NEW-AFTER-CLOSE'; $new.operationId=[Guid]::NewGuid().ToString('N'); $new.expectedVersion=(AfterDetail).summary.version
        Ensure ((Request 'POST' $script:inspectPath ($new | ConvertTo-Json -Depth 15)).Status -eq 409)
    }
    Check 'Unshipped claim blocks picking and direct order cancellation' {
        $source=(Request 'GET' '/api/samples/douyin-valid').Body | ConvertFrom-Json; $source.externalOrderId='HTTP-CANCEL-' + [Guid]::NewGuid().ToString('N')
        $r=Request 'POST' '/api/orders' (@{operationId=[Guid]::NewGuid().ToString('N');order=$source;autoReview=$true} | ConvertTo-Json -Depth 25); Ensure ($r.Status -eq 200)
        $accepted=$r.Body | ConvertFrom-Json; $script:cancelOrderId=$accepted.orderId
        $null=WarehousePost ('/api/warehouse/orders/' + $accepted.orderId + '/address') @{expectedVersion=$accepted.version;address=@{recipient='Synthetic';phone='00000000000';province='Test';city='Test';district='Test';street='Test';postalCode=''};allowMerge=$false;reason='test address'}
        $p=(Request 'GET' ('/api/warehouse/orders/' + $accepted.orderId)).Body | ConvertFrom-Json
        $new=WarehousePost '/api/warehouse/shipments' @{lines=@($p.lines | ForEach-Object { @{orderId=$accepted.orderId;externalLineId=$_.externalLineId;quantity=$_.remaining;expectedOrderVersion=$p.order.version} });reason='test allocation'}
        $script:cancelPackageId=$new.id
        $p=(Request 'GET' ('/api/after-sales/orders/' + $accepted.orderId)).Body | ConvertFrom-Json
        $case=WarehousePost '/api/after-sales/cases' @{orderId=$accepted.orderId;expectedOrderVersion=$p.order.version;sourceCaseNumber='HTTP-CANCEL-001';kind='CancelUnshipped';refundAmount=$p.order.paidAmount;lines=@();reason='request cancellation'}
        $script:cancelCaseId=$case.id
        $r=Request 'POST' ('/api/warehouse/shipments/' + $new.id + '/pick') (@{operationId=[Guid]::NewGuid().ToString('N');expectedVersion=$new.version;actor='HTTP'} | ConvertTo-Json); Ensure ($r.Status -eq 409)
        $o=(Request 'GET' ('/api/orders/' + $accepted.orderId)).Body | ConvertFrom-Json
        Ensure ((Request 'POST' ('/api/orders/' + $accepted.orderId + '/cancel') (@{operationId=[Guid]::NewGuid().ToString('N');expectedVersion=$o.summary.version;reason='bypass aftersales'} | ConvertTo-Json)).Status -eq 409)
        Ensure (((Request 'GET' ('/api/warehouse/shipments/' + $new.id)).Body | ConvertFrom-Json).summary.afterSaleHold)
    }
    Check 'Reject releases hold and a new picking cancellation needs goods-return acknowledgement' {
        $null=WarehousePost ('/api/after-sales/cases/' + $script:cancelCaseId + '/reject') @{expectedVersion=1;reason='withdraw initial decision'}
        $d=(Request 'GET' ('/api/warehouse/shipments/' + $script:cancelPackageId)).Body | ConvertFrom-Json
        $null=WarehousePost ('/api/warehouse/shipments/' + $script:cancelPackageId + '/pick') @{expectedVersion=$d.summary.version}
        $p=(Request 'GET' ('/api/after-sales/orders/' + $script:cancelOrderId)).Body | ConvertFrom-Json
        $case=WarehousePost '/api/after-sales/cases' @{orderId=$script:cancelOrderId;expectedOrderVersion=$p.order.version;sourceCaseNumber='HTTP-CANCEL-002';kind='CancelUnshipped';refundAmount=$p.order.paidAmount;lines=@();reason='cancel picking order'}
        $script:cancelCaseId=$case.id
        $r=Request 'POST' ('/api/after-sales/cases/' + $case.id + '/approve') (@{operationId=[Guid]::NewGuid().ToString('N');expectedVersion=$case.version;actor='HTTP';reason='no return confirmation'} | ConvertTo-Json); Ensure ($r.Status -eq 409)
        $null=WarehousePost ('/api/after-sales/cases/' + $case.id + '/approve') @{expectedVersion=$case.version;confirmPickedGoodsReturned=$true;reason='entire package physically returned'}
        Ensure (((Request 'GET' ('/api/orders/' + $script:cancelOrderId)).Body | ConvertFrom-Json).summary.state -eq 'Cancelled')
        Ensure (((Request 'GET' ('/api/warehouse/shipments/' + $script:cancelPackageId)).Body | ConvertFrom-Json).summary.state -eq 'Voided')
    }
    Check 'After-sales audits pagination and persistence agree after complete HTTP workflow' {
        Stop-TestHost; Start-TestHost
        $audit=(Request 'GET' '/api/after-sales/audit').Body | ConvertFrom-Json; Ensure ($audit.passed -and $audit.cases -eq 3 -and $audit.receipts -eq 1)
        Ensure (((Request 'GET' '/api/business/consistency').Body | ConvertFrom-Json).passed)
        $page=(Request 'GET' '/api/after-sales/cases?state=Closed&pageSize=1').Body | ConvertFrom-Json; Ensure ($page.total -eq 1 -and $page.items.Count -eq 1)
        $stock=(Request 'GET' '/api/inventory').Body | ConvertFrom-Json; Ensure (@($stock | Where-Object { $_.reserved -ne 0 }).Count -eq 0)
    }
    Check 'Finance routes authenticate and reject malformed commands and pages' {
        Ensure ((Request 'GET' '/api/finance/report' '' $false).Status -eq 401)
        foreach($path in @('/api/finance/batches','/api/finance/exports','/api/finance/sku-costs/sku-shirt')) { Ensure ((Request 'POST' $path '{}').Status -eq 400) }
        Ensure ((Request 'GET' '/api/finance/orders?pageSize=0').Status -eq 400)
        Ensure ((Request 'GET' '/api/finance/rows?page=0').Status -eq 400)
    }
    Check 'Finance preview has no writes and preserves explicit column mappings' {
        $case=AfterDetail; $script:financeOrder=$case.summary.orderId
        $order=(Request 'GET' ('/api/orders/'+$script:financeOrder)).Body | ConvertFrom-Json
        $script:financeInput=@{platform=$order.source.platform;shopId=$order.source.shopId;batchNumber='HTTP-FIN-001';fileName='synthetic.csv';columns=@{transaction='流水号';order='订单号';kind='类型';amount='金额';time='发生时间';currency='币种';afterSale='售后编号'};types=@{'收入'='Sale';'退款'='Refund'};content="流水号,订单号,类型,金额,发生时间,币种,售后编号`r`nFIN-SALE,$($order.source.externalOrderId),收入,$($order.source.paidAmount),2026-10-01T10:00:00+08:00,CNY,`r`nFIN-REFUND,$($order.source.externalOrderId),退款,$($case.summary.refundAmount),2026-10-02T10:00:00+08:00,CNY,$($case.summary.sourceCaseNumber)`r`n"}
        $r=Request 'POST' '/api/finance/preview' ($script:financeInput | ConvertTo-Json -Depth 15); Ensure ($r.Status -eq 200) $r.Body
        $p=$r.Body|ConvertFrom-Json;Ensure ($p.valid -and $p.newRows -eq 2)
        Ensure (@((Request 'GET' '/api/finance/batches').Body|ConvertFrom-Json).Count -eq 0)
    }
    Check 'Bill import is persisted and batch identity deduplicates after restart' {
        $result=WarehousePost '/api/finance/batches' @{input=$script:financeInput;reason='synthetic bill import'}; $script:financeBatch=$result.id
        Stop-TestHost; Start-TestHost
        $again=WarehousePost '/api/finance/batches' @{input=$script:financeInput;reason='same batch retry'};Ensure ($again.replayed -and $again.id -eq $script:financeBatch)
        $rows=(Request 'GET' '/api/finance/rows').Body|ConvertFrom-Json;Ensure ($rows.total -eq 2)
        $raw=(Request 'GET' ('/api/finance/batches/'+$script:financeBatch+'/original')).Body|ConvertFrom-Json;Ensure ($raw.content -eq $script:financeInput.content)
    }
    Check 'Invalid mapped types and cross-file changed transactions reject entire batch' {
        $changed=$script:financeInput.Clone();$changed.batchNumber='HTTP-FIN-CONFLICT';$changed.content=$changed.content.Replace('FIN-SALE,','FIN-NEW,').Replace('FIN-REFUND,','FIN-SALE,')
        $r=Request 'POST' '/api/finance/batches' (@{operationId=[Guid]::NewGuid().ToString('N');input=$changed;actor='HTTP';reason='conflicting tx'}|ConvertTo-Json -Depth 15);Ensure ($r.Status -eq 400)
        $changed=$script:financeInput.Clone();$changed.types=@{'收入'='NoSuchKind'}
        Ensure ((Request 'POST' '/api/finance/preview' ($changed|ConvertTo-Json -Depth 15)).Status -eq 400)
        Ensure (((Request 'GET' '/api/finance/rows').Body|ConvertFrom-Json).total -eq 2)
    }
    Check 'Missing historical costs and fee assumptions suppress numerical profit' {
        $r=(Request 'GET' ('/api/finance/orders/'+$script:financeOrder)).Body|ConvertFrom-Json
        Ensure ($null -eq $r.order.profit -and $null -eq $r.order.netCost -and $r.order.problems.Count -ge 2)
        Ensure ((AfterDetail).summary.refundState -eq 'EvidenceRecorded')
        $r=Request 'POST' '/api/finance/exports' (@{operationId=[Guid]::NewGuid().ToString('N');exportNumber='HTTP-EARLY';target='test';orderIds=@($script:financeOrder);actor='HTTP';reason='premature'}|ConvertTo-Json);Ensure ($r.Status -eq 409)
    }
    Check 'Historical snapshot fills once and later catalog cost changes do not revalue it' {
        $costs=(Request 'GET' ('/api/finance/costs?order='+$script:financeOrder)).Body|ConvertFrom-Json
        foreach($cost in $costs) { $null=WarehousePost ('/api/finance/costs/'+$cost.shipmentLineId+'/'+$cost.skuId) @{expectedVersion=$cost.version;unitCost=2;evidence='synthetic historical cost';reason='fill missing snapshot'} }
        $cost=$costs[0]
        $r=Request 'POST' ('/api/finance/costs/'+$cost.shipmentLineId+'/'+$cost.skuId) (@{operationId=[Guid]::NewGuid().ToString('N');expectedVersion=2;unitCost=3;evidence='change';actor='HTTP';reason='overwrite'}|ConvertTo-Json);Ensure ($r.Status -eq 409)
        $null=WarehousePost '/api/finance/sku-costs/sku-shirt' @{expectedVersion=0;unitCost=9;evidence='future cost';reason='future shipments only'}
        Ensure (@((Request 'GET' ('/api/finance/costs?order='+$script:financeOrder)).Body|ConvertFrom-Json|Where-Object {$_.unitCost -ne 2}).Count -eq 0)
    }
    Check 'Explicit zero fee assumptions reconcile while quarantine cost remains' {
        $null=WarehousePost ('/api/finance/orders/'+$script:financeOrder+'/fees') @{expectedVersion=0;platformFee=0;freight=0;evidence='synthetic zero charges';reason='fee review'}
        $d=(Request 'GET' ('/api/finance/orders/'+$script:financeOrder)).Body|ConvertFrom-Json
        Ensure ($d.order.problems.Count -eq 0 -and $d.order.netCost -gt 0 -and $d.order.profit -eq -$d.order.netCost)
        Ensure (((Request 'GET' '/api/finance/report').Body|ConvertFrom-Json).products.Count -gt 0)
    }
    Check 'Immutable finance export contains traceable rows and a verifiable JSON digest' {
        $e=WarehousePost '/api/finance/exports' @{exportNumber='HTTP-FIN-EXPORT';target='generic handoff';orderIds=@($script:financeOrder);reason='prepare export'};$script:financeExport=$e.id
        $d=(Request 'GET' ('/api/finance/exports/'+$e.id)).Body|ConvertFrom-Json;$script:exportJson=$d.json
        $digest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($d.json))).ToLowerInvariant()
        Ensure ($digest -eq $d.summary.hash -and $d.summary.state -eq 'Prepared' -and -not $d.stale)
        Ensure (($d.json|ConvertFrom-Json).transactions.Count -eq 2)
        $again=WarehousePost '/api/finance/exports' @{exportNumber='HTTP-FIN-EXPORT';target='generic handoff';orderIds=@($script:financeOrder);reason='retry export'};Ensure ($again.replayed)
    }
    Check 'Export is only locally saved and prevents destructive bill voiding' {
        $null=WarehousePost ('/api/finance/exports/'+$script:financeExport+'/saved') @{expectedVersion=1;reason='file written by test consumer'}
        $r=Request 'POST' ('/api/finance/batches/'+$script:financeBatch+'/void') (@{operationId=[Guid]::NewGuid().ToString('N');expectedVersion=1;actor='HTTP';reason='cannot void exported row'}|ConvertTo-Json);Ensure ($r.Status -eq 409)
        $d=(Request 'GET' ('/api/finance/exports/'+$script:financeExport)).Body|ConvertFrom-Json;Ensure ($d.summary.state -eq 'Exported' -and $d.json -eq $script:exportJson)
    }
    Check 'Changed fee evidence marks prior export stale without rewriting history' {
        $null=WarehousePost ('/api/finance/orders/'+$script:financeOrder+'/fees') @{expectedVersion=1;platformFee=1;freight=0;evidence='late fee discovered';reason='reconcile new difference'}
        $d=(Request 'GET' ('/api/finance/exports/'+$script:financeExport)).Body|ConvertFrom-Json;Ensure ($d.stale -and $d.json -eq $script:exportJson)
        $r=Request 'POST' ('/api/finance/exports/'+$script:financeExport+'/saved') (@{operationId=[Guid]::NewGuid().ToString('N');expectedVersion=2;actor='HTTP';reason='stale'}|ConvertTo-Json);Ensure ($r.Status -eq 409)
    }
    Check 'Finance queries, structure audit and restart retain all boundaries' {
        Stop-TestHost;Start-TestHost
        Ensure (((Request 'GET' '/api/finance/audit').Body|ConvertFrom-Json).passed)
        Ensure (((Request 'GET' '/api/business/consistency').Body|ConvertFrom-Json).passed)
        Ensure (((Request 'GET' '/api/finance/orders?pageSize=1&differences=true').Body|ConvertFrom-Json).items.Count -eq 1)
        Ensure (((Request 'GET' '/api/finance/rows?unmatched=true').Body|ConvertFrom-Json).total -eq 0)
    }
} finally { Stop-TestHost }
$failures = @($script:results | Where-Object { -not $_.passed }).Count
@{ passed=($failures -eq 0); total=$script:results.Count; failed=$failures; results=$script:results } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $EvidenceDirectory 'api-results.json') -Encoding UTF8
if ($failures -gt 0) { throw "$failures HTTP checks failed" }
