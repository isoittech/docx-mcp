# 受け入れ条件

- 全 16 tool の名称、snake_case schema、required、hint、上限、説明を契約テストする。
- 正常 tool result の text／`structuredContent` と、エラー時の `isError=true`・同一5項目 JSON を実 Streamable HTTP で確認する。
- MCP body、JSON depth、単一 string／合計 string budget と、child process が親環境を継承しないことを境界テストする。
- safe DOCX／DOTX の解析、chunk、opaque target と unsafe package の preflight reject を確認する。
- run 跨ぎ置換、atomic edit、simple SDT populate、DOTX→DOCX を意味構造と未対象 part payload で確認する。
- staged `DocumentSpec` から日本語報告書を生成し、named style、実 list、table、TOC、header/footer、field、East Asia 設定を検査する。
- 生成／編集成果物を OpenXmlValidator、LibreOffice UNO/PDF、pdfinfo、Poppler 全ページ PNG へ通す。
- 新規生成DOCXは更新済みTOC結果を保持し、`w:updateFields`、dirty field、「目次を更新してください」プレースホルダーを含まず、最終bytesのSHA-256とanalysis snapshotが一致する。
- bounded queue、timeout、cancel、restart recovery、draft順序・TTL、scope 付き解析 cache、LRU、revision lineage、artifact retention を競合込みで確認する。
- 新規生成 template は sample 本文／危険 part を持ち込まず、最後の page setup、有効な first／even／default header・footer、検査済み画像だけを継承する。
- container の readiness、MCP 2026-07-28 Streamable HTTP `server/discover`、tools/list、代表 start/add/finish/wait/preview/download、無認証・別scope拒否を E2E で確認する。旧 protocol との互換試験を行う場合だけ legacy `initialize` も確認する。
- `docker compose build`、`docker compose run --rm test`、`docker compose run --rm audit`、`git diff --check` を通す。
- LibreChat/provider image E2E と Microsoft Word 手動確認は環境がない場合だけ理由付き `NOT_RUN` とする。
