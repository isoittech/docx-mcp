# Template ガイド

## 目的

word-mcp は template を 2 つの異なる workflow で扱います。

| workflow | 本文 | 主な用途 |
|---|---|---|
| `word_populate_template` | 既存本文を意図的に保持し、指定した単純 content control の内容だけを置換 | 定型様式への値流し込み |
| 宣言型新規生成 | template の本文を捨て、許可した style／theme／numbering／section／header／footer／page setup だけを継承 | 組織デザインを使う新規文書 |

既存本文を保持する populate と、本文・個人 metadata を除去する新規生成を混同しないでください。どちらも macro-free `.docx`／`.dotx` だけを受け付け、出力は `.docx` です。

## Repository に含めないもの

- 実運用 template、会社名、ロゴ、顧客資料、固有文言
- 個人名、メールアドレス、作成者、最終更新者、custom property
- internal URL、credential、token、署名鍵
- template から生成した DOCX／PDF／PNG

テスト fixture はテストコードから synthetic に生成します。実 template は repository と container image の外にある private directory へ置き、read-only mount します。

## File と template ID

- extension は `.docx` または `.dotx`。DOCM／DOTM は使用できません。
- filename は `<template-id>.docx` または `<template-id>.dotx` にします。
- template ID は ASCII 英数字、hyphen、underscoreのみとし、1〜128 文字にします。
- ID の大文字・小文字だけが異なる file を併置しません。
- symlink、hardlink、subdirectory、glob、relative path を template ID の代わりに使いません。
- ZIP、OPC、XML、relationship、field、media、安全性上限を起動時または利用前に検査します。

実装 schema の上限がこのガイドより厳しい場合は、schema を正本とします。上限を緩和する場合は package guard の hard ceiling とテストを先に更新してください。

## 管理者 template の配置

ホスト上の private directory を用意し、word-mcp container の template root へ read-only mount します。

```dotenv
WORD_MCP_TEMPLATES_PATH=/absolute/path/to/word-templates
WORD_MCP_DEFAULT_TEMPLATE_ID=organization-default
```

ここで示す ID は説明用です。実 organization 名や内部 path を repository へ記録しません。

`WORD_MCP_DEFAULT_TEMPLATE_ID` を設定した場合、サーバーは起動時に file の存在、通常 file であること、安全性、Open XML 整合性、template 構造を検査します。不正・欠落・曖昧な一致があれば readiness を fail closed にします。検査結果は内容 SHA-256 単位で cache し、filename だけで再利用しません。

## Content control の設計

`word_populate_template` の第一選択は Word の Developer tab で作成する content control（`w:sdt`）です。

### Tag を正本にする

- `w:sdtPr/w:tag` を machine-readable な一意 ID にします。
- alias は人間向け表示にだけ使い、同定の正本にしません。
- tag は文書内で一意にします。重複 tag を順序や見た目から推測して更新しません。
- tag は短い ASCII identifier を推奨します。例: `report_title`、`summary`、`owner_name`。
- 顧客名、個人情報、secret を tag に含めません。

初期版で populate できるのは、単純な inline、block、table-cell control です。`sdtPr` を保持し、`sdtContent` だけを意味要素で差し替えます。

### 使用しない control

次の control は推測で更新せず、構造化エラーとして拒否します。

- locked または文書保護下にある control
- data-bound control と custom XML mapping
- nested／repeating section
- picture、date picker、building block 等の未対応 control
- 同じ tag が複数ある control
- field、revision、bookmark 等の安全境界と交差する control

bookmark は、利用者が互換 fallback を明示した場合だけ使います。bookmark 名も一意にし、hidden／reserved 名、交差・不整合 range、複雑 content は使いません。

## Populate template の安全規則

populate は既存本文を配布物へ残すため、新規生成より厳格です。次を含む template は黙って sanitize せず拒否します。

- macro、ActiveX、OLE、embedded package、`altChunk`、暗号化
- external template、linked image、外部データ、禁止 external relationship
- 禁止／未知 field、field-form hyperlink、壊れた／未終端 field
- comments、tracked revisions、hidden text、custom XML
- document protection、digital signature
- unsupported passive content、未対応 media
- locked、data-bound、nested、repeating SDT

許可された passive HTTP(S)／mailto hyperlink が control の外側にある場合も、server はリンク先を取得しません。credential、control character、過大 URL、ほかの scheme は拒否します。

入力値には plain text と、tool schema が許す制約済み semantic content だけを使います。任意 OOXML、HTML、Markdown、field instruction、URL fetch、base64、local path を値として実行しません。

