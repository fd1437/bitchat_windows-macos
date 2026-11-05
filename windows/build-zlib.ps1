param([ValidateSet('win-x64','win-arm64')][string]$Rid,
      [string]$BuildRoot = (Join-Path $env:TEMP 'bitchat-zlib-build'))
$ErrorActionPreference = 'Stop'
$source = Join-Path $BuildRoot 'source'
New-Item -ItemType Directory -Force $source | Out-Null
tar -xzf (Join-Path $PSScriptRoot 'vendor/zlib/zlib-1.3.2.tar.gz') -C $source --strip-components=1
if ($LASTEXITCODE) { throw 'zlib source extraction failed' }
$arch = if ($Rid -eq 'win-arm64') { 'ARM64' } else { 'x64' }
$build = Join-Path $BuildRoot $Rid
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = (& $vswhere -latest -products '*' -format json | ConvertFrom-Json) | Select-Object -First 1
if (!$installation) { throw 'Visual Studio C++ build tools were not found' }
$major = $installation.installationVersion.Split('.')[0]
$cmakeHelp = cmake --help | Out-String
$generatorMatch = [regex]::Match($cmakeHelp, "Visual Studio $major [0-9]{4}")
if (!$generatorMatch.Success) { throw "CMake has no generator for Visual Studio $major" }
$generator = $generatorMatch.Value
cmake -S $source -B $build -G $generator -A $arch "-DCMAKE_GENERATOR_INSTANCE=$($installation.installationPath)" -DZLIB_BUILD_TESTING=OFF -DZLIB_BUILD_STATIC=OFF -DCMAKE_POLICY_DEFAULT_CMP0091=NEW -DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded
if ($LASTEXITCODE) { throw 'zlib configuration failed' }
cmake --build $build --config Release --target zlib --parallel 4
if ($LASTEXITCODE) { throw 'zlib compilation failed' }
$dll = Get-ChildItem (Join-Path $build 'Release') -Filter '*.dll' | Select-Object -First 1
if (!$dll) { throw 'zlib DLL was not produced' }
$destination = Join-Path $PSScriptRoot "vendor/zlib/$Rid"
New-Item -ItemType Directory -Force $destination | Out-Null
Copy-Item $dll.FullName (Join-Path $destination 'bitchat_zlib.dll') -Force
