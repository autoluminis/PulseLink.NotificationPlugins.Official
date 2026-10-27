param([string]$Configuration = 'Release', [string]$OutputDirectory = "$PSScriptRoot/artifacts")
$projectPath = Join-Path $PSScriptRoot 'PulseLink.NotificationPlugins.Dtu.csproj'
$stagingDirectory = Join-Path ([IO.Path]::GetTempPath()) "PulseLink-plugin-$([Guid]::NewGuid().ToString('N'))"
try {
  dotnet publish $projectPath --configuration $Configuration --output $stagingDirectory
  if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }
  $manifestPath = Join-Path $stagingDirectory 'plugin.json'; $assemblyPath = Join-Path $stagingDirectory 'PulseLink.NotificationPlugins.Dtu.dll'
  $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json; $manifest.sha256 = (Get-FileHash $assemblyPath -Algorithm SHA256).Hash; $manifest | ConvertTo-Json | Set-Content $manifestPath -Encoding UTF8
  New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
  Compress-Archive -Path (Join-Path $stagingDirectory '*') -DestinationPath (Join-Path $OutputDirectory 'pulselink.dtu.zip') -Force
} finally { if (Test-Path $stagingDirectory) { Remove-Item -LiteralPath $stagingDirectory -Recurse -Force } }
