#Requires -Version 7.0
<#
.SYNOPSIS
    Stops (and optionally removes) the benchmark Kafka Docker container.

.DESCRIPTION
    Counterpart to kafka-up.ps1. By default the container is stopped so it can be
    restarted cheaply; pass -Remove to delete it (dropping all data, since the KRaft
    log lives inside the container at /tmp).

.PARAMETER ContainerName
    Name of the Docker container. Default: cnq-kafka-bench

.PARAMETER Remove
    Remove the container instead of just stopping it.

.EXAMPLE
    ./scripts/kafka-down.ps1

.EXAMPLE
    ./scripts/kafka-down.ps1 -Remove
#>
[CmdletBinding()]
param(
    [string]$ContainerName = 'cnq-kafka-bench',
    [switch]$Remove
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command docker -ErrorAction SilentlyContinue)) {
    throw "docker CLI was not found on PATH."
}

$existing = docker ps -a --filter "name=^${ContainerName}$" --format '{{.Names}}'
if (-not $existing) {
    Write-Host "Container '$ContainerName' does not exist; nothing to do." -ForegroundColor Yellow
    return
}

$running = docker ps --filter "name=^${ContainerName}$" --format '{{.Names}}'
if ($running) {
    Write-Host "Stopping '$ContainerName'..." -ForegroundColor Cyan
    docker stop $ContainerName | Out-Null
}
else {
    Write-Host "Container '$ContainerName' is already stopped." -ForegroundColor Yellow
}

if ($Remove) {
    Write-Host "Removing '$ContainerName'..." -ForegroundColor Cyan
    docker rm $ContainerName | Out-Null
    Write-Host "Removed '$ContainerName'." -ForegroundColor Green
}
else {
    Write-Host "Stopped '$ContainerName' (use -Remove to delete it)." -ForegroundColor Green
}
