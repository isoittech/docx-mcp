# ADR 0001: ページではなく論理 section／block を編集単位にする

- 状態: 採用

Word の page は font、printer、application、前段編集で変化する組版結果であり永続識別子にならない。本文等の story、Word section、paragraph、table、cell を論理順で解析し、snapshot 固有の opaque target を付ける。page number は LibreOffice preview の視覚参照にだけ用いる。
