# tgdl (Avalonia / 多账号)

Telegram 文件下载器，原生 Windows GUI。原理来自 `C:\Users\Administrator\file\tg-dl`：
手写 MTProto 的 `upload.getFile`，多连接并行拉分片，不依赖 TDLib、不依赖任何 Telegram SDK。

**这一版的重点是多账号**：一个账号 = 一条登录会话 = 一条 TCP 连接。
Telegram 的限流按 auth key 算，账号越多能摊开的额度越多。

输入只有一种：**`chat_id:message_id`**。

## 功能

| 功能 | 位置 |
| --- | --- |
| 导入账号（粘贴 auth key + 选 DC + **立刻连接验证**） | 右侧「账号」→ 导入账号 |
| 记录每个 DC 上次连通的 IP，下次直接用 | 自动（`data/dc-ip.json`） |
| 启动 / 停止连接池（逐个验证账号） | 顶部「启动连接 / 停止连接」 |
| `chat_id:message_id` 加入队列 | 顶部输入框，回车 |
| **相册多文件选择**（文件名 + 大小，可多选） | 加任务时自动弹出 |
| 停止 / 继续 / 删除未完成任务 | 任务卡片按钮（保留分片，续传） |
| 已完成：打开 / 定位 / 删记录 / 删记录+文件 | 任务卡片按钮 |
| 设置：用几个账号、每个账号多少并发、分片、目录 | 右侧「设置」 |
| **网络代理**：直连 / 手动（SOCKS5、HTTP CONNECT）/ 系统代理 | 右侧「设置」→ 网络代理 |
| 0.5s 刷新：「最近 0.5s 拿到的数据量」+ 3 秒滑动平均速度 | 任务卡片右侧 |
| **每条连接的速度**（一个账号一行显示） | 任务卡片第三行 |
| **累计下载用时**（暂停/排队不计入） | 任务卡片右侧 |
| **启动时预热所有已知 DC 的连接** | 顶部「启动连接」 |
| 数据全在 exe 同目录 `data\` | 见下 |

## 账号模型（重点）

```
一个账号
├─ 主 DC（登录时定的，改不了）+ 主 key
└─ 其它 DC：每个 DC 最多一条派生 key
```

* **一个账号对一个 DC 只存一条 key**。已经取消"同一账号多条 key 并行"，
  并发靠"多个账号"，不靠"多条 key"。
* 一个账号对一个 DC 只占**一条 TCP**，这条连接上跑 N 个在途请求（默认 3，最多 10）。
* 派生 key 必须挂在**它自己账号**名下：拿 A 账号导出的 key 去顶 B 账号的连接，
  看起来是 N 个账号并行，实际只有一个账号在干活。

设置里的「使用多少个账号」= 同时用几个账号；不够就有几个用几个，不会报错。

### 导入即验证

粘贴 512 个 hex 字符的 auth key、选它属于哪个 DC，点「导入并验证」：

```
连接 DC → users.getUsers(inputUserSelf) → 拿到 user_id / 名字 → 通过才落盘
```

* 验证失败（key 失效 / DC 选错）当场报错，不会存一条连不上的 key。
* 同一个人再导入另一个 DC 的 key，会自动挂到已有账号名下（按 user_id 认人），
  不会变成两个账号。

### 记住上次的 IP

每个 DC 第一次连接会把候选 IP 挨个试，**哪个真的收到响应就记下来**（`data/dc-ip.json`），
下次直接先连它：

```json
{ "4": "149.154.167.91", "5": "91.108.56.196" }
```

## 数据目录

```
tgdl.exe
data\
  accounts.json   # 账号 + 各自的 key（核心凭据，等价于密码）
  dc-ip.json      # 每个 DC 上次连通的 IP
  settings.json   # 账号数 / 并发 / 分片 / 目录 / api_id
  tasks.json      # 下载队列（含续传用的分片位图）
  entities.json   # chat_id → access_hash 缓存
downloads\        # 默认下载目录
```

`accounts.json`：

```json
{
  "Version": 2,
  "Accounts": [{
    "Id": "…", "UserId": 123456789, "DisplayName": "张三", "Username": "zhangsan",
    "HomeDc": 5,
    "PrimaryKey": "<256 字节 hex>",
    "Keys": { "4": "<hex>" }
  }]
}
```

> 从老版本升级：如果只有 `data/authkeys.json`（旧的"按 DC 分组的 key 池"），
> 启动时会自动迁移成「1 个账号」：home DC 那条当主 key，其它 DC 各取一条，
> 然后把老文件改名成 `authkeys.json.migrated`。多余的 key 不会丢，但不再被使用。

## 相册 / 多文件

一条消息如果属于相册（`grouped_id` 非 0），加任务时会先把同组消息都翻出来，
弹一个列表让你勾选（文件名 + 大小 + 类型，默认全选）。

## 手写 MTProto 的部分

```
Mtproto/
  TlSchema.txt       # 官方 layer 225 的构造函数表
  TlSchema.cs        # 表加载 + 字段表结构
  TlReader.cs        # 通用 TL 反序列化器
  RsaKeys.g.cs       # Telegram 8 个 RSA 公钥
  Rsa.cs             # RSA_PAD
  Crypto.cs          # AES-256-IGE（自动选路）、msg_key 派生、auth key
  AesNiIge.cs        # AES-NI 指令实现的 AES-256-IGE
  DhHandshake.cs     # DH 握手
  MtpTransport.cs    # TCP + intermediate 传输
  MtpConnection.cs   # 封包/解包、msg_id/seq_no、salt、rpc 分发
  MtpApi.cs          # getConfig / getUsers / export|importAuthorization /
                     # getDialogs / getMessages / getHistory / upload.getFile
  DcIpMemory.cs      # 每个 DC 上次连通的 IP
  ConnectionPool.cs  # (账号, DC) → 长连接
  AuthKeyManager.cs  # 导入验证 / 按账号派生
