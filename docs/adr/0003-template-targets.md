# ADR 0003: 名前付き content control と opaque target を正本にする

- 状態: 採用

表示文字列、run index、page number は編集で変化し、alias や bookmark 名は重複・互換差がある。template populate は一意な `w:sdt` tag を第一選択、alias を表示補助、bookmark を明示 fallback とする。通常編集は source SHA と解析 snapshot へ束縛した opaque target を使い、stale／別文書／改ざんを拒否する。
