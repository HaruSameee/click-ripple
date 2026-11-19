Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$frameworkPath = Join-Path $env:WINDIR 'Microsoft.NET'
$compiler = Join-Path $frameworkPath 'Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $frameworkPath 'Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) {
    throw 'The .NET Framework C# compiler was not found.'
}

$outputDir = Join-Path $PSScriptRoot 'dist'
New-Item -ItemType Directory -Path $outputDir -Force | Out-Null

$compilerArgs = @(
    '/nologo'
    '/warnaserror'
    '/target:winexe'
    '/optimize+'
    '/codepage:65001'
    ('/out:' + (Join-Path $outputDir 'ClickRipple.exe'))
    ('/win32manifest:' + (Join-Path $PSScriptRoot 'ClickRipple.manifest'))
    ('/win32icon:' + (Join-Path $PSScriptRoot 'ClickRipple.ico'))
    '/reference:System.Drawing.dll'
    '/reference:System.Windows.Forms.dll'
    (Join-Path $PSScriptRoot 'ClickRipple.cs')
)
& $compiler @compilerArgs
if ($LASTEXITCODE -ne 0) {
    throw "Build failed with exit code $LASTEXITCODE."
}
