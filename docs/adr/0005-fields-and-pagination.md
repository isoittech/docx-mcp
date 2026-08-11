# ADR 0005: フィールド更新と pagination の責務を分離する

- 状態: 採用

Open XML SDK と `w:updateFields` は TOC／PAGE の結果や pagination を計算しない。配布 DOCX は native field、dirty flag、update request を保持する。preview 専用 copy だけを LibreOffice UNO で index 更新し、更新結果を検査後に PDF 化する。preview copy の変更は配布 DOCX へ戻さない。Microsoft Word と Writer の page 一致は保証しない。
