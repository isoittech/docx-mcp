# アーキテクチャ

## 目的と境界

word-mcp は LibreChat／Codex 側のモデルが作成した制約済みの指示を、安全な WordprocessingML へ反映する。調査、文章推敲、外部 URL 取得、LLM 呼び出しは行わない。Microsoft Office Interop、COM、VSTO は使わない。

Word は固定ページではなく再フロー文書であるため、永続編集単位をページにしない。本文、header、footer、footnote、endnote、comment 等の story を列挙し、論理 section、paragraph、table、cell、content control、bookmark に snapshot 固有の `target_id` を割り当てる。ページ番号は LibreOffice が組版した preview PNG の取得にだけ用いる。

## コンポーネント

```text
HTTP /mcp
  -> request limit / Bearer / trusted caller headers
  -> 薄い WordTools
      -> DraftService（同期・永続）
      -> JobService -> bounded queue -> JobWorker（非同期・最大3並列）
          -> InputFileResolver / TemplateRegistry
          -> DocxPackageGuard
          -> IWordDocumentEngine / OpenXmlWordDocumentEngine
          -> DocumentRenderer（UNO -> PDF -> Poppler PNG）
          -> AnalysisRepository / ArtifactService
GET|HEAD /artifacts/{job}/{artifact}/{file}
  -> HMAC capability URL / retention policy
```

永続 repository は JSON を同一 filesystem 上の一時 file へ書いてから atomic rename する。起動時に `running` を `queued` へ戻し、固定済み入力 snapshot から再開する。queue depth は 12、worker は最大 3、1 job は最大 10 分である。

## ID と scope

生の user／conversation ID は保存しない。独立した scope key と domain separation を使う HMAC-SHA256 で user scope と conversation scope を作る。job、draft、analysis、target、artifact は user+conversation scope、LibreChat upload の `latest` は user scope、deployment template は administrator scope で解決する。

`target_id` 自体は推測困難な乱数であり、analysis repository 内で source SHA-256、part URI、story、対象種別、locator と対応する。編集時には analysis、target、入力 snapshot SHA、scope をすべて再検証する。編集成功後は新しい source SHA に対する analysis を発行し、旧 snapshot を stale とする。

## 処理フロー

1. 入力を不透明 ID から通常 file として解決し、job 専用領域へ一度だけコピーして SHA-256 を固定する。
2. ZIP/OPC/XML/media/field/relationship の semantic preflight を行う。active content は本文解析前に拒否する。
3. operation policy に基づき、解析、限定編集、template 流し込み、宣言型生成のいずれかを行う。
4. Open XML SDK で再オープンし、`OpenXmlValidator` を実行する。既存編集は新規 error が増えないこと、新規生成は 0 件を要求する。
5. 配布 DOCX と別の preview copy を UNO で index 更新し、PDF 化する。PDF page count を検査後、明示 page range だけ PNG 化する。
6. job metadata と artifact metadata を atomic 保存し、署名 URL と preview page count を返す。

## フィールドと組版

TOC、PAGE、NUMPAGES 等は固定 allowlist の builder だけが生成する。Open XML SDK は pagination や field result を計算しないため、配布 DOCX には dirty field と `w:updateFields` を保持する。preview copy だけを LibreOffice UNO で index 更新し、配布物へ逆流させない。Microsoft Word と LibreOffice Writer の改ページ一致は保証せず、Open XML 検証、LibreOffice 表示、Microsoft Word 手動確認を別の gate とする。

## 保持と backpressure

大きな analysis は `analysis_id` と cursor で最大 50 件ずつ返す。画像は成功済み job から重複のない 1〜4 ページだけを image block で返す。DOCX、PDF、全画像を MCP 応答へ埋め込まない。`word_wait_for_job` は開始時に concrete job ID へ固定し、通常 45 秒、最大 50 秒だけ待つ。

成果物の実効期限は `min(created_at + 7日, first_docx_download_at + 24時間)` である。preview の閲覧や HEAD は短縮 timer を開始しない。
