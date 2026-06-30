$ErrorActionPreference = "Stop"

$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
$SpecFile = Join-Path $PSScriptRoot "convert_kfx2epub-external-kfxlib.spec"
$IconPng = Join-Path $PSScriptRoot "from_kfx_icon.png"
$IconIco = Join-Path $PSScriptRoot "from_kfx_icon.ico"
$DistDir = Join-Path $ProjectRoot "dist"
$BuildDir = Join-Path $ProjectRoot "build"
$ExeFile = Join-Path $DistDir "convert_kfx2epub.exe"
$PortableDir = Join-Path $DistDir "convert_kfx2epub-portable"
$PortableExe = Join-Path $PortableDir "convert_kfx2epub.exe"

Write-Host "Project root: $ProjectRoot"
Write-Host "Python version:"
python --version

$env:PYTHONHASHSEED = "0"

@"
from pathlib import Path
from PIL import Image

png_path = Path(r"$IconPng")
ico_path = Path(r"$IconIco")

with Image.open(png_path) as image:
    image.save(ico_path, format="ICO", sizes=[(256, 256), (128, 128), (64, 64), (48, 48), (32, 32), (16, 16)])
"@ | python -

if (Test-Path $BuildDir) {
    Remove-Item $BuildDir -Recurse -Force
}
if (Test-Path $ExeFile) {
    Remove-Item $ExeFile -Force
}
if (Test-Path $PortableDir) {
    Remove-Item $PortableDir -Recurse -Force
}

Push-Location $ProjectRoot
try {
    python -m PyInstaller --clean --noconfirm $SpecFile

    New-Item -ItemType Directory -Path $PortableDir | Out-Null
    Copy-Item $ExeFile $PortableExe

    $kfxlibSource = Join-Path $ProjectRoot "kfxlib"
    $kfxlibTarget = Join-Path $PortableDir "kfxlib"
    Copy-Item $kfxlibSource $kfxlibTarget -Recurse

    Get-ChildItem $kfxlibTarget -Recurse -Directory -Filter "__pycache__" | Remove-Item -Recurse -Force
    Get-ChildItem $kfxlibTarget -Recurse -Include "*.pyc", "*.pyo" -File | Remove-Item -Force

    Write-Host "Portable package: $PortableDir"
    Get-FileHash $PortableExe -Algorithm SHA256
}
finally {
    Pop-Location
}
