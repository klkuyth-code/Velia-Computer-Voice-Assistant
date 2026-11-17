$ErrorActionPreference = 'Stop'

$desktopRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$veliaRoot = [IO.Path]::GetFullPath((Join-Path $desktopRoot '..'))
$toolsRoot = [IO.Path]::GetFullPath((Join-Path $veliaRoot '.tools'))
$runtimeRoot = [IO.Path]::GetFullPath((Join-Path $toolsRoot 'wake-word'))
$downloadRoot = [IO.Path]::GetFullPath((Join-Path $toolsRoot 'downloads'))
if (-not $runtimeRoot.StartsWith($toolsRoot, [StringComparison]::OrdinalIgnoreCase)) {
  throw 'Wake runtime target escaped the Velia tools directory.'
}

$whisperVersion = 'v1.9.2'
$archiveName = 'whisper-bin-x64.zip'
$archiveUrl = "https://github.com/ggml-org/whisper.cpp/releases/download/$whisperVersion/$archiveName"
$archiveSha256 = '49dcc16de826f20bd53d44f947a1ae49dfa81f86cad67a64d80820cb192d674a'
$modelName = 'ggml-base.bin'
$modelUrl = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin?download=true'
$modelSha1 = '465707469ff3a37a2b9b8d8f89f2f99de7299dac'
$modelSize = 147951465
$archivePath = Join-Path $downloadRoot "$whisperVersion-$archiveName"
$modelPath = Join-Path $downloadRoot $modelName

function Test-Hash([string]$Path, [string]$Algorithm, [string]$Expected) {
  if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
  $stream = [IO.File]::OpenRead($Path)
  try {
    $hasher = if ($Algorithm -eq 'SHA256') { [Security.Cryptography.SHA256]::Create() } else { [Security.Cryptography.SHA1]::Create() }
    try {
      $actual = ([BitConverter]::ToString($hasher.ComputeHash($stream))).Replace('-', '')
      return $actual.Equals($Expected, [StringComparison]::OrdinalIgnoreCase)
    } finally {
      $hasher.Dispose()
    }
  } finally {
    $stream.Dispose()
  }
}

function Download-Verified([string]$Url, [string]$Path, [string]$Algorithm, [string]$Expected) {
  if (Test-Hash $Path $Algorithm $Expected) { return }
  New-Item -ItemType Directory -Force -Path ([IO.Path]::GetDirectoryName($Path)) | Out-Null
  $partialPath = "$Path.partial"
  Remove-Item -LiteralPath $partialPath -Force -ErrorAction SilentlyContinue
  Invoke-WebRequest -Uri $Url -OutFile $partialPath -UseBasicParsing
  if (-not (Test-Hash $partialPath $Algorithm $Expected)) {
    Remove-Item -LiteralPath $partialPath -Force -ErrorAction SilentlyContinue
    throw "Downloaded file failed $Algorithm verification: $Url"
  }
  Move-Item -LiteralPath $partialPath -Destination $Path -Force
}

function Write-RuntimeManifest([string]$Root) {
  $manifest = [ordered]@{
    schema = 'velia-wake-runtime/v1'
    whisperCppVersion = $whisperVersion
    whisperArchiveUrl = $archiveUrl
    whisperArchiveSha256 = $archiveSha256
    model = $modelName
    modelUrl = $modelUrl
    modelSha1 = $modelSha1
    modelBytes = $modelSize
  }
  $json = $manifest | ConvertTo-Json
  [IO.File]::WriteAllText((Join-Path $Root 'RUNTIME-MANIFEST.json'), $json, (New-Object Text.UTF8Encoding($false)))
}

$runtimeModel = Join-Path $runtimeRoot $modelName
$runtimeCli = Join-Path $runtimeRoot 'whisper-cli.exe'
$requiredRuntimeFiles = @(
  $runtimeCli,
  (Join-Path $runtimeRoot 'ggml.dll'),
  (Join-Path $runtimeRoot 'ggml-base.dll'),
  (Join-Path $runtimeRoot 'whisper.dll'),
  (Join-Path $runtimeRoot 'WHISPER_CPP_LICENSE.txt'),
  (Join-Path $runtimeRoot 'OPENAI_WHISPER_LICENSE.txt')
)
$runtimeReady = (Test-Hash $runtimeModel 'SHA1' $modelSha1) -and
  (($requiredRuntimeFiles | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) }).Count -eq 0)
if ($runtimeReady) {
  Get-ChildItem -LiteralPath $runtimeRoot -File -Filter '*.exe' |
    Where-Object { $_.Name -ne 'whisper-cli.exe' } |
    Remove-Item -Force
  Write-RuntimeManifest $runtimeRoot
  Write-Output 'VELIA_WAKE_RUNTIME_OK'
  exit 0
}

Download-Verified $archiveUrl $archivePath 'SHA256' $archiveSha256
Download-Verified $modelUrl $modelPath 'SHA1' $modelSha1
if ((Get-Item -LiteralPath $modelPath).Length -ne $modelSize) { throw 'Whisper model size verification failed.' }

$stagingRoot = [IO.Path]::GetFullPath((Join-Path $toolsRoot 'wake-word-staging'))
if (-not $stagingRoot.StartsWith($toolsRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid staging directory.' }
Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stagingRoot | Out-Null
try {
  Expand-Archive -LiteralPath $archivePath -DestinationPath $stagingRoot -Force
  $cli = Get-ChildItem -LiteralPath $stagingRoot -Recurse -File -Filter 'whisper-cli.exe' | Select-Object -First 1
  if ($null -eq $cli) { throw 'The official whisper.cpp archive did not contain whisper-cli.exe.' }

  Remove-Item -LiteralPath $runtimeRoot -Recurse -Force -ErrorAction SilentlyContinue
  New-Item -ItemType Directory -Force -Path $runtimeRoot | Out-Null
  Copy-Item -LiteralPath $cli.FullName -Destination $runtimeRoot
  Get-ChildItem -LiteralPath $cli.DirectoryName -File -Filter '*.dll' | Copy-Item -Destination $runtimeRoot
  Copy-Item -LiteralPath $modelPath -Destination $runtimeModel
  Copy-Item -LiteralPath (Join-Path $desktopRoot 'third-party\WHISPER_CPP_LICENSE.txt') -Destination $runtimeRoot
  Copy-Item -LiteralPath (Join-Path $desktopRoot 'third-party\OPENAI_WHISPER_LICENSE.txt') -Destination $runtimeRoot

  Write-RuntimeManifest $runtimeRoot
} finally {
  Remove-Item -LiteralPath $stagingRoot -Recurse -Force -ErrorAction SilentlyContinue
}

if (-not (Test-Hash $runtimeModel 'SHA1' $modelSha1) -or -not (Test-Path -LiteralPath $runtimeCli -PathType Leaf)) {
  throw 'Prepared wake runtime failed final verification.'
}
Write-Output 'VELIA_WAKE_RUNTIME_OK'
