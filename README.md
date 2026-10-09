# WinRebuilder

公開バージョン: **v.1.0.a-pre1**。内部バージョンは `0.4.0.0`（`Version` は `0.4.0`）です。

WinRebuilder は、確認済みの設定から Windows 11 環境を再構築する GUI アプリです。[config.yml](config.yml) が利用者の正規の設定ファイルであり、画面・計画器・実行器は同じ Core モデルを使用します。

## 動作環境

- Windows 11 x64。配布版には .NET ランタイムが含まれます。
- EXE と `config.yml` を置く、書き込み可能なフォルダー。
- winget パッケージの導入には winget が必要です。HKLM のレジストリ操作には管理者権限が必要です。

## インストール

### 標準版

1. [GitHub Releases](https://github.com/COSHIANofc/winrebuilder/releases) から `WinRebuilder.zip` をダウンロードします。
2. 書き込み可能なフォルダーに展開します。
3. `config.yml` を確認し、`WinRebuilder.exe` を起動します。
4. Windows の警告が出た場合は、公式リリースから取得したファイルであることを確認します。

ZIP の中身は `README.md`、`WinRebuilder.exe`、`config.yml`、`config.example.yml` の 4 ファイルだけです。`config.example.yml` は控えめな設定例で、自動読み込みはされません。

### ポータブル版

同じリリースの `WinRebuilder-portable.exe` を書き込み可能なフォルダーに置いて起動します。通常版 EXE と内容は同一です。初回起動時に `config.yml` がなければ、埋め込みの初期設定から EXE の隣に作成します。書き込めない場合はエラーを表示し、別の場所へ黙って保存しません。

## 使い方

左側の「ソフトウェア」「ExplorerPatcher」「設定」「情報」から画面を選びます。ソフトウェア画面では登録済みパッケージと状態を表示します。「状態を確認」は読み取り専用の確認です。追加時は正確な winget ID を入力します。削除は `config.yml` の項目だけを消し、Windows からのアンインストールは行いません。「選択項目をインストール」「すべてインストール」は同じ計画器と実行器を使用します。進行状況は「操作履歴」に表示され、キャンセルも要求できます。

「設定」では `config.yml` の場所を確認・再読み込みし、レジストリのバックアップを読み込んで選択した値を復元できます。後から別の変更が加わった値は上書きしません。起動時に winget の列挙、ネットワーク接続、レジストリ走査は行いません。

## 言語とテーマ

画面の初期言語は日本語です。左側の「日本語」「英語」で即時に切り替えます。選択は利用者のローカルアプリ設定に保存され、`config.yml` には書き込みません。テーマも左側からライト・ダークを切り替えられます。テーマは起動時にライトへ戻ります。詳細な実行エラーには Windows や winget が返す原文が含まれることがあります。

## 設定ファイル

初期登録ソフトウェアは **7-Zip**、**CrystalDiskInfo**、**ExplorerPatcher** です。winget ID はそれぞれ `7zip.7zip`、`CrystalDewWorld.CrystalDiskInfo`、`valinet.ExplorerPatcher` です。Microsoft の [7-Zip](https://github.com/microsoft/winget-pkgs/tree/master/manifests/7/7zip/7zip)、[CrystalDiskInfo](https://github.com/microsoft/winget-pkgs/tree/master/manifests/c/CrystalDewWorld/CrystalDiskInfo)、[ExplorerPatcher](https://github.com/microsoft/winget-pkgs/tree/master/manifests/v/valinet/ExplorerPatcher/26100.8457.70.3) の各 manifest に対応します。インストール前に必ず内容を確認してください。

`config.example.yml` に通常のレジストリ変更はありません。ExplorerPatcher 設定の復元も任意です。プロファイルは最大 64 KiB、バージョン `1`、既知フィールドのみを許可します。重複キー、不明な項目、YAML のアンカー・エイリアス・タグ・マージキー、パッケージ名や winget ID の重複は拒否します。任意のコマンドやスクリプトは実行できません。対応する取得元は winget、正確なアセット指定付き GitHub Releases、SHA-256 指定付き HTTPS URL です。ダウンロードは一時ファイルに保存・検証してから配置します。

GUI は `ConfigurationWorkspace` を通して検証後に原子的に `config.yml` を保存します。`config.yml.bak` を 1 つ保持し、同時編集を検出します。シンボリックリンクや設定フォルダー外へのパス移動は拒否します。

## ExplorerPatcher

ExplorerPatcher はリスクの高いシェル変更として最終フェーズで実行します。`valinet.ExplorerPatcher` の winget パッケージを `26100.8457.70.3` に固定しています。Microsoft の [installer manifest](https://github.com/microsoft/winget-pkgs/blob/master/manifests/v/valinet/ExplorerPatcher/26100.8457.70.3/valinet.ExplorerPatcher.installer.yaml) は公式の `valinet/ExplorerPatcher` リリースを指します。WinRebuilder は Microsoft winget ソース、ID、インストーラー URL、SHA-256 を照合し、導入後にも検出を確認します。直接ダウンロードへの代替経路はありません。

`.reg` 選択は ExplorerPatcher 専用です。ファイル全体を専用の許可リストで検証し、`config.yml` の隣の `settings/` に安全なコピーを保存します。一般的な `.reg` インポートはなく、設定復元に `regedit.exe`、`reg.exe`、PowerShell、cmd は使用しません。

## 安全性と復元

通常の `registry:` は `HKLM\SOFTWARE\Policies` と `HKCU\Software\Policies` の DWORD または文字列だけを許可します。変更前の値を読み取り、型付きバックアップを書き込み、保存内容を再検証し、変更競合がないことを確認してから適用します。適用後に再読込・照合して完了とします。バックアップは `%ProgramData%\WinRebuilder\backups` に保存します。HKLM の書き込みと復元には昇格が必要です。

ドライラン API はレジストリ、パッケージ、ダウンロード、バックアップ、ログ、実行状態を書き換えません。ただし読み取り専用 winget 照会で winget 自身のメタデータキャッシュが更新される場合があります。実際の導入結果と再実行時の冪等性は、破棄可能な Windows 11 環境での検証が引き続き必要です。

## ビルドとテスト

```sh
dotnet restore WinRebuilder.slnx
dotnet build WinRebuilder.slnx
dotnet test WinRebuilder.slnx
dotnet publish src/WinRebuilder.UI/WinRebuilder.UI.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishTrimmed=false -p:EnableCompressionInSingleFile=true -p:PublishReadyToRun=false
```

Core テストは macOS で実行できます。Windows アダプターと WPF のテストは Windows で実行します。macOS 上の WPF ビルド成功は画面の実行確認ではありません。CI は Windows 上で GUI の起動を検査しますが、パッケージ導入やレジストリ変更は行いません。

## リリース

タグのワークフローは公開バージョンとの一致、復元、ビルド、テスト、Windows 上の GUI 起動、成果物の存在と ZIP 内容を確認します。配布版は Windows x64 向け自己完結型・単一ファイル・圧縮あり・トリミングなし・ReadyToRun なしです。EXE は未署名です。

手動アップロードされる資産は `WinRebuilder.zip` と `WinRebuilder-portable.exe` のみです。ソースコード ZIP と tar.gz は GitHub がタグから自動生成します。ポータブル版は公開 EXE のバイト単位のコピーです。ワークフローは GitHub 提供の `GITHUB_TOKEN` だけを使用し、既存リリースの上書きを拒否します。
