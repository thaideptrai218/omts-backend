[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Image,
    [ValidateRange(1, 120)][int] $TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$containerName = "omts-smoke-$([Guid]::NewGuid().ToString('N'))"
$created = $false

try {
    # Bind a random loopback port, avoiding conflicts with a developer's services.
    & docker run --detach --name $containerName --publish '127.0.0.1::8080' `
        --env 'AllowedHosts=localhost' --env 'ASPNETCORE_ENVIRONMENT=Production' $Image
    if ($LASTEXITCODE -ne 0) { throw 'Container creation failed.' }
    $created = $true

    $configuredUser = & docker inspect --format '{{.Config.User}}' $containerName
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect container user.' }
    if ([string]::IsNullOrWhiteSpace($configuredUser) -or $configuredUser -match '^(0|root)(:|$)') {
        throw 'The image must configure a non-root runtime user.'
    }

    $mapping = & docker port $containerName '8080/tcp'
    if ($LASTEXITCODE -ne 0 -or $mapping -notmatch '^127\.0\.0\.1:(\d+)$') {
        throw 'Unable to resolve the published health probe port.'
    }
    $baseUri = "http://localhost:$($Matches[1])"
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $healthy = $false
    do {
        $running = & docker inspect --format '{{.State.Running}}' $containerName
        if ($LASTEXITCODE -ne 0 -or $running -ne 'true') { throw 'Container exited before health probes passed.' }
        try {
            $live = Invoke-WebRequest -Uri "$baseUri/health/live" -TimeoutSec 2 -MaximumRedirection 0
            $ready = Invoke-WebRequest -Uri "$baseUri/health/ready" -TimeoutSec 2 -MaximumRedirection 0
            $healthy = $live.StatusCode -eq 200 -and $ready.StatusCode -eq 200
        }
        catch {
            # Connection refusal and non-success responses are expected during startup.
            $healthy = $false
        }
        if ($healthy) { break }
        Start-Sleep -Milliseconds 500
    } while ([DateTimeOffset]::UtcNow -lt $deadline)

    if (-not $healthy) { throw "Health probes did not pass within $TimeoutSeconds seconds." }
    Write-Host "Non-root container passed /health/live and /health/ready at $baseUri."
}
catch {
    if ($created) {
        & docker logs --tail 100 $containerName
        & docker inspect --format '{{json .State}}' $containerName
    }
    throw
}
finally {
    # Remove even on startup failures; a named container may exist after a failed run.
    & docker rm --force $containerName 2>$null | Out-Null
    if ($created -and $LASTEXITCODE -ne 0) {
        Write-Warning "Could not remove smoke container $containerName."
    }
}
