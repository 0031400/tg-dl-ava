# tgdl (Avalonia / NativeAOT)

Telegram 文件下载器，原生 Windows GUI。核心是把 `D:\file\python\tg-dl` 的原理（原生 MTProto
`upload.getFile` 多连接并行 + 多 auth key 摊限流）用 C# 手写重做一遍，不依赖 TDLib、不依赖任何
Telegram SDK。

输入只有一种：**`chat_id:message_id`**。

## 为什么快

TDLib 自带下载器有两个天花板（源码层面）：

1. `ResourceManager.h` 里 `MAX_RESOURCE_LIMIT = 1 << 21`，整个客户端所有文件加载共享 2 MiB 的
   "在途数据"预算；
2. `PartsManager.cpp` 会把分片放大到 512 KiB，于是 1 GiB 文件在途分片 ≈ 4 个。

这里绕开它：直接对目标 DC 发 `upload.getFile`，每条连接独立文件句柄按偏移写盘，
并发数自己定（默认 3 条 key × 每条 4 个在途 = 12 个并发请求）。

## 功能

| 功能 | 位置 |
| --- | --- |
| **启动 / 停止连接池** | 顶部「启动连接 / 停止连接」，右侧小圆点是状态 |
| 导入 auth key（512 hex） | 右侧「Auth Key」→ 导入 Key |
| 从已有 key 派生新 key（换 DC，免登录） | 右侧「Auth Key」→ 派生 Key |
| 删除单条 key / 清空全部 | key 列表里的「删除」/「清空全部」（两段确认） |
| 同步会话列表（缓存 access_hash） | 右侧「Auth Key」→ 同步会话列表 |
| `chat_id:message_id` 加入下载队列 | 顶部输入框，回车或点「加入队列」 |
| 停止 / 继续 / 删除未完成任务 | 任务卡片上的按钮（停止保留已下分片，继续时续传） |
| 已完成：删记录 / 删记录+文件 / 用系统程序打开 / 定位到资源管理器 | 任务卡片上的按钮 |
| 设置：用多少条 auth key、每条 key 多少并发、分片大小、下载目录 | 右侧「设置」 |
| 0.5s 刷新：显示「最近 0.5s 拿到的数据量」和瞬时速度 | 任务卡片右侧 |
| 数据全部在 exe 同目录 `data\` | 见下 |

## 数据目录

```
tgdl.exe
data\
  authkeys.json     # 所有 auth key（与 Python 版 tg-dl 完全同格式，可直接互拷）
  settings.json     # 并发 / 分片 / 下载目录 / api_id
  tasks.json        # 下载队列（含续传用的分片位图）
  entities.json     # chat_id → access_hash 缓存（同步会话列表后才有）
downloads\          # 默认下载目录（可在设置里改）
```

`authkeys.json`：

```json
{
  "home_dc": 5,
  "home_ip": "91.108.56.196",
  "home_port": 443,
  "keys": { "4": ["<256 字节 hex>", "..."], "5": ["<hex>"] }
}
```

**换机器 = 拷这一个文件。** 里面是裸的 auth key，和密码等价，别外传。

## 手写 MTProto 的部分

```
Mtproto/
  TlSchema.txt       # 官方 layer 225 的构造函数表（tools/gen_tl_schema.py 生成）
  TlSchema.cs        # 表加载 + 字段表结构
  TlReader.cs        # 通用 TL 反序列化器（读 4 字节 id → 查表 → 按字段展开）
  RsaKeys.g.cs       # Telegram 8 个 RSA 公钥（tools/gen_rsa_keys.py 从官方公钥生成）
  Rsa.cs             # RSA_PAD（sha1(data)+data+随机填充到 255 字节 → 裸 RSA 256 字节）
  Crypto.cs          # AES-256-IGE（自动选路）、msg_key 派生、auth key
  AesNiIge.cs        # AES-NI 指令实现的 AES-256-IGE（快路径）
  DhHandshake.cs     # req_pq_multi → resPQ → 分解 pq → req_DH_params →
                     # server_DH_params_ok → set_client_DH_params → dh_gen_ok
  MtpTransport.cs    # TCP + intermediate 传输（0xEEEEEEEE + 4 字节长度前缀）
  MtpConnection.cs   # 封包/解包、msg_id/seq_no、salt、rpc_result / gzip / 容器 / bad_server_salt
  MtpApi.cs          # help.getConfig / auth.export|importAuthorization /
                     # messages.getDialogs / channels.getMessages / messages.getHistory / upload.getFile
  ConnectionPool.cs  # (DC, key) → 长连接缓存
  AuthKeyManager.cs  # 导入 / 派生 / 删除
