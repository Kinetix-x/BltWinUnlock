# BltWinUnlock · Btwu

**让与 Windows 配对并连接的任何蓝牙设备，成为登录凭据。**

简体中文 | [English](README.en.md)

> A Windows credential provider that turns any Bluetooth device paired and connected to Windows into a
> sign-in credential. Press Enter on the lock screen — no password typing, no fingerprint reader, no phone.

带上蓝牙键盘走开，回来在锁屏界面按一下回车就进去了。

仓库名是 `BltWinUnlock`，程序与命令叫 **`btwu`**（Bluetooth Unlock）。

![主面板](docs/screenshots/主面板.png)

---

## 它做什么

- 你在 Windows 里已经配对过的**蓝牙设备**（键盘、鼠标，或任何你勾选的设备）就是钥匙；
- 设备连着 → 锁屏上多一个**默认凭据磁贴**，**按回车即解锁**；
- 设备断开 → 磁贴消失，锁屏与平时完全一样，按回车不会有任何免密效果；
- 默认**不主动锁屏**：什么时候锁由 Windows 决定（合盖 / 超时 / `Win+L`）。

## 它不做什么

- **不做自动登录**：开机、注销、切换用户后仍然要自己输一次密码；
- **不解锁 UAC 提权框**（`CPUS_CREDUI` 一律拒绝，否则你的口令能静默通过管理员确认）；
- 不需要联网、不收集任何数据、不上传任何东西。

## 为什么是「凭据提供程序」

普通程序无法解开 Windows 锁屏——这是设计如此。可行的两条路是「模拟输入」和「自研凭据提供程序」，
本项目选择后者：它由 LogonUI 在锁屏界面里加载，按 Windows 的契约把口令打包提交，行为与系统自带的
密码磁贴同类，只是把「输密码」换成了「按回车」。技术细节见 **[docs/技术说明.md](docs/技术说明.md)**。

## 环境要求

| 项 | 要求 |
| --- | --- |
| 系统 | Windows 10 / 11，x64（另有 `win-arm64` 可自行构建） |
| 权限 | **安装时需要一次管理员**（凭据提供程序要进 `System32` 并在 `HKLM` 注册、计划任务要建） |
| .NET | 用「自包含」包时**不需要**装任何运行时；瘦包需要 .NET 10 桌面运行时 |
| 硬件 | 任意已配对的蓝牙键盘 / 鼠标。不需要指纹、摄像头、手机 App |

## 安装

1. 到 [Releases](../../releases) 下载：`BtwuByKinetix_vX.Y.Z.zip`（自包含，推荐）或 `...-fd.zip`（瘦包）；
2. **整个文件夹解压**出来（不要只挑 exe —— 旁边的 `BtwuCredProv.dll` 是必需的）；
3. 双击 **`启动管理面板.cmd`**；
4. 面板最上面的「安装」→ 点「安装」。**第一次它会先让你勾选触发设备、再录一次 Windows 账户口令，最后才装**——
   一次 UAC 把三步做完；
5. `Win+L` 锁屏再解锁一次：设备在场时按回车就回到桌面。

> 口令要的是 **Windows 账户口令，不是 PIN**。Microsoft 账户要填该账户的登录口令（含邮箱登录名）；
> 本地账户填你的本机密码。面板会自动判断你属于哪一种，并在录入时用两种账户形式各问 Windows 一次。

## 用法

| 想做的事 | 怎么做 |
| --- | --- |
| 换触发设备 | 面板「触发设备」→ 勾选 → 保存 |
| 看它现在的判断 | `btwu status`（或面板顶部那一句话） |
| 临时不用免密 | 面板「暂停」，或 `btwu pause --minutes 60` |
| 全自动（锁屏直接回桌面） | `btwu autosubmit on` |
| 查问题 | `btwu doctor`（全链路自检），面板「诊断」区也有同样入口 |
| 卸载 | 双击 `app\uninstall-admin.cmd`，它会问数据目录要不要一起删 |

完整的命令列表：`btwu --help`。

## 安全模型（请先读完）

**这等价于把钥匙插在门上。**

- 它保管一份**可以替代你口令**的凭据：`cred.bin`，用 DPAPI（machine scope）加密，
  ACL 只允许 SYSTEM 与 Administrators。**本机管理员可以解密它**——想要「不用输密码就进去」，这一点绕不过去；
- 因此**不要用在多人共用的机器上**，也不要用在你不信任管理员的机器上；
- 设备在场就等于你在场：**勾了耳机或手机，它们连着也算你在场**。要收紧成只认 HID 键盘：
  `btwu enroll --require-hid Keyboard`；
- 键盘留在桌上而人走开时，它无法分辨——这是「用设备代替人」这一类方案的共同弱点。

