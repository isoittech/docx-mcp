# Microsoft Word 手動 release checklist

## 位置づけ

この checklist は OpenXmlValidator、LibreOffice UNO/PDF、Poppler PNG の自動 gate を置き換えません。すべての自動 gate が成功した代表 DOCX を、Microsoft Word 固有の互換性と再フローの観点で確認する最終 gate です。

Microsoft Word を利用できない場合は成功扱いにせず、`NOT_RUN` と理由、未確認範囲を release 記録へ残します。本番で Microsoft Word 互換性が要件なら `NOT_RUN` のまま release しません。

## 安全な確認環境

- 実顧客資料ではなく、表紙、TOC、見出し、本文、実 list、表、callout、PNG/JPEG、caption、section break、header、footer、PAGE／NUMPAGES を含む synthetic な日本語代表文書を使う。
- 検査対象 DOCX の SHA-256 と生成 job ID を記録し、元の配布成果物を直接上書きしない。
- Microsoft Word で field 更新や保存を行う場合は検査用 copy を使う。
- macro、ActiveX、external template、linked image、埋め込み package を含む入力を開いて安全性確認する用途には使わない。それらは package guard で先に拒否する。
- 会社名、個人名、メールアドレス、credential、internal URL を screenshot、記録、fixture に残さない。

## 実施記録

| 項目 | 記録 |
|---|---|
| 実施日 |  |
| 確認者 |  |
| release／commit |  |
| job ID |  |
| DOCX SHA-256 |  |
| workflow | 新規生成／置換／atomic edit／populate／insert／refine |
| template ID／SHA-256 | 未使用の場合は `none` |
| OS と version |  |
| Microsoft Word edition／version／build／bitness |  |
| locale と UI language |  |
| printer／PDF driver |  |
| 使用 font と install 状態 |  |
| LibreOffice preview page count |  |
| Microsoft Word page count |  |
| 結果 | `PASS`／`FAIL`／`NOT_RUN` |

Word と LibreOffice の page count が違うこと自体は即失敗ではありません。差分理由と、各環境で内容欠落・重なり・不正 field がないことを確認します。

## 1. 自動 gate の事前確認

- [ ] source／artifact の package guard が成功している。
- [ ] 新規生成は `OpenXmlValidator` error 0 件、既存編集は新規 error が増えていない。
- [ ] LibreOffice UNO の index／field 更新検査が成功している。
- [ ] DOCX→PDF と `pdfinfo` page／size 検査が成功している。
- [ ] Poppler が検査済み範囲の全ページ PNG を生成している。
- [ ] `word_get_preview_images` で 1〜4 ページずつ全ページを確認している。
- [ ] 代表 workflow の protocol-level MCP E2E が成功している。

どれかが未実施または失敗なら、この手動確認を「自動 gate の代替」として続行せず release を止めます。

## 2. Open／repair／保護表示

- [ ] Word で開いたとき「内容に問題が見つかりました」「修復しますか」等の警告が出ない。
- [ ] unreadable content、corrupt relationship、missing image、font error が表示されない。
- [ ] Protected View の表示有無と理由を記録し、警告を無条件に無効化していない。
- [ ] macro／ActiveX／external content／data connection の有効化を求められない。
- [ ] document protection や digital signature の破損警告がない。
- [ ] Word の Accessibility／Document Inspector を実行する場合も検査用 copy で行う。

repair や content recovery が 1 回でも発生した成果物は `FAIL` です。修復後に見た目が正常でも配布しません。

## 3. 文書全体と page setup

- [ ] 表紙、本文、最終ページが期待する順序で存在し、sample／template 本文が漏れていない。
- [ ] A4／Letter、portrait／landscape、margin、column が仕様どおりである。
- [ ] logical section と Word section break の位置が正しい。
- [ ] section ごとの page size／orientation 切替後に不要な空白ページがない。
- [ ] first／even／default header・footer と「前と同じ」の継承が仕様どおりである。
- [ ] header／footer が本文、表、画像と重ならない。
- [ ] 表紙の header／footer／page number 非表示方針が正しい。

## 4. Field、TOC、page number

- [ ] 検査用 copy で全 field を更新し、エラー表示や外部取得 prompt が出ない。
- [ ] 配布DOCXを開いたとき「他のファイルを参照するフィールド」の更新確認ダイアログが表示されない。
- [ ] TOC に見出しが正しい階層・順序で入り、page number と hyperlink が妥当である。
- [ ] `PAGE`、`NUMPAGES`、`SECTION` 等の表示が文書全体で一貫する。
- [ ] field code が本文へ露出せず、未更新の placeholder text が残らない。
- [ ] TOC 更新後に改ページや最終 page count が変わった場合、全ページを最初から再確認した。
- [ ] 新規生成の配布DOCXにはdirty/update要求と未更新placeholderがなく、既存文書の限定編集にはpreview copyの保存差分が逆流していない。

新規生成ではfield更新後のcopyをpackage guardとOpenXmlValidatorへ再度通し、最終bytesからanalysis snapshotを作り直します。既存文書の限定編集では検査用copyを配布成果物へ置き換えません。

## 5. Typography と日本語組版