```

几个踩过的坑（都在代码注释里）：

* **TL 字符串的 4 字节对齐要把长度前缀算进去**：`len < 254` 时 `pad = (4 - (len+1) % 4) % 4`。
  按 `len % 4` 算会在 8 字节的 `pq` 上错 3 个字节，直接导致 resPQ 解析错位。
* **AES-IGE 的链式变量**必须是「异或后的最终密文/明文」，不是 AES 输出的原始块。
* **msg_key 派生只哈希 `msg_key + auth_key[x..x+36]` 这 52 字节**，缓冲区开大了会把垃圾也哈希进去。
* **`rpc_result` 里的结果对象本身也可能是 `gzip_packed`**，要再剥一层。
* **salt 只在 `new_session_created` / `bad_server_salt` 时更新**，别从消息头里读。
* **`upload.getFile` 的 `limit` 必须是 4096 的倍数**。文件末尾那一片如果按「剩余字节数」传
  （几乎必然不是 4096 的倍数），会得到 `400 LIMIT_INVALID`。正确做法是**永远传整个分片大小**，
  服务端只返回剩下的字节，再按实际长度写盘。
* **`importAuthorization` 不能并发**（服务端只放行一条），但 DH 可以并发 —— 所以是「并行 DH + 串行导出/导入」。
* **`exportAuthorization` 不能导出当前会话所在 DC 的授权**，所以主 DC 自己派生不出新 key。

## 用起来

1. 启动 `tgdl.exe`。
2. 右侧「Auth Key」→ 导入 Key：粘贴 512 个 hex 字符，选它属于哪个 DC（一般是你原来登录的 DC，
   例如 5），勾上「设为主 DC」。
   * 已经有 Python 版的话，直接把 `D:\file\python\tg-dl\.tgdl\authkeys.json` 拷成 `data\authkeys.json` 更省事。
3. 点顶部「启动连接」——立刻按设置里的 auth key 数量建立连接（不够会自动派生并写回
   `authkeys.json`）；右侧小圆点变绿就是就绪了。
4. 点「同步会话列表」——频道/超级群必须有 `access_hash` 才能发请求，这一步把它缓存下来。
5. 顶部输入 `-1001234567890:42`，回车。任务进队列后自动开下。
6. 设置里按需调「使用多少条 auth key」和「每条 key 的并发」。总并发建议 ≤ 32。
   * 任务卡片上会显示文件所在的 DC（蓝色小标），需要给那个 DC 多派生几条 key 时才用「派生 Key」。

## 启动 / 停止（连接池）

顶部的「启动连接」是一个显式的连接池开关，不是装饰：

| 动作 | 行为 |
| --- | --- |
| **启动** | 先用主 key 连主 DC 并调一次 `help.getConfig` 验证 key 没失效；然后把这个账号在**主 DC** 上
已有的 key 都连上（主 DC 不能派生，见下）。下载别的 DC 的文件时才按设置数量派生。 |
| 下载时换 DC | 文件在别的 DC 而那个 DC 的 key 不够 → 同样自动派生到设置的数量。 |
| 派生不了 | 直接抛出可操作的提示并跳到「Auth Key」页、自动展开导入面板，例如
「DC4 上没有可用的 auth key，自动派生也失败了（…）。请到「Auth Key」页导入一条 DC4 的 key。」 |
| **停止** | 中断当前任务（变「已暂停」，已下分片保留），然后关掉所有 TCP 连接。队列不丢。 |

队列只在连接就绪（圆点变绿）时才取任务。所以：

* 想预热就点「启动连接」；
* 懒得点也行——**加入任务时会自动启动**；
* 停止之后再点启动，队列会接着往下跑。

> **主 DC 的 key 数量是死的。** `exportAuthorization` 不能导出主 DC 的授权，而且实测连
> 「借道另一台 DC 去导出」也不行 —— 服务端直接回 `303 USER_MIGRATE_<home_dc>`，
> 意思是"这个账号只能在主 DC 上操作"。所以主 DC 上永远只有你登录时产生的那几条 key，
> 设置里的「使用多少条」对它无效（日志会明确说明）。
>
> 程序仍会试一次借道，但**先做一次导出预检**（一个 rpc，几百毫秒），失败就直接退出，
> 不会先花 5~9 秒做 DH 握手。结果记在 `AuthKeyManager.BridgeUnsupported` 里，同进程内不再重试。
>
> 真正需要多条 key 的是**下载所在的 DC**（文件在 DC4 就派生 DC4 的 key），那条路是通的。

## 文件在哪个 DC？不用你选

DC 不是用户选项，是**文件自带的属性**：`document` / `photo` 对象里就有 `dc_id` 字段，
记录这个文件实际存在哪台 DC。流程是

```
输入 chat_id:message_id
  → messages.getMessages / channels.getMessages / messages.getHistory 取消息
  → 从 document.dc_id（或 photo.dc_id）读出文件所在 DC
  → 连到那台 DC，用 upload.getFile 拉分片