发现安全问题请走 [SECURITY.md](SECURITY.md) 的私下渠道，不要开公开 issue。

## 常见问题

**录入时说校验不通过？** 对话框会列出「试过的形式」和「本机登录的账户」两行——照后者填。
两种常见填错：① 填了注册表里出现的微软邮箱（那只是某个应用的登录记录，不一定是 Windows 登录身份）；
② 填了 PIN。也可以先跑 `btwu doctor`，第二行就写着本机是本地账户还是 Microsoft 账户。

**锁屏上没有多出磁贴？** 依次检查：设备是否连着（`btwu status`）、是否处于「暂停」（`disable.cp`）、
凭据提供程序是否注册（`btwu doctor` 的 `[1]`）、`presence.dat` 是否新鲜。

**为什么它不帮我锁屏？** 刻意的。蓝牙键盘空闲会自己断连省电，而「断连」与「人走了」在信号上分不开，
强行做「离开即锁屏」会让每个人为自己的每只键盘去调一个宽限期。锁屏是 Windows 的本职。

**支持 Microsoft 账户吗？** 支持。但 Microsoft 账户在本机的登录名是 `MicrosoftAccount\<登录地址>`，
而本机同名的那个本地账户是**另一个主体**（口令由 Windows 托管）——形式填错时 Windows 只会报
「用户名或密码不正确」，所以工具会两种形式各试一次并自动采用被接受的那一种。

## 从源码构建

需要 **.NET 10 SDK**，以及（仅原生凭据提供程序需要）**Visual Studio 生成工具的 C++ 工作负载 + Windows SDK**：

```bat
build.cmd                                        :: 原生 + .NET 全量构建
powershell -ExecutionPolicy Bypass -File scripts\install.ps1     :: 安装（管理员）
powershell -ExecutionPolicy Bypass -File scripts\uninstall.ps1   :: 完整回滚（管理员）
powershell -ExecutionPolicy Bypass -File scripts\package.ps1     :: 打绿色版（自包含）
powershell -ExecutionPolicy Bypass -File scripts\package.ps1 -FrameworkDependent   :: 打瘦包
```

只想构建托管部分（不需要 MSVC）：

```bat
dotnet publish src\Btwu.Agent\Btwu.Agent.csproj -c Release -o dist\app
```

### 仓库结构

```
src/Btwu.CredProv/      凭据提供程序（C++，安全关键）
src/Btwu.Agent/         常驻 agent + 托盘 + 图形面板（.NET，同一个 btwu.exe）
src/Btwu.Core/          状态文件、凭据保管、账户身份判定、LSA 探针
scripts/                安装 / 卸载 / 打包 / 录入
tools/                  独立诊断工具（不进安装包）：LSA 诊断、状态争用测试、截窗、发包卫生扫描
docs/                   技术说明与截图
```

## 验证

这个项目对「自以为对」的容忍度很低，所以几个判据是写成代码的：

| 工具 | 说明 |
| --- | --- |
| `src/Btwu.CredProv/test/build/harness.exe` | 不注册就能驱动凭据提供程序，**59 条断言**覆盖打包格式、场景策略与全部 fail-closed 行为 |
| `tools/statetest`（`--naive`） | 状态发布的并发争用测试；`--naive` 跑旧实现并要求它失败 |
| `btwu --gui-selftest` | 图形对话框自检（两种窗口尺寸 × 4 + 1 条「复刻旧写法必须失败」） |
| `btwu unlock-probe` | **唯一与锁屏同路**的探针：复现锁屏那一刻的提交（需管理员） |
| `tools/hygienescan` | 发包前的卫生扫描：逐文件、UTF-8 与 UTF-16LE 各扫一遍 |

## 已知限制

- 只覆盖**本地账户**与 **Microsoft 账户**；Entra ID（Azure AD）加入的机器未验证；
- 只在 Windows 11 家庭版（25H2, 200% 缩放）上做过完整实测；其它版本/缩放比例欢迎反馈；
- 「在场」判据是**蓝牙链路是否连着**，不含距离信息（不读 RSSI），因此无法判断你离电脑多远。

## 许可证

**[GPL-3.0](LICENSE)**（GNU General Public License v3.0）。Copyright (C) 2026 Kinetix。

简单说：你可以自由使用、修改、再分发；**但如果你分发了修改后的版本（包括二进制），
GPL-3.0 要求你也以同样的许可证提供对应的源码**——所以这个项目不能变成闭源的商业软件。

程序按「现状」提供，不附带任何担保。使用前请读上面的「安全模型」：这个工具保管着你的口令，
它等价于把钥匙插在门上。

> 你在 Release 里下载到的 zip 是这个仓库的构建产物。**源码就是本仓库**——想自己编译见上面的
> 「从源码构建」，不想编译就用 Release 里的包。
