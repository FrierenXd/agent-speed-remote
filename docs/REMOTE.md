# Remote agents / 远程主机统计

Agent Speed on macOS and Windows can display agents running on Linux, macOS or Windows hosts.
The desktop opens an **opt-in SSH connection** and starts a headless collector under your remote account.
No inbound collector port, daemon, cloud account or transcript synchronization is required.

## 1. 自动部署（推荐）

正式桌面发行包内置六种系统/架构的采集器，不需要自己下载、上传或安装 .NET。
先按第 2 节准备 SSH 密钥与已验证的主机指纹，然后在 **设置 → 数据源 → 远程主机** 添加主机、
填写名称和 SSH 目标，点 **自动部署并连接 / 更新** 即可。无需填写采集器路径或手动选择架构。

应用会自动识别 Linux/macOS/Windows 和 x64/ARM64，选择随桌面打包的版本，通过 SSH stdin 上传，
在本地和远程都校验 SHA-256，解压后运行 `--check` 检查运行时及系统 SQLite，再保存绝对路径、
启用该主机并切换到其统计视图。采集数据前没有额外的云端下载请求，也不依赖 scp/SFTP。

- Linux/macOS 安装到 `~/.local/lib/agentspeed-collector/releases/<SHA256>/`。
- Windows 安装到 `%LOCALAPPDATA%\AgentSpeed\collector\releases\<SHA256>\`。
- 仅写当前 SSH 用户目录，不需要 sudo/管理员权限，不安装常驻服务，不修改 SSH 配置。
- 更新使用不同摘要的独立目录，不覆盖正在运行的文件；相同包会验证并复用已有版本。
- 失败、取消或关闭网络总开关不会修改原有主机配置；上传阶段的临时目录会尽力清理。
  若安装已经完成才取消，可能留下未被引用的完整版本目录，原有配置仍不变。
- 为避免删除其他连接正在使用的文件，旧版本不自动清理；不用后可以手动删除对应版本目录。
- Linux 仍需 glibc 与系统 SQLite、ICU、OpenSSL；不支持 Alpine/musl。Windows 需系统 `tar.exe`。
  自动部署不会安装系统依赖，也不会绕过 SSH 认证或指纹核验。

从源码运行的开发版若没有打包采集器，会明确提示缺少内置包。构建桌面发行包的脚本默认先构建并
内置全部采集器；CI 使用同一工作流的采集器产物，避免重复构建。故桌面发行包体积会相应增加。

### 手动部署 / 自定义路径（可选）

构建机器需要 .NET 10 SDK；目标主机不需要安装 .NET。在仓库根目录运行：

```sh
./scripts/publish-collector.sh linux-x64
# 也支持 linux-arm64、osx-x64、osx-arm64、win-x64、win-arm64；不传参数构建全部。
scp dist/AgentSpeed-Collector-linux-x64.tar.gz my-server:/tmp/
ssh my-server 'mkdir -p ~/.local/lib/agentspeed-collector && tar -xzf /tmp/AgentSpeed-Collector-linux-x64.tar.gz -C ~/.local/lib/agentspeed-collector'
```

必须保留压缩包内的**所有文件**，不要只复制可执行文件。Linux 需要 glibc 发行版以及系统
SQLite 3、ICU、OpenSSL 等 .NET 10 运行依赖；不支持 Alpine/musl。macOS 使用系统 SQLite，
Windows 使用系统 `winsqlite3.dll`。采集账户应是运行 Agent 的同一用户，且能读取其日志。

Windows 目标主机先启用 OpenSSH Server，解压 `win-x64` / `win-arm64` 包到例如
`C:\Tools\AgentSpeedCollector`。macOS 目标主机先启用「远程登录」。

## 2. 准备 SSH

桌面端使用系统 OpenSSH、已有的 SSH 配置与密钥，不保存密码，也不会弹出密码输入框。
Windows 桌面需要安装系统「OpenSSH 客户端」可选功能。建议在 `~/.ssh/config`
（Windows 为 `%USERPROFILE%\.ssh\config`）中配置别名：

```sshconfig
Host my-server
    HostName 192.0.2.10
    User alice
    Port 22
    IdentityFile ~/.ssh/id_ed25519
    IdentitiesOnly yes
