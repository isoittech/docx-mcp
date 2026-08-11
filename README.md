# word-mcp

LibreChat／Codex から Word 文書を安全に解析、限定編集、テンプレート流し込み、宣言型生成する .NET 10 の MCP サーバーです。Streamable HTTP の `/mcp` を stateless transport で提供し、重い処理は永続非同期ジョブとして実行します。

このサーバーは文章の調査や LLM 呼び出しを行いません。LibreChat／Codex 側が作成した制約済みの指示を WordprocessingML へ反映し、Open XML 検証と LibreOffice／Poppler による表示確認用プレビューを提供することが責務です。

## 設計上の前提

- Word 文書は固定ページではなく、再フローする story、論理セクション、block の列として扱います。ページ番号はプレビュー画像の取得にだけ使い、編集対象の永続 ID にはしません。
- 編集対象は解析 snapshot 固有の不透明な `target_id` で指定します。ID は source SHA-256、利用者、会話へ束縛され、編集後の古い `analysis_id`／`target_id` は stale として拒否されます。
- 新規生成は制約済みの `DocumentSpec` だけを受け付けます。任意 OOXML、XML、HTML、Markdown、CSS、コード、シェル、座標、URL、base64、ローカル path は公開入力にできません。
- Open XML SDK はページネーションやフィールド結果を計算しません。配布 DOCX は dirty/update 要求付き field を保持し、プレビュー専用 copy だけを LibreOffice UNO で更新します。
- LibreOffice は編集エンジンではなく、DOCX→PDF の表示確認にだけ使います。Microsoft Word と LibreOffice Writer の改ページ一致は保証しません。

詳細は [アーキテクチャ](docs/architecture.md)、[セキュリティ設計](docs/security.md)、[tool 契約表](docs/tool-contract-matrix.md) を参照してください。

## 対応範囲

- macro-free `.docx`／`.dotx` の安全性 preflight、story・セクション・段落・表・style・field・content control 等の解析
- 大きな解析結果の cursor 分割取得と、snapshot 固有の編集対象 ID
- 複数 run に分割された文字列の安全な置換
- target を使う atomic な段落／見出し／表セル編集、main story の限定 block 挿入・削除、表末尾への行追加
- 単純な inline／block／table-cell content control へのテンプレート流し込み
- 段階 draft と宣言型 `DocumentSpec` による、編集可能な Word ネイティブ文書の生成
- 論理セクションの挿入と、1 セクションずつ最大 2 巡の自動視覚修正
- LibreOffice→PDF→Poppler PNG の全ページプレビュー
- 15 分有効の署名付き成果物 URL と、利用者・会話ごとの所有権検証

## 明示的な非対応範囲

- DOCM／DOTM、VBA、ActiveX、OLE、embedded package、`altChunk`、暗号化文書、禁止 field、linked image 等の active／外部取得機能。これらは preflight で拒否します。
- 変更履歴の作成・承認・拒否、コメント編集、脚注／文末脚注編集、数式、引用文献、complex／repeating SDT、text box、chart、SmartArt の編集
- Microsoft Office Interop、COM、VSTO、LibreOffice を使った DOCX 本体の編集
- 任意 URL の取得、外部 LLM API 呼び出し、任意 XML／HTML／Markdown からの変換
- Microsoft Word と LibreOffice Writer の pixel-perfect な組版一致

検出・保持・拒否の操作別方針は [Word 機能ポリシー](docs/feature-policy.md) に固定しています。

## 必要環境

- Docker Engine と Docker Compose v2
- production では、ブラウザーから到達できる成果物用 HTTPS origin
- LibreChat 統合時は、その導入環境の upload storage を read-only mount できること
- Microsoft Word の手動 release gate を行う場合は、対象 OS 上のサポート中 Microsoft Word と必要フォント

ホストへ .NET SDK、LibreOffice、Poppler を直接導入する必要はありません。

## 起動

1. `.env.example` を `.env` へコピーします。`.env` は Git へ追加しません。
2. 共有秘密と 2 種類の署名鍵を、それぞれ独立した暗号学的乱数へ置き換えます。3 値の使い回しは起動時に拒否されます。
3. storage、LibreChat upload、template の各 path をホスト上の絶対 path で設定します。
4. image を build し、用途に応じた入口を起動します。

