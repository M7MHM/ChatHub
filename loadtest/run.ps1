param(
    [ValidateSet('single', 'matrix', 'max')] [string]$Mode = 'single',
    [int]$Rate = 30,
    [string]$StepRates = '',
    [int]$Replicas = 1,
    [ValidateSet('true', 'false')] [string]$Caching = 'true',
    [string]$Name,
    [string]$Scenario = 'read-heavy',
    [int]$Users = 200,
    [int]$RampUp = 30,
    [int]$Warmup = 30,
    [int]$Steady = 120,
    [int]$RampDown = 15,
    [string]$ExtraEnv = '',
    [int]$MaxPoolSize = 100
)

Add-Type -Namespace LoadTest -Name Power -MemberDefinition '[DllImport("kernel32.dll")] public static extern uint SetThreadExecutionState(uint flags);'
[LoadTest.Power]::SetThreadExecutionState([uint32]'0x80000003') | Out-Null

$root = Split-Path $PSScriptRoot -Parent
$results = Join-Path $PSScriptRoot 'results'
$compose = @('compose', '-f', (Join-Path $root 'docker-compose.yml'), '-f', (Join-Path $root 'docker-compose.loadtest.yml'))
New-Item -ItemType Directory -Force $results | Out-Null

function Invoke-Psql([string]$Sql) {
    $Sql | docker @compose exec -T postgres sh -c 'psql -U "$POSTGRES_USER" -d "$POSTGRES_DB" -At'
}

function Get-Backends {
    docker @compose exec -T postgres sh -c "ps -eo args | grep -E '^postgres: [^ ]+ [^ ]+ [0-9]' | sed -E 's/postgres: [^ ]+ [^ ]+ ([0-9.]+)\(.*\) ([a-z]+).*/\1 \2/' | sort | uniq -c"
}

function Set-Configuration([int]$ApiReplicas, [string]$CachingEnabled) {
    Write-Host "== configuration: $ApiReplicas api replica(s), caching $CachingEnabled, Npgsql max pool size $MaxPoolSize"
    $env:CACHING_ENABLED = $CachingEnabled
    $env:DB_MAX_POOL_SIZE = $MaxPoolSize
    docker @compose up -d 2>&1 | Out-Null
    docker @compose up -d --no-deps --force-recreate --scale "api=$ApiReplicas" api 2>&1 | Out-Null
    Invoke-Psql 'CREATE EXTENSION IF NOT EXISTS pg_stat_statements;' | Out-Null
    Wait-Replicas $ApiReplicas
}

function Wait-Replicas([int]$ApiReplicas) {
    $deadline = (Get-Date).AddMinutes(2)
    $instances = @{}
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest 'http://localhost:8080/health/ready' -UseBasicParsing -TimeoutSec 5
            if ($response.Content -eq 'Healthy') { $instances[$response.Headers['X-Instance-Id']] = $true }
        }
        catch { $instances.Clear() }
        if ($instances.Count -ge $ApiReplicas) { break }
        Start-Sleep -Milliseconds 500
    }
    if ($instances.Count -lt $ApiReplicas) { throw "Only $($instances.Count) of $ApiReplicas replicas answered through nginx." }
    Start-Sleep -Seconds 12
}

