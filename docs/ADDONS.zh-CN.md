# ECL 插件与主题包开发指南

面向第三方开发者的完整参考。包含三个 API 的定义、`addon.json` 全部字段与校验规则、
主题包与 C# 插件的完整示例、分发要求与排错表。

对应启动器版本：**1.0.x**（插件契约 `apiVersion = 1`）

---

## 目录

1. [三个 API 的分工](#一三个-api-的分工)
2. [插件装在哪个目录](#二插件装在哪个目录)
3. [ECL Interface API：addon.json 全字段](#三ecl-interface-apiaddonjson-全字段)
4. [主题包：从零到可用](#四主题包从零到可用)
5. [ECL Addition API：C# 插件](#五ecl-addition-api-c-插件)
6. [ECLUi C# API：往界面里插控件](#六eclui-c-api往界面里插控件)
7. [宿主 API 参考（IEclHost）](#七宿主-api-参考ieclhost)
8. [版本兼容与升级策略](#八版本兼容与升级策略)
9. [分发与许可](#九分发与许可)
10. [排错表](#十排错表)

---

## 一、三个 API 的分工

| API | 面向 | 载体 | 是否执行代码 | 能做什么 |
|---|---|---|---|---|
| **ECL Interface API** | 任何语言 | `addon.json` + 资源文件 | 否 | 声明主题配色、元信息、能力声明 |
| **ECL Addition API** | C# | 程序集实现 `IEclAddon` | 是（进程内） | 启动前钩子、日志、后台任务 |
| **ECLUi C# API** | C# | 程序集实现 `IEclUiExtension` | 是（进程内） | 往四个既定插槽注入 WPF 控件 |

三者的关系：Interface 是**数据契约**，另两个是**运行时扩展**。一个插件可以只做主题包（纯数据），
也可以只做 C# 插件，或两者都做（`type: "mixed"`）。

> **安全边界**：C# 插件以**进程内**方式加载，拥有与启动器相同的权限。ECL 不提供沙箱。
> 因此清单强制要求 `apiVersion`，能力列表（`capabilities`）用于声明意图与审计，
> 不构成强制限制。只安装你信任来源的插件。

---

## 二、插件装在哪个目录

```
<数据根>/addons/
    aurora/                 ← 每个插件一个文件夹，名字随意
        addon.json          ← 必须叫这个名字
        Aurora.dll          ← C# 插件的程序集（主题包不需要）
    my-plugin/
        addon.json
        MyPlugin.dll
        Lib/                ← 插件自己的依赖，放子目录也行
```

`<数据根>` 就是启动器里「设置 → 游戏数据目录」显示的那个路径（相当于 `.minecraft`）。

安装后不用重启：

1. 打开 设置 → 外观 → **Addons and theme packs**
2. 点 **导入主题包** 选一个 `.zip`（里面要有 `addon.json`），或手动把文件夹放进上面的目录后再点 **Rescan**
   删除用 **删除选中**，文件会移到 `addons/.trash`，可手动恢复
3. 状态行会显示 `已加载 N 个主题包、M 个插件`；被拒绝的包显示 `另有 K 个被拒绝`
4. 具体原因写在 `startup.log`（在 ECL.exe 同目录）里，形如
   `addon rejected: …/addon.json (theme.colors.Accent = 'purple' is not #RRGGBB)`

---

## 三、ECL Interface API：`addon.json` 全字段

```json
{
  "schemaVersion": 1,
  "apiVersion": 1,
  "id": "aurora",
  "name": "Aurora",
  "version": "1.0.0",
  "author": "ChenchenMC",
  "description": "A purple-on-ink theme.",
  "type": "theme",
  "entry": { "assembly": "Aurora.dll", "type": "Aurora.Plugin" },
  "theme": {
    "baseFamily": "acrylic",
    "baseAccent": "purple",
    "opacity": 0.92,
    "colors": { "Accent": "#7B5CFF", "TextPrimary": "#F2F0FF" }
  },
  "capabilities": ["ui.card"]
}
```

### 字段表

| 字段 | 必填 | 说明 |
|---|---|---|
| `schemaVersion` | 是 | 必须是 `1`。将来格式演进时用于拒绝不认识的版本 |
| `apiVersion` | 否 | 默认 `1`。大于启动器支持值会被拒绝，提示升级启动器 |
| `id` | 是 | 2–48 字符，**小写字母开头**，仅含 `a-z 0-9 . _ -`；必须与全局唯一 |
| `name` | 否 | 显示名；不填用 `id` |
| `version` | 否 | 你自己的版本号，仅用于展示与排错 |
| `author` / `description` | 否 | 展示用 |
| `type` | 否 | `theme`（默认）/ `plugin` / `mixed` |
| `entry.assembly` | 插件必填 | 相对插件目录的程序集文件名 |
| `entry.type` | 否 | 入口类型全名。**留空**则自动取"唯一实现了 `IEclAddon` 或 `IEclUiExtension` 的类型" |
| `theme` | 主题必填 | 见下节 |
| `capabilities` | 否 | 字符串数组，声明能力（如 `ui.card`、`launch.before`、`task.background`） |

### 校验规则（违反即拒绝，不会拖垮启动器）

| 规则 | 拒绝原因示例 |
|---|---|
| `schemaVersion` 必须为 1 | `schemaVersion 99 is not supported (expected 1)` |
| `apiVersion` 不得高于 1 | `apiVersion 2 is newer than this launcher supports (1)` |
| `id` 格式 | `id must be 2-48 chars of a-z 0-9 . _ - and start with a letter` |
| 插件必须给出 `entry.assembly` 且文件存在 | `entry assembly not found: Aurora.dll` |
| 主题必须有 `theme` 块 | `type is theme/mixed but the theme block is missing` |
| 颜色必须是 `#RRGGBB` 或 `#AARRGGBB` | `theme.colors.Accent = 'purple' is not #RRGGBB or #AARRGGBB` |
| `opacity` 必须在 0.5–1.0 | `theme.opacity must be between 0.5 and 1.0` |
| **对比度守卫**：改了 `Bg*`/`Border*` 却没给任何 `Text*` | `theme.colors repaints background/border but sets no Text* colour (would break contrast); override both, or only Accent* keys` |
| 同一 `id` 只能出现一次 | `duplicate id 'aurora'` |

> 对比度守卫是踩过坑加的：只改背景不改文字色，会出现"白字压白底、界面全看不见"。

---

## 四、主题包：从零到可用

### 最短可用版本（9 行）

`addons/aurora/addon.json`：

```json
{
  "schemaVersion": 1,
  "id": "aurora",
  "name": "Aurora",
  "type": "theme",
  "theme": { "colors": { "Accent": "#7B5CFF" } }
}
```

其余颜色继承内置主题（默认 `original` 族 + `green` 强调色）。**只改强调色**是最安全的起点。

### 完整示例

```json
{
  "schemaVersion": 1,
  "apiVersion": 1,
  "id": "aurora",
  "name": "Aurora",
  "version": "1.0.0",
  "author": "你的名字",
  "description": "Purple on ink.",
  "type": "theme",
  "theme": {
    "baseFamily": "acrylic",
    "baseAccent": "purple",
    "opacity": 0.92,
    "colors": {
      "Accent": "#7B5CFF",
      "AccentDim": "#5B3FD6",
      "AccentAlt": "#9D7BFF",
      "AccentTint": "#337B5CFF",
      "OnAccent": "#FFFFFF",
      "BgWindow": "#0E0E16",
      "BgSidebar": "#12121C",
      "BgCard": "#1A1A26",
      "BgPanel": "#15151F",
      "BgInput": "#0B0B12",
      "TextPrimary": "#F2F0FF",
      "TextSecondary": "#B9B4D4",
      "TextMuted": "#7E7899",
      "BorderSubtle": "#2A2740",
      "BorderStrong": "#3B3760"
    }
  }
}
```

### `baseFamily` / `baseAccent` 可选值

| `baseFamily` | 说明 |
|---|---|
| `original` | 原版风（浅色界面） |
| `acrylic` | 毛玻璃（深色，可调透明度） |
| `windows` | Windows 风 |

| `baseAccent` | |
|---|---|
| `green` / `blue` / `red` / `white` / `purple` | |

### 可覆盖的颜色键

| 分类 | 键 |
|---|---|
| 强调色 | `Accent` `AccentDim` `AccentAlt` `AccentTint` `OnAccent` |
| 强调渐变 | `AccentGradient` `AccentGradientHover`（仅支持纯色覆盖，渐变请用内置） |
| 背景 | `BgWindow` `BgSidebar` `BgCard` `BgCardHover` `BgPanel` `BgInput` |
| 文字 | `TextPrimary` `TextSecondary` `TextMuted` |
| 描边 | `BorderSubtle` `BorderStrong` `BorderHover` |
| 语义色 | `Danger` `Warn` `WarnText` `WarnCardBg` `WarnCardBorder` |

**规则**：改了任何一个 `Bg*` / `Border*`，必须同时给至少一个 `Text*`，否则被拒（见对比度守卫）。

### 启用主题包

设置 → 外观 → **Addons and theme packs** → 下拉选择 → 点「保存设置」→ 启动器重启一次生效。

> 主题在窗口创建前合并，所以必须重启；这是设计约束，不是 bug。

---

## 五、ECL Addition API：C# 插件

### 1. 建工程

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <UseWPF>true</UseWPF>
    <LangVersion>latest</LangVersion>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>
  </PropertyGroup>

  <ItemGroup>
    <!-- 在同一台机器上编译时，直接引用已安装的启动器即可 -->
    <Reference Include="ECL">
      <HintPath>$(EclInstallDir)\ECL.dll</HintPath>
      <Private>false</Private>
    </Reference>
  </ItemGroup>
</Project>
```

> `Private=false` 很关键：不要把 `ECL.dll` 复制进插件目录。宿主已经加载了它，
> 多一份副本会产生两个 `IEclAddon` 类型，插件会加载失败（见排错表第 4 条）。

### 2. 写入口类

```csharp
using System;
using ECL.Addons.Api;

namespace MyPlugin;

public sealed class Entry : IEclAddon
{
    private IEclHost? _host;

    // 必须与 addon.json 的 id 完全一致，否则加载器会拒绝
    public string Id => "my-plugin";

    public void OnLoad(IEclHost host)
    {
        _host = host;
        host.Log($"[my-plugin] loaded, launcher {host.LauncherVersion}");

        host.BeforeLaunch += (_, e) =>
        {
            host.Log($"[my-plugin] about to launch {e.VersionId} in {e.GameDir}");
            // 可以在这里做备份、改写配置、注入 JVM 参数（只读视图，见下方说明）
        };
    }

    public void OnUnload() => _host?.Log("[my-plugin] unloaded");
}
```

### 3. `addon.json`

```json
{
  "schemaVersion": 1,
  "apiVersion": 1,
  "id": "my-plugin",
  "name": "My Plugin",
  "version": "1.0.0",
  "author": "你的名字",
  "type": "plugin",
  "entry": { "assembly": "MyPlugin.dll", "type": "MyPlugin.Entry" },
  "capabilities": ["launch.before"]
}
```

`entry.type` 可以留空，加载器会自动找唯一实现接口的类型——**混淆构建下必须留空**，
因为类名会被改写。

### 4. 目录结构

```
addons/my-plugin/
    addon.json
    MyPlugin.dll          ← 只放你自己的程序集，不要带 ECL.dll
```

### 5. 生命周期与线程

| 方法 | 时机 | 线程 |
|---|---|---|
| 构造函数 | 启动器主窗口创建之后 | UI 线程 |
| `OnLoad(host)` | 构造后立即调用 | UI 线程 |
| `BeforeLaunch` 处理器 | 每次启动游戏前，**进程创建之前** | 后台线程 |
| `OnUnload()` | 启动器退出时 | UI 线程 |

**约束**：

- `BeforeLaunch` 里**不要长时间阻塞**——启动流程会等你返回；建议只做毫秒级的事，重活用后台线程
- 任何异常都会被吞掉并写日志，不会阻断启动或退出；但也不要依赖这一点掩盖 bug
- `JvmArgs` 是只读视图，改它不会影响启动参数（v1 不支持注入，若需要请提需求）

---

## 六、ECLUi C# API：往界面里插控件

同一个程序集里再实现 `IEclUiExtension`：

```csharp
using System.Windows;
using System.Windows.Controls;
using ECL.Addons.Api;

public sealed class Entry : IEclAddon, IEclUiExtension
{
    public string Id => "my-plugin";
    public void OnLoad(IEclHost host) { /* ... */ }
    public void OnUnload() { }

    public FrameworkElement? CreateView(EclUiSlot slot) => slot switch
    {
        EclUiSlot.HomeCard => BuildCard(),
        EclUiSlot.HomeToolbar => BuildButton(),
        _ => null          // 不关心的插槽返回 null
    };

    private static FrameworkElement BuildCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = "Hello from a plugin" });
        return panel;
    }

    private static FrameworkElement BuildButton() =>
        new Button { Content = "Plugin action" };
}
```

### 可用插槽

| 插槽 | 位置 |
|---|---|
| `HomeCard` | 主页版本列表下方 |
| `SettingsCard` | 设置页顶部 |
| `AccountRow` | 账户页账户列表下方 |
| `HomeToolbar` | 主页「打开目录」旁的工具按钮区 |

**规则**：

- `CreateView` 在 UI 线程调用，**可能被调用多次**（页面重建），不要缓存 WPF 对象
- 返回的控件继承启动器的 `DataContext`，可直接绑定
- 不支持的插槽返回 `null`，不要抛异常
- 不要主动 `Show()` 窗口或改动不属于你的控件

---

## 七、宿主 API 参考（`IEclHost`）

| 成员 | 类型 | 说明 |
|---|---|---|
| `LauncherVersion` | `string` | 例：`1.0.1` |
| `DataRoot` | `string` | 当前数据根绝对路径 |
| `Log(string)` | `void` | 写入启动器日志，自动加 `[addon id]` 前缀 |
| `BeforeLaunch` | `event EventHandler<EclLaunchEventArgs>` | 启动前触发 |

`EclLaunchEventArgs`：

| 成员 | 说明 |
|---|---|
| `VersionId` | 即将启动的版本 id，如 `1.20.1-forge-47.3.39` |
| `GameDir` | 该版本的运行目录（`versions/<id>/game`） |
| `JvmArgs` | 已组装的 JVM 参数（只读） |

---

## 八、版本兼容与升级策略

- 契约版本：`EclApiInfo.Version = 1`
- 插件声明 `apiVersion` 高于启动器支持 → **拒绝加载**，日志说明原因
- 破坏性变更会提升 `apiVersion`，并保留旧接口一个完整大版本
- 新增插槽/成员属于**向后兼容**变更：旧插件不实现也不受影响

你可以在运行时读 `EclApiInfo.Version` 做能力探测。

---

## 九、分发与许可

- 启动器主体为 **MPL-2.0**（见仓库根目录 `LICENSE`）
- **你的插件是你自己的作品**，可采用任意许可证；不要求开源
- 若你修改了启动器本身（而非写插件），被修改的**文件**必须按 MPL-2.0 开源并保留署名
- 分发时请注明依赖的 ECL 与最低版本，例如：
  `Requires ECL >= 1.0.1 (apiVersion 1)`
- **不要**在插件包里附带 `ECL.dll`、游戏文件、或任何账号令牌

---

## 十、排错表

| 现象 | 原因 | 处理 |
|---|---|---|
| 状态行显示 `另有 N 个被拒绝` | 清单校验失败 | 看 `startup.log` 里的 `addon rejected:` 行，原因写在括号里 |
| `id must be 2-48 chars…` | id 有大写字母或非法字符 | 全小写，仅 `a-z 0-9 . _ -`，字母开头 |
| `schemaVersion 99 is not supported` | 版本号写错 | 填 `1` |
| `apiVersion 2 is newer than this launcher supports` | 插件要的契约比启动器新 | 升级启动器，或把 `apiVersion` 降到 1 |
| `entry assembly not found: X.dll` | 文件名大小写或路径不对 | `entry.assembly` 相对插件目录，注意大小写 |
| `no type implements IEclAddon/IEclUiExtension` | 类没实现接口，或 `entry.type` 写错 | 留空 `entry.type` 让它自动发现 |
| 插件加载后 `Id` 与清单不符 | 两者不一致 | `IEclAddon.Id` 必须等于 `addon.json` 的 `id` |
| 插件里 `as IEclAddon` 恒为 null | 你把 `ECL.dll` 一起打包了，出现两份类型 | 引用时设 `Private=false`，不要复制 `ECL.dll` |
| 主题包不生效 | 选完没重启 | 「保存设置」会重启一次；或检查下拉里是否选中 |
| 主题包被拒：`would break contrast` | 只改背景没改文字 | 同时提供至少一个 `Text*` |
| 界面文字变英文/中文混乱 | 语言是全局设置 | 设置 → Interface language |

排错时优先看这三处：

1. `ECL.exe` 同目录的 `startup.log`（含 `addon rejected:` 与 `[addon …]` 行）
2. 启动器 日志页（插件的 `host.Log()` 输出在这里）
3. 设置页的状态行（已加载 / 被拒绝数量）

---

## 附：最小可运行示例

仓库内 `samples/aurora-theme/` 是一个可直接复制进 `addons/` 的主题包。
复制到 `<数据根>/addons/aurora/` → Rescan → 下拉选中 Aurora → 保存设置。
