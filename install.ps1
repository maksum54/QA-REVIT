# Pasang add-in QA Revit untuk Revit 2025 (user saat ini). Jalankan dari folder hasil download artifact.
$target = Join-Path $env:APPDATA "Autodesk\Revit\Addins\2025"
New-Item -ItemType Directory -Force (Join-Path $target "QARevit") | Out-Null
Copy-Item (Join-Path $PSScriptRoot "QARevit\*") (Join-Path $target "QARevit") -Recurse -Force
Copy-Item (Join-Path $PSScriptRoot "QARevit.addin") $target -Force
Get-ChildItem (Join-Path $target "QARevit") -Recurse | Unblock-File
Write-Host "QA Revit terpasang di $target. Buka ulang Revit 2025."
