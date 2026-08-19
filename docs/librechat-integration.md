# LibreChat 統合

## 構成と信頼境界

LibreChat の API container と `word-mcp` は Docker の internal network で接続します。LibreChat からの MCP 接続先は Streamable HTTP の `http://word-mcp:8080/mcp` です。この URL は container 間の例であり、ブラウザー向け `WORD_MCP_PUBLIC_BASE_URL` ではありません。

```text
LibreChat API
  -> internal network -> word-mcp:8080/mcp
                           -> no-egress
利用者 browser
  -> HTTPS -> artifact proxy
               -> GET/HEAD /artifacts/... と readiness だけ
```

`/mcp` をインターネットや artifact proxy から公開しません。artifact proxy は `integrations/librechat/nginx.conf` の allowlist 以外を 404 にします。

## LibreChat 設定 fragment

`integrations/librechat/librechat.fragment.yaml` を既存 `librechat.yaml` へ手動でマージします。配列や top-level mapping をそのまま上書きせず、既存設定を保持してください。

fragment には次を必須で残します。

- `mcpSettings.allowedDomains` の `word-mcp`
- `type: streamable-http` と `/mcp` URL
- `serverInstructions: true`
- 50 秒の server wait より長い `timeout`
- `apiKey.source: admin`
- `apiKey.authorization_type: bearer`

`headers.Authorization` は追加しません。共有秘密は管理者提供 API key と LibreChat の Bearer 構成に任せます。Authorization を手書きすると、起動時 probe と実接続の認証経路が分かれ、OAuth 必須と誤判定する構成になり得ます。

### Caller header placeholder の version gate

`X-LibreChat-User-ID`、`X-LibreChat-Conversation-ID`、`X-LibreChat-Message-ID` は認可境界です。tool 引数で代替できません。fragment にはローカルの既存導入例で使われる次の候補を記載しています。

```yaml
headers:
  X-LibreChat-User-ID: "{{LIBRECHAT_USER_ID}}"
  X-LibreChat-Conversation-ID: "{{LIBRECHAT_BODY_CONVERSATIONID}}"
  X-LibreChat-Message-ID: "{{LIBRECHAT_BODY_MESSAGEID}}"
```

ただし、conversation／message の正確な placeholder 名と展開可能な request context は LibreChat の利用 version に依存します。上記を「全 version で正しい値」とみなして本番投入してはいけません。導入時に次を実施します。

1. 利用する LibreChat の正確な image tag／commit と `@librechat/agents` version を記録する。
2. その version の公式設定 schema と MCP header 展開処理の source を確認する。
3. user、conversation、message の各 placeholder が実リクエストで空でなく、`{{...}}` の未展開文字列でもないことを、秘密値を log しない test endpoint／MCP E2E で確認する。
4. 別 user／別 conversation の job、analysis、artifact が `not_found` になることを確認する。

サーバーは空値、control character、未展開 placeholder を fail closed で拒否します。確認できない場合は integration を有効化せず、provider E2E と同様に理由付き `NOT_RUN`／release blocker として記録します。

## Shared secret

LibreChat と word-mcp へ同じ 24 文字以上のランダム値を、各環境の secret 管理機構から注入します。実値を YAML、compose、shell history、Git、log に書きません。

例の変数名は `WORD_MCP_SHARED_SECRET` です。これは artifact signing key、scope HMAC key と異なる値にします。word-mcp は 3 値の長さと相互不一致を起動時に検証します。

起動後は次を確認します。

- 無認証 `/mcp` が拒否される。
- LibreChat の管理者 API key 経由では MCP 2026-07-28 `server/discover` と `tools/list` が成功する。旧 protocol を有効にする場合だけ legacy `initialize` も確認する。
- 起動時 probe が OAuth login を要求しない。
- log、health response、error body に shared secret が出ない。

## Upload 連携

LibreChat の user 別 upload 領域を word-mcp へ read-only mount します。tool は不透明な `source_file_id`／`image_file_id` だけを受け、filename、absolute／relative path、glob、任意 URL は受けません。

