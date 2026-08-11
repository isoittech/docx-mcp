# ADR 0004: 任意 OOXML／HTML／コードを公開入力にしない

- 状態: 採用

任意 markup や code は active content、external relationship、resource exhaustion、組版破損を schema で制限できない。公開生成入力は深さ・件数・文字数を制約した `DocumentSpec` と意味 block に限定し、server 内の固定 builder だけが OOXML と field instruction を生成する。URL は plain text とし取得しない。
