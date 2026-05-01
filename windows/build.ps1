# build.ps1
# Compiles JASS into a single self-contained Windows .exe.
#
# Usage:   .\build.ps1
# Output:  bin\Release\net8.0-windows\win-x64\publish\JASS.exe
#
# Requirements: .NET 8 SDK installed (dotnet --version should print 8.x or
# higher).
#
# Why these flags:
#   -c Release                   optimized build, no debug symbols inline
#   -r win-x64                   target Windows x64 runtime
#   --self-contained             bundle the .NET runtime inside the exe so
#                                the user does not need to install anything
#   PublishSingleFile=true       merge all DLLs into one .exe
#   IncludeNativeLibrariesForSelfExtract=true
#                                also bundle the native runtime DLLs so we
#                                really ship a single file
#   DebugType=none, DebugSymbols=false
#                                no .pdb next to the exe; logs are the
#                                supported diagnostic channel
#
# The result is a single JASS.exe of roughly 65-80 MB. SmartScreen will
# warn the first time an unsigned exe is run; that is documented in
# INSTALL.txt for end users.

$ErrorActionPreference = "Stop"

Write-Host "Cleaning previous build..."
if (Test-Path "bin")     { Remove-Item -Recurse -Force "bin" }
if (Test-Path "obj")     { Remove-Item -Recurse -Force "obj" }

Write-Host "Publishing JASS (self-contained, single-file, win-x64)..."
dotnet publish jass.csproj `
    -c Release `
    -r win-x64 `
    --self-contained `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=none `
    -p:DebugSymbols=false

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet publish failed."
    exit $LASTEXITCODE
}

$exe = "bin\Release\net8.0-windows\win-x64\publish\JASS.exe"
if (-not (Test-Path $exe)) {
    Write-Error "Expected output not found at $exe"
    exit 1
}

$size = [math]::Round((Get-Item $exe).Length / 1MB, 1)
Write-Host ""
Write-Host "Built: $exe ($size MB)"
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Run:   .\$exe"
Write-Host "  2. Look for the JASS icon in the system tray (bottom-right)."
Write-Host "  3. Right-click the icon and choose Quit to exit."