local storage adapter の想定命名例は次です。

```text
/app/uploads/<user-id>/<file-id>__<original-name>.docx
/app/uploads/<user-id>/<file-id>__<original-name>.dotx
/app/uploads/<user-id>/<file-id>__<original-name>.png
/app/uploads/<user-id>/<file-id>__<original-name>.jpg
```

これは storage backend の普遍仕様ではありません。導入 LibreChat の実命名規則を synthetic fixture で固定してください。version や S3 等の backend が変わる場合は `InputFileResolver` を交換し、MCP server が任意 URL を download する方式へ変えません。

Bedrock 等で file ID が model turn に提示されない場合、`source_file_id` を省略できる tool は同じ user scope の最新 DOCX／DOTXだけを解決します。明示 ID を常に優先します。この upload `latest` は user scope であり、job／draft／analysis／target／artifact の user+conversation scope や、管理者 template scope とは異なります。

### Size／MIME の整合

LibreChat、upload proxy、word-mcp の制限を次の順で同じか、外側をわずかに大きく設定します。

- DOCX／DOTX: word-mcp の実ファイル上限は 30 MiB
- PNG／JPEG: server の画像個別・総 size、寸法、総画素上限に合わせる
- reverse proxy upload body: multipart overhead を含め、30 MiB より十分大きい値
- `/mcp` JSON body: 2 MiB。file binary や base64 を MCP JSON へ入れない
- `/mcp` JSON content: 単一 string 値 200,000、property 名と string 値の合計 400,000 UTF-16 code unit 以内

許可 MIME の例は次です。extension だけで信頼せず、word-mcp が magic bytes、content type、OPC structure を再検証します。

- `application/vnd.openxmlformats-officedocument.wordprocessingml.document`
- `application/vnd.openxmlformats-officedocument.wordprocessingml.template`
- `image/png`
- `image/jpeg`

## 既定 template と server instructions

既定 template は upload root ではなく、管理者の private directory から read-only mount します。`WORD_MCP_DEFAULT_TEMPLATE_ID` に対応する macro-free `<template-id>.docx`／`.dotx` を起動時に検証し、不正・欠落なら readiness を fail closed にします。

標準 image は UID/GID `1654:1654` の非 root user で動きます。storage root はこの ID だけが書き込めるようにし、upload／template root はこの ID が読める read-only mount にします。permission error を広すぎる mode や root 実行で回避しません。

`WORD_MCP_FIRST_ASSISTANT_NOTICE` は導入環境固有の短い案内を server instructions へ追加します。OSS の既定値は空です。秘密、社内 URL、個人情報を入れません。LibreChat の `serverInstructions: true` を無効化すると、通常 workflow、待機、全ページ確認、2 巡上限等の指示を model が受け取れないため release blocker です。

## Model に期待する通常運用

- 既存文書を編集する前に `word_analyze` を実行し、`word_get_analysis_chunk` が返す正確な `target_id` だけを使う。解析 ID や target ID を推測しない。
- 非同期 tool の `job_id` は `word_wait_for_job` で待つ。宣言型文書では成功結果の `result.section_keys` を保持し、titleからkeyを推測しない。`word_get_job` を短間隔で反復しない。
- `word_get_job(job_id=latest)` の「状態を問わない直近 job」と、insert／refine の「最新成功済み宣言型文書」を混同しない。
- 新規文書は start → 1 回 3 セクション以内の add → finish の順に作り、空引数で先行呼び出ししない。section title は自動Heading 1なので、body先頭へ同一headingを重ねない。start 後に template／design を変更しない。
- 編集成功後は新しい `output_analysis_id` と target snapshot を使い、古い snapshot を再利用しない。
- 成功 job の `page_count` を確認し、`word_get_preview_images` へ重複しない 1 始まり page number を 1〜4 件ずつ渡して全ページを見る。
- 視覚問題は `word_refine_document_section` へ 1 セクションずつ渡す。修正後は reflow の影響を受ける全ページを再確認し、自律修正は最大 2 巡で止める。
- preview image を取得していなければ「視覚確認済み」と述べず、全ページ確認後にだけ DOCX link を提示する。
- 外部調査は LibreChat 側で行い、MCP へは構造化済みの内容だけを渡す。

