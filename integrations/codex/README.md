# ローカル Codex 統合

## 境界

Codex からは MCP 本体を直接公開せず、Docker の internal network にある `word-mcp:8080` へ loopback proxy から接続します。proxy の host port は必ず `127.0.0.1` へ bind し、LAN の `0.0.0.0` へ公開しません。

proxy は次を行います。

- `/mcp` で Bearer header の存在を要求し、実 secret 検証は word-mcp に委ねる。
- caller が送った identity header を信用せず、local 用の固定 user／conversation／message header で上書きする。
- artifact GET／HEAD と readiness だけを同じ loopback 入口へ中継する。
- access log を無効にし、署名付き URL と secret を残さない。

この例は単一の local 論理会話 scope を意図しています。複数利用者や本番認可には使わず、LibreChat 統合を使用してください。本番 artifact proxy と local MCP proxy を兼用しません。

## 起動

`.env` に独立した secret／key と absolute path を設定し、次を実行します。

```bash
docker compose up -d word-mcp word-codex-proxy
```

compose の port mapping が次であることを確認します。

```yaml
ports:
  - "127.0.0.1:${WORD_MCP_PORT:-18081}:8080"
```

`docker compose ps` の published address が `127.0.0.1` でない場合は使用を止めます。

## Codex 設定

`integrations/codex/config.fragment.toml` を `~/.codex/config.toml` へマージします。Codex の公式設定では Streamable HTTP server に `url`、Bearer token の環境変数名に `bearer_token_env_var` を使います。`tool_timeout_sec` は `word_wait_for_job` の最大 50 秒より長くします。

CLI で登録する場合の同等例です。

```bash
codex mcp add word \
  --url http://127.0.0.1:18081/mcp \
  --bearer-token-env-var WORD_MCP_SHARED_SECRET
```

`WORD_MCP_SHARED_SECRET` の実値は config に書かず、Codex を起動する環境へ secret manager 等から注入します。process list、shell history、dotfile、Git に実値を残しません。

Codex の MCP 設定形式は更新され得るため、導入時は [Codex の MCP 設定](https://learn.chatgpt.com/docs/extend/mcp) と `codex mcp --help` を確認してください。

## 確認

1. `codex mcp list --json` で `word` が有効であることを確認する。
2. 無認証 request が 401、誤った secret が backend で拒否されることを確認する。
3. Streamable HTTP initialize／discover と `tools/list` が成功し、全 16 tool が見えることを確認する。
4. 代表的な start → add → finish → wait → 全 preview → artifact download を `/mcp` 経由で実行する。
5. proxy の container port が host の `127.0.0.1` にだけ publish され、word-mcp 自体には host port がないことを確認する。

固定 local scope のため、別会話の ownership isolation を検証する用途には LibreChat protocol E2E を使います。
