# 受け入れ条件

- 全 16 tool の名称、snake_case schema、required、hint、上限、説明を契約テストする。
- safe DOCX／DOTX の解析、chunk、opaque target と unsafe package の preflight reject を確認する。
- run 跨ぎ置換、atomic edit、simple SDT populate、DOTX→DOCX を意味構造と未対象 part payload で確認する。
- staged `DocumentSpec` から日本語報告書を生成し、named style、実 list、table、TOC、header/footer、field、East Asia 設定を検査する。
- 生成／編集成果物を OpenXmlValidator、LibreOffice UNO/PDF、pdfinfo、Poppler 全ページ PNG へ通す。
- bounded queue、timeout、cancel、restart recovery、draft順序・TTL、revision lineage、artifact retentionを競合込みで確認する。
- container の readiness、Streamable HTTP initialize／discover、tools/list、代表 start/add/finish/wait/preview/download、無認証・別scope拒否を E2E で確認する。
- `docker compose build`、`docker compose run --rm test`、`docker compose run --rm audit`、`git diff --check` を通す。
- LibreChat/provider image E2E と Microsoft Word 手動確認は環境がない場合だけ理由付き `NOT_RUN` とする。
