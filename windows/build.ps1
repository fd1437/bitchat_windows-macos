param([string]$Output = (Join-Path $PSScriptRoot 'publish'))
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    python ../scripts/audit_release.py source
    if ($LASTEXITCODE) { throw 'Source privacy audit failed' }
    ./build-zlib.ps1 -Rid win-x64
    ./build-zlib.ps1 -Rid win-arm64
    dotnet run --project BitChat.InteropTests -c Release
    if ($LASTEXITCODE) { throw 'Interoperability tests failed' }
    foreach ($rid in @('win-x64', 'win-arm64')) {
        dotnet publish BitChat.Windows -c Release -r $rid -o (Join-Path $Output $rid)
        if ($LASTEXITCODE) { throw "Publish failed: $rid" }
        python ../scripts/audit_release.py windows --root (Join-Path $Output $rid) --rid $rid
        if ($LASTEXITCODE) { throw "Release payload privacy audit failed: $rid" }
    }
    $compiler = Get-Command makensis -ErrorAction SilentlyContinue
    if ($compiler) { $compilerPath = $compiler.Source } else { $compilerPath = (Get-Item "${env:ProgramFiles(x86)}\NSIS\makensis.exe").FullName }
    & $compilerPath "/DPAYLOAD=$Output" "/DOUTPUT=$(Join-Path $Output 'BitChat-Setup-unsigned.exe')" installer.nsi
    if ($LASTEXITCODE) { throw 'NSIS packaging failed' }
} finally { Pop-Location }