- [ ] Latin／East Asia font が意図した font で表示され、想定外の fallback や文字化けがない。
- [ ] `w:lang` の日本語設定と校正言語が妥当である。
- [ ] 見出し階層、本文、caption、code の named style が一貫する。
- [ ] 文字切れ、重なり、不自然な禁則、句読点の行頭、過剰な単語分割がない。
- [ ] line spacing、paragraph spacing、indent、tab stop が読みやすい。
- [ ] heading の `keepNext`、paragraph の `keepLines`、widow／orphan 制御が働き、孤立見出しがない。
- [ ] bold／italic／code 等の semantic run と、run 境界をまたぐ置換後の書式が正しい。
- [ ] 先頭／末尾空白を意図した text が失われていない。

## 6. List と numbering

- [ ] 箇条書き・自動採番が文字 `・`／`1.` の手入力ではなく Word native list として編集できる。
- [ ] ordered／unordered list と最大 4 階層の indent、番号形式が仕様どおりである。
- [ ] list の再開／継続が正しく、template 既存 numbering と ID 衝突していない。
- [ ] insert／refine 後も後続 list の番号が予期せず変化していない。
- [ ] copy／paste や 1 項目編集後に list structure が破損しない。

## 7. Table

- [ ] 全 table が page width 内に収まり、列幅と table grid が仕様どおりである。
- [ ] cell text が切れず、重なりや過剰な折返しがない。
- [ ] header row が page 跨ぎで繰り返される。
- [ ] row split 可否、cell margin、vertical alignment が妥当である。
- [ ] merged cell を含む対応外 table が誤って編集されていない。
- [ ] caption／description と本文からの参照が正しい。
- [ ] table-cell content control を populate した場合、cell structure と `sdtPr` が保持される。

## 8. Image と caption

- [ ] 画像は embedded PNG／JPEG で、linked image や未対応形式ではない。
- [ ] 縦横比、cropping、解像感、配置、text wrapping が妥当である。
- [ ] 画像が page／margin／header・footer と衝突しない。
- [ ] alt text が空でなく、内容を簡潔に説明する。
- [ ] caption の順序、style、対象画像との対応が正しい。
- [ ] Word の再保存後も image relationship と表示が維持される。

## 9. Content control／template

- [ ] populate 対象の tag が一意で、指定した control だけが更新されている。
- [ ] alias、tag、bookmark fallback を混同していない。
- [ ] `sdtPr` が保持され、`sdtContent` だけが意味要素で差し替わっている。
- [ ] inline、block、table-cell control の編集可能性が保たれている。
- [ ] locked、data-bound、nested、repeating control が成果物へ紛れ込んでいない。
- [ ] `.dotx` 入力の出力が `.docx` として開き、new document template と誤認されない。
- [ ] 新規生成では template の sample 本文、comment、revision、hidden text、custom XML、個人 metadata が残らない。
- [ ] author／last modified by に Git user や運用者メールが自動転記されていない。

## 10. 編集 workflow の回帰

- [ ] 置換対象の期待元文字列・期待一致件数が満たされ、余分な箇所が変わっていない。
- [ ] field、revision、SDT、bookmark、tab、line break、story 境界をまたぐ置換が行われていない。
- [ ] atomic edit は全操作が反映され、部分成功 artifact が配布されていない。
- [ ] 編集後に新しい `output_analysis_id` が発行され、古い target を再利用していない。
- [ ] header、footer、footnote、endnote 等の非対象 story が意図せず変わっていない。
- [ ] insert／refine の後続 reflow を含め、影響ページだけでなく全ページを確認した。

## 11. Save／reopen／編集可能性

検査用 copy で実施します。

- [ ] Word で保存し、閉じて再度開いても repair 警告がない。
- [ ] paragraph、heading、list、table、image、field が画像化されず native object として編集できる。
- [ ] 短い本文編集、list 項目追加、table cell 編集で document structure が壊れない。
- [ ] save 後の page count、TOC、header／footer、numbering の変化を記録した。
- [ ] save 後の copy を再配布する場合は別 artifact として package guard と全自動 gate を再実行する。

## 12. LibreOffice preview との差分記録

次を page 単位で比較し、差があれば「Word が正しい」と決めつけず、内容欠落や仕様違反がないか確認します。

- page count と section 開始 page
- font fallback、改行、widow／orphan、孤立見出し
- TOC、PAGE／NUMPAGES、header／footer
- table 幅、row split、header row repeat
- image size、cropping、caption
- 不要な空白 page と最終 page balance

記録例:

| page／section | 差分 | 許容理由または修正 | 結果 |
|---|---|---|---|
|  |  |  | `PASS`／`FAIL` |

## 合否基準

`PASS` にできるのは次をすべて満たす場合だけです。

- repair、active content、外部取得、内容欠落、重なり、文字切れがない。
- field／TOC／page number、section、header／footer、list、table、image が意図どおりである。
- Word と LibreOffice の差が説明可能で、両方に重大な表示不良がない。
- 保存・再 open と限定的な手動編集で native Word 構造が維持される。
- 自動 gate と全ページ MCP preview 確認が先に成功している。

`FAIL` の場合は成果物を配布せず、再現手順、DOCX SHA-256、Word build、最初に問題が見える section／page、期待値、実際値を記録します。顧客データや本文全体は issue／log に貼らず、synthetic fixture で再現して修正後に全 gate をやり直します。