DOTX を populate した出力は main content type と document type を DOCX に変換します。元 template file を上書きしません。

## 宣言型新規生成での継承

新規生成で template を選んだ場合、template の main story を成果物へ残しません。次の allowlist だけを正本または補完値として継承します。

- styles と theme
- numbering 定義
- section properties、page size、orientation、margin、column
- first／even／default header・footer とその link policy
- 継承対象に必要な検査済み PNG／JPEG media

次は除去します。

- sample 本文、placeholder 値、orphaned part
- comments、削除／moveFrom の revision 内容、hidden text、custom XML
- attached template、thumbnail、digital signature、protection
- author、last modified by、会社名等を含む core／custom properties
- 許可されていない external relationship と media

page setup は template の最後の section property から allowlist 項目だけを使います。first／even／default header・footer は template section を後方から探索して有効な参照を解決し、生成した全 section へ再設定します。継承する header／footer 内の insertion／moveTo は revision wrapper を外して表示内容だけを残し、削除／moveFrom、comment marker、hidden text は除去します。参照画像は検査済み embedded PNG／JPEG だけを新しい relationship ID でコピーします。

明示 `DocumentSpec` の design 値は template の正本を破壊しません。既存定義を変更せず、未指定値だけを補完し、必要な style／numbering を ID 衝突なく追加します。`numId`、`abstractNumId`、style ID、bookmark ID/name、relationship ID、`wp:docPr id` 等は一元 allocator で採番します。

Git commit author や運用者のメールアドレスを DOCX author へ自動転記しません。

## 新規文書での template 選択

`word_start_document` の template source は次から選び、start 成功後は finish まで固定します。

| 値 | 意味 |
|---|---|
| `default` | 管理者 scope の既定 template |
| `none` | template なし |
| `latest` | 同じ user scope の最新 upload DOCX／DOTX |
| 明示 `file_id` | 同じ user scope の指定 upload |

deployment template は管理者 scope、upload `latest` は user scope、draft は user+conversation scope です。`latest` をグローバル file として扱いません。明示 `file_id` を常に優先し、start 後に source を差し替えません。

## Template 作成手順

1. Microsoft Word のサポート中 version で新しい macro-free DOCX／DOTX を作る。
2. page setup、section、style、theme、numbering、header／footer を定義する。
3. populate 用なら Developer tab で単純 content control を追加し、一意な tag を設定する。
4. control の lock、data binding、repeating、nesting が無効であることを確認する。
5. sample 本文と metadata に実個人情報・顧客情報がない synthetic 値だけを使う。
6. Document Inspector で comment、revision、hidden text、custom XML、document property、embedded object を確認する。
7. external template、linked image、外部データ接続、macro、署名、保護がないことを確認する。
8. 必要画像は PNG／JPEG の embedded image に限定し、alt text、寸法、解像度を設定する。
9. template root へ配置し、readiness と `word_analyze`／`word_populate_template` の代表 test を実行する。
10. 出力 DOCX を OpenXmlValidator、LibreOffice、Poppler、Microsoft Word 手動 checklist へ通す。

## Populate input の例

公開 JSON schema を正本とし、次は概念例として扱ってください。tool や field 名の大小文字を推測せず、実 `tools/list` の snake_case schema を確認します。

```json
{
  "source_file_id": "latest",
  "fields": [
    {
      "tag": "report_title",
      "text": "月次報告"
    },
    {
      "tag": "summary",
      "text": "synthetic な確認用本文"
    }
  ]
}
```

同じ tag を複数指定したり、alias と tag を混在させたりしません。実個人情報や秘密値を test fixture に使いません。

## Release checklist

- [ ] `.docx`／`.dotx` であり、macro-enabled content type がない。
- [ ] template ID と filename が一致し、ASCII 英数字／hyphen／underscore、1〜128 文字である。
- [ ] repository／image 外の private directory から read-only mount される。
- [ ] content control tag が一意で、alias は同定に使っていない。
- [ ] locked、data-bound、nested、repeating、unsupported control がない。
- [ ] comment、revision、hidden text、custom XML、protection、signature がない。
- [ ] external template、linked image、外部データ、禁止 field がない。
- [ ] embedded media は検査可能な PNG／JPEG で、alt text がある。
- [ ] sample 本文、author、last modified by、個人／顧客 metadata が成果物へ漏れない。
- [ ] DOTX→DOCX の content type と document type が正しい。
- [ ] 新規生成で template 本文が残らず、populate では指定 control 外の payload が保持される。
- [ ] Word／LibreOffice の双方で修復警告なく開き、全ページを確認した。
