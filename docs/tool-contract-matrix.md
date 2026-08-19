# MCP tool 契約表

公開 JSON は snake_case だけを使用する。すべての ID は不透明であり、推測やローカル path への変換を禁止する。`latest` は tool ごとの scope と意味を変えない。

| tool | mode | hint | 主入力 | 発行／消費 ID | scope / TTL | 上限・終端 | 成功後 |
|---|---|---|---|---|---|---|---|
| `word_get_capabilities` | sync | RO, idempotent | なし | なし | caller | compact response | workflow を選ぶ |
| `word_analyze` | async | RO, idempotent | `source_file_id`=`latest`可 | file/artifact -> job, analysis | upload=user、artifact=user+conversation、analysis=1h | unsafe は `rejected_unsafe_document` | wait |
| `word_get_analysis_chunk` | sync | RO, idempotent | analysis, kind, cursor | analysis/target | user+conversation / 1h | 1〜50件 | target を正確に使う |
| `word_render_preview` | async | RO, idempotent | source file/artifact | job | 同上 | 50 pages | wait、全 preview |
| `word_replace_text` | async | destructive | analysis, target, expected text/count, replacement | analysis/target -> job/output analysis | user+conversation / 1h | 1〜100置換 | wait、新 snapshot |
| `word_apply_edits` | async | destructive | analysis, 1〜50 atomic edits | analysis/target -> job/output analysis | user+conversation / 1h | 全成功または全失敗 | wait、新 snapshot |
| `word_populate_template` | async | destructive | source, 1〜100 field | file -> job/output analysis | user+conversation | simple SDT/bookmark のみ | wait、全 preview |
| `word_start_document` | sync | destructive, retry-idempotent | metadata/layout/design/expected sections/template | draft、提出済み再送ではjob | user+conversation+trusted message / 1h | 1〜50 sections、同一message再送は既存draft | add、提出済みなら既存jobへ |
| `word_add_sections_to_draft` | sync | destructive | draft, 1〜3 completed sections | draft | user+conversation / 1h | 60 block・30,000文字/回 | add または finish |
| `word_finish_document` | async | destructive, retry-idempotent | completed draft | draft -> job | user+conversation | section 数一致必須、同じdraftの再送は既存job | wait、全 preview |
| `word_insert_document_sections` | async | destructive | `job_id=latest`, 1〜3 sections, position | successful declarative job -> job | user+conversation | latest=最新成功宣言型文書 | wait、全 preview |
| `word_refine_document_section` | async | destructive | `job_id=latest`, 1 section replacement | successful declarative job -> job | user+conversation | 自動修正は section 単位・最大2巡 | wait、全 preview |
| `word_get_job` | sync | RO, idempotent | `job_id`=`latest`可 | job | user+conversation / artifact期限まで | latest=状態不問の直近 job | 状態に従う |
| `word_wait_for_job` | sync wait | RO, idempotent | job, 1〜50秒 | job（宣言型成功時は `result.section_keys`） | user+conversation | terminal または timeout snapshot | key保持、成功なら全 preview |
| `word_get_preview_images` | sync | RO, idempotent | successful job, page numbers | job | user+conversation | 重複なし1〜4、1始まり | 全 page を確認 |
| `word_cancel_job` | sync | destructive | job | job | user+conversation | queued/running のみ | get job |

共通 terminal state は `succeeded`、`failed`、`canceled`、`timed_out`、`rejected_unsafe_document`。tool 実行中の入力エラーは明示的な MCP `CallToolResult` で返し、wire 上の `isError=true`、JSON text block、同内容の `structuredContent` を持たせる。JSON は `status`、`code`、`field_path`、`message`、`correction` の5項目とする。queue full は `resource_exhausted`、scope 不一致は存在を漏らさず `not_found` とする。
