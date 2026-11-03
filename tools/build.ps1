param(
    [switch]$Test,
    [switch]$Final
)

$ErrorActionPreference = 'Stop'

# 仓库根目录 = tools 的上一级
$here = $PSScriptRoot
if (-not $here) { $here = Split-Path -Parent $MyInvocation.MyCommand.Path }
$dir = Split-Path -Parent $here
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$src = Join-Path $dir 'src\QQCacheTray.cs'
$ico = Join-Path $dir 'assets\QQCacheTray.ico'
$manifest = Join-Path $dir 'app.manifest'

if (-not (Test-Path $csc)) { throw "找不到 csc.exe：$csc" }
if (-not (Test-Path $src)) { throw "找不到源码：$src" }

function Invoke-Build([string]$out, [string[]]$extra) {
    $cscArgs = @(
        '/nologo', '/target:winexe', '/codepage:65001', '/optimize+',
        "/win32icon:$ico",
        "/out:$out",
        '/r:System.dll', '/r:System.Core.dll', '/r:System.Drawing.dll', '/r:System.Windows.Forms.dll'
    ) + $extra + @($src)

    & $csc @cscArgs
    if ($LASTEXITCODE -ne 0) { throw "csc failed with $LASTEXITCODE" }
    "built: $out"
}

if ($Test) {
    Invoke-Build (Join-Path $dir '_test.exe') @()
}

if ($Final -or (-not $Test)) {
    Invoke-Build (Join-Path $dir 'QQCacheTray.exe') @("/win32manifest:$manifest")
}