```

踩过的坑（都在代码注释里）：

* **TL 字符串 4 字节对齐要把长度前缀算进去**（`len < 254` 时 `pad = (4-(len+1)%4)%4`）。
* **AES-IGE 的链式变量**是"异或后的最终密文/明文"，不是 AES 原始输出块。
* **msg_key 派生只哈希 52 字节**（msg_key 16 + auth_key 36）。
* **`rpc_result` 的结果对象本身可能是 `gzip_packed`**，要再剥一层。
* **salt 只在 `new_session_created` / `bad_server_salt` 更新**。
* **`file_reference` 和 `access_hash` 都是"账号作用域"的**：拿 A 账号的给 B 账号用，
  服务端回 `FILE_REFERENCE_INVALID` / `CHANNEL_INVALID`。所以多账号时**每个账号必须自己取一次消息**，
  会话列表也要每个账号各自同步一遍。
* **非主 DC 只放行文件类方法**：在文件所在 DC 上调 `messages.getMessages` / `channels.getMessages`
  会回 `303 USER_MIGRATE_<主DC>`。取消息必须在账号自己的主 DC 上，下载才走文件 DC。
* **分片位图必须按"分片序号"索引**，不能拿 `pending` 列表的下标去标记。第一次下载两者恰好相等，
  一旦续传（pending 被压缩过）就会全错位：位图停在原地、每次重连都重下同一段、永远下不完。
* **`file_reference` 和 `access_hash` 都是"账号作用域"的**：拿 A 账号的给 B 账号用，
  服务端回 `FILE_REFERENCE_INVALID` / `CHANNEL_INVALID`。所以多账号时**每个账号必须自己取一次消息**，
  会话列表也要每个账号各自同步一遍。
* **非主 DC 只放行文件类方法**：在文件所在 DC 上调 `messages.getMessages` / `channels.getMessages`
  会回 `303 USER_MIGRATE_<主DC>`。取消息必须在账号自己的主 DC 上，下载才走文件 DC。
* **分片位图必须按"分片序号"索引**，不能拿 `pending` 列表的下标去标记。第一次下载两者恰好相等，
  一旦续传（pending 被压缩过）就会全错位：位图停在原地、每次重连都重下同一段、永远下不完。
* **`upload.getFile` 的 `limit` 必须是 4096 的倍数**：末尾那一片如果传"剩余字节数"，
  会得到 `400 LIMIT_INVALID`。永远传整个分片，服务端只返回剩下的字节。
* **`exportAuthorization` 不能导出"当前连接所在 DC"的授权**，也不能导出主 DC 的
  （实测回 `303 USER_MIGRATE_<home_dc>`）。所以主 DC 上只有登录时那几条 key。
* **非内容消息（`msgs_ack`）的 `seq_no` 不是 0，而是 `2 × 已发内容消息数`**。
  发 0 会被判 `32 seq_no too low`，ack 全被拒 → 服务端不停重发未确认消息 → 最后直接踢连接
  （表现为 `Unable to read beyond the end of the stream`）。内容消息是 `2k+1`，ack 是 `2k`。
* **`msg_id` / `seq_no` 必须在发送锁内按"实际发送顺序"分配**，否则并发发送时顺序错乱，
  服务端判 `msg_id/seq_no too low`。
* **重试要重新分配 `seq_no`，不能复用**：被拒的消息不计入服务端计数，而并发期间别的请求
  已经分配了更大的号，复用旧号会被判 `seq_no too low`。
* **`importAuthorization` 不能并发**，但 DH 可以 —— 所以是「先导出 + 再握手 + 再导入」。

## 网络代理

Telegram 走 TCP 443，直连连不上时必须配代理。设置页有三种模式：

| 模式 | 行为 |
| --- | --- |
| 不使用代理（直连） | 直接连 DC IP |
| 使用代理（手动配置） | SOCKS5 或 HTTP CONNECT，可填用户名密码 |
| 使用系统代理 | 读注册表 `HKCU\...\Internet Settings` 的 `ProxyEnable` / `ProxyServer`；没启用就走直连 |

* **VLESS**：直接粘贴 `vless://` 分享链接（`type=ws` + `security=tls`，或 `tcp`）。
  WebSocket 帧的缓冲区必须按**实际长度**分配（帧头 2/4/10 字节 + 4 字节掩码 + payload），
  按最坏情况分配会多写出几个零字节，服务端把那些 0 当成下一个帧头 → 整条流废掉
  （服务端日志表现为 `unexpected EOF`，因为客户端等不到响应就自己断了）。
  请求头布局 `ver(1) + uuid(16) + addonLen(1) + cmd(1) + port(2,BE) + atyp(1) + addr` ——
  **uuid 不能漏、port 必须在 atyp 之前**，错一个字节服务端就会报 `unknown address family: N`。
