$ErrorActionPreference = "Continue"
$sdk = "D:\Android\Sdk"
$licDir = Join-Path $sdk "licenses"

$raw = @'
8933bad161af4178b1185d1a37fbf41ea5269c55
d56f5187479451eabf01fb78af6dfcb131a6481e
24333f8a63b6825ea9c5514f83c2829b004d1fee
84831b9409646a918e30573bab4c9c91346d8abd
d975f751698a77b662f1254ddbeed3901e976f5a
24333f8a63b6825ea9c5514f83c2829b004d1fee
79120722343a897a6330b1d3a6b9e8b05f9e2b46
859f317696f67ef3d7f30a50a5560e7834b43903
601085b94cd77f0b54ff86406957099ebe79c4d6
33b6a2b64607f11b759f320ef9dff4ae5c47d97a
'@

New-Item -ItemType Directory -Force -Path $licDir | Out-Null
$raw | Set-Content -Path (Join-Path $licDir "android-sdk-license") -Encoding ASCII
$raw | Set-Content -Path (Join-Path $licDir "android-sdk-preview-license") -Encoding ASCII

Write-Host "licenses written"
Get-ChildItem $licDir | Select-Object -ExpandProperty Name
