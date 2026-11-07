<#
  Prismil の新しい版を作って、GitHub Releases に載せるスクリプト。

  使い方（このフォルダで PowerShell から）:
    1. 手元で作るだけ（アップロードしない）
         .\release.ps1 -Version 1.0.0
       → Releases フォルダに Setup.exe などができる。

    2. 作って GitHub に載せる（配った人のアプリが自動で更新される）
         .\release.ps1 -Version 1.0.0 -Upload
       → 配る人にはリリースページのリンクを送る。

  アップロードには、環境変数 GITHUB_TOKEN にトークンが必要（トークンはファイルに書かない）。
  手順は RELEASE.md を参照。

  ・バージョンは毎回、前より大きい番号にする（1.0.0 → 1.0.1 → 1.1.0 …）
  ・更新内容を書いたメモを付けたいときは -Notes .\notes.md
#>
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [switch]$Upload,
    [string]$Notes
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
Set-Location $root

# 更新を置く GitHub リポジトリの URL は、アプリと同じ値（Services\UpdateService.cs）を使う
$svc = Get-Content (Join-Path $root 'Services\UpdateService.cs') -Raw -Encoding UTF8
$repoUrl = [regex]::Match($svc, 'GitHubRepoUrl\s*=\s*"([^"]*)"').Groups[1].Value.TrimEnd('/')

if ($Upload) {
    if ([string]::IsNullOrWhiteSpace($repoUrl)) { throw 'Services\UpdateService.cs の GitHubRepoUrl が空です。リポジトリの URL を入れてください。' }
    if ([string]::IsNullOrWhiteSpace($env:GITHUB_TOKEN)) { throw '環境変数 GITHUB_TOKEN にトークンを設定してから実行してください（RELEASE.md 参照）。' }
}

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Host 'vpk が無いので入れます…'
    dotnet tool install -g vpk
}

$publishDir = Join-Path $root 'publish'
$releasesDir = Join-Path $root 'Releases'

Write-Host "== 1/4 アプリをビルド (v$Version)"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
# Releases は毎回作り直す（前の版は、アップロード時に GitHub から取ってくる）
if (Test-Path $releasesDir) { Remove-Item $releasesDir -Recurse -Force }
# self-contained: 使う人の PC に .NET を入れなくても動くようにする
dotnet publish (Join-Path $root 'ZenTube.csproj') -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'ビルドに失敗しました。' }

if ($Upload) {
    Write-Host '== 2/4 前の版を取得（変わった部分だけの小さな更新ファイルを作るため）'
    vpk download github --repoUrl $repoUrl --token $env:GITHUB_TOKEN -o $releasesDir
    if ($LASTEXITCODE -ne 0) { Write-Host '  （前の版が無いか取得できなかったので、今回は全体の更新ファイルだけ作ります）' }
} else {
    Write-Host '== 2/4 （アップロードしないので、前の版の取得は省略）'
}

Write-Host '== 3/4 インストーラーと更新ファイルを作成'
$packArgs = @('pack', '--packId', 'Prismil', '--packVersion', $Version, '--packDir', $publishDir,
              '--mainExe', 'Prismil.exe', '--packTitle', 'Prismil', '--packAuthors', 'Prismil',
              '--icon', (Join-Path $root 'Assets\prismil.ico'), '-o', $releasesDir)
if ($Notes) { $packArgs += @('--releaseNotes', (Resolve-Path $Notes).Path) }
vpk @packArgs
if ($LASTEXITCODE -ne 0) { throw 'パッケージ作成に失敗しました。' }

if ($Upload) {
    Write-Host '== 4/4 GitHub Releases にアップロード'
    vpk upload github --repoUrl $repoUrl --token $env:GITHUB_TOKEN -o $releasesDir `
        --publish --releaseName "Prismil v$Version" --tag "v$Version"
    if ($LASTEXITCODE -ne 0) { throw 'アップロードに失敗しました。' }
    Write-Host "完了: v$Version を公開しました。"
    Write-Host "  配るリンク: $repoUrl/releases/latest/download/Prismil-win-Setup.exe"
    Write-Host '  すでに入れている人のアプリは、次の起動から自動で更新されます。'
} else {
    Write-Host '== 4/4 （アップロードは省略）'
    Write-Host "完了: $releasesDir に作りました。"
}
