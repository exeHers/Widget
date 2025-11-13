# SideHUD Publishing Script
# Creates a standalone executable ready for installation

Write-Host "Building SideHUD for Windows..." -ForegroundColor Cyan

# Build standalone executable
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true

if ($LASTEXITCODE -eq 0) {
    $publishPath = "bin\Release\net8.0-windows\win-x64\publish"
    Write-Host "`n✓ Build successful!" -ForegroundColor Green
    Write-Host "`nExecutable location: $((Resolve-Path $publishPath).Path)\SideHUD.exe" -ForegroundColor Yellow
    Write-Host "`nTo install:" -ForegroundColor Cyan
    Write-Host "1. Copy the entire 'publish' folder to your desired installation location" -ForegroundColor White
    Write-Host "2. Run SideHUD.exe once - it will auto-enable startup" -ForegroundColor White
    Write-Host "3. Restart your PC to verify auto-startup" -ForegroundColor White
} else {
    Write-Host "`n✗ Build failed. Check errors above." -ForegroundColor Red
    exit 1
}

