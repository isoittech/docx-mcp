# セキュリティ設計

## 信頼境界

- `/mcp` は 24 文字以上の Bearer shared secret を固定時間比較する。
- user、conversation、message は reverse proxy が付与する信頼済み header からだけ読み、未展開 placeholder、空値、control character を拒否する。
- 保存 scope は専用 HMAC key で pseudonymize する。shared secret、artifact signing key、scope key は長さと相互不一致を起動時検証する。
- `/mcp` は本番で外部公開しない。MCP container は internal network に置き、artifact GET/HEAD と readiness だけを別 proxy が公開する。

## 入力と parser

`file_id` は ASCII の不透明 ID だけを許す。upload root 下の通常 file を descriptor 情報と実 path の双方で確認し、symlink、hardlink、境界外 path、複数一致を拒否する。検査前に job directory へ一度コピーし、その SHA-256 を固定する。

初期 hard ceiling は 30 MiB、ZIP 5,000 entry、展開後 300 MiB、圧縮率 250、1,000 block、200,000文字、10,000 table cell、40画像、50明示 page break、50 render page である。単一 XML part、relationship、XML depth、属性数、画像寸法・画素にも上限を設ける。request body は 2 MiB、JSON depth は 32 を上限とする。

ZIP traversal、absolute entry、正規化重複、DTD/XXE、暗号化、macro、ActiveX、OLE、embedded package、altChunk、禁止 field、禁止 external relationship、未対応 media は Open XML SDK と LibreOffice の前に拒否する。許可する external relationship は正規な passive HTTP(S)/mailto hyperlink だけで、サーバーは取得しない。

## child process

LibreOffice と Poppler は検査済みの明示 path／page range だけを `ProcessStartInfo.ArgumentList` へ渡し、shell を使わない。job ごとの一時 LibreOffice profile、timeout、cancel 時の process tree kill、PDF page/size と PNG count/size 上限を適用する。

## artifact とログ

artifact token は version、job ID、artifact ID、正規化 filename、expiry、disposition を HMAC-SHA256 へ束縛し、固定時間比較する。GET/HEAD 以外を拒否し、`Cache-Control: no-store`、安全な `Content-Disposition`、`X-Content-Type-Options: nosniff` を付ける。

ログへ本文、file 内容、元 filename、secret、署名 URL、生の個人 ID、メールアドレスを出さない。構造化 log は pseudonymous scope、job ID、operation、error code、size/count だけを扱う。

## 脅威モデル要約

| 脅威 | 主な対策 |
|---|---|
| 他利用者の job／file／artifact 参照 | keyed scope、全 repository lookup の ownership 再検証、not-found 応答 |
| path traversal／link race | opaque ID、root containment、regular/link count 検査、snapshot copy、SHA束縛 |
| ZIP/XML/image bomb | envelope と semantic ceiling、DTD禁止、header parser、LibreOffice前guard |
| active content 実行／外部取得 | macro等拒否、external allowlist、no-egress、URLはplain text |
| stale target による誤編集 | analysis/source SHA/target/scope の多重束縛、編集後snapshot更新 |
| shell injection | path非公開、ArgumentList、固定 executable／option、shell不使用 |
| disk／queue枯渇 | bounded queue、並列数、TTL、利用者／全体quota、LRU、retention worker |
| capability URL 改ざん | versioned HMAC、短いTTL、filename/disposition束縛、fixed-time compare |
