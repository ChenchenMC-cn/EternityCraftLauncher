# Eternity Craft Launcher (ECL)

[English](README.md) · **简体中文**

一个原生、轻量的 Minecraft: Java Edition 启动器。C++ 负责吞吐，Java 负责运行时，
C# 负责界面与编排。

> **当前版本 1.0.1** — 原版 / Fabric / Quilt / Forge / OptiFine 五种都能装能启动，OptiFine 可挂 Forge，
> 内置按版本隔离的模组管理，**微软正版登录需要自备 Azure client_id**（见「微软登录」一节，
> 源码不再内置任何官方 ID），单文件自解压安装包，配置可随文件夹携带（便携模式）。

**下载哪个？** 认准 **`ECL-Setup.exe`**，双击装完就能用：

| 文件 | 大小 | 适合谁 |
|---|---|---|
| **`ECL-Setup.exe`** ← 推荐 | ≈ 2.6 MB | **首选**。双击安装，缺 .NET 会弹窗带你下载安装（微软官方页），装完自动启动 |
| `EternityCraftLauncher-single/ECL.exe` | ≈ 59 MB | **免安装**。自带运行时，拷到 U 盘/别的电脑都能直接跑 |
| `EternityCraftLauncher-lite/ECL.exe` | ≈ 1.9 MB | 单文件，但**要自己先装 .NET 10 Desktop Runtime** |
| `dist/`（`ECL.exe` 只有 533 KB） | ≈ 2 MB | 开发用，必须和同目录文件一起跑 |

> 双击后如果弹出 "You must install .NET Desktop Runtime"，说明你拿到的是后两种之一
> —— 用 `ECL-Setup.exe` 重装，或去 `aka.ms/dotnet/app-launch-failed` 装 .NET 10 桌面运行时。

---

## 开源范围与许可

本项目采用 **Mozilla Public License 2.0**（全文见 [LICENSE](LICENSE)）。

**为什么是 MPL 而不是 GPL**：GPL 要求"整个程序"都开源，和"只公开一部分"直接冲突。
MPL 是**文件级** copyleft —— 改了我哪个文件，那个文件就必须开源并保留我的署名；
没碰过的文件可以闭源。这是唯一能同时满足「部分公开」和「借用必须开源」的选择。

### 公开什么

| 目录 / 文件 | 状态 | 说明 |
|---|---|---|
| `src/ECL.Launcher/**` | ✅ 公开 | 界面、安装、模组、版本管理、崩溃分析 |
| `src/ECL.Native/include/ecl_native.h` | ✅ 公开 | 原生层 C ABI 接口定义 |
| `src/ECL.Runtime/**` | ✅ 公开 | Java 探针与启动包装 |
| `tools/**`、`scripts/**`、`AllRebuild.bat` | ✅ 公开 | 构建脚本、安装器、运行时检查 |
| `src/ECL.Native/src/**` | ❌ 不公开 | 原生下载 / 解压实现 |
| `Services/MicrosoftAuthService.cs` | ❌ 不公开 | 微软登录流程 |
| `Services/Dpapi.cs`、`Models/Account.cs` | ❌ 不公开 | 令牌加密与账户模型 |
| `Services/AuthlibInjector.cs` | ❌ 不公开 | 第三方登录注入 |

> 公开树由 `scripts/export-public.sh` 从工作区生成（排除清单见 `scripts/public-exclude.txt`），
> 导出时会自动检查有没有把官方 client id、令牌、账号文件带出去。

**公开部分不能直接编译** —— 缺上面几块实现。这是有意的：接口和调用方式都看得到，
关键实现留在作者手里。想跑起来就自己补一个 `MicrosoftAuthService`（登录）和原生 DLL。

### 如果我要借用

MPL 2.0 已经强制两件事，`NOTICE` 里写得更明白：

1. **改过的文件必须开源**（同样用 MPL 2.0 或兼容协议）；
2. **不能删署名**，并在你的 README / 关于页写明来源与修改：

```
本项目基于 Eternity Craft Launcher (ECL)
原作者：ChenchenMC  https://github.com/ChenchenMC-cn/EternityCraftLauncher
本版本由 <你> 修改，改动见 <仓库地址>
```

新增的文件你想闭源、想商用都行 —— MPL 不管。做在线服务也不需要公开服务端
（那是 AGPL 的约束，本项目没选）。

## 为什么做这个

市面上主流的第三方启动器（PCL2 / HMCL）在部分环境下已无法稳定获取游戏本体。
ECL 不内置任何游戏文件，只做一件事：**用官方元数据接口把版本装好并启动**。

