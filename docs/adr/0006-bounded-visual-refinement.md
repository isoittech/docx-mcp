# ADR 0006: 自動視覚修正を section 単位・最大2巡に制限する

- 状態: 採用

前段の編集で後続全 page が再フローするため、page 差し替えや全体再生成を自律反復すると lineage と停止条件が崩れる。成功済み宣言型文書の最新 revision に対し、1 logical section の完全仕様だけを逐次適用する。root、parent、round、同巡の修正 section を保存し、古い分岐、一括修正、3巡目を拒否する。後日の明示的な利用者編集は別 operation として許可する。
