# AGENTS.md

## プロジェクト概要

- LibreChat／Codex から安全に Word 文書を解析・限定編集・生成する、.NET 10 の stateless Streamable HTTP MCP サーバーである。
- Word 文書は固定ページではなく、再フローする story、論理セクション、block の列として扱う。ページ番号はプレビュー参照にだけ使う。
- Open XML SDK は `IWordDocumentEngine` の背後に置き、LibreOffice はプレビュー用の組版・PDF化に限定する。

## よく使うコマンド

- `docker compose build`: アプリとテスト用イメージをビルドする。
- `docker compose run --rm test`: 単体、契約、Open XML、レンダリング統合テストを一括実行する。
- `docker compose run --rm audit`: NuGet の既知脆弱性と非推奨依存を確認する。
- `docker compose up -d word-mcp word-codex-proxy`: ローカル MCP と loopback proxy を起動する。
- `git diff --check`: commit 前に空白エラーを確認する。

## アーキテクチャ方針

- MCP tool 層は入力受付と構造化エラー変換だけにし、重い処理は永続非同期ジョブへ渡す。
- `target_id` は解析 snapshot 内の不透明 ID とし、source SHA-256、利用者、会話へ束縛する。編集後は必ず新 snapshot を使い、旧 target を stale として拒否する。
- 既存 DOCX の編集では対象 part と明示した付随 part だけを書き換え、それ以外の展開後 ZIP entry payload を byte-identical に保つ。
- 任意 OOXML、HTML、Markdown、コード、シェル、座標、URL、base64、ローカル path を公開入力へ追加しない。生成は制約済み `DocumentSpec` だけを受ける。
- Package guard は active content、危険 field、禁止 external relationship、未対応 media、ZIP/XML bomb を Open XML 処理や LibreOffice より前に fail closed で拒否する。
- MCP 本体から外部ネットワークへ接続しない。成果物配信 proxy とローカル MCP proxy の責務を混同しない。
- 生成・編集後は OpenXmlValidator、新規エラー比較、LibreOffice PDF、Poppler 全ページ PNG の順で gate を通す。

## テスト方針

- fixture はテストコードで合成し、実顧客資料、実テンプレート、会社名、ロゴを Git に入れない。
- バイナリ全体の golden 比較ではなく、正規化 XML、意味構造、未対象 part payload、再オープン、検証、レンダリングを確認する。
- field は分割 `instrText`、nested field、未終端を含めて検査する。変更履歴では削除内の `w:delText` と通常／挿入内の `w:t` を区別する。
- 日本語生成では East Asia の font/lang、実 numbering、`keepNext`、`keepLines`、表幅と section property の位置を回帰テストする。

## 注意点・落とし穴

- `w:updateFields` はフィールド結果を計算しない。配布 DOCX は dirty/update 要求を保持し、プレビュー copy だけを UNO で index 更新する。
- 表示文字列は複数の `w:r`／`w:t` に分割される。`InnerText` の全面再生成で run 書式を壊さない。
- field、revision、content control、bookmark、tab、改行、story 境界をまたぐ置換を暗黙に行わない。
- 新規生成でテンプレートを使う場合、許可した style/theme/numbering/section/header/footer だけを継承し、サンプル本文と個人 metadata を持ち込まない。
- 先頭または末尾に空白を持つ `w:t` には `xml:space="preserve"` を付ける。
