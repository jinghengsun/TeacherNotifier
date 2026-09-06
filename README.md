# 老师消息通知（TeacherNotifier）

ClassIsland 插件：通过 [NapCat](https://napneko.github.io/) 接收老师的 QQ 消息，并在 ClassIsland 上以提醒方式通知。适用于教室电脑挂 ClassIsland、机器人小号接收老师通知的场景。

## 功能

- **双连接模式**：HTTP POST 上报 / 反向 WebSocket，设置界面一键切换，改完自动重载监听（无需重启 ClassIsland）。
- **Token 鉴权**：校验 NapCat 上报请求，防止本机其他程序伪造消息。
  - HTTP 模式校验 `x-signature`（NapCat 的 `sha1(token+body)` 签名）；
  - WebSocket 模式校验 `Authorization: Bearer <token>`。
- **白名单过滤**：老师 QQ 白名单、群号白名单，均可留空表示不限制。
- **备注显示**：通知标题 `{name}` 优先显示机器人小号给老师留的备注名。
  1. 手动备注映射（最可靠，不依赖外部 API）；
  2. 自动查询 NapCat `get_stranger_info` API；
  3. 回退到群名片 → 昵称 → QQ 号。
- **自定义通知**：标题模板、正文滚动重复次数（速度）、显示时长、语音朗读开关与模板。

## 安装

1. 将 `.cipx` 包在 ClassIsland 中通过「应用设置 → 插件 → 从本地安装」导入，或直接把插件文件夹放到 ClassIsland 的 `data/Plugins/<插件 id>/` 目录。
2. 打开 ClassIsland【应用设置】→【老师消息通知】，按需配置。

## NapCat 侧配置

插件始终是**服务器**，NapCat 始终是**客户端**。在 NapCat WebUI 的「网络配置」中二选一：

| 模式 | NapCat 选什么 | 填的地址 |
| --- | --- | --- |
| HTTP | HTTP 客户端（上报） | `http://127.0.0.1:<端口>/` |
| WebSocket | 反向 WebSocket 客户端 | `ws://127.0.0.1:<端口>/` |

- **消息格式**选 `array`（JSON 数组 / OneBot 11 标准），`string`（CQ 码）也能用但 `array` 更精确。
- **上报自身消息**建议关闭（插件已内置 `忽略机器人自己发送的消息` 过滤）。
- 若 NapCat 侧配置了 access token，务必与插件设置界面中的「访问令牌」保持一致，否则会被 401 拒绝。

## 显示备注（`{name}` 优先用备注）

消息事件本身不携带备注，插件通过以下优先级确定 `{name}`：

1. **手动备注映射**（设置界面「手动备注映射」，格式 `QQ号=备注名`，逗号分隔），最可靠；
2. **自动查 NapCat API**：需在 NapCat 开启「HTTP 服务器」，并在插件设置界面填好「NapCat HTTP API 地址」和「NapCat HTTP API Token」；
3. 回退到群名片 → 昵称 → QQ 号。

## 设置项速查

| 设置 | 说明 |
| --- | --- |
| 连接模式 | HTTP POST 上报 / 反向 WebSocket |
| 监听端口 | 插件监听的端口 |
| 访问令牌 | 校验 NapCat 上报用的 token（留空不校验） |
| NapCat HTTP API 地址 | 查备注用的 NapCat HTTP 服务器地址 |
| NapCat HTTP API Token | 上述 HTTP 服务器的 access token |
| 手动备注映射 | `QQ号=备注名`，逗号分隔 |
| 老师 QQ 白名单 / 群号白名单 | 留空不限制 |
| 标题模板 / 语音模板 | 支持下方占位符 |
| 标题显示时长 / 正文显示时长 | 秒 |
| 正文滚动重复次数 | 越大滚动越快 |
| 启用语音朗读 | 开关 |

## 占位符

标题 / 语音模板支持以下占位符：

| 占位符 | 含义 |
| --- | --- |
| `{name}` | 备注（优先）→ 群名片 → 昵称 → QQ 号 |
| `{remark}` | 备注名（可能为空） |
| `{qq}` | 发送者 QQ 号 |
| `{group}` | 群号（私聊为空） |
| `{message}` | 消息内容 |

## 构建

- 需要 .NET 8 SDK。
- `dotnet build -c Release` 编译。
- `dotnet build -c Release -p:CreateCipx=true` 一键打包 `.cipx`（需 PowerShell 7 的 `pwsh` 用于生成 MD5 校验）。

## 许可证

MIT
