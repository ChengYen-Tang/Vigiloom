param([string]$SdkVersion = '', [switch]$Recovery)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$scratch = Join-Path $root '.codex-deployment-smoke'
if (Test-Path -LiteralPath $scratch) { throw "Scratch directory already exists: $scratch" }
New-Item -ItemType Directory $scratch | Out-Null
$oldTemp = $env:TEMP
$oldTmp = $env:TMP
$id = [guid]::NewGuid().ToString('N').Substring(0, 12)
$tag = "vigiloom-server:req40-deploy-$id"
$network = "vigiloom-req40-$id"
$accessNetwork = "vigiloom-web-access-$id"
$builder = "vigiloom-builder-$id"
$builderCreated = $false
$sqlName = "vigiloom-sql-$id"
$webName = "vigiloom-web-$id"
$workerName = "vigiloom-worker-$id"
$sqlImage = 'mcr.microsoft.com/mssql/server:2022-latest'
$builderImage = 'moby/buildkit:buildx-stable-1'
$password = 'Vg!' + [guid]::NewGuid().ToString('N') + '9a'
$certPassword = [guid]::NewGuid().ToString('N')
function Get-AntiforgeryToken([string]$html) {
    foreach ($match in [regex]::Matches($html, '<input\b[^>]*>', 'IgnoreCase')) {
        $tag = $match.Value
        if ($tag -notmatch 'name="__RequestVerificationToken"') { continue }
        if ($tag -notmatch 'value="([^"]+)"') { throw 'Antiforgery token is missing.' }
        return [Net.WebUtility]::HtmlDecode($Matches[1])
    }
    throw 'Antiforgery field not found.'
}
function Get-WebPort {
    $mapped = @(docker port $webName 7043/tcp 2>&1)
    if ($LASTEXITCODE -ne 0 -or $mapped.Count -ne 1 -or "$($mapped[0])" -notmatch ':(\d+)$') {
        $logs = @(docker logs --tail 30 $webName 2>&1) | ForEach-Object { "$_".Replace($password, '[redacted]').Replace($certPassword, '[redacted]').Replace('temporary-test-token-with-at-least-32-characters', '[redacted]') }
        throw "Web port unavailable. Container logs: $($logs -join ' ')"
    }
    return $Matches[1]
}
docker image inspect $sqlImage *> $null
$sqlImageWasPresent = $LASTEXITCODE -eq 0
docker image inspect $builderImage *> $null
$builderImageWasPresent = $LASTEXITCODE -eq 0