## Preview image の provider E2E

MCP image block が LibreChat UI に表示されることと、次の model turn がその画像内容を認識できることは別です。次の経路を実際の provider ごとに試験します。

```text
word_get_preview_images
  -> LibreChat MCP client
  -> 利用 provider の multimodal request 変換
  -> 次の model turn
  -> 画像内にだけ置いた synthetic marker の認識
```

特に Bedrock では、利用中 LibreChat／`@librechat/agents` が MCP image artifact を次の request へ再投入するかを version ごとに確認します。未対応の場合は、導入環境側で次の条件を満たす fail-closed patch を用意します。

- 対象 dependency の exact version と code shape を検査し、不一致なら build を失敗させる。
- 通常 Dockerfile 群の dependency install 後に適用する。
- dependency 更新時に upstream 修正を確認し、patch の削除または更新を行う。
- 更新後に provider image E2E を再実行する。

この repository の汎用 fragment は特定 LibreChat／provider version に依存する patch を同梱しません。実 LibreChat UI／provider credential がない環境では protocol-level E2E を省略せず、provider image E2E だけを `NOT_RUN` として、環境不足の理由を release 記録へ残します。

## 成果物公開

`WORD_MCP_PUBLIC_BASE_URL` は利用者 browser から到達できる absolute HTTPS URL にします。明示的 local development 以外の HTTP をサーバーは拒否します。公開 proxy は次だけを通します。

- `GET`／`HEAD /artifacts/...`。HMAC 署名と 15 分の URL expiry は application が検証
- `GET /health/ready`

`POST /mcp`、storage volume、LibreChat upload、template directory は公開しません。署名付き URL を access log、analytics、Referer へ残さないようにし、proxy と application の双方で `Cache-Control: no-store`、`X-Content-Type-Options: nosniff`、安全な `Content-Disposition` を確認します。

compose fragment は artifact proxy の HTTP port を host loopback にだけ bind し、その前段の HTTPS ingress で TLS を終端する例です。同じ Docker network 上の既存 HTTPS ingress へ接続する場合は host port 自体を削除します。artifact proxy の port を TLS なしで `0.0.0.0` へ publish しません。

## Compose fragment

`integrations/librechat/compose.fragment.yaml` は既存 LibreChat compose へ手動で統合する例です。次は導入環境で必ず調整します。

- LibreChat API service 名と upload host path
- pin 済み word-mcp／proxy image reference
- secret manager からの値注入
- browser 向け HTTPS port／ingress
- storage quota、backup、retention worker
- Docker network 名と既存 resource limit

fragment の相対 path は word-mcp repository root を基準にした例です。LibreChat repository へ置く場合は path を読み替えます。

## Release verification

- [ ] 利用 LibreChat commit、container tag、`@librechat/agents` version を記録した。
- [ ] その version で caller header の正確な placeholder 名を確認した。
- [ ] `allowedDomains`、Streamable HTTP、`serverInstructions: true`、admin Bearer key が有効である。
- [ ] `headers.Authorization` を追加していない。
- [ ] MCP timeout が 50 秒より長い。
- [ ] upload 命名 fixture と read-only mount が実 backend に一致する。
- [ ] DOCX／DOTX／PNG／JPEG の size、extension、MIME と proxy body limit が整合する。
- [ ] `/mcp` は internal、artifact proxy は GET／HEAD と readiness だけである。
- [ ] 無認証、未展開 placeholder、別 user、別 conversation を拒否する。
- [ ] MCP 2026-07-28 `server/discover`、`tools/list`、代表 workflow、artifact download の protocol E2E が成功する。旧 protocol を有効にする場合は legacy `initialize` も成功する。
- [ ] 全 16 tool と server instructions が LibreChat から見える。
- [ ] 実 provider の次 model turn が preview image の内容を認識する。未実施なら理由付き `NOT_RUN` である。
