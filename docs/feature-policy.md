# Word 機能 × 操作ポリシー

`preserve` は対象と交差しない既存 package payload を保持する意味である。`sanitize` は新規生成 template の allowlist 継承時だけ行い、既存文書の populate では黙って除去しない。

| 機能 | analyze/render | replace/apply | populate | new-generation template |
|---|---|---|---|---|
| macro、ActiveX、OLE、embedded package、altChunk | reject | reject | reject | reject |
| 禁止／未知 field、field-form hyperlink | reject | reject | reject | reject |
| 許可済み passive HTTP(S)/mailto hyperlink | allow/preserve・取得禁止 | target 非交差のみ preserve | target 非交差のみ preserve | sanitize（生成せず plain text） |
| PAGE/NUMPAGES/SECTION/SECTIONPAGES/TOC/REF/PAGEREF/SEQ/STYLEREF/DATE/TIME | allow/preserve | 境界交差を reject | 単純 SDT 外なら preserve | allowlist builder のみ |
| comment | allow/preserve・検出 | target 交差を reject | reject | sanitize |
| tracked changes | allow/preserve・検出 | target 交差を reject | reject | sanitize |
| footnote/endnote | allow/preserve・story解析 | 初期版は reject | reject | sanitize |
| equation | allow/preserve・検出 | target 交差を reject | reject | sanitize |
| CITATION／BIBLIOGRAPHY field | reject | reject | reject | reject |
| simple inline/block/cell SDT | allow | 境界交差を reject | allow | sanitize |
| locked/data-bound/nested/repeating SDT | allow・unsupported表示 | reject | reject | sanitize |
| text box、chart、SmartArt | allow/preserve・検出 | reject | reject | sanitize |
| custom XML、hidden text | allow/preserve・検出 | reject | reject | sanitize |
| document protection | allow・通知 | reject | reject | sanitize |
| digital signature | allow・通知 | reject | reject | sanitize |
| embedded PNG/JPEG | allow（厳格検査） | preserve | preserve | allowlist継承またはopaque image入力 |
| SVG/TIFF/GIF/WebP/WMF/EMF、linked image | reject | reject | reject | reject |

未知機能は operation ごとに fail closed とし、`preserve` と推測しない。変更履歴の新規作成、承認、拒否は行わない。