```bash
docker compose build
docker compose up -d word-mcp
```

ローカル Codex も使う場合は loopback proxy を追加します。

```bash
docker compose up -d word-mcp word-codex-proxy
```

MCP 本体をホストやインターネットへ直接 publish しないでください。本番 LibreChat では internal network の `http://word-mcp:8080/mcp` から接続し、成果物 GET/HEAD と readiness だけを別 proxy で公開します。ローカル Codex proxy は `127.0.0.1` にだけ bind します。

## 環境変数

| 変数 | 要件 |
|---|---|
| `WORD_MCP_SHARED_SECRET` | 24 文字以上。LibreChat／Codex が Bearer token として使う共有秘密 |
| `WORD_MCP_ARTIFACT_SIGNING_KEY` | 32 文字以上。成果物 URL 専用。ほかの鍵と異なる値 |
| `WORD_MCP_SCOPE_HMAC_KEY` | 32 文字以上。利用者／会話 scope の pseudonymization 専用 |
| `WORD_MCP_PUBLIC_BASE_URL` | ブラウザーから到達する absolute HTTPS URL。明示的 local development だけ HTTP 可 |
| `WORD_MCP_STORAGE_PATH` | job、draft、analysis、artifact の永続 storage 用 absolute path |
| `WORD_MCP_UPLOADS_PATH` | LibreChat upload root の absolute path。コンテナ内では read-only |
| `WORD_MCP_TEMPLATES_PATH` | 管理者 template root の absolute path。コンテナ内では read-only |
| `WORD_MCP_DEFAULT_TEMPLATE_ID` | 任意。起動時に検証する既定 template ID |
| `WORD_MCP_FIRST_ASSISTANT_NOTICE` | 任意。server instructions へ追加する導入環境固有の初回案内。OSS 既定は空 |
| `WORD_MCP_LOCAL_DEVELOPMENT` | local HTTP を許す明示フラグ。本番では `false` |
| `WORD_MCP_PORT` | local Codex proxy の loopback 公開 port。既定例は `18081` |

秘密値、実 template、顧客資料、生成 DOCX／PDF／PNG をリポジトリへ追加しないでください。設定値の詳細は `.env.example` を参照してください。

## Server limit

以下は hard ceiling です。導入設定で小さくできますが、これを超えて緩和できません。

| 対象 | 上限 |
|---|---:|
| MCP request body | 2 MiB |
| JSON depth | 32 |
| DOCX／DOTX 入力 | 30 MiB |
| ZIP entry | 5,000 |
| 展開後合計 | 300 MiB |
| 圧縮率 | 250 倍 |
| 単一 XML part／XML depth／1 element の属性 | 32 MiB／64／128 |
| 1 part の relationship | 2,000 |
| semantic block／文字 | 1,000／200,000 |
| 表 cell／画像／明示 page break | 10,000／40／50 |
| 画像の個別／合計 size | 12 MiB／40 MiB |
| 画像の一辺／個別・合計画素 | 12,000 px／80,000,000・200,000,000 px |
| レンダリング後ページ | 50 |
| PDF／preview 合計 | 100 MiB／250 MiB |
| worker／queue depth／1 job | 最大 3 並列／12／10 分 |
| `word_wait_for_job` | 通常 45 秒、最大 50 秒 |
| analysis chunk | 最大 50 件 |
| preview image | 1 回 1〜4 ページ、重複不可、1 始まり |
| draft | 1 時間 |
| 1 回の draft 追加 | 3 セクション、60 block、30,000 文字 |
| 1 conversation の永続 item／合計 | 128／512 MiB |

上限超過時は payload 全体を log せず、`status=invalid_input` または `resource_exhausted` の構造化エラーを返します。

## 公開 MCP tool

全 16 tool は `word_` prefix と snake_case JSON を使用します。

