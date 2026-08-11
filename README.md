# MCEIndex MCP

面向 [有意义中国经济指数（mceindex.com）](https://mceindex.com/) 的本地索引 MCP 服务。它将公开页面中的指标卡、统计期、解释文本和 Plotly 图表写入 SQLite，并通过 stdio 提供结构化查询。

MCEIndex MCP 是独立项目，抓取范围限于公开页面并遵守站点访问控制。查询结果包含 `sourceUrl` 和 `fetchedAt`，便于核对来源与抓取时间。

## 功能

- 读取月度总览和单项经济指标
- 按栏目读取正文、表格和图表
- 使用 SQLite FTS5 trigram 搜索中文内容与指标代码
- 将抓取结果保存在本地，支持离线查询
- 通过 MCP JSON Schema 返回结构化数据

## 快速开始

### 1. 准备运行环境

| 依赖 | 要求 |
|---|---|
| [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) | 构建和安装 |
| [Node.js 24 LTS](https://nodejs.org/) | `node` 或 `node.exe` 位于 `PATH` |
| SQLite 3 | 提供系统 SQLite 动态库 |
| Chrome 或 Chromium | 渲染 Streamlit 页面 |

Windows 10+ 提供 `winsqlite3.dll`，macOS 提供 `libsqlite3.dylib`。Debian 和 Ubuntu 可安装 `libsqlite3-0`：

```bash
sudo apt-get update
sudo apt-get install -y libsqlite3-0 chromium
```

检查 .NET 和 Node.js：

```text
dotnet --version
node --version
```

### 2. 构建

```text
git clone https://github.com/Star-Trails/mceindex-mcp.git
cd mceindex-mcp
dotnet restore
dotnet pack src/MceIndex.Mcp/MceIndex.Mcp.csproj -c Release -o artifacts
```

打包产物位于 `artifacts/MCEIndex.Mcp.3.6.0.nupkg`。

### 3. 安装

将 `<TOOL_PATH>` 替换为工具安装目录：

```text
dotnet tool install --tool-path "<TOOL_PATH>" --add-source ./artifacts MCEIndex.Mcp --version 3.6.0
dotnet tool list --tool-path "<TOOL_PATH>"
```

| 平台 | `<TOOL_PATH>` 示例 | MCP `command` |
|---|---|---|
| Windows | `C:\Users\USER\AppData\Local\mceindex-mcp` | `C:\Users\USER\AppData\Local\mceindex-mcp\mceindex-mcp.exe` |
| Linux | `/home/USER/.local/share/mceindex-mcp` | `/home/USER/.local/share/mceindex-mcp/mceindex-mcp` |
| macOS | `/Users/USER/.local/share/mceindex-mcp` | `/Users/USER/.local/share/mceindex-mcp/mceindex-mcp` |

安装成功后，`dotnet tool list` 显示 `mceindex.mcp 3.6.0`。

### 更新与卸载

```text
dotnet tool update --tool-path "<TOOL_PATH>" --add-source ./artifacts MCEIndex.Mcp --version 3.6.0 --no-cache
dotnet tool uninstall --tool-path "<TOOL_PATH>" MCEIndex.Mcp
```

## 配置 MCP 客户端

`command` 使用安装后的绝对路径：

```json
{
  "mcpServers": {
    "mceindex": {
      "command": "ABSOLUTE_PATH_TO_MCEINDEX_MCP"
    }
  }
}
```

Codex 使用以下配置：

```toml
[mcp_servers.mceindex]
command = "ABSOLUTE_PATH_TO_MCEINDEX_MCP"
startup_timeout_sec = 60
tool_timeout_sec = 180
```

服务从客户端的 `PATH` 查找 Node.js，并从标准安装目录查找 Chrome 或 Chromium。自定义路径使用以下环境变量：

```text
PLAYWRIGHT_NODEJS_PATH=<Node.js 可执行文件绝对路径>
MCEINDEX_BROWSER_EXECUTABLE=<Chrome 或 Chromium 可执行文件绝对路径>
```

## 工具

| 工具 | 用途 | 主要参数 |
|---|---|---|
| `get_latest` | 返回月度总览的六组最新读数 | 无 |
| `get_indicator` | 按代码或中文名称读取指标 | `indicator` |
| `list_pages` | 列出已索引栏目和刷新状态 | 无 |
| `get_page` | 读取栏目摘要、正文、表格或图表 | `page`、`view`、`offset`、`limit` |
| `search_index` | 搜索中文内容或指标代码 | `query`、`page`、`kind`、`mode`、`offset`、`limit` |
| `refresh_index` | 刷新全部栏目 | `force=false` |

`get_latest` 的读数包含网站值、统计期、来源和核验信息。`get_page` 与 `search_index` 使用 `offset` 和 `limit` 分页。

## 数据更新

- 当前 MCP 进程的首个查询会刷新索引，并在刷新完成后读取 SQLite。
- 后续查询直接读取本地索引。
- 首次刷新失败且本地已有数据时，查询返回最近一次成功结果；空库返回 `INDEX_EMPTY`。
- `refresh_index(force=false)` 遵守 24 小时刷新间隔。
- `refresh_index(force=true)` 忽略 24 小时间隔，仍执行 60 秒硬冷却。
- 单页抓取失败时保留该页最近一次成功内容。

`refresh_index` 返回 `completed`、`partial` 或 `skipped`。

## 环境变量

| 环境变量 | 默认值 | 用途 |
|---|---|---|
| `MCEINDEX_BASE_URL` | `https://mceindex.com/` | 数据源地址；HTTP 仅用于本机测试 |
| `MCEINDEX_DB_PATH` | 平台缓存目录下的 `mceindex_mcp/mceindex.db` | SQLite 索引路径 |
| `MCEINDEX_BROWSER_EXECUTABLE` | 自动探测 | Chrome 或 Chromium 绝对路径 |
| `PLAYWRIGHT_NODEJS_PATH` | 从 `PATH` 探测 | Node.js 绝对路径 |
| `MCEINDEX_BROWSER_USER_AGENT` | 内置 Chrome UA | 浏览器 User-Agent |
| `MCEINDEX_BROWSER_PROFILE` | 空 | 持久化浏览器 profile |
| `MCEINDEX_CF_CLEARANCE` | 空 | 合法取得的 `cf_clearance` Cookie |
| `MCEINDEX_HEADLESS` | `true` | 无头模式开关 |
| `MCEINDEX_TIMEOUT_MS` | `45000` | 单页超时 |
| `MCEINDEX_SETTLE_MS` | `1200` | DOM 稳定等待时间 |
| `MCEINDEX_REFRESH_INTERVAL_MS` | `86400000` | 普通刷新间隔 |
| `MCEINDEX_CRAWL_DELAY_MS` | `3000` | 页面请求间隔 |
| `MCEINDEX_CRAWL_CONCURRENCY` | `1` | 抓取并发，范围 1–4 |
| `MCEINDEX_MAX_PAGES` | `20` | 单次刷新页面上限，范围 5–100 |

Cloudflare 验证失败返回 `ACCESS_CHALLENGE`。其他错误码包括 `BROWSER_NOT_FOUND`、`LOAD_TIMEOUT`、`PAGE_NOT_FOUND`、`INDICATOR_NOT_FOUND`、`INDEX_EMPTY`、`INVALID_CONFIGURATION`、`EXTRACTION_FAILED`、`DATABASE_ERROR` 和 `INTERNAL_ERROR`。

## 开发

直接运行：

```bash
dotnet run --project src/MceIndex.Mcp/MceIndex.Mcp.csproj
```

构建与测试：

```bash
dotnet restore
dotnet build MceIndex.slnx --no-restore
dotnet test MceIndex.slnx --no-restore
dotnet pack src/MceIndex.Mcp/MceIndex.Mcp.csproj -c Release --no-restore -o artifacts
```

进程通过 stdio 传输 MCP JSON-RPC。stdout 专用于协议，stderr 输出运行日志。设置 `MCEINDEX_TEST_BROWSER` 可运行浏览器集成测试。
