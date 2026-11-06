<#
  Prismil の新しい版を作って、Cloudflare R2 に載せるスクリプト。

  使い方（このフォルダで PowerShell から）:
    1. 手元で作るだけ（アップロードしない）
         .\release.ps1 -Version 1.0.0
       → Releases フォルダに Setup.exe などができる。

    2. 作って R2 に載せる（配った人のアプリが自動で更新される）
         .\release.ps1 -Version 1.0.0 -Upload
       → 配る人には https://pub-xxxx.r2.dev/Prismil-win-Setup.exe のリンクを送る。

  アップロードには、次の環境変数が必要（鍵はファイルに書かない）:
    R2_ACCOUNT_ID         Cloudflare のアカウント ID
    R2_ACCESS_KEY_ID      R2 の API トークンの「アクセスキー ID」
    R2_SECRET_ACCESS_KEY  R2 の API トークンの「シークレットアクセスキー」
    R2_BUCKET             バケット名（省略時は prismil）

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

# 更新の置き場所の公開 URL は、アプリと同じ値（Services\UpdateService.cs）を使う
$svc = Get-Content (Join-Path $root 'Services\UpdateService.cs') -Raw -Encoding UTF8
$feedUrl = [regex]::Match($svc, 'UpdateFeedUrl\s*=\s*"([^"]*)"').Groups[1].Value.TrimEnd('/')
$bucket = if ($env:R2_BUCKET) { $env:R2_BUCKET } else { 'prismil' }

if ($Upload) {
    if ([string]::IsNullOrWhiteSpace($feedUrl)) { throw 'Services\UpdateService.cs の UpdateFeedUrl が空です。R2 の公開 URL を入れてください。' }
    foreach ($name in 'R2_ACCOUNT_ID', 'R2_ACCESS_KEY_ID', 'R2_SECRET_ACCESS_KEY') {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) { throw "環境変数 $name を設定してから実行してください。" }
    }
}

if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
    Write-Host 'vpk が無いので入れます…'
    dotnet tool install -g vpk
}

$publishDir = Join-Path $root 'publish'
$releasesDir = Join-Path $root 'Releases'

Write-Host "== 1/4 アプリをビルド (v$Version)"
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
# Releases は毎回作り直す（前の版は、アップロード時に R2 から取ってくる）
if (Test-Path $releasesDir) { Remove-Item $releasesDir -Recurse -Force }
# self-contained: 使う人の PC に .NET を入れなくても動くようにする
dotnet publish (Join-Path $root 'ZenTube.csproj') -c Release -r win-x64 --self-contained true `
    -p:Version=$Version -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'ビルドに失敗しました。' }

if ($Upload) {
    Write-Host '== 2/4 前の版を取得（変わった部分だけの小さな更新ファイルを作るため）'
    vpk download http --url $feedUrl -o $releasesDir --allowEmptyChannel
    if ($LASTEXITCODE -ne 0) { Write-Host '  （前の版を取得できなかったので、今回は全体の更新ファイルだけ作ります）' }
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
    Write-Host "== 4/4 Cloudflare R2（バケット: $bucket）にアップロード"
    # R2 は Amazon S3 と同じやり方でつなげる。古い版は 5 つまで残して、それより前は消す
    vpk upload s3 -o $releasesDir --bucket $bucket `
        --endpoint "https://$($env:R2_ACCOUNT_ID).r2.cloudflarestorage.com" --region auto `
        --keyId $env:R2_ACCESS_KEY_ID --secret $env:R2_SECRET_ACCESS_KEY `
        --disableChecksumValidation --keepMaxReleases 5
    if ($LASTEXITCODE -ne 0) { throw 'アップロードに失敗しました。' }
    Write-Host "完了: v$Version を公開しました。"
    Write-Host "  配るリンク: $feedUrl/Prismil-win-Setup.exe"
    Write-Host '  すでに入れている人のアプリは、次の起動から自動で更新されます。'
} else {
    Write-Host '== 4/4 （アップロードは省略）'
    Write-Host "完了: $releasesDir に作りました。"
}