| tool | 応答 | 用途 |
|---|---|---|
| `word_get_capabilities` | 同期・read-only | 対応機能、上限、workflow、保持／拒否方針を取得 |
| `word_analyze` | 非同期 job | DOCX／DOTX または成果物を安全に解析 |
| `word_get_analysis_chunk` | 同期・read-only | outline、block、table、control 等を cursor 付きで取得 |
| `word_render_preview` | 非同期 job | 検査済み文書の preview を生成 |
| `word_replace_text` | 非同期 job | run 境界を考慮した期待一致数付き文字置換 |
| `word_apply_edits` | 非同期 job | `target_id` に対する全成功／全失敗の限定差分編集 |
| `word_populate_template` | 非同期 job | 単純 SDT tag、明示的 bookmark fallback へ流し込み |
| `word_start_document` | 同期 draft 更新 | 新規文書の metadata、layout、design、template を固定 |
| `word_add_sections_to_draft` | 同期 draft 更新 | 完成済み論理セクションを順番に少数ずつ追加 |
| `word_finish_document` | 非同期 job | 完成 draft から DOCX と preview を生成 |
| `word_insert_document_sections` | 非同期 job | 成功済み宣言型文書へ追加セクションだけを挿入 |
| `word_refine_document_section` | 非同期 job | 論理セクション 1 つを完全仕様で差し替え |
| `word_get_job` | 同期・read-only | 待たない状態確認または障害復旧 |
| `word_wait_for_job` | 同期・read-only wait | concrete job を bounded wait |
| `word_get_preview_images` | 同期・read-only | 成功 job の指定 1〜4 ページを MCP image block で取得 |
| `word_cancel_job` | 同期 | 所有権確認後、queued／running job の cancel を受理 |

ID を推測したり、別文書・別会話へ流用したりしないでください。空引数で tool を先行呼び出しせず、必須入力を schema に従って一度に渡します。

## `latest` の scope

`latest` は全体共通のグローバル最新ではありません。

| 対象 | scope と意味 |
|---|---|
| LibreChat upload source | 同じ user scope 内の最新の安全な DOCX／DOTX。明示 `file_id` を常に優先 |
| `word_get_job(job_id=latest)` | 同じ user+conversation scope の、状態を問わない直近 job |
| insert／refine の `job_id=latest` | 同じ user+conversation scope の最新成功済み宣言型文書 |
| draft／analysis／target／artifact | user+conversation scope。schema が `latest` を明示しない箇所へ文字列を送らない |
| deployment template | 管理者 scope。利用者 upload の `latest` と別物 |

「直近 job」と「最新の成功済み生成文書」を混同しないでください。job wait は receipt に含まれる concrete `job_id` を使います。

## 通常 workflow

### 既存文書を解析・編集する

1. `word_analyze` を呼び、返された `job_id` を `word_wait_for_job` で待ちます。
2. 成功結果の `analysis_id` を使い、必要な種類だけ `word_get_analysis_chunk` で取得します。
3. 返却された正確な `target_id` を `word_replace_text` または `word_apply_edits` へ渡します。ID は推測しません。
4. 編集 job を待ち、結果の新しい `output_analysis_id` と target snapshot へ切り替えます。
5. `word_get_preview_images` を 1〜4 ページずつ呼び、全ページを確認します。
6. 最終確認後だけ署名付き DOCX link を利用者へ提示します。

unsafe 文書は `rejected_unsafe_document` で終端し、本文や target を返しません。原因 code と修正指示に従って入力文書自体を安全化してください。

### テンプレートへ流し込む

1. [テンプレートガイド](docs/template-guide.md) に従い、macro-free DOCX／DOTX と一意な SDT tag を用意します。
2. 必要なら `word_analyze` で利用可能な control と非対応 feature を確認します。
3. `word_populate_template` へ tag と値を渡します。alias だけを正本にせず、曖昧な tag、locked、data-bound、nested、repeating control を使いません。
4. job 完了後に全ページを確認します。DOTX の出力は DOCX です。

### 新規文書を作る

1. 期待論理セクション数、metadata、layout、theme／design、template 選択を決め、`word_start_document` を 1 回呼びます。
2. 返された `draft_id` へ、`word_add_sections_to_draft` で順番どおり最大 3 セクションずつ追加します。受理済みセクションを再送しません。
3. `remaining_section_count=0` のときだけ `word_finish_document` を 1 回呼びます。start 後に template や全体 design を変更しません。
4. job を待ち、全 preview page を確認します。
5. 問題があれば `word_refine_document_section` へ 1 セクションだけの完全な差し替え仕様を渡し、成功後に全ページを再確認します。自律的な見た目修正は最大 2 巡です。
6. セクション追加は `word_insert_document_sections` へ追加分だけを渡します。成功済み文書を start／finish から作り直しません。

