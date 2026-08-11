# Third-party notices

この文書は word-mcp が直接参照する NuGet package と、標準 container 構成で実行する主要な第三者ソフトウェアの通知をまとめたものです。確認日は 2026-08-11 です。exact version は `Directory.Packages.props`、NuGet lock file、Dockerfile の base image／OS package lock を正本とします。

この一覧は word-mcp 自身の LICENSE を定めるものではありません。プロジェクト自身のライセンスはユーザー選択が未確定です。

## Direct NuGet dependencies

| Component | Pinned version | License | Upstream notice／source |
|---|---:|---|---|
| ModelContextProtocol.AspNetCore | 2.1.0 | Apache-2.0（NuGet package metadata） | [MCP C# SDK LICENSE](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/LICENSE)、[THIRD-PARTY-NOTICES](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/THIRD-PARTY-NOTICES.txt) |
| DocumentFormat.OpenXml | 3.5.1 | MIT | [Open XML SDK LICENSE](https://github.com/dotnet/Open-XML-SDK/blob/main/LICENSE)、[NOTICE](https://github.com/dotnet/Open-XML-SDK/blob/main/NOTICE) |
| Microsoft.NET.Test.Sdk | 18.8.1 | MIT | [VSTest LICENSE](https://github.com/microsoft/vstest/blob/main/LICENSE) |
| xunit.v3 | 3.2.2 | Apache-2.0。一部 adapted MSBuild code は MIT | [xUnit LICENSE](https://github.com/xunit/xunit/blob/main/LICENSE) |
| xunit.runner.visualstudio | 3.1.5 | Apache-2.0 | [xUnit Visual Studio runner License.txt](https://github.com/xunit/visualstudio.xunit/blob/main/License.txt) |

現在の runtime lock graph には次も含まれます。

| Transitive component | Locked version | License family |
|---|---:|---|
| ModelContextProtocol／ModelContextProtocol.Core | 2.1.0 | Apache-2.0 package metadata。upstream の移行前 MIT notice も保持 |
| DocumentFormat.OpenXml.Framework | 3.5.1 | MIT |
| Microsoft.Extensions.AI.Abstractions | 10.8.3 | MIT |
| System.IO.Packaging | 10.0.2 | MIT |

配布時は lock file から実際の dependency graph を再生成し、各 `.nupkg` の license expression／license file と notice を確認してください。MCP C# SDK upstream の LICENSE にある移行前 MIT code の表示と third-party notice もそのまま保持します。

## Runtime／container components

| Component | 用途 | 主な license | Authority |
|---|---|---|---|
| .NET 10／ASP.NET Core runtime | application runtime | MIT。distribution 同梱の `LICENSE.txt`／`ThirdPartyNotices.txt` が最終的に優先 | [.NET runtime LICENSE](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT) |
| LibreOffice Writer 24.2.7 | preview copy の field／index 更新と DOCX→PDF | MPL-2.0。build により Apache-2.0、LGPL、GPL、その他第三者 code を含む | [LibreOffice licenses](https://www.libreoffice.org/about-us/licenses/)、install image 内の license information |
| Poppler utilities 24.02.0 | `pdfinfo` と PDF→PNG | upstream／distribution package の GPL-2.0／GPL-3.0 系。exact package copyright を優先 | [Poppler project](https://poppler.freedesktop.org/)、container 内 `/usr/share/doc/poppler-utils/copyright` |
| nginx 1.31.3 | local MCP proxy／artifact-only proxy | 2-clause BSD-style license | [nginx LICENSE](https://github.com/nginx/nginx/blob/master/LICENSE) |
| Noto CJK fonts（image に含む場合） | 日本語 preview font | SIL Open Font License 1.1 | [Noto CJK LICENSE](https://github.com/notofonts/noto-cjk/blob/main/LICENSE) |

LibreOffice と Linux distribution package は多数の第三者 component を含み、実際の build／distribution により一覧が異なります。この repository の短い表で置き換えず、container 内の次の資料を削除しないでください。

- LibreOffice install directory の `LICENSE`／`NOTICE`／license information
- `/usr/share/doc/<package>/copyright`
- .NET runtime distribution の `LICENSE.txt` と `ThirdPartyNotices.txt`
- NuGet package に含まれる license／notice file

## Copyright notices

- Open XML SDK and related .NET components: Copyright © .NET Foundation and contributors.
- Microsoft.NET.Test.Sdk／VSTest: Copyright © Microsoft Corporation.
- xUnit.net: Copyright © .NET Foundation and contributors. xUnit の upstream LICENSE に記載された adapted component の通知も保持します。
- nginx: Copyright © 2002–2021 Igor Sysoev; Copyright © 2011–2026 Nginx, Inc.
- LibreOffice: Copyright © The Document Foundation and LibreOffice contributors。install image の version 固有通知を参照してください。
- Poppler: Copyright © Poppler／Xpdf contributors。distribution package の version 固有 copyright file を参照してください。
- Noto CJK fonts: Copyright © Google LLC and font contributors。font package に同梱された OFL notice を参照してください。

名称と商標は各権利者に帰属します。これらの表示は権利者による word-mcp の承認を意味しません。

## Redistribution checklist

- [ ] `Directory.Packages.props` と lock file の exact version が一致する。
- [ ] `dotnet list package --include-transitive` 相当の一覧を release artifact と照合する。
- [ ] NuGet、base image、apt package の脆弱性／非推奨監査を実行する。
- [ ] container image の SBOM を生成し、実 package とこの notice の差分を確認する。
- [ ] Apache-2.0、MIT、MPL、GPL、OFL 等の version 固有 license／notice file を配布物から削除していない。
- [ ] LibreOffice と Poppler を変更して再配布する場合、使用する build の source offer／source availability 等の該当 license 条件を法務と確認する。
- [ ] 新しい package、font、binary、fixture、code copy を追加した場合、出所・license・notice をこの文書へ反映する。
- [ ] project 自身の LICENSE が選択済みで、第三者 component の license と混同されていない。

ライセンス条件について疑義がある場合は、この文書だけを法的助言として扱わず、配布形態と対象 jurisdiction に応じて専門家へ確認してください。
