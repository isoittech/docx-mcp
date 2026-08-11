# ADR 0002: Open XML 実装を IWordDocumentEngine の背後に置く

- 状態: 採用

Open XML SDK は OPC と WordprocessingML の低水準操作・検証に適する一方、pagination と field result を計算しない。解析、限定編集、template populate、宣言型生成を interface の背後へ置き、MCP／job／artifact の境界を実装詳細から分離する。LibreOffice は renderer としてのみ利用する。