非同期 tool が `job_id` を返したら `word_get_job` を短間隔で反復せず、まず `word_wait_for_job` を使います。preview を取得していない場合は「視覚確認済み」と扱いません。

## 既定テンプレート

管理者が用意した macro-free `<template-id>.docx`／`<template-id>.dotx` を、リポジトリ外の private directory から read-only mount します。template ID は ASCII 英数字、hyphen、underscoreだけを使い、実 template、会社名、ロゴ、顧客資料、固有文言を OSS repository や image に含めません。

`WORD_MCP_DEFAULT_TEMPLATE_ID` が設定されている場合は、起動時に存在、安全性、Open XML 整合性、構造を検査し、失敗時は readiness を fail closed にします。新規文書の template source は `default`、`none`、同じ user scope の upload `latest`、または明示 `file_id` から start 時に選び、その workflow 中は固定します。

詳細と作成チェックリストは [テンプレートガイド](docs/template-guide.md) を参照してください。

## LibreChat／Codex 統合

- LibreChat: [統合手順](docs/librechat-integration.md) と `integrations/librechat/`
- Codex: `integrations/codex/README.md` と loopback proxy／`config.toml` fragment

LibreChat fragment は `mcpSettings.allowedDomains`、`serverInstructions: true`、Streamable HTTP URL、管理者提供 Bearer key を使います。`headers.Authorization` を手書きしません。caller header の placeholder は LibreChat のバージョンや request context に依存するため、導入中の正確な version の schema／source で確認してから本番へ適用してください。

LibreChat UI に preview が見えるだけでは provider E2E の合格になりません。実際の LibreChat→利用 provider→次の model turn で画像内容を認識できることを環境ごとに検証します。UI／provider 環境がなければ protocol E2E は実行し、provider image E2E だけを理由付き `NOT_RUN` とします。

## セキュリティと保持期限

- Bearer shared secret を固定時間比較し、user／conversation／message は信頼済み HTTP header からだけ取得します。これらを tool 引数にしません。
- upload は不透明な `file_id` から解決し、path、glob、任意 URL、symlink、hardlink、root 外参照を拒否します。検証前に 1 回 snapshot copy し、SHA-256 で固定します。
- ZIP／OPC／XML／field／relationship／media を Open XML SDK と LibreOffice より前に fail closed で検査します。
- MCP container は non-root、read-only、`cap_drop: ALL`、no-egress の internal network で動かします。
- ログへ本文、file 内容、元 filename、secret、署名 URL、生の個人 ID、メールアドレスを出しません。
- 成果物 URL は HMAC-SHA256 署名付きで 15 分有効です。artifact 本体の保持期限は `min(生成から7日, 最初のDOCXダウンロードから24時間)` です。preview 閲覧と HEAD は 24 時間 timer を開始しません。

## Word／LibreOffice の既知差

- フォントの有無、font fallback、禁則処理、hyphenation、表の自動調整、field 実装により改ページが変わります。
- `w:updateFields` は field 結果を生成しません。Word で開いたときに TOC／PAGE／NUMPAGES の更新確認が必要です。
- LibreOffice preview は自動 gate ですが、Microsoft Word 互換性の代替ではありません。release 前に [Microsoft Word 手動確認](docs/manual-word-release-checklist.md) を実施してください。

## テストと監査

```bash
docker compose build
docker compose run --rm test
docker compose run --rm audit
git diff --check
```

標準 test は単体、契約、Open XML、レンダリング統合テストを含みます。実 container の `/mcp` に対する Streamable HTTP E2E と、代表日本語文書の全ページ確認も release gate です。LibreChat/provider image E2E と Microsoft Word 手動確認は利用環境が必要なため、実施できない場合は `NOT_RUN` と理由を記録します。

## ライセンス

利用する第三者ソフトウェアと通知は [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) を参照してください。このプロジェクト自身の LICENSE はユーザー選択が未確定のため、この repository では定めていません。配布前に LICENSE を選択し、依存 lock file、container package、SBOM と notice の一致を再確認してください。