try {
    $env:TEMP = $scratch
    $env:TMP = $scratch
    if ($SdkVersion) {
        $temporaryConfig = @{ sdk = @{ version = $SdkVersion; rollForward = 'latestFeature'; allowPrerelease = $true } } | ConvertTo-Json -Compress
        [IO.File]::WriteAllText((Join-Path $scratch 'global.json'), $temporaryConfig)
    }
    Push-Location $scratch
    try {
        foreach ($component in @('Web', 'Worker', 'Migrator')) {
            $project = Join-Path $root "src/Hosts/Vigiloom.$component/Vigiloom.$component.csproj"
            dotnet publish $project -c Release -r linux-x64 --self-contained true -o (Join-Path $scratch ($component.ToLowerInvariant())) --disable-build-servers -p:UseSharedCompilation=false -v:q
            if ($LASTEXITCODE -ne 0) { throw "Publish failed: $component" }
        }
        Copy-Item (Join-Path $root 'packaging/server/Dockerfile') $scratch
        docker buildx create --name $builder --driver docker-container | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Isolated Docker builder creation failed.' }
        $builderCreated = $true
        docker buildx build --builder $builder --load -q -t $tag -f (Join-Path $scratch 'Dockerfile') $scratch | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Container build failed.' }
        $rsa = [Security.Cryptography.RSA]::Create(2048)
        try {
            $request = [Security.Cryptography.X509Certificates.CertificateRequest]::new('CN=localhost', $rsa,
                [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
            $certificate = $request.CreateSelfSigned([DateTimeOffset]::UtcNow.AddMinutes(-1), [DateTimeOffset]::UtcNow.AddDays(1))
            try { [IO.File]::WriteAllBytes((Join-Path $scratch 'tls.pfx'), $certificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Pfx, $certPassword)) }
            finally { $certificate.Dispose() }
        } finally { $rsa.Dispose() }
        New-Item -ItemType Directory (Join-Path $scratch 'keys'), (Join-Path $scratch 'packages') | Out-Null
        docker network create --internal $network | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Test network creation failed.' }
        docker network create $accessNetwork | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Test Web access network creation failed.' }
        docker run -d --name $sqlName --network $network -e ACCEPT_EULA=Y -e "MSSQL_SA_PASSWORD=$password" $sqlImage | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'SQL Server container startup failed.' }
        $ready = $false
        for ($attempt = 0; $attempt -lt 90 -and -not $ready; $attempt++) {
            docker exec $sqlName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $password -C -b -Q 'SELECT 1' *> $null
            if ($LASTEXITCODE -eq 0) { $ready = $true } else { Start-Sleep -Seconds 2 }
        }
        if (-not $ready) { throw 'SQL Server did not become ready.' }
        docker exec $sqlName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $password -C -b -Q 'CREATE DATABASE VigiloomSmoke' | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Test database creation failed.' }
        $sqlConnection = "Server=$sqlName,1433;Database=VigiloomSmoke;User ID=sa;Password=$password;Encrypt=True;TrustServerCertificate=True"
        docker run --rm --network $network -e "VIGILOOM_MIGRATION_SQL_CONNECTION=$sqlConnection" --workdir /app/migrator --entrypoint /app/migrator/Vigiloom.Migrator $tag fresh-install
        if ($LASTEXITCODE -ne 0) { throw 'Container migration failed.' }
        $keysMount = (Join-Path $scratch 'keys') + ':/data/keys'
        $packagesMount = (Join-Path $scratch 'packages') + ':/data/packages'
        $tlsMount = (Join-Path $scratch 'tls.pfx') + ':/run/tls.pfx:ro'
        docker create --name $webName --network $accessNetwork -p '127.0.0.1::7043' -e 'ASPNETCORE_URLS=https://+:7043' -e 'ASPNETCORE_Kestrel__Certificates__Default__Path=/run/tls.pfx' -e "ASPNETCORE_Kestrel__Certificates__Default__Password=$certPassword" -e "ConnectionStrings__Vigiloom=$sqlConnection" -e 'Vigiloom__DataProtection__KeyRingPath=/data/keys' -e 'Vigiloom__AgentPackages__CachePath=/data/packages' -e 'Vigiloom__SetupToken=temporary-test-token-with-at-least-32-characters' -v $keysMount -v $packagesMount -v $tlsMount $tag | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Web container creation failed.' }
        docker network connect $network $webName
        if ($LASTEXITCODE -ne 0) { throw 'Could not connect Web to isolated SQL network.' }
        docker start $webName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Web container startup failed.' }
        docker run -d --name $workerName --network $network -e "ConnectionStrings__Vigiloom=$sqlConnection" -e 'Vigiloom__DataProtection__KeyRingPath=/data/keys' -v $keysMount --workdir /app/worker --entrypoint /app/worker/Vigiloom.Worker $tag | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Worker container startup failed.' }
        $port = Get-WebPort
        $response = $null
        for ($attempt = 0; $attempt -lt 45 -and $null -eq $response; $attempt++) {
            try { $response = Invoke-WebRequest -Uri "https://127.0.0.1:$port/_framework/blazor.web.js" -SkipCertificateCheck -TimeoutSec 2 } catch { Start-Sleep -Seconds 1 }
        }
        if ($null -eq $response -or $response.StatusCode -ne 200 -or $response.Content.Length -lt 1000) { throw 'HTTPS Blazor asset check failed.' }
        $setup = Invoke-WebRequest -Uri "https://127.0.0.1:$port/account/setup" -SkipCertificateCheck -TimeoutSec 10
        if ($setup.StatusCode -ne 200) { throw 'Setup page check failed.' }
        if ((docker inspect -f '{{.State.Running}}' $workerName) -ne 'true') { throw 'Worker did not remain running.' }
        $heartbeat = $false
        for ($attempt = 0; $attempt -lt 30 -and -not $heartbeat; $attempt++) {
            $count = docker exec $sqlName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $password -C -b -d VigiloomSmoke -h -1 -W -Q 'SET NOCOUNT ON; SELECT COUNT(*) FROM [hangfire].[Server]'
            if ($LASTEXITCODE -eq 0 -and ($count | Out-String).Trim() -match '^\d+$' -and [int]($count | Out-String).Trim() -gt 0) { $heartbeat = $true }
            else { Start-Sleep -Seconds 1 }
        }
        if (-not $heartbeat) { throw 'Worker scheduler heartbeat not observed.' }
        'SQL Server migration, HTTPS Web, Blazor assets and Worker scheduler heartbeat passed.'
        if ($Recovery) {
            $base = "https://127.0.0.1:$port"
            $setupPage = Invoke-WebRequest -Uri "$base/account/setup" -SkipCertificateCheck -SessionVariable session
            $setupBody = @{ _handler = 'setup'; __RequestVerificationToken = (Get-AntiforgeryToken $setupPage.Content)
                'Input.SetupToken' = 'temporary-test-token-with-at-least-32-characters'; 'Input.UserName' = 'recovery-admin'
                'Input.Password' = 'Temporary-Recovery-123!'; 'Input.ConfirmPassword' = 'Temporary-Recovery-123!' }
            $null = Invoke-WebRequest -Uri "$base/account/setup" -Method Post -Body $setupBody -WebSession $session -SkipCertificateCheck
            $loginPage = Invoke-WebRequest -Uri "$base/account/login" -WebSession $session -SkipCertificateCheck
            $loginBody = @{ _handler = 'login'; __RequestVerificationToken = (Get-AntiforgeryToken $loginPage.Content)
                'Input.UserName' = 'recovery-admin'; 'Input.Password' = 'Temporary-Recovery-123!' }
            $null = Invoke-WebRequest -Uri "$base/account/login" -Method Post -Body $loginBody -WebSession $session -SkipCertificateCheck
            $overview = Invoke-WebRequest -Uri "$base/api/v1/workspace/overview" -WebSession $session -SkipCertificateCheck
            if ($overview.StatusCode -ne 200) { throw 'Test administrator login failed.' }
            if (@(Get-ChildItem -LiteralPath (Join-Path $scratch 'keys') -Filter '*.xml').Count -eq 0) { throw 'Data Protection key ring was not created.' }
            [IO.File]::WriteAllText((Join-Path $scratch 'packages/recovery-fixture.txt'), 'isolated package-directory fixture')
            $packageHash = (Get-FileHash -LiteralPath (Join-Path $scratch 'packages/recovery-fixture.txt')).Hash
            docker stop -t 10 $webName $workerName | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not stop test hosts before backup.' }
            Copy-Item -LiteralPath (Join-Path $scratch 'keys') -Destination (Join-Path $scratch 'restored-keys') -Recurse
            Copy-Item -LiteralPath (Join-Path $scratch 'packages') -Destination (Join-Path $scratch 'restored-packages') -Recurse
            Copy-Item -LiteralPath (Join-Path $scratch 'tls.pfx') -Destination (Join-Path $scratch 'restored-tls.pfx')
            if ((Get-FileHash -LiteralPath (Join-Path $scratch 'restored-packages/recovery-fixture.txt')).Hash -ne $packageHash) { throw 'Package-directory fixture copy failed.' }
            $backup = "BACKUP DATABASE [VigiloomSmoke] TO DISK = N'/var/opt/mssql/data/vigiloom-recovery.bak' WITH COPY_ONLY, CHECKSUM, INIT"
            docker exec $sqlName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $password -C -b -Q $backup | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Test database backup failed.' }
            $timer = [Diagnostics.Stopwatch]::StartNew()
            $restore = @"
DECLARE @data sysname = (SELECT TOP (1) name FROM sys.master_files WHERE database_id = DB_ID(N'VigiloomSmoke') AND type = 0 ORDER BY file_id);
DECLARE @log sysname = (SELECT TOP (1) name FROM sys.master_files WHERE database_id = DB_ID(N'VigiloomSmoke') AND type = 1 ORDER BY file_id);
DECLARE @sql nvarchar(max) = N'RESTORE DATABASE [VigiloomRestored] FROM DISK = N''/var/opt/mssql/data/vigiloom-recovery.bak'' WITH CHECKSUM, MOVE ' + QUOTENAME(@data, '''') + N' TO N''/var/opt/mssql/data/vigiloom-restored.mdf'', MOVE ' + QUOTENAME(@log, '''') + N' TO N''/var/opt/mssql/data/vigiloom-restored.ldf''';
EXEC sys.sp_executesql @sql;
"@
            docker exec $sqlName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $password -C -b -Q $restore | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Test database restore failed.' }
            $restoredConnection = "Server=$sqlName,1433;Database=VigiloomRestored;User ID=sa;Password=$password;Encrypt=True;TrustServerCertificate=True"
            docker run --rm --network $network -e "VIGILOOM_MIGRATION_SQL_CONNECTION=$restoredConnection" --workdir /app/migrator --entrypoint /app/migrator/Vigiloom.Migrator $tag
            if ($LASTEXITCODE -ne 0) { throw 'Restored database migration failed.' }
            $marker = (docker exec $sqlName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $password -C -b -h -1 -W -Q "SET NOCOUNT ON; SELECT CONVERT(varchar(30), SYSUTCDATETIME(), 126)").Trim()
            if ($LASTEXITCODE -ne 0 -or $marker -notmatch '^\d{4}-\d{2}-\d{2}T') { throw 'Could not obtain SQL recovery timestamp.' }
            docker rm $webName $workerName | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Could not remove stopped test hosts.' }
            $restoredKeys = (Join-Path $scratch 'restored-keys') + ':/data/keys'
            $restoredPackages = (Join-Path $scratch 'restored-packages') + ':/data/packages'
            $restoredTls = (Join-Path $scratch 'restored-tls.pfx') + ':/run/tls.pfx:ro'
            docker create --name $webName --network $accessNetwork -p '127.0.0.1::7043' -e 'ASPNETCORE_URLS=https://+:7043' -e 'ASPNETCORE_Kestrel__Certificates__Default__Path=/run/tls.pfx' -e "ASPNETCORE_Kestrel__Certificates__Default__Password=$certPassword" -e "ConnectionStrings__Vigiloom=$restoredConnection" -e 'Vigiloom__DataProtection__KeyRingPath=/data/keys' -e 'Vigiloom__AgentPackages__CachePath=/data/packages' -v $restoredKeys -v $restoredPackages -v $restoredTls $tag | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Restored Web container creation failed.' }
            docker network connect $network $webName
            if ($LASTEXITCODE -ne 0) { throw 'Could not connect restored Web to isolated SQL network.' }
            docker start $webName | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Restored Web failed to start.' }
            docker run -d --name $workerName --network $network -e "ConnectionStrings__Vigiloom=$restoredConnection" -e 'Vigiloom__DataProtection__KeyRingPath=/data/keys' -v $restoredKeys --workdir /app/worker --entrypoint /app/worker/Vigiloom.Worker $tag | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Restored Worker failed to start.' }
            $restoredBase = "https://127.0.0.1:$(Get-WebPort)"
            $restoredOverview = $null; $restoredHeartbeat = $false
            for ($attempt = 0; $attempt -lt 90 -and ($null -eq $restoredOverview -or -not $restoredHeartbeat); $attempt++) {
                if ($null -eq $restoredOverview) {
                    try { $restoredOverview = Invoke-WebRequest -Uri "$restoredBase/api/v1/workspace/overview" -WebSession $session -SkipCertificateCheck -TimeoutSec 2 } catch { }
                }
                if (-not $restoredHeartbeat) {
                    $count = docker exec $sqlName /opt/mssql-tools18/bin/sqlcmd -S localhost -U sa -P $password -C -b -d VigiloomRestored -h -1 -W -Q "SET NOCOUNT ON; SELECT COUNT(*) FROM [hangfire].[Server] WHERE [LastHeartbeat] >= CONVERT(datetime2, N'$marker', 126)"
                    $restoredHeartbeat = $LASTEXITCODE -eq 0 -and ($count | Out-String).Trim() -match '^\d+$' -and [int]($count | Out-String).Trim() -gt 0
                }
                if ($null -eq $restoredOverview -or -not $restoredHeartbeat) { Start-Sleep -Seconds 1 }
            }
            if ($null -eq $restoredOverview -or $restoredOverview.StatusCode -ne 200 -or -not $restoredHeartbeat) { throw 'Restored login or Worker heartbeat was not verified.' }
            $timer.Stop()
            "Recovery: restored database, copied key ring / TLS / package-directory fixture, reused authenticated session and observed Worker heartbeat in $([math]::Round($timer.Elapsed.TotalSeconds, 2)) seconds."
        }
    } finally { Pop-Location }
} finally {
    $cleanupErrors = [Collections.Generic.List[string]]::new()
    $containers = @(docker ps -a --format '{{.Names}}')
    if ($LASTEXITCODE -ne 0) { $cleanupErrors.Add('Could not inspect Docker containers') }
    else {
        foreach ($name in @($webName, $workerName, $sqlName)) {
            if ($name -notin $containers) { continue }
            docker rm -fv $name *> $null
            if ($LASTEXITCODE -ne 0) { $cleanupErrors.Add("Could not remove container $name") }
        }
        $remaining = @(docker ps -a --format '{{.Names}}')
        if ($LASTEXITCODE -ne 0) { $cleanupErrors.Add('Could not verify Docker container cleanup') }
        else {
            foreach ($name in @($webName, $workerName, $sqlName)) {
                if ($name -in $remaining) { $cleanupErrors.Add("Container still exists: $name") }
            }
        }
    }
    foreach ($testNetwork in @($network, $accessNetwork)) {
        docker network inspect $testNetwork *> $null
        if ($LASTEXITCODE -eq 0) {
            docker network rm $testNetwork *> $null
            if ($LASTEXITCODE -ne 0) { $cleanupErrors.Add("Could not remove network $testNetwork") }
        }
    }
    if ($builderCreated) {
        docker buildx rm $builder *> $null
        if ($LASTEXITCODE -ne 0) { $cleanupErrors.Add("Could not remove builder $builder") }
    }
    docker image inspect $tag *> $null
    if ($LASTEXITCODE -eq 0) {
        docker image rm $tag *> $null
        if ($LASTEXITCODE -ne 0) { $cleanupErrors.Add("Could not remove image $tag") }
    }
    if (-not $sqlImageWasPresent) {
        docker image inspect $sqlImage *> $null
        if ($LASTEXITCODE -eq 0) { docker image rm $sqlImage *> $null }
    }
    if (-not $builderImageWasPresent) {
        docker image inspect $builderImage *> $null
        if ($LASTEXITCODE -eq 0) { docker image rm $builderImage *> $null }
    }
    $env:TEMP = $oldTemp
    $env:TMP = $oldTmp
    if ($cleanupErrors.Count -eq 0 -and (Test-Path $scratch) -and (Resolve-Path $scratch).Path -eq (Join-Path $root '.codex-deployment-smoke')) {
        try {
            Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction Stop
            if (Test-Path $scratch) { $cleanupErrors.Add("Scratch directory remains: $scratch") }
        } catch { $cleanupErrors.Add("Could not remove scratch directory: $scratch") }
    }
    if ($cleanupErrors.Count -gt 0) { throw "Cleanup incomplete; retained $scratch. $($cleanupErrors -join '; ')" }
}