function Invoke-LoadTest([string]$RunName, [int]$RunRate) {
    Invoke-Psql 'SELECT pg_stat_statements_reset();' | Out-Null
    $log = Join-Path $results "$RunName.log"
    $errorLog = Join-Path $results "$RunName.stderr.log"
    $arguments = $compose + @('--profile', 'loadtest', 'run', '--rm',
        '-e', "SCENARIO=$Scenario", '-e', "RESULT_NAME=$RunName", '-e', "RATE=$RunRate", '-e', "USERS=$Users",
        '-e', "RAMP_UP=$RampUp", '-e', "WARMUP=$Warmup", '-e', "STEADY=$Steady", '-e', "RAMP_DOWN=$RampDown")
    foreach ($pair in ($ExtraEnv -split ',' | Where-Object { $_ })) { $arguments += @('-e', $pair.Trim()) }
    $arguments += @('k6', 'run', '-q', 'chathub.js')

    Write-Host "-- $RunName at $RunRate iterations/s"
    $runStart = Get-Date
    $process = Start-Process docker -ArgumentList $arguments -NoNewWindow -PassThru `
        -RedirectStandardOutput $log -RedirectStandardError $errorLog
    while (-not $process.HasExited -and -not (Select-String -Path $log, $errorLog -Pattern 'SETUP_DONE' -Quiet)) {
        Start-Sleep -Milliseconds 500
    }

    $samples = @()
    if (-not $process.HasExited) {
        Start-Sleep -Seconds ($RampUp + $Warmup)
        $end = (Get-Date).AddSeconds($Steady)
        while ((Get-Date) -lt $end -and -not $process.HasExited) {
            $at = Get-Date -Format 's'
            foreach ($line in docker stats --no-stream --format '{{.Name}};{{.CPUPerc}};{{.MemUsage}}') {
                $name, $cpu, $memory = $line -split ';'
                if ($name -like 'chathub-*') {
                    $samples += [pscustomobject]@{ At = $at; Container = $name; Cpu = [double]($cpu -replace '%', ''); Memory = ($memory -split ' / ')[0] }
                }
            }
        }
    }
    $process.WaitForExit()

    $samples | Export-Csv (Join-Path $results "$RunName.stats.csv") -NoTypeInformation
    $cpu = $samples | Group-Object Container | Sort-Object Name | ForEach-Object {
        [pscustomobject]@{
            Container = $_.Name
            AvgCpu = [math]::Round(($_.Group | Measure-Object Cpu -Average).Average, 1)
            MaxCpu = [math]::Round(($_.Group | Measure-Object Cpu -Maximum).Maximum, 1)
            Samples = $_.Count
        }
    }
    $cpu | ConvertTo-Json | Set-Content (Join-Path $results "$RunName.cpu.json")

    Get-Backends | Set-Content (Join-Path $results "$RunName.connections.txt")
    $replicas = @(docker @compose ps -q api).Count
    docker @compose restart api 2>&1 | Out-Null
    Invoke-Psql @'
SELECT calls, round(mean_exec_time::numeric, 2) AS mean_ms, round(total_exec_time::numeric) AS total_ms,
       regexp_replace(left(query, 160), '\s+', ' ', 'g') AS query
FROM pg_stat_statements WHERE query NOT ILIKE '%pg_stat_statements%'
ORDER BY total_exec_time DESC LIMIT 10;
'@ | Set-Content (Join-Path $results "$RunName.pgstat.txt")
    Wait-Replicas $replicas

    $standby = Get-WinEvent -FilterHashtable @{ LogName = 'System'; ProviderName = 'Microsoft-Windows-Kernel-Power'; Id = 506; StartTime = $runStart } -ErrorAction SilentlyContinue
    if ($standby) {
        $message = "INVALID: the machine entered standby at $($standby[-1].TimeCreated) during this run; discard its numbers."
        Set-Content (Join-Path $results "$RunName.INVALID.txt") $message
        Write-Host $message -ForegroundColor Red
    }

    Write-Host "postgres backends right after the run (count, client, state):"
    Get-Content (Join-Path $results "$RunName.connections.txt") | ForEach-Object { Write-Host "  $_" }
    Get-Content $log | Select-String -Pattern '^===|^steady|^endpoint|^me |^conversations|^messages|^send|^all|^realtime|^delivery|^thresholds' |
        ForEach-Object { Write-Host $_.Line }
    $cpu | Format-Table -AutoSize | Out-String | Write-Host
    return Get-Content (Join-Path $results "$RunName.summary.json") -Raw | ConvertFrom-Json
}

switch ($Mode) {
    'single' {
        Set-Configuration $Replicas $Caching
        $runName = if ($Name) { $Name } else { "r$Replicas-cache-$Caching-$Rate" }
        Invoke-LoadTest $runName $Rate | Out-Null
    }
    'matrix' {
        foreach ($config in @(@(1, 'false'), @(1, 'true'), @(2, 'true'))) {
            Set-Configuration $config[0] $config[1]
            Invoke-LoadTest "matrix-r$($config[0])-cache-$($config[1])-$Rate" $Rate | Out-Null
        }
    }
    'max' {
        Set-Configuration $Replicas $Caching
        foreach ($stepRate in ($StepRates -split ',' | Where-Object { $_ } | ForEach-Object { [int]$_ })) {
            $poolSuffix = if ($MaxPoolSize -ne 100) { "-pool$MaxPoolSize" } else { '' }
            $summary = Invoke-LoadTest "max-r$Replicas-cache-$Caching$poolSuffix-$stepRate" $stepRate
            if (-not $summary.thresholdsPassed) {
                Write-Host "== $stepRate iterations/s broke the thresholds: $($summary.failedThresholds -join '; ')"
                break
            }
        }
    }
}
