# HTTP Server Test Script
# This script builds SharpTS and runs the HTTP server example
#
# Usage: .\http-test.ps1
#
# NOTE: The http module is not yet integrated into the SharpTS runtime.
#       This script is used to test progress toward that goal.
#       Once integrated, the server will start on http://localhost:3000

$ErrorActionPreference = "Stop"

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  SharpTS HTTP Server Test" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Step 1: Build the solution
Write-Host "[1/2] Building SharpTS solution..." -ForegroundColor Yellow
dotnet build SharpTS.sln --configuration Debug --verbosity quiet

if ($LASTEXITCODE -ne 0) {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}

Write-Host "Build successful!" -ForegroundColor Green
Write-Host ""

# Step 2: Run the HTTP server example
Write-Host "[2/2] Running HTTP server example..." -ForegroundColor Yellow
Write-Host ""

# Run the example
$output = dotnet run --project SharpTS.csproj --no-build -- Examples/http.ts 2>&1

# Check for module not found error (expected until http is integrated)
if ($output -match "Cannot resolve bare specifier 'http'") {
    Write-Host "----------------------------------------" -ForegroundColor DarkGray
    Write-Host "Expected: http module not yet integrated" -ForegroundColor Yellow
    Write-Host "----------------------------------------" -ForegroundColor DarkGray
    Write-Host ""
    Write-Host "Next steps to enable http module:" -ForegroundColor Cyan
    Write-Host "  1. Add reference: SharpTS.csproj -> SharpTS.Node" -ForegroundColor White
    Write-Host "  2. Create: Runtime/BuiltIns/Modules/HttpModuleInterpreter.cs" -ForegroundColor White
    Write-Host "  3. Register module in ModuleResolver" -ForegroundColor White
    Write-Host "  4. Integrate NodeEventLoop for async execution" -ForegroundColor White
    Write-Host ""
    exit 0
}

# If we got here, the server should be running or there's another error
Write-Host $output

# If server is running
if ($output -match "Server running") {
    Write-Host "----------------------------------------" -ForegroundColor DarkGray
    Write-Host "Server running at http://localhost:3000" -ForegroundColor Green
    Write-Host "Press Ctrl+C to stop the server" -ForegroundColor DarkGray
    Write-Host "----------------------------------------" -ForegroundColor DarkGray
}
