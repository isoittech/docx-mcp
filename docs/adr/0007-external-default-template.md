# ADR 0007: 既定テンプレートを外部 read-only mount で扱う

- 状態: 採用

実 template、会社名、logo、顧客資料を repository／image に含めない。管理者 scope の `<template-id>.docx|.dotx` を read-only mount し、起動時に安全性・Open XML・構造を fail closed 検証する。解析 cache は content SHA 単位とする。workflow start で `default`、`none`、`latest`、明示 file ID を選んで固定する。

新規生成では template 本文を成果物へ残さず、allowlist の style、theme、numbering、section property、header/footer、page setup と必要 media だけを継承する。populate は本文を意図的に保つ別 workflow であり、unsupported passive content を黙って sanitize しない。
