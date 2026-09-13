#Requires -Version 7.0
<#
.SYNOPSIS
    Starts a single-node Kafka (KRaft) broker in Docker for the benchmarks.

.DESCRIPTION
    Creates or reuses a container named 'cnq-kafka-bench', publishing the broker on the
    host and advertising 'localhost:<Port>' so the benchmark client can follow the
    metadata. Idempotent: a stopped container is restarted, a running one is left alone.
    Waits until the broker answers before returning.

.PARAMETER ContainerName
    Name of the Docker container. Default: cnq-kafka-bench

.PARAMETER Port
    Host port the broker is published on; also the advertised port. Default: 9092

.PARAMETER Image
    Kafka image to run. Default: apache/kafka:latest

.PARAMETER TimeoutSeconds
    How long to wait for the broker to become ready. Default: 60

.EXAMPLE
    ./scripts/kafka-up.ps1

.EXAMPLE
    ./scripts/kafka-up.ps1 -Port 19092
#>
[CmdletBinding()]
param(
    [string]$ContainerName = 'cnq-kafka-bench',
    [int]$Port = 9092,
    [string]$Image = 'apache/kafka:latest',
    [int]$TimeoutSeconds = 60
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "docker CLI was not found on PATH."
}

if (-not (docker info *> $null) -and $LASTEXITCODE -ne 0) {
    throw "Docker does not appear to be running. Start Docker Desktop and retry."
}

$existing = docker ps -a --filter "name=^${ContainerName}$" --format '{{.Names}}'
if ($existing) {
    $running = docker ps --filter "name=^${ContainerName}$" --format '{{.Names}}'
    if ($running) {
        Write-Host "Container '$ContainerName' is already running." -ForegroundColor Yellow
    }
    else {
        Write-Host "Starting existing container '$ContainerName'..." -ForegroundColor Cyan
        docker start $ContainerName | Out-Null
    }
}
else {
    $portOwner = docker ps --filter "publish=${Port}" --format '{{.Names}} ({{.Ports}})'
    if ($portOwner) {
        throw "Port $Port is already published by: $portOwner. Stop it or choose a different -Port."
    }

    Write-Host "Creating container '$ContainerName' from $Image on port $Port..." -ForegroundColor Cyan
    $runArgs = @(
        'run', '-d', '--name', $ContainerName,
        '-p', "${Port}:${Port}",
        '-e', 'KAFKA_NODE_ID=1',
        '-e', 'KAFKA_PROCESS_ROLES=broker,controller',
        '-e', "KAFKA_LISTENERS=PLAINTEXT://0.0.0.0:${Port},CONTROLLER://0.0.0.0:9093",
        '-e', "KAFKA_ADVERTISED_LISTENERS=PLAINTEXT://localhost:${Port}",
        '-e', 'KAFKA_CONTROLLER_LISTENER_NAMES=CONTROLLER',
        '-e', 'KAFKA_LISTENER_SECURITY_PROTOCOL_MAP=CONTROLLER:PLAINTEXT,PLAINTEXT:PLAINTEXT',
        '-e', 'KAFKA_CONTROLLER_QUORUM_VOTERS=1@localhost:9093',
        '-e', 'KAFKA_INTER_BROKER_LISTENER_NAME=PLAINTEXT',
        '-e', 'KAFKA_OFFSETS_TOPIC_REPLICATION_FACTOR=1',
        '-e', 'KAFKA_TRANSACTION_STATE_LOG_REPLICATION_FACTOR=1',
        '-e', 'KAFKA_TRANSACTION_STATE_LOG_MIN_ISR=1',
        '-e', 'KAFKA_GROUP_INITIAL_REBALANCE_DELAY_MS=0',
        '-e', 'KAFKA_LOG_DIRS=/tmp/kraft-combined-logs',
        $Image
    )
    docker @runArgs | Out-Null
}

Write-Host "Waiting for the broker to become ready..." -ForegroundColor Cyan
$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
$ready = $false
while ((Get-Date) -lt $deadline) {
    docker exec $ContainerName /opt/kafka/bin/kafka-broker-api-versions.sh --bootstrap-server "localhost:${Port}" *> $null
    if ($LASTEXITCODE -eq 0) {
        $ready = $true
        break
    }

    Start-Sleep -Seconds 2
}

if (-not $ready) {
    throw "Broker did not become ready within $TimeoutSeconds seconds. Check 'docker logs $ContainerName'."
}

Write-Host "Kafka is ready. KAFKA_BOOTSTRAP_SERVERS=localhost:$Port" -ForegroundColor Green
