# Prismil の配り方（GitHub Releases）

配る人には **ダウンロード用のリンク** を送るだけ。使う人は GitHub のアカウントがなくてもダウンロードでき、
インストールした人のアプリは、新しい版を出すと自動で更新されます。

ソースコードを GitHub に上げる必要はありません。リポジトリには README が 1 つあるだけで、
そこに配布用のファイル（Setup.exe と更新ファイル）を「リリース」として載せます。

---

## 最初に 1 回だけやること

### 1. リポジトリを作る
1. https://github.com/new を開く（GitHub にログインした状態で）
2. Repository name: `prismil`
3. **Public**（公開）を選ぶ（非公開だと、使う人がダウンロードできません）
4. **Add a README file** にチェックを入れる（空のリポジトリだとリリースを作れないため）
5. **Create repository**
6. できたページの URL（`https://github.com/あなたのユーザー名/prismil`）を **開発者（Claude）に伝える**
   → アプリの `Services\UpdateService.cs` の `GitHubRepoUrl` に入ります。

### 2. アップロード用のトークンを作る
1. https://github.com/settings/personal-access-tokens/new を開く（Fine-grained token）
2. Token name: `prismil-release`、Expiration（有効期限）はお好みで（期限が切れたら作り直す）
3. **Repository access** → **Only select repositories** → `prismil` を選ぶ
4. **Permissions** → **Repository permissions** → **Contents** を **Read and write** にする
5. **Generate token** → 表示されたトークン（`github_pat_…`）をコピー（一度しか表示されません）

> トークンはパスワードと同じです。チャットやファイルに貼らず、次の手順で自分の PC にだけ設定してください。

### 3. トークンを自分の PC に設定する
PowerShell で次を実行（`…` の部分をトークンに置き換える）。1 回やれば、次からは不要です。

```powershell
[Environment]::SetEnvironmentVariable('GITHUB_TOKEN', '…', 'User')
```

設定したら、PowerShell を一度閉じて開き直す。

---

## 版を出すたびにやること

このフォルダ（`C:\Claude\zentube`）で PowerShell を開いて:

```powershell
.\release.ps1 -Version 1.0.0 -Upload
```

- 2 回目からは番号を上げる（`1.0.1`、`1.1.0` …）。同じ番号や小さい番号は使えません。
- 終わると「配るリンク」が表示されます:
  `https://github.com/あなたのユーザー名/prismil/releases/latest/download/Prismil-win-Setup.exe`
- このリンクは **いつでも最新版の Setup.exe** を指すので、Discord に一度貼れば、版を上げても貼り直さなくて大丈夫です。
- すでに入れている人は、アプリを起動すると自動で更新されます。

---

## 受け取った人へ伝えること
- リンクから `Prismil-win-Setup.exe` をダウンロードして実行するだけでインストールできます。
- 「Windows によって PC が保護されました」と出たら、**詳細情報** → **実行** で進めてください（署名をしていないアプリで出る警告です）。
- アンインストールは Windows の「設定 → アプリ」からできます。
