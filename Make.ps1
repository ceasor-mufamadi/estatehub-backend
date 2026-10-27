<#
.SYNOPSIS
  PowerShell Makefile for the EstateHub gRPC backend (same shape as chieta-etqa-backend's).

.DESCRIPTION
  Every .NET command runs inside the SDK container from docker-compose.dev.yml, so no local
  .NET SDK is needed. Docker Desktop must be open. On macOS: brew install powershell, then pwsh ./Make.ps1 <target>.
  Without PowerShell, the README lists the equivalent docker compose commands.
#>

param (
    [Parameter(Position=0, Mandatory=$true)]
    [ValidateSet(
        "dev-up", "dev-down", "build", "test", "test-coverage", "run",
        "ef-add", "ef-update", "ef-remove",
        "docker-build", "hooks-install", "help")]
    [string]$Target,

    [Parameter(Position=1)]
    [string]$Name
)

# -----------------------------
# Config
# -----------------------------
$Compose            = "docker compose -f docker-compose.dev.yml"
$ProjectPath        = "src/EstateHub.Api/EstateHub.Api.csproj"
$InfrastructurePath = "src/EstateHub.Infrastructure/EstateHub.Infrastructure.csproj"
$TestsPath          = "tests/EstateHub.Tests/EstateHub.Tests.csproj"
$ImageName          = "estatehub-backend"
$VersionFile        = "./version"

if (-not (Test-Path $VersionFile)) {
    Write-Host "[ERROR] version file not found" -ForegroundColor Red
    exit 1
}

$Version = (Get-Content $VersionFile -Raw).Trim()

function Write-Info($msg) {
    Write-Host "[INFO]" $msg -ForegroundColor Cyan
}

function Write-ErrorMsg($msg) {
    Write-Host "[ERROR]" $msg -ForegroundColor Red
    exit 1
}

function Sdk([string]$Command) {
    Invoke-Expression "$Compose run --rm sdk $Command"
}

switch ($Target) {
    "dev-up" {
        Write-Info "Starting Postgres + Mailpit (leave them running)..."
        Invoke-Expression "$Compose up -d"
    }
    "dev-down" {
        Write-Info "Stopping dev containers (data volumes are kept)..."
        Invoke-Expression "$Compose down"
    }
    "build" {
        Write-Info "Restoring and building the solution..."
        Sdk "dotnet build"
    }
    "test" {
        Write-Info "Running the test suite..."
        Sdk "dotnet test $TestsPath"
    }
    "test-coverage" {
        Write-Info "Running tests with coverage collection..."
        Sdk "dotnet test $TestsPath --collect:`"XPlat Code Coverage`" --results-directory ./coverage"
    }
    "run" {
        Write-Info "Running the API (gRPC :5269, gRPC-Web :5268)..."
        Invoke-Expression "$Compose --profile app up api"
    }
    "ef-add" {
        if (-not $Name) { Write-ErrorMsg "Usage: ./Make.ps1 ef-add <MigrationName>" }
        Write-Info "Adding EF Core migration '$Name'..."
        Sdk "sh -c 'dotnet tool restore && dotnet ef migrations add $Name --project $InfrastructurePath --startup-project $InfrastructurePath --output-dir Data/Migrations'"
    }
    "ef-update" {
        Write-Info "Applying EF Core migrations to estatehub_dev (as estatehub_migrator)..."
        Sdk "sh -c 'dotnet tool restore && dotnet ef database update --project $InfrastructurePath --startup-project $InfrastructurePath'"
    }
    "ef-remove" {
        Write-Info "Removing the last EF Core migration..."
        Sdk "sh -c 'dotnet tool restore && dotnet ef migrations remove --project $InfrastructurePath --startup-project $InfrastructurePath'"
    }
    "docker-build" {
        Write-Info "Building Docker image ${ImageName}:${Version} ..."
        docker build -t "${ImageName}:${Version}" .
    }
    "hooks-install" {
        Write-Info "Enabling the repo's git hooks (.githooks/pre-commit)..."
        git config core.hooksPath .githooks
    }
    default {
        Write-Host ""
        Write-Host "Available commands:" -ForegroundColor Yellow
        Write-Host "  ./Make.ps1 dev-up          - Start Postgres + Mailpit"
        Write-Host "  ./Make.ps1 dev-down        - Stop them (data kept)"
        Write-Host "  ./Make.ps1 build           - Build the solution (SDK container)"
        Write-Host "  ./Make.ps1 test            - Run the test suite (SDK container)"
        Write-Host "  ./Make.ps1 test-coverage   - Tests + coverage under ./coverage"
        Write-Host "  ./Make.ps1 run             - Run the API with dotnet watch"
        Write-Host "  ./Make.ps1 ef-add <Name>   - Add an EF Core migration"
        Write-Host "  ./Make.ps1 ef-update       - Apply migrations to estatehub_dev"
        Write-Host "  ./Make.ps1 ef-remove       - Remove the last migration"
        Write-Host "  ./Make.ps1 docker-build    - Build the production image"
        Write-Host "  ./Make.ps1 hooks-install   - Enable the pre-commit hook"
        Write-Host ""
    }
}