* **SOCKS5**：RFC 1928/1929，支持无认证和用户名密码两种。
* **HTTP**：`CONNECT host:443`，支持 `Proxy-Authenticate: Basic`。
* 代理是**在 TCP 层**打隧道，DH 握手、`upload.getFile` 全都走它，不是只代理某个请求。
* 「测试代理」按钮会用当前表单的值实测到 DC2:443 的隧道，不用先保存。
* 改了代理点「保存设置」会**自动用新代理重连**（旧连接还走老路，必须重建）。

实测（`127.0.0.1:50000`，同一个端口同时支持两种）：

```
代理：SOCKS5 127.0.0.1:50000
TCP 隧道建立成功（149.154.167.41:443，47 ms）
DC2 DH 成功（3894 ms）auth_key=0x691595152089ca01
help.getConfig 成功（659 ms）-> config
```

命令行自测：`tgdl.exe --proxytest 127.0.0.1:50000 socks5`（或 `http`）。

## 速度显示说明

界面上有两个数字，含义不同：

* **最近 0.5s +X** —— 原始窗口值，只统计最近 0.5 秒实际收到的字节。它会 0 / 20 MiB/s 来回跳，
  属于正常现象（1 MiB 分片是整块到达的），**不要用它判断快慢**。
* **X MiB/s** —— 最近约 3 秒的滑动平均，判断快慢看这个。

想严格比较两个构建，用命令行模式跑固定字节数看总耗时：

```bat
tgdl.exe --dl <chat:mid> 150
```

实测（同机、同时段、A/B 交替、各 150 MiB）：

| 构建 | 用时 | 平均 | 峰值 |
| --- | --- | --- | --- |
| Debug (JIT) | 17.9s | 8.5~8.8 MiB/s | 19.8 |
| **NativeAOT** | 15.7~15.8s | **10.5~10.6 MiB/s** | 19.8 |

AOT 比 JIT 快 20% 左右。

## 性能：AES-IGE 实测（512 KiB × 400 次）

| 实现 | 加密 | 解密 |
| --- | --- | --- |
| C# 托管 `Aes.Create + TransformBlock` | 104.6 MB/s | 98.7 MB/s |
| C libcrypto (OpenSSL 3) `AES_encrypt` | 82.5 MB/s | 44.7 MB/s |
| **C# AES-NI 内联（现在默认）** | **852 MB/s** | **848 MB/s** |

IGE 是串行链式，没法批量铺开，只能一块一块调 AES；所以胜负手是"软件 AES vs 硬件 AES"，
不是"托管 vs 原生"。有 AES-NI 就走硬件指令，没有就退回托管实现。

## 自检命令

WinExe 没有 stdout，结果写到 `data\selftest.log`：

```bat
tgdl.exe --selftest        :: 连 DC 做 DH + help.getConfig
tgdl.exe --prototest       :: 用未登录的 key 调各方法，回 401 就说明请求序列化没问题
tgdl.exe --cryptotest      :: AES-IGE / 密钥派生的固定测试向量
tgdl.exe --teststore       :: accounts.json 读写往返
tgdl.exe --sessiontest     :: 启动/停止状态机 + 缺账号时的提示
tgdl.exe --probe <hex> <dc>:: 用真实 key 连上去，打印会话列表前 20 条
```

## 编译

```bat
dotnet build -c Debug
dotnet run
```

> 开发阶段只编 debug。NativeAOT 发布（`dotnet publish -c Release -r win-x64`）需要
> Windows SDK 的 .lib，本机只装了 MSVC 没装 SDK，链接会报 `LNK1181: advapi32.lib`。

## 已知限制

* **access_hash 会自动补**：先查本地缓存 → 再用别的账号见过的 `@username` 走
  `contacts.resolveUsername` 反查（公开频道/用户即使不是成员也能读）→ 最后才同步自己的会话列表。
  只有「私有频道 + 不是成员」才真的拿不到定位信息。
* **主 DC 上只有登录时那几条 key**：`exportAuthorization` 不允许导出主 DC 的授权。
* **CDN 文件**：不带 `cdn_supported`，服务端仍可能返回 `FILE_CDN_REDIRECT`，此时直接报错。
* 同一时刻只下一个文件（队列串行），这样 N 个账号的并发才能全给当前文件。
