# ADR 0005: フィールド更新と pagination の責務を分離する

- 状態: 採用

Open XML SDK と `w:updateFields` は TOC／PAGE の結果や pagination を計算しない。また、`w:updateFields`を残した生成DOCXは、外部参照がなくてもMicrosoft Wordの汎用フィールド更新警告を起動時に表示し得る。

宣言型の新規生成では、専用copyをLibreOffice UNOで最大3パス更新し、収束、見出し対応、目次ページ番号範囲を検査する。その更新済みcopyからdirty flagと`w:updateFields`を除去し、LibreOffice由来の既知のschema順序と重複`paperSrc`／row on-off表現だけを正規化する。package guardとOffice 2019 OpenXmlValidatorを再実行し、成功したbytesのSHA-256からanalysis snapshotを作り直して配布する。既存文書の限定編集では、未対象part保持契約を優先し、この最終化をDOCXへ逆流させない。Microsoft Word と Writer のpage一致は保証しない。
