[CmdletBinding()]
param([string]$Configuration = 'Release', [string]$OutputDirectory = "$PSScriptRoot/artifacts")
$ErrorActionPreference = 'Stop'
$solution = Join-Path $PSScriptRoot 'PulseLink.NotificationPlugins.Official.sln'
& dotnet build $solution --configuration $Configuration --no-restore -m:1
if ($LASTEXITCODE -ne 0) { throw "Official plugin solution build failed ($LASTEXITCODE)." }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
foreach ($plugin in @('PulseLink.NotificationPlugins.WxPusher','PulseLink.NotificationPlugins.Dtu')) {
  $project = Join-Path $PSScriptRoot "$plugin/$plugin.csproj"
  $manifestSource = Join-Path $PSScriptRoot "$plugin/plugin.json"
  $output = Join-Path $OutputDirectory $plugin
  if (Test-Path $output) { Remove-Item -LiteralPath $output -Recurse -Force }
  New-Item -ItemType Directory -Path $output | Out-Null
  & dotnet publish $project --configuration $Configuration --no-restore --output $output
  if ($LASTEXITCODE -ne 0) { throw "Publish failed: $plugin ($LASTEXITCODE)." }
  if ($plugin -eq 'PulseLink.NotificationPlugins.Dtu') {
    Copy-Item (Join-Path $PSScriptRoot "$plugin/ui/*") $output -Recurse -Force
  }
  Copy-Item $manifestSource $output -Force
  $assembly = Join-Path $output "$plugin.dll"
  if (-not (Test-Path $assembly)) { throw "Published assembly missing: $assembly" }
  $manifestPath = Join-Path $output 'plugin.json'
  $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
  $manifest.sha256 = (Get-FileHash $assembly -Algorithm SHA256).Hash
  $manifest | ConvertTo-Json -Depth 10 | Set-Content $manifestPath -Encoding utf8
  $zip = Join-Path $OutputDirectory "$($manifest.pluginId)-$($manifest.version).zip"
  if (Test-Path $zip) { Remove-Item -LiteralPath $zip -Force }
  Compress-Archive -Path (Join-Path $output '*') -DestinationPath $zip -CompressionLevel Optimal
  Write-Output "Created $zip"
}
