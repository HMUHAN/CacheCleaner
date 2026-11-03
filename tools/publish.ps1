param(
    [Parameter(Mandatory = $true)][string]$User,
    [string]$Repo = 'CacheCleaner',
    [string]$Tag = 'v1.1.0',
    [switch]$NoPush
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Resolve-Git {
    $cmd = Get-Command git -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }

    $candidates = @(
        (Join-Path $env:ProgramFiles 'Git\cmd\git.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Git\cmd\git.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Git\cmd\git.exe'),
        (Join-Path $env:LOCALAPPDATA 'GitHubDesktop\app-*\resources\app\git\cmd\git.exe')
    )

    foreach ($c in $candidates) {
        if (-not $c) { continue }
        $hit = Get-ChildItem $c -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($hit) { return $hit.FullName }
    }

    $dsh = Get-ChildItem "$env:USERPROFILE\.dsh\dsh-ops\bash-ipc\*\ucrt64\bin\git.exe" -ErrorAction SilentlyContinue |
           Select-Object -First 1
    if ($dsh) { return $dsh.FullName }

    return $null
}

$git = Resolve-Git
if (-not $git) {
    Write-Host "没有找到 git。" -ForegroundColor Red
    Write-Host ""
    Write-Host "先装 Git for Windows（自带凭据管理器，推送时会开浏览器登录）：" -ForegroundColor Yellow
    Write-Host "  winget install --id Git.Git -e --source winget" -ForegroundColor White
    Write-Host ""
    Write-Host "装完重新打开 PowerShell，再运行本脚本。"
    exit 1
}

Write-Host "使用 git：$git" -ForegroundColor DarkGray

function Run-Git([string[]]$gitArgs) {
    & $git @gitArgs
    if ($LASTEXITCODE -ne 0) { throw "git $($gitArgs -join ' ') 失败（$LASTEXITCODE）" }
}

Write-Host "== 1) 写入仓库地址 ==" -ForegroundColor Cyan
$src = Join-Path $root 'src\QQCacheTray.cs'
$json = Join-Path $root 'latest.json'

$text = Get-Content $src -Raw -Encoding UTF8
$text = $text -replace 'https://raw\.githubusercontent\.com/[^/]+/[^/]+/main/latest\.json',
                        "https://raw.githubusercontent.com/$User/$Repo/main/latest.json"
Set-Content -Path $src -Value $text -Encoding UTF8 -NoNewline

$j = Get-Content $json -Raw -Encoding UTF8
$j = $j -replace 'https://github\.com/[^/]+/[^/]+/releases/download/[^/]+/QQCacheTray\.exe',
                  "https://github.com/$User/$Repo/releases/download/$Tag/QQCacheTray.exe"
Set-Content -Path $json -Value $j -Encoding UTF8 -NoNewline

Write-Host "   manifest -> https://raw.githubusercontent.com/$User/$Repo/main/latest.json"
Write-Host "   download -> https://github.com/$User/$Repo/releases/download/$Tag/QQCacheTray.exe"

Write-Host "== 2) 本地提交 ==" -ForegroundColor Cyan
if (-not (Test-Path (Join-Path $root '.git'))) { Run-Git @('init') }

$name = & $git config user.name
if (-not $name) { Run-Git @('config', 'user.name', $User) }
$mail = & $git config user.email
if (-not $mail) { Run-Git @('config', 'user.email', "$User@users.noreply.github.com") }

Run-Git @('add', '-A')
& $git commit -m "feat: cache cleaner $Tag" | Out-Null
if ($LASTEXITCODE -ne 0) { Write-Host "   （没有新改动或提交失败，继续）" -ForegroundColor DarkGray }
Run-Git @('branch', '-M', 'main')

Write-Host "== 3) 关联远端 ==" -ForegroundColor Cyan
$url = "https://github.com/$User/$Repo.git"
& $git remote remove origin 2>$null
Run-Git @('remote', 'add', 'origin', $url)
Write-Host "   $url"

if ($NoPush) {
    Write-Host "== 已跳过推送（-NoPush）==" -ForegroundColor Yellow
    exit 0
}

Write-Host "== 4) 推送 main ==" -ForegroundColor Cyan
& $git push -u origin main
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "推送失败。常见原因：" -ForegroundColor Yellow
    Write-Host "  1. GitHub 上还没建仓库 $User/$Repo（建空仓库，不要勾 README）"
    Write-Host "  2. 登录窗口点了取消 -> 重跑脚本重新授权"
    Write-Host "  3. 网络访问不了 github.com"
    exit 1
}

Write-Host "== 5) 打标签并推送 ==" -ForegroundColor Cyan
$existing = & $git tag --list $Tag
if (-not $existing) { Run-Git @('tag', $Tag) }
Run-Git @('push', 'origin', $Tag)

Write-Host ""
Write-Host "完成！Actions：https://github.com/$User/$Repo/actions" -ForegroundColor Green
