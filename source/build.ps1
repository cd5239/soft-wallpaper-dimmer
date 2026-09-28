$ErrorActionPreference = 'Stop'
$compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compilerPath)) {
    $compilerPath = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compilerPath)) { throw 'Windows .NET Framework compiler is not available.' }
$destinationPath = Join-Path (Split-Path -Parent $PSScriptRoot) '柔光壁纸.exe'
& $compilerPath /nologo /target:winexe /optimize+ /platform:anycpu /r:System.Drawing.dll /r:System.Windows.Forms.dll "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')" "/win32icon:$(Join-Path $PSScriptRoot 'app.ico')" "/out:$destinationPath" (Join-Path $PSScriptRoot 'WallpaperDimmer.cs')
if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
Write-Output "Built: $destinationPath"
