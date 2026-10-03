# 老师消息通知（TeacherNotifier）

ClassIsland 插件：通过 [NapCat](https://napneko.github.io/) 或 [SnowLuma](https://snowluma.github.io/) 接收老师的 QQ 消息，并在 ClassIsland 上以提醒方式通知。适用于教室电脑挂 ClassIsland、机器人小号接收老师通知的场景。

支持任意兼容 OneBot 11 的机器人框架（NapCat 与 SnowLuma 均已核对通过）。

## 功能

- **双连接模式**：HTTP POST 上报 / 反向 WebSocket，设置界面一键切换，改完自动重载监听（无需重启 ClassIsland）。
- **Token 鉴权**：校验上报请求，防止本机其他程序伪造消息。
  - HTTP 模式校验 `x-signature`（HMAC-SHA1 签名，与 NapCat、SnowLuma 的实现一致）；
  - WebSocket 模式校验 `Authorization: Bearer <token>`。
- **白名单过滤**：老师 QQ 白名单、群号白名单，均可留空表示不限制。
- **备注显示**：通知标题 `{name}` 优先显示机器人小号给老师留的备注名。
  1. 手动备注映射（最可靠，不依赖外部 API）；
  2. 好友列表备注（`get_friend_list`，备注的正确来源）；
  3. 群消息回退到群名片（`get_group_member_info`）；
  4. 陌生人信息（`get_stranger_info`）；
  5. 回退到事件自带的群名片 → 昵称 → QQ 号。
- **自定义通知**：标题模板、正文滚动重复次数（速度）、显示时长、语音朗读开关与模板。

## 安装

1. 将 `.cipx` 包在 ClassIsland 中通过「应用设置 → 插件 → 从本地安装」导入，或直接把插件文件夹放到 ClassIsland 的 `data/Plugins/<插件 id>/` 目录。
2. 打开 ClassIsland【应用设置】→【老师消息通知】，按需配置。

## 机器人框架侧配置

插件始终是**服务器**，机器人框架始终是**客户端**。在框架的网络配置中二选一：

| 模式 | 框架选什么 | 填的地址 |
| --- | --- | --- |
| HTTP | HTTP 客户端（上报）/ HTTP POST | `http://127.0.0.1:<端口>/` |
| WebSocket | 反向 WebSocket 客户端 | `ws://127.0.0.1:<端口>/` |

- **消息格式**选 `array`（JSON 数组 / OneBot 11 标准），`string`（CQ 码）也能用但 `array` 更精确。
- **上报自身消息**建议关闭。SnowLuma 会把自身消息标记为 `post_type: message_sent`，插件会自动忽略；NapCat 若上报自身消息，插件也会通过 `self_id == user_id` 过滤。
- **access token 必须两边一致**：框架侧的 access token 要和插件设置里的「访问令牌」完全相同，否则上报会被 401 拒绝。
  插件按框架的实际算法（`HMAC-SHA1(key=token, data=请求体)`）校验 `x-signature`。

### NapCat

在 NapCat WebUI 的「网络配置」中新增 HTTP 客户端或反向 WebSocket 客户端，地址填上表对应值。
查备注还需开启「HTTP 服务器」（默认 `http://127.0.0.1:18801`），并填入插件的「机器人 HTTP API 地址」。

### SnowLuma

SnowLuma 在 Windows 上原生运行、自动注入桌面版 QQ，不需要 Docker：

1. 从 [SnowLuma Releases](https://github.com/SnowLuma/SnowLuma/releases) 下载 `win-x64` 完整版并解压（例如 `C:\SnowLuma`）；
2. 用与 QQ **相同的 Windows 用户**、相同权限运行 `launcher.bat`（注入需要能访问 QQ 进程）；
3. 打开 `http://127.0.0.1:5099/` 进 WebUI，用启动日志里的一次性密码登录；
4. 在登录的 QQ 窗口扫码登录；
5. 在 WebUI 里配置 OneBot 连接：默认 OneBot HTTP 为 `3000`、WebSocket 为 `3001`。

对应插件设置：

| 插件设置项 | SnowLuma 填什么 |
| --- | --- |
| 监听端口 | 插件自己监听的端口（如 8090），不需要和 SnowLuma 的端口相同 |
| 访问令牌 | SnowLuma 里该 OneBot 连接的 access token |
| 机器人 HTTP API 地址 | `http://127.0.0.1:3000`（默认 OneBot HTTP 端口） |
| 机器人 HTTP API Token | 上一步那个 HTTP 服务的 access token |

> SnowLuma 的 native hook 与 QQ 版本强绑定。QQ 自动升级到 SnowLuma 尚未支持的版本时，注入会失败——先在 WebUI 里确认 hook 是否正常加载。

> 注意：SnowLuma 的许可为**源码可见的非商业许可**，不是 OSI 开源许可。另需说明的是，SnowLuma 与 NapCat 都是注入到真实 QQ 客户端进程的方案，**换框架并不能规避腾讯对注入行为的检测**；账号频繁掉线应优先排查账号运行环境（参见下方「账号掉线排查」）。

## 显示备注（`{name}` 优先用备注）

消息事件本身不携带备注，插件按以下优先级确定 `{name}`：

1. **手动备注映射**（设置界面「手动备注映射」，格式 `QQ号=备注名`，逗号分隔），最可靠；
2. **好友列表备注**：需要填「机器人 HTTP API 地址」。这是备注的正确来源——`get_friend_list` 的 `remark` 字段就是机器人给好友设置的备注；
3. **群名片**：老师在群里发言且不是机器人好友时，查 `get_group_member_info` 的 `card`；
4. **陌生人信息**：`get_stranger_info` 的 `remark`（该接口面向非好友，对好友经常返回空，仅作兜底）；
5. 回退到事件自带的群名片 → 昵称 → QQ 号。

备注查询结果会缓存 30 分钟（只缓存非空结果），好友列表缓存 10 分钟。在 QQ 里改完备注后，最多等缓存过期即可生效；想立即生效可以重启插件或改一次 API 地址。

## 账号掉线排查

插件的唯一外发请求是查询备注的 HTTP API，不登录 QQ、不调用任何账号相关 API，**不会导致账号掉线**。掉线请从框架侧排查：

- **同一账号不要在多处同时运行**：这是同类框架掉线最常见的原因，会话会互相顶掉。测试时务必确保同一 QQ 号只有一个框架实例在跑。
- **不要在同一设备/网络登录常用账号**：可能与机器人账号互相影响。
- NapCat 可在配置里把 `o3Hook` 设为 `0` 关闭包拦截，官方文档称可改善频繁掉线。
- 网络环境复杂（IP 频繁变化、机房 IP）时，按官方建议配置 SOCKS5 代理或更换 IP。

参考：[NapCat 安全相关](https://doc.napneko.icu/other/security)。

## 设置项速查

| 设置 | 说明 |
| --- | --- |
| 连接模式 | HTTP POST 上报 / 反向 WebSocket |
| 监听端口 | 插件监听的端口 |
| 访问令牌 | **接收上报**时校验用的 token，要和框架上报/反向 WS 配置里的 access token 一致（留空不校验） |
| 机器人 HTTP API 地址 | 查询备注用的地址：NapCat 默认 `http://127.0.0.1:18801`，SnowLuma 默认 `http://127.0.0.1:3000` |
| 机器人 HTTP API Token | **主动查询**时携带的 token，对应上面那个 HTTP 服务的 access token |
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