```

界面上这个值显示成任务卡片上的蓝色 `DC4` 小标，日志里也会打
`解析成功：xxx.mp4（189 MB，DC4）`。

如果服务端回 `FILE_MIGRATE_n`（文件被挪到别的 DC 了），程序会跟着换到 DC n 继续下，
不用手动干预。

## 自检命令

WinExe 没有 stdout，结果都写到 `data\selftest.log`：

```bat
tgdl.exe --selftest              :: 连 DC2/4/5 各做一次 DH + help.getConfig，验证加密通道
tgdl.exe --prototest             :: 用未登录的 key 调各方法，回 401 就说明请求序列化没问题
tgdl.exe --cryptotest            :: AES-IGE / 密钥派生的固定测试向量（对齐 Telethon 结果）
tgdl.exe --teststore             :: authkeys.json 读写往返
tgdl.exe --sessiontest           :: 启动/停止状态机，以及缺 key 时的提示文案
tgdl.exe --probe <hexkey> <dc>   :: 用真实 key 连上去，打印会话列表前 20 条
```

## 编译 / 发布

```bat
dotnet build -c Debug
dotnet run
```

原生 AOT（单文件、无运行时依赖）：

```bat
dotnet publish -c Release -r win-x64
```

> NativeAOT 在 Windows 上要用 MSVC 的 `link.exe` 和 **Windows SDK 的 .lib**。
> 只装 Visual Studio 的 C++ 工具链是不够的，还要在 VS Installer 里勾上
> 「Windows 11 SDK」；否则链接会报 `LNK1181: 无法打开输入文件 advapi32.lib`。
> 没装 SDK 时可以先出自包含单文件版：
>
> ```bat
> dotnet publish -c Release -r win-x64 -p:PublishAot=false -p:PublishSingleFile=true --self-contained true -o publish
> ```

## 性能：AES-IGE 实测（512 KiB 分片 × 400 次）

同一个 CPU（支持 AES-NI），三份实现、同样的输入，逐字节比对过密文一致：

| 实现 | 加密 | 解密 | 说明 |
| --- | --- | --- | --- |
| C# 托管 `Aes.Create + TransformBlock` | 104.6 MB/s | 98.7 MB/s | 每 16 字节过一次托管↔原生边界 |
| **C libcrypto (OpenSSL 3) `AES_encrypt`** | 82.5 MB/s | 44.7 MB/s | 反而更慢 |
| **C# AES-NI 内联（现在默认）** | **852 MB/s** | **848 MB/s** | 硬件指令，单块 ~0.5µs/32 块 |

结论：**没有引入 libcrypto 的必要**。

* IGE 是串行链式（后一块依赖前一块密文），没法像 CTR/ECB 那样批量铺开，
  所以只能一块一块调 AES。OpenSSL 的 `AES_encrypt` 是 legacy API，
  每块一次函数调用 + 能力检测，实测这份 `libcrypto-3-x64.dll` 还没走到 AES-NI，
  比 .NET 的 BCrypt 路径更慢。Python 版之所以需要 libssl，是因为纯 Python AES 只有
  300 KB/s —— C# 天生没这个问题。
* 换成 AES-NI 内联后比旧实现快 **8.5 倍**。按 20 MB/s 下载速度算，解密只占单核 2%，
  旧实现要占 19%（多连接叠加时会明显吃 CPU）。

复现：`cd tools/aesbench && dotnet run -c Release`（需要先按 `build.sh` 编出 shim）。

## 已知限制

* **需要 access_hash**：只给了 `chat_id` 时，频道/超级群必须先在「同步会话列表」里出现过。
  账号不在那个会话里会直接报错。
* **主 DC 派生不出 key**：`exportAuthorization` 不允许导出当前会话 DC 的授权。文件恰好在主 DC 上时
  只能用已有的那 1 条 key 下载。
* **CDN 文件**：请求里不带 `cdn_supported`，服务端仍可能返回 `FILE_CDN_REDIRECT`，此时会直接报错
  （不做 CDN 解密）。
* **目标 DC 只能是"非主 DC"**：派生面板里选主 DC 时按钮会直接禁用并说明原因。
* **api_id**：auth key 本身不绑定 api_id，但每条连接都要带一个有效的 api_id 做 `initConnection`。
  默认用 Telegram Desktop 的 `2040`，可在设置里改。
* 同一时刻只下一个文件（队列串行），这样才能把 N×C 的并发全给当前文件。
