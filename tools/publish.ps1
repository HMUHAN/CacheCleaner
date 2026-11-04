param(
    [Parameter(Mandatory = $true)][string]$User,
    [string]$Repo = 'CacheCleaner',
    [string]$Tag = 'v1.3.3',
    [switch]$NoPush
)

# 用 Continue：git 往 stderr 写进度时不会被 PowerShell 当成终止错误
$ErrorActionPreference = 'Continue'

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
    return $null
}

$git = Resolve-Git
if (-not $git) {
    Write-Host "没有找到 git，先装 Git for Windows：" -ForegroundColor Red
    Write-Host "  winget install --id Git.Git -e --source winget"
    exit 1
}

function Run-Git([string[]]$gitArgs) {
    & $git @gitArgs
    if ($LASTEXITCODE -ne 0) { throw "git $($gitArgs -join ' ') 失败（$LASTEXITCODE）" }
}

Write-Host "== 1) 写入仓库地址 ==" -ForegroundColor Cyan
$src = Join-Path $root 'src\QQCacheTray.cs'
$text = Get-Content $src -Raw -Encoding UTF8
$text = $text -replace 'https://raw\.githubusercontent\.com/[^/]+/[^/]+/main/latest\.json',
                        "https://raw.githubusercontent.com/$User/$Repo/main/latest.json"
Set-Content -Path $src -Value $text -Encoding UTF8 -NoNewline
Write-Host "   manifest -> https://raw.githubusercontent.com/$User/$Repo/main/latest.json"

Write-Host "== 2) 初始化 / 关联远端 ==" -ForegroundColor Cyan
if (-not (Test-Path (Join-Path $root '.git'))) { Run-Git @('init') }

$name = & $git config user.name
if (-not $name) { Run-Git @('config', 'user.name', $User) }
$mail = & $git config user.email
if (-not $mail) { Run-Git @('config', 'user.email', "$User@users.noreply.github.com") }

$url = "https://github.com/$User/$Repo.git"
& $git remote remove origin *> $null
Run-Git @('remote', 'add', 'origin', $url)
Write-Host "   $url"

Write-Host "== 3) 对齐远端 main ==" -ForegroundColor Cyan
& $git fetch origin 2>$null
$hasRemoteMain = $false
if ($LASTEXITCODE -eq 0) {
    & $git rev-parse --verify origin/main *> $null
    if ($LASTEXITCODE -eq 0) { $hasRemoteMain = $true }
}

if ($hasRemoteMain) {
    # 把本地提交撤回到工作区（文件改动都留着），HEAD 对齐远端，避免每次都冲突
    & $git reset --soft origin/main 2>&1 | Out-Host
    if ($LASTEXITCODE -ne 0) { Write-Host "   reset 失败，继续按现状提交" -ForegroundColor Yellow }

    # latest.json 由 CI 生成，本地永远用远端那份
    & $git checkout origin/main -- latest.json 2>$null
    Write-Host "   已对齐 origin/main"
}

Write-Host "== 4) 本地提交 ==" -ForegroundColor Cyan
& $git add -A

$dirty = & $git status --porcelain
if ($dirty) {
    & $git commit -m "feat: cache cleaner $Tag" | Out-Null
    if ($LASTEXITCODE -eq 0) { Write-Host "   已提交" }
    else { Write-Host "   提交返回 $LASTEXITCODE（可能没有改动）" -ForegroundColor DarkGray }
} else {
    Write-Host "   没有新改动，跳过提交"
}

& $git branch -M main

if ($NoPush) {
    Write-Host "== 已跳过推送（-NoPush）==" -ForegroundColor Yellow
    exit 0
}

Write-Host "== 5) 推送 main ==" -ForegroundColor Cyan
& $git push -u origin main
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "推送失败。常见原因：" -ForegroundColor Yellow
    Write-Host "  1. 仓库不存在或没有权限"
    Write-Host "  2. 登录窗口点了取消 -> 重跑脚本"
    Write-Host "  3. 网络访问不了 github.com"
    exit 1
}

Write-Host "== 6) 打标签并推送 ==" -ForegroundColor Cyan
$existing = & $git tag --list $Tag
if ($existing) { & $git tag -d $Tag | Out-Null }
Run-Git @('tag', $Tag)
& $git push origin $Tag --force
if ($LASTEXITCODE -ne 0) { throw "推送标签失败" }

Write-Host ""
Write-Host "完成！Actions：https://github.com/$User/$Repo/actions" -ForegroundColor Green
