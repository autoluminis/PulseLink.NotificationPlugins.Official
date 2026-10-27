param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$OutputDirectory = "$PSScriptRoot/artifacts"
)

$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'PulseLink.NotificationPlugins.WxPusher.csproj'
$buildOutput = Join-Path $PSScriptRoot "bin/$Configuration/net10.0"
$assemblyName = 'PulseLink.NotificationPlugins.WxPusher.dll'
$pluginId = 'pulselink.wxpusher'
$archivePath = Join-Path $OutputDirectory "$pluginId.zip"
$stagingDirectory = Join-Path ([System.IO.Path]::GetTempPath()) "PulseLink-plugin-$([Guid]::NewGuid().ToString('N'))"

try {
    dotnet build $projectPath --configuration $Configuration
    if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed.' }

    New-Item -ItemType Directory -Path $stagingDirectory -Force | Out-Null
    Get-ChildItem -Path $buildOutput -File | Copy-Item -Destination $stagingDirectory
    $assemblyPath = Join-Path $stagingDirectory $assemblyName
    $manifestPath = Join-Path $stagingDirectory 'plugin.json'
    if (-not (Test-Path $assemblyPath) -or -not (Test-Path $manifestPath)) {
        throw 'Plugin output is missing the entry assembly or plugin.json.'
    }

    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $manifest.sha256 = (Get-FileHash $assemblyPath -Algorithm SHA256).Hash
    $manifest | ConvertTo-Json | Set-Content $manifestPath -Encoding utf8

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    if (Test-Path $archivePath) { Remove-Item -LiteralPath $archivePath -Force }
    Compress-Archive -Path (Join-Path $stagingDirectory '*') -DestinationPath $archivePath -CompressionLevel Optimal
    Write-Output "Plugin package created: $archivePath"
}
finally {
    if (Test-Path $stagingDirectory) { Remove-Item -LiteralPath $stagingDirectory -Recurse -Force }
}
