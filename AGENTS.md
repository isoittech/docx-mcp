# AGENTS.md

## プロジェクト概要

- LibreChat／Codex から安全に Word 文書を解析・限定編集・生成する、.NET 10 の stateless Streamable HTTP MCP サーバーである。
- Word 文書は固定ページではなく、再フローする story、論理セクション、block の列として扱う。ページ番号はプレビュー参照にだけ使う。
- Open XML SDK は `IWordDocumentEngine` の背後に置き、LibreOffice はプレビュー用の組版・PDF化に限定する。

## よく使うコマンド

- `docker compose --profile tools build`: runtime、標準テスト、依存監査の各イメージをビルドする。
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
- LibreChat uploadは信頼済み`X-LibreChat-Attachment-File-IDs`がある場合、現在メッセージのopaque file IDだけを解決する。`-`は空scope、header省略はlocal client互換とする。境界内に複数のDOCX／DOTXがある`latest`は更新時刻で選ばず曖昧として拒否する。
- 生成・編集後は OpenXmlValidator、新規エラー比較、LibreOffice PDF、Poppler 全ページ PNG の順で gate を通す。DOCXの非空表セル文字とPDF抽出文字を照合し、欠落または検証不能なら`warnings`へ出して表の視覚確認合格と断言させない。
- 過去の `old_pptx-mcp/.roo/.../docx.py` は proprietary な第三者コードである。参照・コピー・派生をせず、Word 実装は公式仕様と公開ライブラリだけから clean-room で保守する。

## テスト方針

- fixture はテストコードで合成し、実顧客資料、実テンプレート、会社名、ロゴを Git に入れない。
- バイナリ全体の golden 比較ではなく、正規化 XML、意味構造、未対象 part payload、再オープン、検証、レンダリングを確認する。
- field は分割 `instrText`、nested field、未終端を含めて検査する。変更履歴では削除内の `w:delText` と通常／挿入内の `w:t` を区別する。
- 日本語生成では East Asia の font/lang、実 numbering、`keepNext`、`keepLines`、表幅と section property の位置を回帰テストする。

## 注意点・落とし穴

- MCP C# SDK 2.1 の top-level tool 引数名には `JsonSerializerOptions.PropertyNamingPolicy` が適用されない。公開 snake_case は `AIParameterName` で固定し、custom `JsonSerializerOptions` には `TypeInfoResolver` を明示する。
- MCP C# SDK 2.1 で `ToolError` DTO を直接返すと成功扱いになる。tool error は明示的な `CallToolResult` とし、`IsError=true`、同一5項目 JSON の text／`StructuredContent` を返す。DataAnnotations の制約は公開 schema 用なので、同じ範囲を server 側でも検証する。
- 現行ホストでは Docker 既定 seccomp と `no-new-privileges:true` の組み合わせが errno 524 で起動失敗する。seccomp を無効化せず、非 root、read-only、`cap_drop: ALL` で補完し、runtime が対応した環境では `no-new-privileges` を再評価する。
- restore 前に各 `packages.lock.json` を image へコピーし、必ず `dotnet restore --locked-mode` を使う。lock file を暗黙再生成して依存固定を迂回しない。
- `w:updateFields` はフィールド結果を計算せず、Word起動時の汎用外部参照警告を誘発し得る。新規生成DOCXはUNOでindexを最大3パス更新し、更新済みcopyからdirty/update要求を除去してOpen XML／package guardを再検証したものだけを配布する。
- TOC の更新は、更新後の改ページで番号が変わり得るため、更新済みcopyを再オープンして最大3パスで収束を確認する。収束しない、目次番号が PDF の実ページ数を超える、またはLibreOffice保存差分のschema正規化後に検証エラーが残る場合は配布せず fail closed にする。
- 視覚回帰はruntimeと同じ固定版LibreOffice（現在24.2.7）で行う。ホストのLibreOffice 7.3.7では、24.2.7で全列表示できる表の第2列以降が欠落した実例があるため、旧版ホストの再描画だけでstg成果物を不合格にしない。Microsoft Word互換性は別途手動確認として扱う。
- 表示文字列は複数の `w:r`／`w:t` に分割される。`InnerText` の全面再生成で run 書式を壊さない。
- field、revision、content control、bookmark、tab、改行、story 境界をまたぐ置換を暗黙に行わない。
- 新規生成でテンプレートを使う場合、許可した style/theme/numbering/section/header/footer だけを継承し、サンプル本文と個人 metadata を持ち込まない。
- 先頭または末尾に空白を持つ `w:t` には `xml:space="preserve"` を付ける。
- storage、upload、template root は同一・親子関係を許さない。PNG は寸法 header だけで受理せず、chunk CRC、IDAT 順序、IEND まで構造検査する。
- `ArtifactRecord.Path` は `job.json` や MCP 応答へ保存・公開しない。通常の repository 読み取りでは `PublishedRunId` 配下（旧形式は job 配下の一意候補）から basename、byte 数、所有 root、非リンクを検証して復元し、retention sweep だけは期限判定用 metadata を path 復元なしで読む。