```

先在终端执行 `ssh my-server`，**通过可信渠道核对服务器指纹**并完成首次信任，确认密钥登录可用。
加密私钥需先加载到 SSH agent。应用强制 `BatchMode=yes` 和 `StrictHostKeyChecking=yes`，
不会自动接受未知服务器；非默认端口、跳板机和身份选择均写在 SSH config 中。

不要把同一机器通过多个别名重复添加，否则会重复统计；应用只能检测相同目标字符串。
远程账户的 SSH shell 配置不得向 stdout 输出欢迎语，否则会破坏统计协议。

## 3. 配置桌面端

在 **设置 → 数据源 → 远程主机** 中添加主机：

- 名称：例如「构建服务器」。远程 Agent 列表以此代替项目名。
- SSH 目标：`my-server` 或 `alice@example.com`，不是 URL，也不是带端口的命令行。
- 采集器路径：使用远程**绝对路径**，例如 `/home/alice/.local/lib/agentspeed-collector/agentspeed-collector`。
  路径按字面量执行，不展开 `~`、环境变量，不接受附加命令行参数。
- 远程 shell：Linux/macOS 选 POSIX；Windows 选 PowerShell，路径例如
  `C:\Tools\AgentSpeedCollector\agentspeed-collector.exe`。
- 自动部署时点「自动部署并连接 / 更新」，成功后路径和 shell 自动填入并保存。
  自定义路径则启用并点「应用」，选择「本机」「全部主机」或具体主机。最多配置 16 台。

网络总开关必须开启；关闭后所有 SSH 连接都会停止。本机监控不受影响。
数据源启用开关同时作用于远程；桌面上配置的本地日志目录不会发送给服务器。

### 非默认远程日志目录

默认读取远程账户的标准日志位置。需要自定义目录时，在远程创建包装脚本，并将桌面上的
采集器路径指向该脚本：

```sh
#!/bin/sh
exec /home/alice/.local/lib/agentspeed-collector/agentspeed-collector \
    --codex /srv/agents/codex/sessions --claude /srv/agents/claude/projects "$@"
```

记得 `chmod +x`。支持 `--codex`、`--claude`、`--dsh`、`--opencode`、`--gemini`、`--omp`、
`--antigravity`、`--workbuddy`、`--workbuddy-ai`、`--zcode`、`--kimi`、`--grok-build` 和
`--store`。Windows 可用 `.cmd` 包装器转发 `%*`。目录含义与本机对应数据源一致。

## 4. 数据和生命周期

- 每秒推送实时速度、模型、Agent 状态、今日 token/费用、最近 300 秒曲线。
- 远程完整历史分析完成后传输每日/模型/执行模式汇总；后台定期重扫，桌面刷新可重启连接立即重扫。
  同一份累计快照反复收到时是**替换**，不会重复累加。
- 各主机 ID 隔离；多主机实时速率、token、费用可以相加。多主机峰值由对齐的实测秒桶计算，
  不相加各自主机的独立峰值；合并历史日峰值无法还原时显示未知。
  多主机和来源子集峰值仅覆盖本次连接期间观察到的窗口，不保证还原连接前全天峰值。
- 使用桌面时区划分日期；双方时钟误差超过两分钟会拒绝数据。请启用时间同步。
- 超过 5 秒无快照时实时值归零，保留已收到的今日累计与历史。30 秒无完整帧则中断并重连；
  重试间隔从 1 秒指数退避到 60 秒。退出或停用连接后关闭 SSH 和采集器。
- 桌面缓存只含远程每日汇总，按主机、目标、采集器路径、shell、时区隔离。
  重启且离线时可看缓存历史，不显示伪造实时速度；今日实时面板等待新快照。
- 清除历史会清除本地缓存，不删除远程原始日志；下次刷新/重新分析可能从原始日志恢复。
  采集器自己的历史库位于远程用户应用数据目录 `AgentSpeed/collector/<key>/history.sqlite`。

## 5. 隐私与边界

监控统计流仅发送统计数据、模型名称和匿名哈希 ID，不传提示词、对话、项目名称、日志完整路径或凭据。
自动部署另传内置采集器包以及平台、程序安装路径等部署元数据，不上传或同步 Agent 日志。
统计输出仅到你配置的桌面主机。采集器不请求互联网价格表，费用使用内置/已有本地价格绑定。

远程数据不写入本机 transcript 历史数据库，不归因到本机进程流量，也不参与本机排行榜实时上报。
流量面板仍表示**桌面本机**，不是远程流量。远程响应完成脉冲/通知暂不转发。
监控连接仅运行读取统计的采集进程。只有明确点击「自动部署并连接 / 更新」时，应用才会额外执行
平台探测、上传与用户目录安装脚本；不会安装其他远程软件或修改服务器配置。

## 排障

1. 在桌面终端确认 `ssh -o BatchMode=yes -o StrictHostKeyChecking=yes my-server true` 成功。
2. 手动检查采集器：`ssh -T my-server '/absolute/path/agentspeed-collector --stream --timezone UTC'`，
   应持续输出 JSON 行；`Ctrl-C` 退出。不要使用 `ssh -n`，stdin 关闭会让采集器退出。
3. 断开：检查 SSH config、已验证的 known_hosts、私钥 agent、绝对路径和目标系统运行依赖。
4. 协议错误：确认桌面和采集器版本一致、shell 不输出额外文字、时间同步且日志规模未超出安全上限。
5. 已连接但无数据：确认以 Agent 所属账户运行，检查数据源开关和日志目录；云端且不落日志的会话仍无法统计。

## 开发验证

```sh
dotnet test windows/tests/AgentSpeed.Core.Tests -c Release --filter FullyQualifiedName~Remote
dotnet test windows/tests/AgentSpeed.Presentation.Tests -c Release
./scripts/publish-collector.sh
python3 scripts/package-collectors.py
# Linux，需要 OpenSSH Server；使用临时密钥/配置和仅监听 loopback 的独立 sshd，不修改现有 SSH 配置。
sudo python3 scripts/test-remote-ssh.py --dotnet "$(command -v dotnet)"
# 在 macOS 上运行完整原生测试：
cd macos && swift test
```