- 不打包、不分发游戏本体，所有 jar / 资源均来自 Mojang、FabricMC、MinecraftForge 官方源
- 不修改、不破解游戏文件
- 使用即代表你同意 [Minecraft EULA](https://www.minecraft.net/eula)

---

## 界面

主界面是**账户 + 已安装版本**，不是下载页。安装是独立的一页。

```
┌──────┬──────────────────────────────────────────────┐
│      │  准备开始                                     │
│ 侧栏  │  [ 头像 ] Steve · 离线账户        [管理账户]   │
│      │  已安装的版本                    [刷新][安装]  │
│ 主页  │  ┌────────┐ ┌────────┐ ┌────────┐           │
│ 安装  │  │ 原版    │ │ FABRIC │ │ FORGE  │           │
│ 模组  │  │1.20.1  │ │0.20.1  │ │+OptiFine│          │
│ 账户  │  └────────┘ └────────┘ └────────┘           │
│ 设置  │  Minecraft 1.20.1 · 2 个模组    [ 开始游戏 ]  │
│ 日志  │                                              │
└──────┴──────────────────────────────────────────────┘
```

---

## 架构

三个语言不是堆料，各自解决自己擅长的问题：

| 组件 | 语言 | 职责 | 产物 |
|---|---|---|---|
| **ECL.Native** | C++20 | 并行 SHA-1、多线程 HTTP、ZIP 解压（自研 inflate） | `ECL.Native.dll` (~220 KB) |
| **ECL.Runtime** | Java 8 | JVM 能力探测、启动包装器（崩溃可读化） | `ECL.Runtime.jar` (~6 KB) |
| **ECL.Launcher** | C# / WPF (.NET 10) | 界面、版本元数据、classpath、进程管理、账户 | `ECL.exe` (~620 KB) |

### 为什么下载和哈希放在 C++

装一个现代版本要处理 **3600+ 个文件、约 700 MB**。托管运行时做这件事有两个代价：
每次 IO 都要过 GC 和托管边界；并发一高，线程池调度和 GC 压力互相拖累。

原生实现里每条工作线程常驻一个 WinHTTP session 和连接，文件读写走裸 `ReadFile`，
进度用原子计数器汇总后每 60 ms 回传 UI 一次。

### 为什么 Java 侧也要有一个 jar

启动器得知道「这台机器上的某个 javaw.exe 到底能不能跑这个版本」。解析 `java -version`
的文本输出不可靠（各发行版格式都不一样）。`ecl.runtime.Probe` 直接在目标 JVM 里跑，
输出一行 JSON：`major / vendor / bitness / classVersion / maxHeap`。

`ecl.runtime.Bootstrap` 是可选启动包装器，把 `InvocationTargetException` 拆成一行可读错误：

```
[ECL] game crashed during start-up: java.lang.UnsupportedClassVersionError
[ECL] hint: This version needs a newer Java runtime. Open ECL settings and pick Java 17 or 21.
```

可以在设置里关掉，退回直连启动。

---

## 模组加载器

### Fabric 与 Quilt

两者都是「meta 服务器直接给出完整 profile」的模式，流程完全一样：拉 profile →
写 `versions/<id>/` → 交给普通下载计划器补齐依赖。不跑任何安装器进程。

| | Fabric | Quilt |
|---|---|---|
| meta 端点 | `meta.fabricmc.net/v2` | `meta.quiltmc.org/v3` |
| 版本列表结构 | 嵌套 `loader.version` | 同样嵌套 |
| 稳定版判定 | 用 API 的 `stable` 字段 | **API 没有 stable 字段**，靠版本串判断（无 `-beta/-alpha/-rc/-pre` 即稳定） |
| 主类 | `KnotClient` | `KnotClient`（不同包名） |

Quilt 的库来自 Fabric 的 maven（`maven.fabricmc.net`），条目里自带 `url`，直接用它。

### Forge

Forge 需要字节码补丁（processors 管线），自己重实现是个维护陷阱，所以直接驱动**官方安装器**：

```
java -jar forge-<mc>-<forge>-installer.jar --installClient <dataDir>
```

ECL 负责补齐它需要的环境，都是实测踩出来的：

| 坑 | 处理 |
|---|---|
| 缺 `launcher_profiles.json` → "you need to run the launcher first" | 安装前写一个最小合法的 stub |
| 上次中断留下的 `versions/<id>/` → 安装器认为已装，跳过补丁步骤 | 安装前总是删除该目录 |
| Maven 坐标是 `<mc>-<forge>`，目录名是 `<mc>-forge-<forge>` | 下载 URL 用前者，返回 id 用后者 |
| classpath 里放 `1.20.1.jar` → 模块层报 `package net.minecraft.server exported twice` | 把 vanilla jar 复制成 `<profile>.jar`，正好命中 Forge 的 `ignoreList` |

### OptiFine（单装）

optifine.net 没有 API，下载链接藏在一层广告页后面，且要带对 Referer 才给 token：

```
/downloads  →  adloadx?f=<jar>&x=<会话标记>  →  downloadx?f=<jar>&x=<完整token>  →  .jar
```

拿到的 jar 是 GUI 安装器，它的 `main` **忽略命令行参数**，永远装到
`Utils.getWorkingDirectory()`。好在 `optifine.Installer` 暴露了
`public static void doInstall(File minecraftDir)`，所以 ECL 加了一个 Java 入口
（`ecl.runtime.OptifineInstall`）反射调用它：

```bat
java -cp ECL.Runtime.jar;OptiFine_1.20.1_HD_U_I6.jar ecl.runtime.OptifineInstall <数据目录>
```

> OptiFine 必须放在 **classpath** 上而不是自定义 ClassLoader：安装器靠
> `optifine.Installer` 的 code source 定位自己的 jar，只有应用类路径才拿得到。

安装完它会生成 `versions/1.20.1-OptiFine_HD_U_I6/`（含打好补丁的 23 MB jar）和
一个只有 463 字节的 json（`inheritsFrom: 1.20.1` + tweaker），父版本的库由
`VersionResolver` 合并进来。

### OptiFine 作为 Forge 模组

OptiFine 的 jar 本身就是合法的 Forge 模组（`META-INF/mods.toml` 里 `modId="optifine"`，
外加一个 `ITransformationService`），所以「挂 Forge」不需要跑安装器，把 jar 放进
该 Forge 档案的 `mods/` 就行。

**但 1.20.1 上不能用 Forge 47.4+。** 实测 47.4.28 启动必崩：

```
java.lang.NoSuchMethodError: 'net.minecraft.resources.ResourceLocation
    net.minecraft.resources.ResourceLocation.parse(java.lang.String)'
    at net.minecraftforge.registries.ObjectHolderRegistry.scanTarget(...)
```

原因是 OptiFine 的 transformer 抹掉了 `ResourceLocation.parse`（1.20.1 根本没有这个方法），
而 Forge 47.4 的 `ObjectHolderRegistry` 偏偏调用它。空 Forge 不会触发——只要 `mods/` 里有
任意模组，mod 扫描就会走到那段代码。**47.0–47.3 正常**（47.2.0 与 47.3.39 都实测过）。

所以 ECL 做了三层处理，不靠文档提醒：

| 层 | 行为 |
|---|---|
| 版本列表 | 勾上「同时装 OptiFine」时，47.4+ 的构建直接从列表里隐藏 |
| 安装前 | 兜底断言：选中 47.4+ 直接报错，不让你装完再崩 |
| 模组页一键装 | Forge 47.4+ 档案上按钮变灰，写明「Forge 47.4+ 不支持 OptiFine」 |

### inheritsFrom 合并

`VersionResolver` 按官方启动器的规则折叠继承链：子版本标量覆盖父版本，
libraries 合并（同名子覆盖父），arguments 父在前子在后，jar 用子版本自己的副本。

---

---

## 模组管理

### 版本隔离（前提）

`mods/` 是 `--gameDir` 下的子目录，所以**先得让每个版本有自己的游戏目录**，否则
Forge 会去加载 Fabric 的 jar，OptiFine 模组也会漏进单装 OptiFine 的档案。

开启后（设置页「版本隔离」，默认开）：

```
versions/<id>/game/          <- 每个版本独立：saves / mods / options.txt / config
versions/<id>/game/mods/
libraries/  assets/          <- 仍然共享，不额外占空间
```

### 元数据从 jar 里读，不连网

| 模组类型 | 读取的条目 | 取出的字段 |
|---|---|---|
| Fabric / Quilt | `fabric.mod.json` | id / name / version / authors |
| Forge / OptiFine | `META-INF/mods.toml` | `[[mods]]` 首块的 modId / displayName / version |
| 1.12 时代 | `mcmod.info` | modid / name / version / authorList |

不认识的 jar 也照样列出来，只是没有名字——启动器没资格隐藏用户自己放进去的文件。

Forge 的 `version` 常常写成 `${file.jarVersion}` 这种模板变量，直接显示没有意义，
所以遇到 `${` 就留空。

### Modrinth 搜索安装

模组页顶部「从 Modrinth 安装」展开市场面板：按当前版本的游戏版本 + 加载器过滤，搜到结果
点「安装」就把最新兼容版 jar 直接装进 `mods/`（走 ModService 的元数据解析，不匹配会提示）。

| 端点 | 用途 |
|---|---|
| `api.modrinth.com/v2/search` | 搜索（facets 过滤 `versions` + `categories`） |
| `api.modrinth.com/v2/project/{id}/version` | 取最新兼容文件的下载 URL |
| `cdn.modrinth.com` | jar 本体（下载失败时错误信息里会带上直链，方便手动下载后「导入」） |

- 只接 Modrinth：**CurseForge 的 API 需要付费密钥**，ECL 不会匿名硬编码别人的 key。
- 下载量按中文习惯格式化（`2.53亿` / `8849.38万`）。
- 原版 / OptiFine 单装版本不能加载模组，市场面板直接给提示而不是给个会失败的按钮。

### Java 运行时自动下载

缺对应大版本的 Java 时，启动器自动下载 Eclipse Temurin JRE 并装进
`<数据目录>/runtime/java-<major>/`，下次直接复用。设置页可关。

| 目标 | 选择规则 |
|---|---|
| 版本要求 ≤ 8 | **必须 Java 8**（老版本在 Java 9+ 上必崩，绝不 fallback 到新 JVM） |
| 要求 9–17 | ≥ 要求的最小 JVM（1.17 要 16 → 装 17） |
| 要求 ≥ 21 | ≥ 21 |

下载链：`api.adoptium.net`（元数据 + SHA-256）→ 清华 TUNA 镜像优先、
GitHub 官方源兜底。包大小与校验和都核对后才落盘，解压后由 Probe 重新探测。

### 游戏数据目录（.minecraft 等效）

设置页「存储」可以直接输入或**浏览**选择。目录结构就是标准 .minecraft 布局
（versions / libraries / assets / mods），把路径指到已有的 `.minecraft` 即可接管。

### 启用 / 禁用 / 移除

| 动作 | 做法 | 为什么 |
|---|---|---|
| 禁用 | `x.jar` → `x.jar.disabled` | 加载器只认 `.jar`，改名即生效，不动文件内容 |
| 移除 | 移到 `mods/.ecl-trash/<时间戳>-<文件名>` | 300 MB 的整合包 jar 不该因为点错一下就永久没了 |
| 导入 | 先验证是能打开的 zip，重名加 ` (2)` 后缀 | 不覆盖用户已有文件 |

不匹配会明确提示：Fabric 模组跑在 Quilt 上视为兼容，Forge 与 Fabric 互不通用，
原版版本上有启用的模组时会直接说「这些不会生效」。

---

## 主题

设置页「外观」里有三套主题风格 × 五种强调色（绿 / 蓝 / 红 / 白 / 紫），点「保存设置」后
启动器自动重启一次以应用（配色在样式解析时就烧进模板，所以是重启而非热切换）。

| 风格 | 外观 | 实现 |
|---|---|---|
| 原版风 | 深色（默认） | 纯 WPF 配色表 |
| 毛玻璃 | 亚克力 + 半透明 + 圆角 | `SetWindowCompositionAttribute`(ACRYLICBLURBEHIND) + `DwmSetWindowAttribute`(圆角) |
| Windows 风 | 浅色卡片 | 亮色配色表 |

- 毛玻璃需要 **Windows 10 1803 或更新**（`ACCENT_ENABLE_ACRYLICBLURBEHIND` 的引入版本）。
- **圆角只在 Windows 11 生效**：`DWMWA_WINDOW_CORNER_PREFERENCE` 是 Win11 的 DWM 属性，
  Win10 上调用是静默 no-op，窗口保持直角——这是操作系统限制，启动器没法回填。
- 配色全部集中在 `Themes/ThemeManager.cs` 一张表里，`Theme.xaml` 只留控件模板并用
  StaticResource 引用颜色。启动顺序是**先灌配色、再解析样式**，所以换主题 = 重启进程。

实测（本机 Windows 11）：`--shot` 逐页渲染 + 像素抽样——原版风平均亮度 23（近黑）、
Windows 风 239/240（近白），确实是两套盘；亚克力诊断日志 `blur on, corners round`
（SetWindowCompositionAttribute 成功 + DWM 圆角属性被接受）。

## 微软账户（正版登录）

密码**只在微软官方页面**输入，启动器永远看不到；令牌用 DPAPI 加密后落盘。

登录链路：`MS OAuth → Xbox Live (user.auth.xboxlive.com) → XSTS →
api.minecraftservices.com`，之后 `user_type=msa`，可以进正版服务器。
令牌临近过期时自动续期。

### 推荐：用你自己的 Azure 应用（PKCE + 环回）

填了自己的 client_id 后，点登录 → 系统浏览器 → **登录完自动跳回启动器**，
不用复制任何东西。

#### 0. 先确认你有自己的目录（个人账户必看）

用个人 Microsoft 账户（outlook / hotmail / qq 邮箱）**第一次**登录 Azure 时，微软会把你
接到一个叫 **"Microsoft Services"** 的系统租户（`f8cdef31-a31e-4b4a-93e4-5f571e91255a`）。
那个租户**没有关联目录**，不能注册应用，会直接报：

> 所选的用户帐户在租户"Microsoft Services"中不存在，且无法访问该租户中的应用程序…

或者在打开「创建租户」时直接 401「你没有访问权限」。

这不是点错了，是微软的默认行为，而且**没有"直接建租户"的自助入口** —— 必须先有一个
能用的租户。按"有没有信用卡"分两条线：

**没有信用卡**（只有这些不需要卡）：

| 方案 | 硬伤 |
|---|---|
| [Azure for Students](https://azure.microsoft.com/free/students) | 要学校邮箱，免卡 100 美元额度 + 12 个月免费服务 |
| [M365 Developer Program](https://developer.microsoft.com/microsoft-365/dev-program) | 沙盒租户，**90 天不活跃会停用**，client id 跟着失效，只能自用不能内置 |
| [Entra External ID 试用](https://aka.ms/ciam-free-trial) | 30 天租户，到期删除；且它是 external 租户，能否注册"个人 MSA"类型应用未验证 |

**有信用卡**：

| 方案 | 说明 |
|---|---|
| [Azure 免费账户](https://azure.microsoft.com/free) | 注册顺带建租户，自动全局管理员。**注册不收费**，绑卡只验 1 美元（退），不开付费资源不扣费 |

租户建好后：右上角头像 → **「切换目录」** → 选新租户 → 再去「应用注册」。

#### A. 注册应用（约 3 分钟）

1. [portal.azure.com](https://portal.azure.com) → Microsoft Entra ID → 应用注册 → 新注册
2. 名称随便填（**不能含 Mojang / Minecraft / Microsoft / Live / Xbox / Discord / Hypixel**，
   这是 Mojang 审核的硬规则；叫 `ECL` 就行）
3. 账户类型选**「仅个人 Microsoft 账户」**，重定向 URI **留空**，点注册
4. 进应用 → 「身份验证」→ 添加平台 → **移动和桌面应用程序** → 勾选 `http://localhost`
   （列表里没有就手动添加 `http://localhost`）→ 配置
5. 同一页底部「高级设置」→ **允许公共客户端流 = 是** → 保存

#### B. 拿两个 ID

| 要什么 | 在哪 |
|---|---|
| **应用程序(客户端) ID** | 应用「概述」页第一行 |
| **目录(租户) ID** | 「概述」页第二行（白名单表单要它，容易漏） |

#### C. 申请 Minecraft API 权限（必须，审核要等）

打开 [aka.ms/mce-reviewappid](https://aka.ms/mce-reviewappid)，9 个字段：

| # | 字段 | 怎么填 |
|---|---|---|
| 1 | 已读 EULA | 勾 **Yes** |
| 2 | 联系邮箱 | **必须和 Azure Portal 账户的联系邮箱一致**（会交叉核对） |
| 3 | 请求类型 | **New AppID for Approval** |
| 4 | Application Name | 和你注册的应用同名，遵守上面那条命名禁令 |
| 5 | Application ID | 应用程序(客户端) ID |
| 6 | Tenant ID | 目录(租户) ID |
| 7 | 网站/域名 | 必填。没有官网就用 GitHub 仓库或项目主页 |
| 8 | Justification | 说明这是一个第三方 MC 启动器，用微软账号认证、校验游戏所有权 |
| 9 | 其他信息 | 可留空 |

> **审核是每周一次的**（表单原文 "Submissions are reviewed weekly"），批下来可能要几天到一周。
> 没批之前 `login_with_xbox` 会返回 **403**，ECL 会直接提示去这个表单。
> 重复提交不会更快。

#### D. 填进启动器

把客户端 ID 粘到账户页输入框 → 保存设置。之后登录自动回跳。

| 措施 | 说明 |
|---|---|
| **Authorization Code + PKCE (S256)** | 公共客户端标准流程，二进制里没有 client secret |
| **环回重定向** | `localhost` + 随机空闲端口（RFC 8252），只用一次就关闭 |
| **state 校验** | 随机 128 bit，回调不匹配直接拒绝（防 CSRF） |
| **DPAPI 加密落盘** | `CryptProtectData` + 应用熵 + CurrentUser；换用户或换机器都解不开 |
| **令牌绑定来源** | `accounts.json` 记录 `authFlow` + `clientId`，续期用同一个应用和端点 |
| **日志脱敏** | `--accessToken` 的值写盘前替换成 `<redacted>` |

自检覆盖（`ECL.exe --self-test`）：PKCE S256 对 RFC 7636 官方测试向量、
loopback 在任意路径收码、DPAPI 往返/防篡改、`accounts.json` 无明文。**13 项**。

### client_id 是必填的（1.0 起）

源码里 **`DefaultClientId` 现在是空字符串**：不填就登不了正版，启动器会直接告诉你
去哪填（账户页有「去填 client_id」按钮，字段在 设置 → 微软账户）。理由见下面
「公开发布前必读」——把微软官方启动器的 id 打进公开仓库等于冒用身份。

自己注册一个 Azure 应用的 7 步（`portal.azure.com`）：

| 步 | 操作 |
|---|---|
| 1 | 应用注册 → 新注册 |
| 2 | 名称随便填（不能含 Minecraft / Mojang / Microsoft / Xbox） |
| 3 | 账户类型选「仅个人 Microsoft 账户」，重定向 URI 留空 |
| 4 | 身份验证 → 添加平台 → 移动和桌面应用程序 → 勾 `http://localhost` |
| 5 | 同页底部「允许公共客户端流」设为「是」→ 保存 |
| 6 | 复制「应用程序(客户端) ID」 |
| 7 | 申请 Minecraft API 权限（`aka.ms/mce-reviewappid`，白名单批下来前登录会返回 403） |

<details>
<summary>历史：早先内置官方 id 时实测到的坑（留作记录）</summary>

内置 `00000000402b5…（后四位省略）` 能跑，但体验差：

- 它只注册了 `https://login.live.com/oauth20_desktop.srf`，**拒绝任何 loopback**
  （实测 `http://localhost:<port>`、`http://127.0.0.1:<port>`、无端口 `http://localhost`
  全部 `invalid_request: redirect_uri is not valid`）。
- **设备码流不可用**：申请 8 位码成功，兑换令牌时 `invalid_grant`
  （`MBI_SSL` 和 `XboxLive.signin` 两种 scope 都试过，实测两次）。
- 所以只剩「授权页 + 手动粘贴回调 URL」一条路，而那个页面会警告
  「Microsoft 绝不会要求你复制或分享此 URL」——等于教坏用户，不该拿它当默认。

结论：**「不注册 Azure」和「不复制敏感 URL」在微软当前策略下互斥**。

</details>

### ⚠ 公开发布前必读

`00000000402b5…（后四位省略）` 是**微软给 Minecraft 官方启动器**的 client id，只适合自己用：

1. 第三方内置它等于冒用官方应用身份。PCL2、HMCL、Prism **都没有**内置它，
   而是各自注册应用并申请 Mojang 白名单。
2. 微软随时可以收紧（设备码流程被拒就是信号）。一旦收紧，所有内置它的启动器同时失效。
3. 用户把账号授权给一个冒用官方身份的应用，不是好实践。

公开分发走这两条之一：

- **每个用户自带**：把 `MicrosoftAuthService.DefaultClientId` 改成 `""`，
  用户在账户页填自己的 client_id（就是上面那 6 步）。
- **项目方自带**（推荐，用户零配置）：你注册一个 Azure 应用、拿到 Minecraft API 权限，
  把 `DefaultClientId` 改成**你自己的** client id。所有用户开箱即用，且合规 ——
  这正是 PCL2 / HMCL / Prism 的做法。

**无论哪种，都不要把 `00000000402b5…（后四位省略）` 打进公开版本。**

> 1.0 起源码里仍保留这个字符串，但它**只用于识别老配置**：`
> settings.json` 里已经有它的用户，继续走「粘贴回调 URL」那条路，不会被静默改坏。
> 默认值是空字符串，新装用户拿不到它，也不会被引导去用它。

---

## 版本自动探测 · `eclversions.config`

启动、点刷新、每次安装结束后，ECL 会扫一遍 `<数据目录>/versions/`，把结果写到
数据根目录的 **`eclversions.config`**（JSON，可直接看/可被别的工具读）。

它记录的不只是 ECL 自己装的 —— **别的启动器留下的档案也会被认出来**：

| health | 含义 |
|---|---|
| `ready` | json 能解析，且自身或父版本有客户端 jar → 可以启动 |
| `missing-jar` | 有 json 但找不到 jar |
| `missing-json` | 只有目录，没有版本 json |
| `broken-json` | json 存在但解析失败 |

```json
{
  "generatedAt": "2026-10-25T16:26:59+08:00",
  "dataRoot": "C:\\Users\\15332\\AppData\\Roaming\\EternityCraft",
  "scanned": 3,
  "ready": 3,
  "versions": [
    {
      "id": "1.20.1-forge-47.4.28",
      "health": "ready",
      "kind": "forge",
      "baseVersion": "1.20.1",
      "loaderVersion": "47.4.28",
      "type": "release",
      "mainClass": "cpw.mods.bootstraplauncher.BootstrapLauncher",
      "inheritsFrom": "1.20.1",
      "hasJar": true,
      "hasJson": true,
      "installedByEcl": true,
      "modCount": 0,
      "lastWrite": "2026-10-25T16:20:35.9257575+08:00"
    }
  ]
}
```

- `installedByEcl=false` 说明这个档案不是 ECL 装的（PCL2/HMCL/手动拷贝），但仍会被列出并可启动。
- 只列 `ready` 的版本会进主页列表；其余状态只在 config 和日志里提示，不会污染界面。
- 手动核对：`ECL.exe --scan-versions --log scan.log`。

---

## 作者

| | |
|---|---|
| 开发者 | **ChenchenMC**（中国） |
| 邮箱 | 595802821@qq.com / 153326010@qq.com |
| 性质 | 国际性 Minecraft: Java Edition 第三方启动器，不附带任何游戏本体文件 |

与 Mojang Studios、Microsoft Corporation 无隶属、赞助或授权关系。
Minecraft 是 Mojang Studios 的商标。

## 用户协议（六种语言）

首次启动（以及协议版本号提升后的第一次启动）会弹出「用户协议与免责声明」。
窗口顶部的语言选择器提供 **6 种**语言，切换后**正文和界面文案一起变**
（标题栏、语言标签、按钮、底部提示、回落提示都跟着走）。

| 代码 | 语言 |
|---|---|
| `zh_cn` | 简体中文 |
| `zh_hk` | 繁體中文（香港）—— 用词偏港式（軟件 / 用戶 / 私隱） |
| `zh_tw` | 繁體中文（台灣）—— 用词偏台式（軟體 / 使用者 / 隱私） |
| `ja_jp` | 日本語 |
| `en_gb` | English (UK) —— 英式拼写（authorised / licence / centre） |
| `en_us` | English (US) —— 美式拼写（authorized / license / center） |

**为什么只有 6 种**：法律文本（尤其免责条款）机器翻译出错是有实际后果的。
宁可只给能保证质量的版本，也不给 142 份看着像那么回事的机器译文。

想加别的语言时不用重新编译，丢一个 JSON 就行：

```
<数据目录>/terms/ko_kr.json
{
  "display": "한국어",
  "title": "이용 약관 및 면책 조항",
  "subtitle": "...",
  "languageLabel": "언어",
  "acceptHint": "...",
  "acceptButton": "동의하고 계속",
  "declineButton": "종료",
  "fallbackNotice": "...",
  "blocks": [ { "heading": "1. ...", "body": "..." } ],
  "signature": "개발자: ChenchenMC"
}
```

外部文件优先于内置译文；缺的界面字段会复用英文那份，格式错了则整体忽略并回落。

其他行为：

- 同意 → 写盘 `termsAccepted=true` + `termsVersion`，之后不再打扰
- 拒绝 → 直接退出，**不会留下半可用的界面**
- 重新触发：改 `settings.json` 的 `termsVersion` 为 0，或调高 `TermsText.Version`
- 自动化截图：`ECL.exe --shot-terms out.png [--terms-lang zh_hk]`
- `--shot` 系列自动跳过协议弹窗
- 自检覆盖：6 份译文非空、6 份正文互不相同、未知语言回落英文

## 游戏语言

设置页「游戏 → 游戏内语言」，内置 **142 种** Minecraft 1.20.1 官方语言
（从 vanilla asset index 提取，带本地名，显示成「简体中文 (zh_cn)」这种格式）。

| 选项 | 行为 |
|---|---|
| **自动**（默认） | 按 Windows 显示语言映射：`zh-CN → zh_cn`、`zh-TW/HK → zh_tw`，其余精确匹配后再按两字母前缀回落，最后 `en_us` |
| 具体语言 | 启动游戏时追加 `--lang <code>` |

不选「自动」时才会显式传 `--lang`；「自动」交给 JVM 的 locale，行为和原版一致。
设置页会实时显示解析结果（「跟随系统（简体中文 · zh_cn）」或「启动时传 --lang ja_jp」）。

验证：`ECL.exe --launch-command 1.20.1` 的输出里应含 `--lang zh_cn`；
自检里也有三项覆盖（表长度 142、系统语言可解析、显式选择优先）。

---

## 功能

- ✅ 主页：账户卡片 + 已安装版本选择 + 一键启动
- ✅ 安装页：Minecraft 版本列表（正式版/快照过滤）+ 五种加载器，Forge 可同时挂 OptiFine
- ✅ 账户页：多账户管理（微软 / 离线），切换、删除、当前标记
- ✅ **微软正版登录**：自备 Azure 应用走 PKCE + 环回（浏览器登录后自动回跳）；内置官方 id 仅作自用兜底
- ✅ 原版 / Fabric / Quilt / Forge / OptiFine 全链路安装与启动
- ✅ 模组管理：按版本隔离、读写 jar 元数据、启用/禁用/导入/回收站、加载器不匹配提示
- ✅ Modrinth 搜索与一键安装（CurseForge 需要 API 密钥，未接入）
- ✅ 主题：三套风格 × 五强调色，保存后自动重启生效
- ✅ 并行下载 + `.part` 断点续传 + 已存在文件按大小跳过
- ✅ 多 Java 探测 + **缺失时自动下载 Temurin JRE**（8/17/21，清华镜像优先，SHA-256 校验）
- ✅ 游戏数据目录可自定义（浏览选择，标准 .minecraft 布局，可直接接管旧目录）
- ✅ 启动参数按 vanilla 规则替换，含 rules 过滤与架构过滤
- ✅ 实时日志（原始 XML 落盘，UI 显示清洗后的文本）
- ✅ 可选镜像加速（BMCLAPI），官方源作为回退
- ✅ 无界面模式：`--install` / `--install-loader` / `--launch` / `--launch-command` /
  `--mods` / `--mod-import` / `--mod-toggle` / `--mod-trash` / `--shot`

暂不支持：模组仓库搜索与自动下载依赖、Java 运行时自动下载。

---

## 任务菜单

状态栏右侧会出现「N 个任务进行中」，点开是一个任务面板：

| 状态 | 点色 |
|---|---|
| 进行中 | 绿 |
| 完成 | 灰 |
| 失败 | 红 |
| 已取消 | 黄 |

每行显示任务名、**最新一条日志**（这就是进度 —— 下载路径本来就会打印 `4.0 / 41.8 MB`）、
状态文字，以及**进行中才出现的「取消」按钮**。

- 覆盖的操作：安装版本（含加载器）、启动游戏
- 最多保留 20 条，超出自动丢弃最旧的；「清空已完成」一键清理
- 取消走的是各操作已有的 `CancellationTokenSource`，行为和原来的取消按钮一致
- **游戏跑起来之后不出现在任务里** —— 那是「运行中」状态，强杀进程会丢存档，
  所以启动任务在进程起来的那一刻就标记为「完成：已启动」

实现：`TaskItem` + `MainViewModel.BeginTask/EndTask`，`Log()` 会顺手把最新一行
写进当前任务的 `Detail`，所以不需要给每条下载链路单独接进度回调。

截图回归：`ECL.exe --shot out.png --shot-page 0 --shot-task`
（会把面板填成四种状态并单独渲染成 `out-tasks.png`）。

### 单文件 + 混淆（发布用）

加一个开关就行，产物还是**一个 exe**（约 59 MB），但里面的 `ECL.dll` 已经过 Obfuscar 处理：

```bat
dotnet publish src\ECL.Launcher\ECL.Launcher.csproj -c Release -r win-x64 ^
  --self-contained true -p:PublishSingleFile=true -p:Obfuscate=true -o build\singlefile-obf
```

**顺序问题**：单文件打包会把 dll 嵌进 exe，之后再混淆就来不及了。所以 csproj 里挂了一个
MSBuild target，在 `GenerateSingleFileBundle` **之前**跑 Obfuscar。

**一个坑**：打包器读的是 `obj/<Config>/<TFM>/<RID>/ECL.dll`，不是 `bin/` 里那份。
只替换 `bin/` 的话，`bin/` 看起来是对的、exe 里却是没混淆的原件。target 现在两个都替换。

验证（未压缩版可以直接搜字符串，压缩后搜不了）：

```
exe size 132,213,447
  用户协议与免责声明   readable=False
  版本探测            readable=False
  eclversions.config readable=False
  Forge 安装器        readable=False
```

`ECL.dll` 835,584 → 925,184 字节；exe 62,067,114 → 62,111,506。

| 形式 | 大小 | 说明 |
|---|---|---|
| 单文件（普通） | 59.2 MB | 不传 `Obfuscate` |
| **单文件 + 混淆** | 59.2 MB | 传 `-p:Obfuscate=true`，**发布用这个** |

## 每版本 JVM 参数

主页选中某个版本后，启动按钮上方会出现「JVM 参数」输入框：

- **只对当前选中的版本生效**，存在 `settings.json` 的 `versionJvmArgs` 里（键是版本 id）
- **留空 = 继承**设置页里的全局 `extraJvmArgs`
- 追加在全局参数**之后**，后出现的 `-XX:` 会覆盖先出现的
- 失焦即保存；写盘失败会明确报错（"看起来保存了但没保存"最难查）

```
全局：  -XX:+UseG1GC
版本：  -XX:MaxGCPauseMillis=50
实际：  ... -XX:+UseG1GC -XX:MaxGCPauseMillis=50
```

验证：`ECL.exe --launch-command <版本id>` 打印的命令行里能看到两段都在。

## 长路径

Windows 默认 `LongPathsEnabled=0`，路径超过 260 字符时解压会莫名失败。
`PathGuard` 在 natives 解压和 Java 安装前检查目标路径：

- 系统已启用长路径（`LongPathsEnabled=1`）→ 直接放行，零开销
- 否则路径超过约 240 字符 → 抛带完整路径和解决方案的错误，而不是半路失败

```
natives 目录 的路径太长（271 字符，上限约 240）：...
1. 把游戏数据目录改短一点（设置 → 存储，例如 D:\MC）；
2. 启用系统长路径支持（管理员执行那条 New-ItemProperty）
```

## 模组依赖检查

`RefreshMods` 之后会读每个 jar 声明的依赖，检查**已启用**的模组里有没有满足：

- Fabric / Quilt：`fabric.mod.json` 的 `depends`
- Forge：`META-INF/mods.toml` 的 `[[dependencies.<modid>]]`（只取 `mandatory=true`）
- 平台自身提供的 id（`minecraft` / `java` / `fabricloader` / `quilt_loader` / `forge` / `neoforge`）自动跳过

缺失时模组页顶部显示：

> 缺少前置模组：cloth-config (>=11.0.0) ← Sodium。补上之后游戏才能正常加载。

**只提示，不自动安装** —— 自动解析依赖需要一个模组版本求解器，做不好会装错版本，
比缺依赖更难查。手动补一次比装错强。

排查命令：

```bat
ECL.exe --check-mods 1.20.1 --log mods.log
```

输出每个 jar 的 id / 平台 / 依赖，最后列出缺失项；有缺失时退出码为 1。

## 模组市场：先选版本，再安装

搜索结果不再"点一下装最新版"，而是**点「选版本」进到版本列表**：

- 只列**适配当前游戏版本 + 加载器**的版本（筛选走 API 参数，不是本地过滤）
- 默认只显示**正式版**，勾选「显示测试版 / 内测版」才出现 beta / alpha
- 每行显示：版本号、通道、目标（`1.20.1 · fabric`）、文件大小、下载次数
- 安装后自动做一次依赖检查，缺前置会弹窗

## 依赖弹窗

安装完一个模组后，如果它依赖的模组不在已启用列表里，弹出：

```
缺少 2 个前置模组
游戏启动时会因为缺这些模组而崩溃。现在补上，还是稍后自己处理？

  cloth-config (>=11.0.0)          被 Sodium 需要      [去安装]
  fabric-api                       被 Iris Shaders 需要 [去安装]

                                          [稍后再说]
```

「去安装」会：关弹窗 → 打开市场 → 用 mod id 搜索 → 自动进第一个结果的版本列表。
**两步解决一个依赖**，且每一步都能看清版本号。

平台自带的 id（`minecraft` / `forge` / `fabricloader` / `quilt_loader` / `neoforge`）不参与检查。

## 版本维护：校验 / 补全

主页选中版本后，JVM 参数下面有三个按钮：

| 按钮 | 作用 |
|---|---|
| **校验文件** | 按版本 json 逐个核对 libraries / assets 的**大小**和 **SHA-1** |
| **补全文件** | 重新跑安装计划，native 下载器会跳过已正常的文件，只补缺失/损坏的 |
| **打开目录** | 打开这个版本的 game 目录 |

校验时先比大小（免费），只有大小对得上才算 SHA-1，所以正常安装很快：

```
1.20.1：3650 个文件 / 699 MB → 8.6 秒，0 问题
1.7.10：721 个文件 → 0 问题
删掉 1.7.10.jar 后：1 个缺失 → 退出码 1，明确指出 versions\1.7.10\1.7.10.jar
```

失败项在启动按钮上方列出（最多 110 px 高，超出滚动），不会把按钮顶出屏幕。

命令行等价物：

```bat
ECL.exe --verify 1.20.1 --log verify.log     :: 有问题时退出码 1
ECL.exe --check-mods 1.20.1 --log mods.log   :: 依赖缺失时退出码 1
```

## 游戏语言（正确做法）

**Minecraft 客户端没有 `--lang` 参数**。传了只会得到：

```
[Client thread/INFO]: Completely ignored arguments: [--lang, zh_cn]
```

语言是从 `options.txt` 读的，所以 ECL 在启动前改这个文件：

```ini
version:3465
autoJump:false
...
lang:zh_cn
```

- 只改 `lang:` 那一行，其他设置原样保留
- 文件不存在时创建一份最小的（游戏启动后会补全其余项）
- 写不进去（目录只读）不阻止启动，只在日志里警告

### 「自动」不写文件，切回去还会撤销

| 设置 | 行为 |
|---|---|
| **自动**（默认） | **完全不碰 options.txt** —— 游戏本来就跟系统语言 |
| 具体语言 | 写 `lang:<code>`，并在 settings.json 记录 `lastAppliedLanguage` |
| 从具体语言切回自动 | 如果 options.txt 里的 `lang:` 仍等于我们记的那个值 → **删掉这一行** |

为什么要记 `lastAppliedLanguage`：

- 不记 → 每次启动都强制写，**玩家在游戏里改语言会被改回来**，永远改不动
- 记了 → 切回自动时能精确撤掉自己写的那行；如果玩家在游戏里改过（值对不上），**不碰**

三条路径都实测过：

```
auto + 残留 lang:zh_cn   → 启动后 0 行，标记清空
设成 ja_jp               → options.txt 出现 lang:ja_jp，标记 = ja_jp
再切回 auto              → 那行被移除，标记 = ""
```

验证：`--launch` 后看 `versions/<id>/game/options.txt`，
日志里 `Completely ignored arguments` 的计数是 **0**。

## 1.20.2+ 与离线账户

**1.20.2 及更新版本，用离线账户启动会崩**，与本启动器无关：

```
Failed to verify authentication
com.mojang.authlib.exceptions.InvalidCredentialsException: Status: 401
  at YggdrasilUserApiService.fetchProperties
  at YggdrasilAuthenticationService.createUserApiService
  at net.minecraft.client.main.Main.main
```

根因：1.20.2 把 `createUserApiService` 放进 `Minecraft` 构造函数里且**不捕获失败**，
假 token（`--accessToken 0`）必然 401。**1.20.1 及更早也 401，但捕获后继续**，只影响皮肤。

ECL 的处理：启动前检测「离线账户 + 1.20.2+」，直接给出说明而不是让它崩：

> 1.20.2 及更新的版本在离线账户下会在启动时崩溃（游戏内部认证失败，与本启动器无关）。
> 请改用微软正版账户登录，或选择 1.20.1 及更早的版本。

判据：`LaunchService.OfflineBlocked(account, baseVersion)`（按版本号数值比较，不是字符串）。
自检里有四条覆盖：`1.20.1` 放行、`1.20.2` 拦、`1.21` 拦、`1.7.10` 放行。

> `1.21` 这种没有 patch 段的版本曾经被误判成"安全"（旧逻辑要求 `parts.Length >= 3`），
> 用 1.21 的玩家不会被提醒就直接崩了。

## 内存设置过大

`-Xmx` 是**上限不是预分配**——32 GB 的机器跑 `-Xmx60000M` 照样能起（实测）。
所以 ECL 只**警告不阻止**：

```
警告：内存分配 60000 MB 超过本机物理内存（32557 MB）的 75%。
游戏仍能启动，但加载大型整合包时容易 OOM 或让系统频繁使用页面文件。
建议改成 16278 MB 左右。
```

判据：`LaunchService.MemoryWarning(settings)`，阈值是物理内存的 75%
（`GC.GetGCMemoryInfo().TotalAvailableMemoryBytes`，在容器里会自动用 cgroup 限制）。

## 重装加载器不会动存档

Forge 的安装器如果发现 `versions/<id>/<id>.json` 已经存在，会**跳过所有 processors**，
产出缺 srg/extra/client jar 的坏档案。所以重装前必须清掉上一次的产物。

**只清安装器自己写的文件，`game/` 一律不动**：

```
versions/1.20.1-forge-47.4.28/
├── 1.20.1-forge-47.4.28.json    ← 删（否则 installer 跳过）
├── 1.20.1-forge-47.4.28.jar     ← 删
├── .installed                    ← 删
├── natives/                      ← 删
└── game/                         ← 保留（saves / mods / options.txt）
```

早先的实现是 `Directory.Delete(versionDir, recursive: true)`，重装一次就把
玩家的存档和模组全删了。**这是数据丢失级别的 bug，已修**，并且在探针文件上验证过：

```
装之前： game/mods/PROBE-mod.jar, game/saves/probe-world/level.dat
装之后： 两个都还在
```

## 发布体积

| 形式 | 大小 | 需要 .NET 10 Desktop Runtime |
|---|---|---|
| `dist.zip`（8 个文件） | **2.2 MB** | 是 |
| `EternityCraftLauncher-lite/ECL.exe`（单文件） | **1.9 MB** | 是 |
| `EternityCraftLauncher-single/ECL.exe`（单文件自带运行时） | **59 MB** | 否 |
| `ECL-Setup.exe`（自解压，内含 lite） | **2.6 MB** | 是（安装器会先检查） |

`--self-contained false` + `PublishSingleFile` 得到 1.7 MB；带上运行时是 59 MB，
差的 57 MB 全是 .NET 与 WPF。

**注意**：`EnableCompressionInSingleFile` 只对 self-contained 有效，否则报 `NETSDK1176`。
csproj 里已经按 `SelfContained` 分开设。

## 微软登录：只有设备码这一条路

内置的 2013 Live public client id（`00000000402b5…（后四位省略）`）**只接受**
`https://login.live.com/oauth20_desktop.srf` 这一个 redirect，

- v2.0 authorize + `http://localhost:<port>` → `invalid_request: redirect_uri is not valid`
- 无端口的 `http://localhost` 也一样
- v2.0 `/devicecode` → `AADSTS700016`（它不是 AAD 应用）

而 `oauth20_desktop.srf` 是微软自己的页面，第三方 app 监听不到。
**所以授权码流走不通，只能走设备码**。

### 流程

1. 点「使用微软账户登录」
2. 后台 `POST login.live.com/oauth20_connect.srf`，拿回 8 位码
3. 界面显示大号代码 + 「复制代码」+「打开 microsoft.com/link」
4. 用户在浏览器输入，启动器轮询 `oauth20_token.srf`
5. 成功 → Xbox Live → XSTS → Minecraft services → 存账户

实测（`--device-code`，只申请不轮询）：

```
client id : 00000000402b5…（后四位省略） (legacy=True)
正在申请微软登录代码…
USER CODE : 2RFQ3ZPS
VERIFY AT : https://www.microsoft.com/link
EXPIRES   : 2026-10-28 14:04:23
RESULT: device code issued           (0.75 秒)
```

### 曾经错在哪

`MainViewModel` 里曾有一套「打开网页 → 登录完复制地址栏 → 粘贴回来」的实现，
判断条件也是 `UsesLegacyLiveClientId`，但和 `MicrosoftAuthService.InteractiveLoginAsync`
里的设备码流程**是两套东西**。UI 走的正是那条粘贴路线，而它依赖的
`oauth20_desktop.srf` 回调第三方拿不到，于是「复制网页登录」永远失败。

现在统一：ViewModel 只调 `InteractiveLoginAsync`，设备码通过
`IProgress<DeviceCodePrompt>` 回传，界面显示代码而不是让你复制网址。

排查命令：

```bat
ECL.exe --device-code --log code.log   :: 只申请设备码，验证端点可达 + client id 被接受
```

## 崩溃诊断与报告

游戏或启动器出问题时，不用让人去翻几千行英文日志。

### 游戏退出后自动解释原因

`CrashAnalyzer` 拿退出码 + 日志最后 200 行做匹配，直接给出中文结论和建议：

| 日志特征 | 结论 |
|---|---|
| `OutOfMemoryError` / `Java heap space` | 内存不足（含该给多少的建议） |
| `Pixel format not accelerated` / `GLFW error 65542` | 显卡驱动不支持 OpenGL |
| `Mixin apply failed` / `Critical injection failure` | 模组 Mixin 冲突 |
| `DuplicateModsFoundException` | 有重复安装的模组 |
| `Missing or unsupported mandatory dependencies` | 缺少前置模组 |
| `NoSuchMethodError` / `NoClassDefFoundError` | 模组或库版本不兼容 |
| `EXCEPTION_ACCESS_VIOLATION` | 访问冲突（驱动/优化模组） |
| `UnknownHostException` / `Connection refused` | 网络问题 |
| `is not a valid mod file` / `ZipException` | 模组文件损坏 |
| 退出码 `0xC0000005` 等 | 按信号给出对应解释 |

**日志证据优先于退出码**：0xC0000005 上面如果有 `OutOfMemoryError`，那是内存问题，
不是驱动问题。匹配不到就直说「没匹配到已知特征」，不编原因。

结论同时写进 UI 日志和落盘的 `logs/<版本>-latest.log`。

### 两个独立的报告工具

都是原生 C++，不依赖 .NET —— 启动器崩了它们照样能跑。

| 工具 | 用途 | 数据 |
|---|---|---|
| `eclcr.exe` | 启动器自己崩溃（未处理异常） | `ECL\crash\crash-*.txt` |
| `eclnfer.exe` | 非致命错误（下载失败、模组读不出来…） | `ECL\errors.jsonl` |

日志页右上角有「崩溃报告」「非致命错误」两个按钮，工具不在时会自动隐藏。

**崩溃报告内容**：时间、版本、系统版本与 build 号、位数、.NET 版本、程序目录、
上下文（UI 线程 / 后台线程）、异常类型与完整堆栈、**内部异常链**、`startup.log` 全文。

**非致命错误**是 JSON Lines，一行一条，可追加可 grep：

```json
{"time":"2026-10-30 22:41:02","version":"1.0","kind":"mod","message":"无法读取 sodium.jar 的元数据","detail":"..."}
```

两个窗口都能「复制全部」和「打开所在文件夹」。

### 测试钩子

```bat
ECL.exe --force-crash          :: 在 UI 线程抛异常，验证崩溃报告链路
ECL.exe --auto-accept-terms    :: 自动点「同意」，验证协议→主窗口的交接
```

## 首次启动：同意协议后应用退出

**现象**：第一次运行，协议窗口点「同意」之后启动器直接没了；再启动一次却能正常进。
便携版更糟——**永远进不去**，每次都重新弹协议。

### 两个独立的缺陷叠在一起

**① `ShutdownMode` 没设**

`TermsWindow` 是启动时**唯一**的窗口。WPF 默认 `ShutdownMode.OnLastWindowClose`，
它一关，`Application` 判定"最后一个窗口关了"，直接 `Shutdown()` ——
这时候 `MainWindow` 还没来得及 `new` 出来。

```csharp
// 修：弹窗期间挂住退出，主窗口出来后再恢复
var previousMode = Current.ShutdownMode;
Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
try { accepted = win.ShowDialog() == true && win.Accepted; }
finally { Current.ShutdownMode = previousMode; }
```

`ShotTerms`（截图路径）里早就写了 `ShutdownMode.OnExplicitShutdown`，
说明这个坑踩过，只是**没修正常路径**。

**② 接受标记写到 `%APPDATA%`，便携版读不到**

```csharp
// 旧代码：路径写死
settings.Save(Path.Combine(AppData, "EternityCraft", "settings.json"));
```

便携模式读的是 `ECL\settings.json`，标记却写进 `%APPDATA%` —— 下次启动照旧读不到，
**每次都弹协议，每次都被 ① 杀掉**。这就是"永久进不去"。

改成 `LauncherSettings.ResolveSettingsPath()`，和读取端保持一致。

### 验证

装了便携版，用 `--auto-accept-terms`（测试钩子：自动点「同意」）跑三次：

```
首次   terms prompt: alreadyAccepted=False
       dialog closed: accepted=True
       terms saved to ...\ECL\settings.json      ← 便携位置
       creating main window / main window shown
       → 进程存活，主窗口在

再次   creating main window / main window shown     ← 没有 terms prompt
       → 直接进

常驻   ECL\data\ 生成 accounts.json settings.json versions libraries assets logs
```

`settings.json` 里 `"termsAccepted": true, "termsVersion": 1`。

### 启动日志

启动器旁边会有个 `startup.log`，记录首次启动走到哪一步：

```
[21:17:31.801] creating main window
[21:17:32.836] main window shown
```

GUI 进程没有控制台，主窗口又不一定出得来，出问题时先看这个文件。删掉无妨，下次启动会重建。

## 安装包：ECL-Setup.exe

一个自解压外壳（C++，~2 MB），双击后在**自己所在目录**建一个 `ECL\` 文件夹，
写入三个文件并接力启动：

```
你的文件夹\
├── ECL-Setup.exe
└── ECL\
    ├── ECL.exe            混淆后的启动器（framework-dependent 单文件，1.7 MB）
    ├── ECL.Runtime.jar    Java 探针，放在启动器旁边
    ├── rtcheck.exe        运行时检查（C++，177 KB）
    └── settings.json      首次运行时由启动器生成
        accounts.json
        versions\         各版本的 game 目录
```

**为什么不是 zip**：解压后要用户自己找对的那个 exe，还要先装 .NET。这两步都能省掉。

### rtcheck.exe：替代 Windows 的英文提示

启动器是 .NET 10 + WPF。没装桌面运行时时 Windows 弹的是英文框，说不清要装什么。
`rtcheck.exe` 先跑，自己判断：

| 情况 | 行为 |
|---|---|
| 装了 .NET 10 Desktop | 直接启动 `ECL.exe`，窗口都不闪 |
| 没装 | 弹中文窗口（可切 English），带「下载并安装」按钮 |

检测两条路，任一命中即视为已安装：

```
1. <ProgramFiles>\dotnet\shared\Microsoft.WindowsDesktop.App\10.*
2. HKLM\SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App
   （32 位进程要带 KEY_WOW64_64KEY，否则被重定向到 WOWOW6432Node）
```

只查 `Microsoft.NETCore.App` 是不够的——那是控制台运行时，WPF 需要 WindowsDesktop.App。

两个测试开关（正常使用用不到）：

```bat
rtcheck.exe --check-only      :: 只探测：0 = 有，2 = 没有，不弹窗
rtcheck.exe --force-missing   :: 假装没装，用来检查窗口布局
```

### 重装 / 更新

再跑一次 `ECL-Setup.exe` 就是更新：**只覆盖那三个程序文件**，其他东西一律不碰。

如果 `ECL.exe` 正开着，写入会失败并提示「先关闭启动器」——不会留下半更新的目录。

### 测试记录（2026-10-29）

| 场景 | 做法 | 结果 |
|---|---|---|
| 全新安装 | 空目录双击 | `ECL\` 下 3 个文件，rtcheck 接力启动，GUI 截图 48,285 字节 / 绑定错误 0 |
| 重装更新 | 装两次，中间塞 `versions/my-test-version/game/mods/KEEP.jar` | 程序文件时间戳更新，`KEEP.jar` 与自定义文件**都还在** |
| 启动器开着时重装 | 先起 ECL.exe，再跑 setup | `setup.log: FAILED ECL.exe: error 32`（共享冲突）+ 「先关闭启动器」提示 |
| 缺少 ECL.exe | 改名后跑 rtcheck | 日志 `launcher not found: ...`，窗口提示后退出 |
| 运行时检测 | `rtcheck --check-only` | `detected: folder: 10.0.10`，rc=0 |
| 运行时缺失 | `rtcheck --force-missing` | 走弹窗分支，日志 `NOT installed` |

### 配置放在哪：便携模式

`ECL-Setup.exe` 会在 `ECL\` 下写一个最小的 `settings.json`：

```json
{
  "dataRoot": "data"
}
```

启动器启动时先看**自己旁边**有没有 `settings.json`，有就用它（`LauncherSettings.ResolveSettingsPath`）。
于是所有东西都落在 `ECL\data\` 里：

```
ECL\
├── ECL.exe / ECL.Runtime.jar / rtcheck.exe
├── settings.json      ← 便携标记（只有 dataRoot 一行）
└── data\
    ├── settings.json  启动器写入的完整设置
    ├── accounts.json  账户与令牌（DPAPI 加密）
    ├── versions\      各版本游戏目录
    ├── libraries\ assets\
    ├── runtime\       自动下载的 Java
    └── logs\
```

**整个 `ECL` 文件夹可以直接拷到 U 盘带走。** 相对路径 `data` 是相对 **exe 所在目录**
解析的（不是工作目录——快捷方式的起始位置经常是别的地方）。

删掉 `ECL\settings.json` 就退回 `%APPDATA%\EternityCraft`，两种模式互不干扰。

> **升级提醒**：老版本的存档和版本都在 `%APPDATA%\EternityCraft`。
> 换成便携版之后启动器读的是 `ECL\data`，看起来像"版本没了"——数据没删。
> 需要的话把 `%APPDATA%\EternityCraft` 里的 `versions`、`libraries`、`assets`、`accounts.json`
> 拷进 `ECL\data` 即可。

### 用现成的 .minecraft（最常见的"版本没了"）

启动器只在自己那一个数据目录里找 `versions\`：

| 安装形态 | 数据目录 |
|---|---|
| 安装版 | `%APPDATA%\EternityCraft` |
| 便携版 | `ECL\data` |

所以**把 `.minecraft` 整个替换掉是不会生效的** —— 文件落到了 `%APPDATA%\.minecraft`，
启动器根本没往那儿看。这不是解析失败，是路径不同。

从 1.0 Fix 3 起不用手改设置了：主页显示"还没有安装任何版本"时，会

1. 报出**当前实际查找的目录**（`当前查找目录：…`），一眼看出找的不是你以为的地方；
2. 自动扫几个常见位置（exe 旁的 `.minecraft`、`%APPDATA%\.minecraft`、
   上两级目录的 `.minecraft`、`%APPDATA%\EternityCraft`），找到就显示
   `发现已有 Minecraft 目录：<路径>` 和版本数量，点 **使用这个目录** 直接接管；
3. 目录在别处（`D:\Games\.minecraft`）时用 **选择目录…**，选 `.minecraft`、
   它的 `versions`，或者单个版本文件夹都行。

**接管时会自动关闭「版本隔离」**：那个目录里的版本没有 `versions\<id>\game\`，
开着隔离启动器会新建一个空游戏目录，你的 `mods`、`saves`、`options.txt` 就"不见"了。
关掉后 `--gameDir` 直接指到该目录，一切沿用原样。

> 接管只是**改一行设置**（`dataRoot` + `isolateByVersion:false`），不复制、不移动文件。
> 想反悔就在设置页把数据目录改回去。

### 启动前的完整性检查（闪退多半是缺文件）

外部拷来的目录经常**只有 `versions/<id>`（json + jar），没有 `libraries/` 和 `assets/`**；
下载中途关掉启动器也会留下同样的半成品。以前启动器只看"json 在 + jar 在"就认为能启动，
结果 Java 一秒内抛 `NoClassDefFoundError` 退出，看起来就是"闪退"。

从 1.0 Fix 4 起，点开始游戏会先做一次**存在性 + 大小**扫描（不算哈希，3627 个文件
几十毫秒）：

- 齐全 → 直接启动，日志记 `启动前检查：3627 个文件齐全`
- 缺东西 → 先补齐再启动，界面上是 `补齐 N 个缺失文件…` + 下载进度
- 补不齐 → 明确报错并给出第一个缺失路径，不把 JVM 拉起来

命令行同样是这个逻辑：

```
pre-flight: 3627 files, 3626 missing
pre-flight: repairing 3626 file(s) (~677 MB)
下载游戏文件：52 个文件（77.5 MB）   → 9.8s
下载资源文件：3575 个文件（621.3 MB）→ 192.8s
pre-flight: after repair 0 still missing
spawned: java -Xmx4096M … net.minecraft.client.main.Main …
```

> 下载器本身带三次重试；真正导致"下不全"的是**下载没跑完就退出**。现在下一次启动会自动补齐，
> 也可以在「维护」里手动点 `校验文件` / `补全文件`。

命令行等价物（测试与批处理用）：

```bat
ECL.exe --find-installs --log f.log      :: 列出发现的目录与版本数
ECL.exe --adopt D:\Games\.minecraft      :: 接管（写 settings.json）
ECL.exe --adopt .minecraft --dry-run     :: 只看结果，不写设置
```


### 构建

```bat
tools\rtcheck\build_manual.bat     :: 先有 rtcheck.exe
tools\sfx\build.bat                :: 再打 ECL-Setup.exe
```

两个脚本都**不调用 vcvars64.bat**：它内部会调 `reg.exe` 查注册表，某些环境直接拒绝执行，
结果是 `INCLUDE` 为空、编译报「无法打开包括文件: windows.h」。脚本改为手动拼
`INCLUDE`/`LIB`/`PATH`，并注意：

- **SDK 要挑完整的那个**。装了 WDK 的机器会有 `10.0.28xxx` 这种更新版本号，但 WDK
  **不带 ucrt 头**，必须校验 `ucrt\stdio.h` 和 `um\windows.h` 都存在。
- **.bat 只能写 ASCII**。cmd 按 OEM 代码页读 bat，中文注释会被当成命令执行，
  报 `'heck.exe' 不是内部或外部命令`（`rtcheck.exe` 被截断）。

## Java 依赖（能不能不装 Java？）

**不能** —— Minecraft: Java Edition 就是一个 Java 程序，没有 JVM 就没法跑。
但**用户不需要自己装**：ECL 会自动下载。

### 自动下载（默认开启）

设置里 `autoDownloadJava` 默认 `true`。首次启动某个版本时：

1. 读该版本的 `javaVersion.majorVersion`（1.20.1 → 17，1.7.10 → 8）
2. 按版本映射到 LTS：`≤8 → 8`，`≤17 → 17`，其余 → `21`
3. 从 Adoptium 查 Temurin **JRE**（不是 JDK，只要运行时）
4. **清华 TUNA 镜像优先**，失败回落官方源
5. 大小 + **SHA-256 双重校验**
6. 解压到 `<数据目录>/runtime/java-<major>/`，之后复用

实测（本机，2026-10）：

```
查询 Temurin Java 17（JRE x64）…
下载 Java 17（OpenJDK17U-jre_x64_windows_hotspot_17.0.20.1_1.zip）
  41.8 MB / 用时 4.4 秒
SHA-256 校验通过
Java 17 安装完成：...\runtime\java-17
OK: ...\runtime\java-17\bin\javaw.exe
```

手动触发（不需要开界面）：

```bat
ECL.exe --ensure-java 17 --log java.log
```

### 完全离线怎么办

`JavaLocator` 会扫描 `<数据目录>/runtime/*/bin/java.exe`（以及其下一层子目录）。
所以把任意 JRE 放进去就能被认出来：

```
<数据目录>/runtime/java-17/bin/javaw.exe
<数据目录>/runtime/java-8/bin/javaw.exe
```

做离线发布包时，把 JRE 目录一起打包，首次运行前解压到上述位置即可。
体积代价：Java 17 JRE 约 126 MB 解压后（zip 41.8 MB），Java 8 约 100 MB。

只支持 1.20.1 这类现代版本的话，带 **Java 17 一个**就够；要兼顾 1.7.10 还得带 Java 8。

## 单文件版本

```bat
dotnet publish src\ECL.Launcher\ECL.Launcher.csproj -c Release -r win-x64 ^
  --self-contained true -p:PublishSingleFile=true -o build\singlefile
```

产物是**一个 `ECL.exe`**，自带 .NET 10 运行时，双击即用，不需要先装 .NET。

| 项 | 怎么处理 |
|---|---|
| **.NET / WPF 运行时** | `--self-contained true`，全部打包进 exe |
| **ECL.Native.dll**（C++，P/Invoke） | `IncludeNativeLibrariesForSelfExtract=true`，首次运行解压到临时目录，`DllImport` 自动找到 |
| **ECL.Runtime.jar**（Java 引导） | 作为 `EmbeddedResource` 嵌入；启动时若 exe 旁边没有，就提取到 `<数据目录>/cache/ECL.Runtime.jar` 并复用它 |
| **体积** | `EnableCompressionInSingleFile=true`，约 **59 MB**（不压缩约 150 MB） |

体积对比：

| 形式 | 大小 | 前提 |
|---|---|---|
| 单文件 | 59 MB | 无，双击即用 |
| 多文件（`dist/`） | 1.6 MB | 需装 .NET 10 Desktop Runtime |
| 混淆 + 多文件 | 1.7 MB | 同上 |

开机/启动会慢一点（首帧前要解压 natives），后续运行正常。

验证：

```
PASS  native core loadable  - ECL.Native 0.1.0
=== ALL PASS ===   （20 项）
```

单文件版的协议页截图与多文件版 **MD5 完全一致**（`1af8774ac968`），说明 WPF、资源、绑定在单文件模式下没有差异。

## 版本号

正式版了，名字里不再有 `Beta`，前面也不加 `v`——号本身就是状态：

| 形态 | 含义 | 数字位 |
|---|---|---|
| `1.0` | 正式版（当前） | `1.0.0` |
| `1.0 Fix 1` … `1.0 Fix 9` | 小更新 / 修复 | `1.0.1` … `1.0.9` |
| `1.1` | 中等更新 | `1.1.0` |
| `2.0` | 大版本 | `2.0.0` |
| `1.0 Build01` … `Build99` | 开发版（不发布） | `1.0.0.1` … `1.0.0.99` |

第十次修复不是 `Fix 10`，而是进位到 `1.1`：

| 第几次修复 | 版本 |
|---|---|
| 1 | 1.0 Fix 1 |
| 9 | 1.0 Fix 9 |
| 10 | 1.1 |
| 19 | 1.2 |

### 改版本号

```bat
scripts\version.bat                 :: 看当前
scripts\version.bat -Bump           :: 小更新一格（Fix → 进位）
scripts\version.bat -Medium         :: 中等更新（1.0 → 1.1）
scripts\version.bat -Major          :: 大版本（1.1 → 2.0）
scripts\version.bat -Dev            :: 开发版（1.0 → 1.0 Build01）
scripts\version.bat -Bump -DryRun   :: 只看会变成什么，不写盘
```

> 踩过的坑：脚本里别用 `$major` / `$minor` 这类变量名——PS 的变量名不区分大小写，
> `$major` 就是参数 `[switch]$Major`，赋值会直接抛
> "无法将 Int32 的 1 值转换为 SwitchParameter"。内部状态统一用 `$cur` 前缀。

一次改写两个地方，别手改：

- `src/ECL.Launcher/ECL.Launcher.csproj` —— 标题栏徽章、关于页、日志、崩溃报告、
  启动参数都从这里取（`InformationalVersion` 给人看；`AssemblyVersion` / `FileVersion`
  给机器看，形如 `1.0.2`，保证单调递增）
- `tools/sfx/payload.rc` —— `ECL-Setup.exe` 的资源管理器属性

`AllRebuild.bat` **默认自动进位**。改 bug 反复试编译时加 `--no-bump`，正式出包不要加。

> 进位发生在编译**之前**（版本号得编进 exe），所以构建失败也会吃掉一个号 —— 这也是
> 迭代时要 `--no-bump` 的原因。

---

## 构建

前置：**Windows 10/11 x64**、Visual Studio 2022/2026（含 C++ 桌面工作负载）、
.NET 10 SDK、JDK 8+（编译 Java 组件）。

### 全量重编（出全套产物）

```bat
AllRebuild.bat
```

九步，顺序不是随便排的：

| 步 | 做什么 | 为什么在这个位置 |
|----|--------|------------------|
| 1 | 清 `bin` / `obj` / `build\javaout` | 只有 `--clean` 才执行 |
| 2 | 编 C++ 核心 → `build\native\ECL.Native.dll` | 后两步都要拿它 |
| 3 | 编 Java 探针 → `build\ECL.Runtime.jar` | csproj 把它当 `EmbeddedResource`，必须在 `dotnet build` **之前** |
| 4 | `dotnet build` → `bin\Release\net10.0-windows` | |
| 5 | 编 `rtcheck` / `eclcr` / `eclnfer` | `dist\` 要带上两个报告工具 |
| 6 | 组 `dist\` + `dist.zip` | `tools\sfx\payload.rc` 嵌的是 `dist\ECL.Runtime.jar` |
| 7 | publish lite（混淆 + 单文件 + 框架依赖） | 装包要嵌这个 exe |
| 8 | publish single（混淆 + 单文件 + 自包含 ~62 MB） | |
| 9 | 链 `ECL-Setup.exe` + 汇总 | |

末尾跑一次 `--self-test`（对自包含单文件版），统计 PASS/FAIL 并打印失败项。

开关：`--clean`（先清中间产物）、`--no-test`（跳过自检）、`--keep-native`
（复用已有 `ECL.Native.dll`，没装 VS 时用）。

产物：

```
build\native\ECL.Native.dll         ~220 KB   C++ 核心
build\ECL.Runtime.jar               ~7 KB     Java 8 探针
dist\ + dist.zip                    框架依赖，未混淆（开发用）
..\EternityCraftLauncher-lite\      混淆单文件，需 .NET 10
..\EternityCraftLauncher-single\    混淆单文件，自包含
..\EternityCraftLauncher-setup\     ECL-Setup.exe + 两个报告工具 + rtcheck
```

### 只改 C# 时

```bat
scripts\build_all.bat
```

编译原生 + Java + UI 并组好 `dist\`，不碰混淆和装包，快得多。

`dist\` 是框架依赖发布，目标机器需要
[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)；
要免安装单目录：

```bat
dotnet publish src\ECL.Launcher\ECL.Launcher.csproj -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o dist-standalone
```

### 构建踩坑

- **`.bat` 只写 ASCII**。cmd 按 OEM 代码页读脚本，中文注释会被当命令执行
  （`'heck.exe' 不是内部或外部命令` 就是 `rtcheck.exe` 被吃掉前两字节）。
- **别用 `vcvars64.bat`**。它会调 `reg.exe`，受限环境里直接失败且 `INCLUDE` 为空，
  报「无法打开包括文件: windows.h」。所有构建脚本自己扫 `vswhere` + SDK 拼环境变量。
- **SDK 版本号骗人**。装了 WDK 时会有 `10.0.28000.0` 这种更新的目录，但它没有 ucrt 头。
  必须校验 `ucrt\stdio.h` **和** `um\windows.h` 同时存在。
- **`tar -f` 不吃盘符路径**。`tar -a -c -f F:\x\dist.zip` 会被当成 `host:path`，
  报 `Cannot connect to F:`。从项目根目录用相对路径。
- **`cl` 的 `/link` 必须放最后**，之后所有参数归 link.exe。


---

## 性能基线

有数字才好谈"优化太差"。`--bench` 把关键路径逐个计时，同一个数据目录前后对比即可。

```bat
ECL.exe --bench 1.20.1 --data D:\MC --log bench.log     :: 不含哈希校验
ECL.exe --bench 1.20.1 --data D:\MC --hash              :: 连全量哈希一起跑
```

**基准环境**：Windows 11 (10.0.26200)、10 个已安装档案（含 Forge / Fabric / OptiFine）、
1.20.1 完整安装（3627 个文件 / 698.8 MB）、JDK 17/18/21 已装。

| 指标 | 数值 | 备注 |
|---|---|---|
| 冷启动：进程 → 主窗口可见 | **1.26 s** | 见下面分解 |
| ├ `new MainWindow()` | 564 ms | XAML 解析整棵界面树 |
| ├ `Show()` 到首帧 | 483 ms | 首次渲染 + 字体 |
| └ 启动器骨架（设置/日志/事项） | 214 ms | |
| 版本探测（10 个目录） | 48 ms | 读 json、判定 health |
| 版本列表映射（10 个可用） | 131 ms | 含模组计数 |
| 写 `eclversions.config` | 19 ms | |
| 构建下载计划（3627 文件） | 45 ms | 含 inheritsFrom 合并 |
| 启动前快速检查（3627 文件） | **77 ms** | 并行后；串行曾是 416 ms |
| 托管堆 / 工作集 | 12.5 MB / 75 MB | |
| 线程数 | 19 | |

**已经做过的优化**：

- 启动前检查从串行 `foreach` 改成 `Parallel.ForEach`（8 并发），
  **416 ms → 77 ms**。只做元数据查询，天然可并行。

**还没做、值得做的**：

- 冷启动 1.26 s 里 **1.05 s 是 XAML 解析 + 首帧渲染**，与业务逻辑无关。
  根因是 `MainWindow.xaml` 一个文件装了 6 个页面（1400+ 行）。拆成每页一个
  `UserControl`、切换时按需加载，预计能砍到 400–600 ms。
- 全量哈希校验没进上表：3627 个文件在带杀软实时扫描的机器上是分钟级，
  所以 `--bench` 默认跳过，要测就加 `--hash`。

---

## 命令行

```bat
ECL.exe --install 1.20.1 --data D:\MC --threads 24 --log install.log
ECL.exe --install-loader fabric 1.20.1 --loader 0.20.1 --data D:\MC --log fabric.log
ECL.exe --install-loader quilt  1.20.1 --loader 0.24.0  --data D:\MC --log quilt.log
ECL.exe --install-loader forge  1.20.1 --loader 47.4.28 --data D:\MC --log forge.log
ECL.exe --install-loader optifine 1.20.1 --loader HD_U_I6 --data D:\MC --log optifine.log
ECL.exe --install-loader forge 1.20.1 --loader 47.3.39 --optifine HD_U_I6 --data D:\MC
ECL.exe --install-optifine-mod 1.20.1 --data D:\MC --log ofmod.log   :: 装进已有的 Forge 档案
ECL.exe --mods "1.20.1-forge-47.2.0" --data D:\MC --log mods.log
ECL.exe --mod-import "fabric-loader-0.20.1-1.20.1" D:\mods\sodium.jar --data D:\MC
ECL.exe --mod-toggle "fabric-loader-0.20.1-1.20.1" sodium.jar off --data D:\MC
ECL.exe --mod-trash  "fabric-loader-0.20.1-1.20.1" sodium.jar --data D:\MC
ECL.exe --mod-search sodium --mc 1.20.1 --loader fabric --data D:\MC --log s.log
ECL.exe --mod-install "fabric-loader-0.20.1-1.20.1" sodium --data D:\MC --log i.log
ECL.exe --shot t.png --theme windows:blue   :: 预览主题（不写入设置）
ECL.exe --install-loader forge 1.7.10       :: 老版本：legacy 安装路径 + 自动下载 Java 8
ECL.exe --launch 1.20.1 --data D:\MC --seconds 30
ECL.exe --launch-command 1.20.1 --data D:\MC --log cmd.log
ECL.exe --shot ui.png --data D:\MC     :: 逐页渲染截图（UI 回归）
ECL.exe --shot page.png --shot-page 2 --shot-version "1.20.1-forge-47.2.0"   :: 定向截图
ECL.exe --bench 1.20.1 --data D://MC --log bench.log   :: 性能基线（关键路径计时）
ECL.exe --find-installs --log f.log                    :: 列出机器上其它 .minecraft
ECL.exe --adopt D://Games//.minecraft                   :: 接管已有安装
ECL.exe --loader-meta fabric 1.20.1                    :: 加载器元数据连通性诊断
ECL.exe --skin-head some-skin.png                      :: 皮肤能否裁成 8x8 头像
```

---

## 目录结构

```
src/
  ECL.Native/            C++ 核心
    include/ecl_native.h   扁平 C ABI
    src/hash.cpp           BCrypt 并行 SHA-1
    src/http.cpp           WinHTTP 下载引擎（连接复用 / Range / 重试）
    src/zip.cpp            ZIP 中央目录解析 + 自研 inflate + CRC32
  ECL.Runtime/java/      Java 组件（Probe / Bootstrap）
  ECL.Launcher/
    Services/            ApiClient / InstallService / LaunchService / JavaLocator
                         FabricInstaller / QuiltInstaller / ForgeInstaller / OptiFineInstaller
                         ModService / VersionResolver / InstalledVersionScanner
                         MicrosoftAuthService / Dpapi / AccountStore
    ViewModels/          MainViewModel（+ .Install / .Mods partial）
    Themes/Theme.xaml    深色主题与控件模板
scripts/                 build_all.bat / build_native.bat
```

---

## 性能设计要点

| 点 | 做法 | 收益 |
|---|---|---|
| 并发下载 | 每线程一个 WinHTTP session，同 host 复用连接 | 省掉 3600 次 TLS 握手 |
| 断点续传 | `.part` + `Range: bytes=N-`，成功后才 rename | 断网不用从头下 |
| 跳过重复 | 先比文件大小再决定是否下载 | 二次安装 0 流量 |
| 哈希 | 每线程复用 BCrypt provider，256 KB 分块 | 避免每文件重开算法句柄 |
| 解压 | 自研 inflate（stored / fixed / dynamic Huffman） | 无 zlib 依赖 |
| 进度 | 原子计数 + 60 ms 节流 | UI 不因进度回调卡顿 |
| 列表 | VirtualizingStackPanel + 回收 | 400+ 版本滚动不掉帧 |

---

## 实测记录（本机 2026-10，官方源）

**原版 1.20.1，24 线程**

| 阶段 | 文件数 | 大小 | 耗时 | 均速 |
|---|---|---|---|---|
| 游戏文件 | 66 | 80.0 MB | 24.1 s | 3.32 MB/s |
| 资源文件 | 3598 | 621.5 MB | 129.8 s | 4.79 MB/s |
| 合计 | **3664** | **701.6 MB** | **154.5 s** | — |

**Fabric 0.20.1 on 1.20.1**：增量 8 个 jar，9.1 s（8.48 MB/s），assets 3598 个全部命中缓存（0.4 s）
**Forge 47.4.28 on 1.20.1**：安装器下载 + 10 个 processor 打补丁，约 2′；classpath 81 项

**启动验证（各跑满 30–45 s 视为成功）**

| 版本 | 关键日志 |
|---|---|
| 原版 1.20.1 | `Backend library: LWJGL version 3.3.1 build 7` |
| Fabric 0.20.1 | `Loading Minecraft 1.20.1 with Fabric Loader 0.20.1` |
| Forge 47.4.28 | `ModLauncher 10.0.9 starting` → `Reloading ResourceManager: vanilla, mod_resources` |
| Quilt 0.24.0 | `Loading Minecraft 1.20.1 with Quilt Loader 0.24.0` → 起窗 |
| OptiFine HD_U_I6 | `OptiFineTweaker: injectIntoClassLoader` → `[OptiFine] Reloading custom textures` |
| **Forge 47.2.0 + OptiFine 模组** | `OptiFineTransformationService.onLoad` → `Targets: 412` → `[OptiFine] Disable Forge light pipeline`，跑满 70 s 无崩溃报告 |
| **Forge 47.3.39 + OptiFine 模组** | 同一条命令装完即启动，跑满 60 s |
| **Fabric + Fabric API** | `Loading 57 mods: - fabric-api 0.92.2+1.20.1` → 资源重载含全部 fabric 模块，跑满 35 s |
| **Forge 1.7.10（legacy）** | 自动下载 Java 8 → `Forge Mod Loader version 7.99.40.1614` → `identified 3 mods` → 资源重载，跑满 40 s 无崩溃 |

完整性核对：`assets/indexes/5.json` 3598 条记录 / 3575 个唯一 hash / 磁盘缺失 **0**
解压正确性：测试包（stored + fixed + dynamic Huffman + 空文件 + 1 MB 混合）与 Python `zipfile` 逐字节一致

**模组链路（2026-10-23）**

| 验证 | 结果 |
|---|---|
| 导入 Fabric 模组 | `fabric-api.jar` → 解析出 `FABRIC / fabric-api / 0.92.2+1.20.1 / FabricMC` |
| 导入到 Forge 档案 | 同一文件报 `compatible with Forge: False`（不匹配检测生效） |
| 禁用 | `fabric-api.jar` → `fabric-api.jar.disabled`，磁盘上确认改名成功 |
| 启用 | 反向改名回 `.jar` |
| 移除 | 移到 `mods/.ecl-trash/20261023-231847-fabric-api.jar`，mods 目录剩 1 个 |
| Forge 47.2.0 + OptiFine 启动 | `OptiFineTransformationService.onLoad`、`Targets: 412`、`[OptiFine] Disable Forge light pipeline` |
| Forge 47.4.28 + OptiFine 启动 | `NoSuchMethodError: ResourceLocation.parse`（已由代码拦在安装前） |

---

## 皮肤与披风

账户页有独立卡片，三来源：

| 来源 | 怎么拿 | 说明 |
|---|---|---|
| 正版 | `api.minecraftservices.com/minecraft/profile` | 需要已登录的微软账户，连披风一起下 |
| LittleSkin | 按用户名查 Yggdrasil | 不需要对方账号；公开皮肤即可 |
| 本地 PNG | 文件选择器 | 只收 64×64 / 64×32，尺寸不对直接报具体像素 |

- 左侧是 **16×32 正面人形预览**（头/躯干/左右臂/左右腿，含第二层皮肤叠加），
  右边是披风；全部按像素合成，不联网的渲染。
- 应用后写 `<dataRoot>/skins/current.json`，并把纹理推给本机 Yggdrasil 服务；
  启动游戏时用 `-javaagent:authlib-injector.jar=<本机地址>` 注入，**不上传任何地方**。
- 主页账户卡的头像是从皮肤 `(8,8)` 裁出的 8×8 脸（含帽子层），缓存在
  `<dataRoot>/skins/heads/<uuid>.png`，离线也显示。

---

## 已知限制

- Forge 47.4+ 与 OptiFine 不共存（上游 bug，ECL 会隐藏这些构建并拦在安装前）
- 关闭「版本隔离」后所有版本共享同一个 `mods/`（老行为，不建议）
- 模组仓库只接 Modrinth；`cdn.modrinth.com` 下载需网络可达（国内部分网络不通，错误会给出直链）
- 毛玻璃圆角仅在 Windows 11 生效；Win10 上窗口保持直角

## 毛玻璃不透明度

设置 → 外观 → **毛玻璃不透明度**（70%–100%，默认 **95%**）。一个值驱动整套配色：
窗口 = 该值，卡片 = +5%，悬停 = +8%，文字始终白色。

- 拉到底（70%）是"磨砂玻璃"，能看见壁纸；
- 拉到顶（100%）就是纯色，字最好认；
- 改完点「保存设置」会重启一次（配色在解析时烘焙进样式，和换主题同理）。

底层：`ThemeManager.AcrylicSurfaces(opacity)` 按 `master + offset` 生成全部 `#AARRGGBB`；
DWM tint 的 alpha 反过来随不透明度下降（`(1-opacity)×1.6`，夹在 4%–45%），
避免"表面已经很实还压一层黑纱"。
- 离线模式无皮肤；微软账户有正版皮肤
- 自动下载的 Java 只覆盖 8 / 17 / 21 三个 LTS；快照要求更高版本时仍会提示手动安装
- 长路径（> 260 字符）未做 `\\?\` 前缀
- Forge 安装依赖官方安装器，进度是它自己打印的文本，无法精确到百分比

---

## 自检

不用联网、不用账号，验证安全原语：

```bat
ECL.exe --self-test --log selftest.log
```

覆盖：DPAPI 往返、密文每次不同（随机 IV）、篡改被拒绝、`accounts.json` 无明文 token、
token 能正确还原、离线 UUID 稳定、损坏密文降级为 null。

本机结果（2026-10-23）：**11 项全部 PASS**。
