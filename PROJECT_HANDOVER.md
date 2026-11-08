# ModManager 项目交接文档

> 生成时间：2026-10-17
> 生成方式：对当前工作区真实代码的静态审计。**本次未运行构建与测试**，所有"通过/失败"结论若来自历史记录均已标注。
> 项目不是 Git 仓库，无法用历史 diff 推断变更；所有结论以文件当前内容为准。

---

## 1. 项目简介与目标

ModManager 是一个 Windows 桌面 Mod 管理器（.NET 8 + WPF + C#），面向从 Nexus Mods 获取 Mod 的玩家。

它做的事情：

- 扫描本机 Steam 库，识别已安装游戏，按游戏定义决定"支持级别"
- 从 Nexus Mods（REST v1 + GraphQL v2）搜索、浏览、下载 Mod
- 把压缩包解压、分析根目录、计划文件落点、事务式部署到游戏目录、失败可回滚
- 管理冲突（同一文件被多个 Mod 提供）、依赖（必需/可选/缺失/循环）、加载顺序
- 完全免费的本地方英译中：Argos 官方语言包 + CTranslate2 推理，独立 Python 进程
- 浅色/深色双主题

设计取向（从代码注释可读出，非推测）：**安全优先于便利**。所有写游戏目录的操作必须经过路径校验、事务日志与备份；宁可明确报错也不静默成功。

---

## 2. 当前状态摘要

| 维度 | 实测值 |
|---|---|
| 解决方案 | `ModManager.sln`，3 个项目（Core / App / Tests） |
| 源码规模 | 106 个 `.cs`（34,455 行）+ 15 个 `.xaml`（4,800 行） |
| 第三方 NuGet 依赖 | **仅 1 个**：SharpCompress 1.0.0（Core） |
| App 项目 UI 框架依赖 | **零**（全部样式自建） |
| 内置游戏定义 | **7 个**（2 通用 + 5 专用） |
| 测试文件 | 26 个，214 个声明测试方法 |
| 本地翻译引擎 | 已安装可用（`translate-en_zh-1_9`，81.7 MB） |
| 构建/测试状态 | **本次未重新验证**（最后已知状态：Rebuild 0 错误，228/228 测试通过 —— 历史记录） |

**关键提醒**：工作区中 `OnlineModsViewModel.cs` 与 `OnlineModsView.xaml` 有一批**尚未经过完整端到端验收**的翻译/列表改动（详见 §10、§11）。

---

## 3. 技术栈与运行依赖

### 3.1 编译期

| 项目 | TargetFramework | 关键属性 |
|---|---|---|
| `src/ModManager.Core/ModManager.Core.csproj` | `net8.0` | `PackageReference SharpCompress 1.0.0`；`EmbeddedResource Services\LocalTranslation\engine_worker.py` |
| `src/ModManager.App/ModManager.App.csproj` | `net8.0-windows` | `OutputType=WinExe`，`UseWPF=true`，**无任何 PackageReference** |
| `tests/ModManager.Tests/ModManager.Tests.csproj` | `net8.0-windows` | `UseWPF=true`；xunit 2.9.2 / Microsoft.NET.Test.Sdk 17.11.1；`InvariantGlobalization=false`（必须，否则 WPF 字体缓存崩溃） |

### 3.2 运行时外部依赖

| 依赖 | 用途 | 位置 | 是否必需 |
|---|---|---|---|
| .NET 8 Desktop Runtime | 运行 | 系统 | 必需 |
| 系统 ICU | WPF 字体/文化 | Windows 自带 `icu.dll` | 必需（缺失则需 invariant 模式，WPF 会挂） |
| Python 3.10+ | 本地翻译引擎 | 系统安装，程序只读不装 | 仅本地翻译需要 |
| CTranslate2 / SentencePiece / NumPy / PyYAML | 翻译推理 | `<Data>\Translation\runtime\Lib\site-packages` | 仅本地翻译需要 |
| Argos 英中语言包 | 翻译模型 | `<Data>\Translation\packages\translate-en_zh-1_9` | 仅本地翻译需要 |

**不需要**：torch / stanza / spacy（这是刻意的设计决定，见 §6.4）。

### 3.3 工具项目（不在 sln 中，需单独 `dotnet run --project`）

| 项目 | 用途 |
|---|---|
| `tools/SmokeRun` | 无头端到端引擎验证（安装/卸载/回滚） |
| `tools/TranslationSmoke` | 本地翻译真实冒烟（`--initialize` / `--appdata`） |
| `tools/NexusProbe` | Nexus API 探测（33 KB，独立程序） |

---

## 4. 目录结构与关键文件索引

```
ModManager/
├── ModManager.sln
├── src/
│   ├── ModManager.Core/            # 引擎：无 WPF 依赖
│   │   ├── AppPaths.cs             # ★ 所有数据目录的唯一来源
│   │   ├── AppSettings.cs          # ★ 设置模型（Nexus/Translation/Deployment/Safety/Ui）
│   │   ├── Abstractions.cs         # ★ 核心接口：IAppLogger/ISecretStore/IPathSafetyService/各 Repository
│   │   ├── Enums.cs                # 全部枚举（ModState/DeploymentMethod/JournalState…）
│   │   ├── SupportRecords.cs       # ★ OperationResult<T>、GameInstance、LogEntry
│   │   ├── NexusModels.cs          # ★ Nexus 全部数据模型 + 凭据
│   │   ├── SteamLibrary.cs         # 扫描结果模型、GameSupportKind
│   │   ├── ModMetadata.cs          # ★ ModMetadata/ModManifest/Profile/ConflictResolution
│   │   ├── Journal.cs              # 事务日志模型与 Undo 动作
│   │   ├── DeploymentPlan.cs       # ★ 计划模型 + FileConflict + PathSafetyVerdict
│   │   ├── InstallPlan.cs          # 根目录分析 / 安装预览模型
│   │   ├── GameDefinition.cs       # ★ 游戏定义模型（ModRoot/Loader/能力）
│   │   ├── Services/
│   │   │   ├── ModInstallService.cs        # ★ 安装编排入口
│   │   │   ├── DeploymentPlanner.cs        # ★ 纯函数计划器
│   │   │   ├── DeploymentApplier.cs        # ★ 事务执行器
│   │   │   ├── JournalService.cs           # ★ 崩溃恢复
│   │   │   ├── ArchiveReader.cs            # ★ SharpCompress 解压 + 安全检查
│   │   │   ├── ModAnalyzer.cs              # ★ 压缩包根目录检测
│   │   │   ├── ConflictService.cs          # 冲突检测/解释
│   │   │   ├── DependencyResolver.cs       # 依赖图解析（MaxDepth=12）
│   │   │   ├── DependencyAndHealthServices.cs  # 缺失依赖/健康检查/外部内容
│   │   │   ├── NexusProvider.cs            # ★ 78 KB，Nexus 全部 API
│   │   │   ├── DownloadManager.cs          # ★ 46 KB，分块下载/恢复
│   │   │   ├── SteamLibraryScanner.cs      # ★ Steam 库发现
│   │   │   ├── GameDetector.cs             # 非 Steam 检测（注册表/常见目录）
│   │   │   ├── GameAdapterRegistry.cs      # 适配器注册
│   │   │   ├── GameCatalogue.cs            # ★ 7 个内置游戏定义
│   │   │   ├── GenericGameAdapter.cs       # 通用适配器（21 KB）
│   │   │   ├── LibraryRepository.cs        # 持久化聚合
│   │   │   ├── SecretStore.cs              # DPAPI 密钥库
│   │   │   ├── ImageCacheService.cs        # 图片磁盘缓存 + 404 记忆
│   │   │   ├── NexusGameImageCache.cs      # Nexus 游戏封面映射（7 天 TTL）
│   │   │   ├── GameArtworkResolver.cs      # ★ 封面来源决策（Nexus→Steam→无）
│   │   │   ├── TranslationService.cs       # ★ 翻译门面（缓存 + 本地/在线调度）
│   │   │   ├── LocalTranslation/           # ★ 本地翻译子系统（见 §6.4）
│   │   │   └── ...
│   │   ├── Safety/
│   │   │   ├── PathSafetyService.cs        # ★ 27 KB，写入/删除的唯一闸门
│   │   │   └── FileSystemOps.cs            # hardlink/symlink P/Invoke
│   │   └── Persistence/JsonDocumentStore.cs
│   └── ModManager.App/
│       ├── App.xaml / App.xaml.cs  # ★ 入口 + 全局异常处理 + 主题应用
│       ├── ApplicationHost.cs      # ★ 手写组合根（无 DI 容器）
│       ├── Theme/
│       │   ├── Fluent.xaml         # ★ 20.8 KB 设计 token
│       │   ├── Controls.xaml       # ★ 40.4 KB 控件样式
│       │   └── AppearanceService.cs # ★ 运行时主题切换
│       ├── ViewModels/             # 13 个文件
│       └── Views/                  # 12 个 .xaml + 转换器 + code-behind
├── tests/ModManager.Tests/         # 26 个文件，214 个测试方法
├── tools/                          # 3 个独立工具项目
└── artifacts/                      # 测试产物与截图（非源码）
```

---

## 5. 整体架构与启动流程

### 5.1 分层

```
WPF Views (XAML)
      ↕ 数据绑定
ViewModels (ModManager.App)   ← 只依赖 Core 的接口
      ↕
ApplicationHost (手写组合根)
      ↕
Core Services / Safety / Persistence   ← 零 WPF 引用
```

**Core 不引用 WPF**（`ModManager.Core.csproj` 只有 `net8.0`），这是硬约束，新代码不要破坏。

### 5.2 启动顺序（`App.xaml.cs`）

```
App ctor
  ├─ FrameworkElement.LanguageProperty 覆盖为当前文化（修复 en-US 缺失导致的绑定崩溃）
  ├─ AppDomain.UnhandledException / DispatcherUnhandledException → 写 startup-error.log
OnStartup
  ├─ ApplicationHost.CreateAsync()
  │    ├─ AppPaths.EnsureCreated()
  │    ├─ 构造全部服务（顺序见 ApplicationHost.cs）
  │    └─ journals.RecoverAsync() + downloads.RecoverAsync()   ← UI 之前完成
  ├─ AppearanceService.Apply(Theme == "Dark")                  ← 窗口构造之前，避免首帧闪烁
  ├─ new ShellViewModel(host)
  ├─ new MainWindow { DataContext = shell }; window.Show()
  └─ await shell.InitializeAsync()                            ← 加载游戏列表
```

### 5.3 MVVM 实现（无框架）

| 组成 | 实现位置 |
|---|---|
| 基类 | `ViewModels/ViewModelBase.cs`：`SetProperty` / `OnPropertyChanged`，实现 `INotifyPropertyChanged` |
| 命令 | 同文件：`RelayCommand` / `RelayCommand<T>`（`CanExecuteChanged` 由 ViewModel 显式调用） |
| 集合 | 同文件：`BulkObservableCollection<T>`（`ReplaceAll` 批量替换，减少通知风暴） |
| 页面契约 | `ShellViewModel.cs` 内 `IPageViewModel { Task RefreshAsync(); }` |
| View 映射 | `App.xaml` 中 `DataTemplate DataType="{x:Type vm:XxxViewModel}"` → `views:XxxView`（view-first） |
| 页面创建 | `PageDescriptor` 持 `Func<IPageViewModel>` 工厂，**首次导航时才构造**（延迟创建） |
| 服务获取 | 无 DI 容器；`ApplicationHost` 手写构造并作为属性暴露，ViewModel 构造参数接收 |

### 5.4 导航

- `ShellViewModel.PageGroups`（`PageGroup { Title, Pages }`）绑定到侧栏 `ItemsControl`
- 每个 `PageDescriptor(Name, GroupTitle, Description, RequiredCapability, Factory)`
- **按活动游戏能力过滤**：游戏没有 `FileConflicts` 能力就没有"冲突"页
- 导航实现：`MainWindow.xaml` 的 `RadioButton` 挂 `Checked="OnNavigationChecked"` → `ShellViewModel.NavigateTo(page.Name)`
  - **注意**：这里不用 `Command`。原因是 `Command` 只在真实点击时触发，UI Automation / 键盘助记符通过 automation peer 设 `IsChecked` 不会触发 Command，曾导致"高亮变了但页面没变"

---

## 6. 功能模块实现说明

### 6.1 游戏库与游戏识别

**调用链**

```
GamesViewModel.ScanAsync()
  ├─ new SteamLibraryScanner().Scan()            → SteamScanResult
  ├─ new GameSupportResolver().ResolveAll(...)   → List<ResolvedGame>
  ├─ new GameDetector() 遍历非 Steam 适配器       → 注册表/常见目录
  └─ LoadGameArtworkAsync() / LoadRegisteredArtworkAsync()
        └─ NexusGameImageCache.GetAsync(nexus, domains)
        └─ GameArtworkResolver.Resolve(steamAppId, nexusDomain, artwork)
        └─ CoverImageLoader.LoadAsync(url)
```

**关键类型**

| 类型 | 文件 | 说明 |
|---|---|---|
| `SteamLibraryScanner` | `SteamLibraryScanner.cs` | 读 `libraryfolders.vdf` 与 `appmanifest_*.acf`，返回 `SteamScanResult` |
| `GameSupportResolver` | 同上 | 把扫描结果分级为 `GameSupportKind`：Supported / Generic / Invalid |
| `GameAdapterRegistry` | `GameAdapterRegistry.cs` | `All()` / `Get(gameId)`，返回 `IGameAdapter` |
| `GameCatalogue` | `GameCatalogue.cs` | **7 个内置 `GameDefinition`** |
| `GenericGameAdapter` | `GenericGameAdapter.cs` | 通用适配器：无定义的游戏也能管理（探针 = 常见 Mod 目录启发式） |
| `GameDefinition` | `GameDefinition.cs` | `Id/Name/SteamAppId/NexusDomain/ModRoot/Capabilities/DeploymentPreference/ModFileExtensions/StripPrefixes/Loader` |
| `GameCapability` | `GameCapability.cs` | 位标志：HardlinkDeployment / SymlinkDeployment / Profiles / FileConflicts / Dependencies / ModLoader |

**当前内置游戏（实测自 `GameCatalogue.cs`）**

| Id | 名称 | SteamAppId | NexusDomain |
|---|---|---|---|
| `generic` | Generic Game (any game) | — | — |
| `bepinex-generic` | Generic Unity Game (BepInEx) | — | — |
| `subnautica` | Subnautica | 264710 | `subnautica` |
| `stardewvalley` | Stardew Valley | 413150 | `stardewvalley` |
| `palworld` | Palworld | 1623730 | `palworld` |
| `skyrimse` | Skyrim Special Edition | 489830 | `skyrimspecialedition` |
| `cyberpunk2077` | Cyberpunk 2077 | 1091500 | `cyberpunk2077` |

**"支持"与"通用"的区别**

- **专用支持**：有独立 `GameDefinition`（带 SteamAppId + NexusDomain + 可能的 ModLoader 定义 + 部署偏好），行为可定制
- **通用模式**：走 `GenericGameAdapter`，ModRoot 由用户选择或启发式推断，能力取默认子集
- **异常**：安装路径不存在 / 无有效 ModRoot 且无法推断 → `GameSupportKind.Invalid`

**封面来源优先级**（`GameArtworkResolver`）

1. 已缓存的 Nexus artwork（仅当该游戏**有** NexusDomain）
2. Steam CDN 按 **SteamAppId**：`https://cdn.cloudflare.steamstatic.com/steam/apps/{appid}/library_600x900.jpg`
3. 无 → 占位

**明确禁止**：按游戏名模糊匹配封面（会误配重制版/DLC）。

**新增一个游戏支持需要改**：在 `GameCatalogue.cs` 增加一个 `GameDefinition` 静态定义（SteamAppId、NexusDomain、ModRoot、Capabilities、可选 ModLoader），必要时在 `GameAdapterRegistry` 注册专用适配器。**不需要改 UI**。

### 6.2 本地 Mod 管理

**两条入口**

| 入口 | ViewModel | 服务 |
|---|---|---|
| 本地 Mod 页（已安装列表） | `ModsViewModel` | `ModInstallService` / `DeploymentApplier` |
| Nexus 下载后安装 | `OnlineModsViewModel` → `DownloadsViewModel` | 同上 |

**安装管线（真实调用链）**

```
ModInstallService.StageAsync(archivePath, gameId)      → StagedMod（解压到 <Data>\Mods\<modId>\）
ModInstallService.PreviewAsync(...)                    → InstallPreview（根分析 + 计划预览）
ModInstallService.InstallAsync(...)                    → DeployResult
   ├─ DeploymentPlanner.BuildPlan(...)                 → DeploymentPlan（纯函数，可预览）
   ├─ JournalService.Open(...)                         → 事务日志
   ├─ DeploymentApplier.ApplyAsync(plan, journal)
   │    └─ 每步：PathSafetyService.ValidateWrite → 备份 → 落文件 → journal.Append
   └─ JournalService.WriteCommit(...) + Clear(...)
```

**关键类型**

| 类型 | 职责 |
|---|---|
| `ModInstallService` | 编排器。`StageAsync` / `PreviewAsync` / `InstallAsync` / `UninstallAsync` / `PurgeStaging` / `BuildModSet` |
| `DeploymentPlanner` | **纯函数**。`BuildPlan` / `BuildPreviewPlan`，输出 `DeploymentPlan`（文件级操作列表） |
| `DeploymentApplier` | 事务执行。`ApplyAsync` / `UndeployAsync`，每步落 `Journal` |
| `JournalService` | 崩溃恢复。`Open/Append/RecordUndo/Transition/FindInterrupted/RecoverAsync` |
| `ArchiveReader` | SharpCompress 解压；`IndexAsync` / `ExtractAsync`；**内置安全限制** `ArchiveLimits`（MaxEntries 200k、单文件 8 GB、总量 64 GB、压缩比 1000、路径长度 32000） |
| `ModAnalyzer` | `Analyze(adapter, index)` / `AnalyzeDirectory` → `RootAnalysis`（推断 Mod 根目录，带置信度信号） |
| `PathSafetyService` | 写入/删除的唯一闸门（见 §6.7） |

**支持的压缩格式**：`ArchiveReader.SupportedFormats` —— zip / 7z / rar / tar / gz 系（SharpCompress 提供；upstream README 说明 RAR 只读）。

**备份与回滚**

- 覆盖前备份到 `<Data>\Backups\<gameId>\<时间戳>-<modId>\`
- 每个被覆盖/删除的文件在 `Journal` 里记录 `UndoAction`（`RestoreOverwrittenFileUndo` / `RemovePlacedFileUndo` / `RestoreDeletedFileUndo` / `RemoveDirectoryUndo`）
- 崩溃后 `JournalService.RecoverAsync` 读取未完成 journal 并回滚

**四个完成度层级（重要）**

| 层级 | 状态 |
|---|---|
| UI 按钮存在 | ✅ 全部主流程 |
| Command 已绑定 | ✅ 已审计：13 个视图共 **86 个 `Command="{Binding …}"`**，均指向真实命令 |
| 服务真实执行 | ✅ 安装/卸载/启用/禁用由 `ModInstallService` + `DeploymentApplier` 真实执行 |
| 端到端运行验证 | ⚠️ `tools/SmokeRun` 覆盖安装/卸载/回滚；**本轮未重新运行** |

### 6.3 Nexus 在线平台

**协议与端点（常量实测自 `NexusProvider.cs`）**

| 常量 | 值 |
|---|---|
| `ApiBase` | `https://api.nexusmods.com/v1` |
| `GraphQlEndpoint` | `https://api.nexusmods.com/v2/graphql` |
| `OAuthAuthorizeEndpoint` | `https://users.nexusmods.com/oauth/authorize` |
| `OAuthTokenEndpoint` | `https://users.nexusmods.com/oauth/token` |

**认证**

- 两种：API Key（`apikey` 头）或 OAuth（Bearer）
- 凭据存 `SecretStore`（DPAPI 加密，`<Data>\secrets.dat`），**绝不写入 settings.json / 日志**
- 密钥名常量：`ISecretStore.NexusApiKey` / `NexusAccessToken` / `NexusRefreshToken` / `TranslationApiKey`

**公开方法（`NexusProvider`）**

```
ValidateCredentialsAsync / BeginOAuth / CompleteOAuthAsync / SignOut
GetGamesAsync
SearchModsAsync(NexusSearchQuery)
GetGameArtworkAsync(domains)
GetModDetailAsync(domain, modId)
GetModFilesAsync(domain, modId)
GetDownloadLinkAsync(domain, modId, fileId, key, expires)
CheckForUpdatesAsync(...)
GetDownloadHeaders()
LastError (NexusError) — 分类失败原因
```

**数据模型**（`NexusModels.cs`）

| 类型 | 关键字段 |
|---|---|
| `NexusModSummary` | ModId / Name / Author / Summary / Version / `Category` / PictureUrl / PageUrl / DownloadCount / EndorsementCount / UpdatedAt |
| `NexusModDetail` | `Summary` + Description（HTML）+ Tags + Files + Requirements |
| `NexusModFile` | FileId / Name / `Category`（MAIN/OPTIONAL）+ SizeBytes / UploadedAt / IsPrimary |
| `NexusModReference` | 依赖引用（modId / 名称是否必需） |
| `NexusDownloadLink` | 下载 URL + 需要的头 |
| `NexusGameArtwork` | DomainName / GameId / Schema / TileUrl / ThumbnailUrl / HeroUrl / **`CardUrl`（计算属性）** |

**下载**

`DownloadManager`（46 KB）：`EnqueueAsync` / `PauseAsync` / `ResumeAsync` / `CancelAsync` / `RetryAsync` / `RemoveAsync` / `PauseAllAsync` / `RecoverAsync`。支持：

- HTTP Range 分块、断点续传（`DownloadControlFile` 记录进度）
- 停滞检测 `StallTimeout = 60s`、连接超时 30s、重试基数 2s
- 部分文件 `<Data>\Downloads\<id>.part`

**权限限制（必须如实告知用户，不能当作已完整可用）**

| 能力 | 限制 |
|---|---|
| `GetDownloadLinkAsync` | **Nexus 要求 Premium 会员**才能通过 API 生成下载链接；非会员返回权限错误 |
| 作者关闭管理器下载 | 只能"前往 Nexus 页面"手动下载，UI 有明确分支（`OnlineModActionPlan`） |
| 未配置密钥 | 浏览可降级，但搜索/详情不可用；UI 显示 `NeedsSetup` |
| 速率限制 | `NexusSettings.MinimumRequestInterval`（默认 250 ms），`MetadataCacheDuration` 15 分钟 |

**冲突与依赖**

| 服务 | 方法 | 说明 |
|---|---|---|
| `ConflictService` | `Analyze(gameId, mods, rules)` → `List<FileConflict>`；`Explain(...)` → `ConflictExplanation` | 同一相对路径被多个 Mod 提供即冲突；胜者由加载顺序/规则决定 |
| `DependencyResolver` | `ResolveLocal` / `ResolveRequirements` / `ResolveNexusReferences` | `MaxDepth = 12`，产出 `DependencyReport`（含循环检测） |
| `DependencyService` | `FindMissing(...)` | 必需/可选/缺失/版本不满足 |
| `VersionRequirement` / `VersionOrder` | `VersionOrder.cs` | 版本比较与约束 |

**如何关联本地**：`NexusLibraryLinker`（`NexusLinkState` / `NexusLibraryLink`），把 Nexus modId 与 `ModMetadata.NexusModId` 对上。

**测试是否用真实服务**：**全部用模拟数据**。`tests/ModManager.Tests/NexusTestDoubles.cs` 的 `FakeNexusProvider` + `StubHttpHandler`（脚本化 HTTP）。**没有任何测试访问真实 Nexus API**。

### 6.4 翻译系统（重点）

#### 6.4.1 双层结构

```
ITranslationService (TranslationService.cs)          ← 门面：缓存 + 引擎选择
├─ 本地：LocalArgosTranslationEngine                  ← 免费离线
│    └─ ArgosWorkerClient ──stdin/stdout JSON lines──> engine_worker.py（Python 子进程）
└─ 在线：任何 OpenAI 兼容 /chat/completions 接口       ← 可留空
```

#### 6.4.2 为什么不用 `pip install argostranslate`

`argostranslate/sbd.py` 在模块顶层**无条件** `import stanza`，而 stanza 依赖 torch + spacy（约 1 GB），其唯一作用是句子边界检测。

所以本项目的做法是：**用 Argos 官方语言包 + Argos 自己的推理栈（CTranslate2 + SentencePiece）**，推理参数逐行对照 `argostranslate.translate.apply_packaged_translation`：

```
beam_size=4, length_penalty=0.2, replace_unknowns=True,
max_batch_size=32, batch_type="tokens", num_hypotheses=1, return_scores=True
```

分句改由 C# 侧 `TranslationSegmenter` 完成（本来就必须做，为了保 Markdown 结构）。依赖从约 1 GB 降到约 32 MB，模型加载实测约 0.1 秒。

#### 6.4.3 运行时路径

| 内容 | 路径 |
|---|---|
| 引擎根 | `<Data>\Translation\` |
| Python venv | `<Data>\Translation\runtime\`（`Scripts\python.exe`、`Lib\site-packages`） |
| 语言包 | `<Data>\Translation\packages\translate-en_zh-1_9\`（metadata.json + model/ + sentencepiece.model + stanza/） |
| worker 脚本 | `<Data>\Translation\engine_worker.py`（**从程序集 EmbeddedResource 释放**） |
| 下载缓存 | `<Data>\Translation\downloads\` |
| 离线 wheel | `<Data>\Translation\wheels\`（可选，用于慢网络/离线安装） |

#### 6.4.4 协议与生命周期

`engine_worker.py` 走 **stdin/stdout 的 JSON-lines**：

```
请求  {"id":1,"cmd":"ping"}
      {"id":2,"cmd":"translate","texts":["...","..."]}
      {"id":3,"cmd":"shutdown"}
响应  {"id":1,"ok":true,"pong":true,"package":"translate-en_zh-1_9",...}
      {"id":2,"ok":true,"translations":["...","..."],"seconds":0.258}
      {"id":2,"ok":false,"error":"..."}
```

- 进程**常驻**（模型只加载一次），`ArgosWorkerClient` 用 `SemaphoreSlim` 串行化请求
- 启动时读首行判断 ready / fatal
- 超时：单次请求 90 秒（`TranslationTimeout`）；初始化单步 10 分钟（`SetupStepTimeout`）
- 取消/超时 → **杀掉进程**，下次调用重启（请求-响应是原子往返，没有安全的中途取消点）
- stderr 单独线程读取，失败时附在错误信息里

#### 6.4.5 初始化

`LocalArgosTranslationEngine.InitializeAsync(progress, ct)` 幂等：

1. 从程序集释放 `engine_worker.py`
2. 定位 Python（跳过 `WindowsApps` 存根，因为那是 0 字节别名）
3. 创建 venv（若不存在）
4. 安装推理库：**优先** `--no-index --find-links <Data>\Translation\wheels`，否则 PyPI
5. 解析官方索引 `https://raw.githubusercontent.com/argosopentech/argospm-index/main/index.json`，取 `en → zh`
6. 下载 `.argosmodel`（可复用 `<Data>\Translation\downloads` 里已存在的归档，显示来源/大小/进度）
7. 解压到 `packages\`
8. **`SelfTestAsync`：真实翻译一句并断言输出含中文**，通过才报 Ready

状态枚举：`LocalEngineState { NotInitialized, Initializing, Ready, Failed }`。

#### 6.4.6 文本保护（`TranslationTextGuard`）

把不能翻译的内容替换成占位符，翻译后还原。占位符格式是**实测选定的 `{n}`**：

| 候选 | 结果 |
|---|---|
| PUA `\uE000` / 控制字符 / `{{n}}` / `«n»` | ❌ 模型输出 `QQ7` 之类垃圾 |
| `[[n]]` | 单句短文本存活，**多占位符段落会破**（`[[0]]` → `[ [0]`） |
| **`{n}`** | ✅ 5/5 测试句全部严格存活，编号与顺序正确 |

保护对象：代码块、行内代码、Markdown 链接目标、裸 URL、盘符/UNC/Unix 路径、命令行、版本号、文件名+扩展名、格式占位符、字母数字标识符、全大写缩写（带常见词黑名单）。

**返回值语义**：`GuardRestoreResult { Text, MissingIndices, DuplicatedIndices, IsIntact }`。占位符丢失或重复 → `IsIntact=false` → 调用方**保留原文并报错**，绝不输出半还原文本。

#### 6.4.7 分段（`TranslationSegmenter`）

- 按行分段，识别：标题 / 列表 / 引用 / 代码围栏 / 缩进代码 / 表格
- 代码围栏与分隔线 → `Verbatim`（**永不送翻译**）
- 表格行按 `|` 拆成单元格，管道符不进模型
- `DistinctTexts()` 对同段文本去重（一页 20 个 Mod 共享样板句只翻一次）
- `Compose()` 按 `LineIndex`/`PartIndex` 精确重组
- **已知限制**：是 Markdown 感知的行分段器，不是完整 CommonMark 解析器

#### 6.4.8 缓存

| 项 | 值 |
|---|---|
| 位置 | `<Data>\Cache\Translations\<Sanitize(key)>.json` |
| 键 | 调用方传入的稳定 key（如 `cyberpunk2077-107-full`） |
| 指纹 | `SHA256(title + summary + description + engineId)` |
| 失效 | 指纹变化即重译 |
| engineId | 本地 `local-argos-ct2-en-zh-1.9`；在线用 model 名 |

**同一 Mod 的本地/在线结果不会互相污染**，因为 engineId 参与指纹。

#### 6.4.9 OnlineModRow 的翻译数据模型（近期改动，已实测存在）

文件：`src/ModManager.App/ViewModels/OnlineModsViewModel.cs`

| 成员 | 行号 | 语义 |
|---|---|---|
| `TranslatedTitle`（可变） | ~1455 | 中文标题（原始缓存） |
| `TranslatedSummary`（可变） | 1483 | 中文简介 —— **本轮新增**，此前行级没有这个字段 |
| `HasTranslatedTitle` | ~1499 | |
| `HasTranslatedSummary` | 1501 | |
| `DisplayTitle` | 1512 | `HasTranslatedTitle ? 中文 : Name` —— **主标题应绑这个** |
| `OriginalTitle` | 1515 | 恒为英文 `Name` |
| `ShowOriginalTitle` | 1518 | 有译文且与原名不同才显示次要行 |
| `DisplaySummary` | 1522 | `HasTranslatedSummary ? 中文 : SummaryText` |

**ViewModel 侧还有**：`DetailTitleText`（363 行）读 **选中行** 的 `DisplayTitle`（不是面板自己的副本），保证"整页翻译"能影响详情标题。

#### 6.4.10 XAML 绑定实况（实测 `OnlineModsView.xaml`）

| 行 | 绑定 |
|---|---|
| 252 | `ItemContainerStyle="{StaticResource TableRow}"`（扁平列表，非卡片） |
| 304 | `Text="{Binding DisplayTitle}"` ← 中文主标题 |
| 313 | `Text="{Binding OriginalTitle}"` + `Visibility="{Binding ShowOriginalTitle}"` ← 英文次要行 |
| 319 | `Text="{Binding DisplaySummary}"` |
| 404/408 | 详情面板 `SelectedMod.OriginalTitle` / `SelectedMod.ShowOriginalTitle` |
| 114 | 按钮文案 `"翻译本页"` |

#### 6.4.11 触发入口

| 入口 | 命令 | 行为 |
|---|---|---|
| 列表工具栏 | `TranslatePageCommand` → `TranslatePageTitlesAsync()` | 对**本页未翻译行**逐条翻译**标题 + 简介**（一次请求，cache key 后缀 `-full`） |
| 详情 | `TranslateTitleCommand` | 只翻标题 |
| 详情 | `TranslateDescriptionCommand` | 翻标题+简介+完整描述，并**回写** `row.TranslatedTitle` / `row.TranslatedSummary` |
| 详情 | `ToggleSourceCommand` | 原文/中文切换（`_showSource`） |

#### 6.4.12 翻译失败时的显示

- `TranslationStatus` 显示真实原因（引擎不可用 / 超时 / 占位符被改）
- 失败**保留原文**，不显示伪成功
- `TranslationStatus` 文案区分"来自缓存"与"翻译完成"，并带引擎标签（如 `本地 Argos en→zh 1.9`）

### 6.5 冲突与依赖管理

**冲突**

```
ConflictService.Analyze(gameId, modSet, rules) → List<FileConflict>
  输入：每个 Mod 的 DeployedFile 列表（相对路径）
  算法：同一 relativeTarget 被 ≥2 个 Mod 提供 → 冲突
  胜者：活动 Profile 的 ModOrder 靠后者覆盖靠前者；显式 ConflictResolution 优先
ConflictService.Explain(...) → ConflictExplanation（含 ConflictStep 链）
```

- 加载顺序**真实影响**结果（`DeploymentPlanner` 按 `Profile.ModOrder` 顺序生成计划）
- 冲突**只在信息层解决**：提供规则（`PreferWinnerCommand` / `PreferLoserCommand` / `ClearRuleCommand`），不会自动删除文件

**依赖**

```
DependencyResolver.ResolveLocal(root, all)          → 本地 Mod 之间
DependencyResolver.ResolveRequirements(...)         → 版本约束
DependencyResolver.ResolveNexusReferences(...)      → Nexus 引用
DependencyService.FindMissing(...)                  → MissingDependency 列表
DependencyGraph：DependencyNodeState（Satisfied/Missing/VersionMismatch/…）
```

- `MaxDepth = 12`，超出视为异常，防止恶意/损坏依赖形成无限链
- 循环依赖：`DependencyGraph` 记录并在 `DependencyReport` 中标注

**安装前 vs 安装后**：`ModInstallService.PreviewAsync` 在写盘前给出 `InstallPreview`（含冲突与缺失依赖）；`HealthService.CheckAsync` 在安装后做健康检查。

### 6.6 UI 与设计系统

**主窗口**（`Views/MainWindow.xaml`，10.7 KB）

```
Window (1440x880, Min 1024x620)
└─ Grid
   ├─ 204px 侧栏：品牌 + PageGroups 导航 + 底部连接状态
   └─ 内容区
      ├─ 52px 顶栏：当前游戏（封面/名/能力）| 全局搜索 | 状态 + 刷新 + 设置
      └─ ContentControl Content="{Binding ActivePage}"
```

**页面清单**（12 个 `Views/*.xaml`）

| 文件 | 大小 | 职责 |
|---|---|---|
| `DashboardView.xaml` | 24 KB | 首页：问候 + 当前游戏 hero + 游戏卡片 + 快捷操作 + 最近活动 |
| `GamesView.xaml` | 33.7 KB | 游戏库：表格 + 检测列表 + 手动添加 |
| `ModsView.xaml` | 33.9 KB | 本地 Mod：搜索/筛选 + 列表 + 详情 + 安装预览 |
| `OnlineModsView.xaml` | 30.7 KB | Nexus 浏览：筛选 + 扁平列表 + 详情（含翻译） |
| `DownloadsView.xaml` | 9.8 KB | 下载任务 |
| `LoadOrderView.xaml` | 6 KB | 加载顺序 |
| `ConflictsView.xaml` | 12.2 KB | 冲突 |
| `ProfilesView.xaml` | 5.4 KB | 配置方案 |
| `SettingsView.xaml` | 25 KB | 设置（Nexus/翻译/部署/界面/安全/目录） |
| `LogsView.xaml` | 13.5 KB | 日志 + 事务历史 |
| `LaunchSettingsWindow.xaml` | 2.2 KB | 启动参数对话框 |
| `MainWindow.xaml` | 10.7 KB | 主窗口 |

**设计 token**（`Theme/Fluent.xaml`，20.8 KB）

| 类别 | 内容 |
|---|---|
| 颜色 | `ColorBackground/Surface/SurfaceRaised/SurfaceMuted/Border/BorderStrong/Text/TextSecondary/TextDisabled/Accent*/OnAccent/Success/Warning/Danger/Info` + 四个 `*FillBrush`（16% / 深色 22% 透明度） |
| 字号 | `FontSizeCaption=12` / `Body=13` / `Subtitle=15` / `Title=20` / `Display=28` |
| 字体 | `UiFont = "Microsoft YaHei UI, Microsoft YaHei, Segoe UI, Arial"`；`MonoFont = "Cascadia Mono, Consolas, …"` |
| 间距 | `PagePadding=28` / `CardPadding=18` / `ControlPadding=12,7` / `CardRadius=12` / `ControlRadius=8` / `PillRadius=999` |
| 文本样式 | `PageTitleText(22)` / `PageSubtitleText` / `SubtitleText` / `BodyText` / `SecondaryText` / `CaptionText` / `SectionHeaderText` / `MonoText` |
| 容器 | `Card` / `RaisedCard` / `FlatCard` / `StatCard` / `Callout` / `Divider` / `TableSurface` |
| 行/表 | `TableRow`（无圆角、底部分隔线、hover/选中态）/ `TableHeaderText` / `FieldLabelText` / `FieldValueText` |

**控件样式**（`Theme/Controls.xaml`，40.4 KB）：`PrimaryButton` / `SecondaryButton` / `DangerButton` / `NavButton` / `IconButton` / `ChipButton` / `StatusPill` / `AppTextBox` / `BareTextBox` / `AppPasswordBox` / `AppCheckBox` / `AppComboBox(+Item)` / `AppListBox(+Item)` / `SidebarListItem` / `AppTabControl(+Item)` / `AppProgressBar` / `SlimScrollThumb` / `UnderlineTab` / `TableRow` / `MoreMenuToggle` / `MenuActionButton` / `MenuSurface` / `Callout`

**主题切换**（`Theme/AppearanceService.cs`，9.2 KB）

- **机制**：运行时**替换** `Application.Current.Resources` 里的画刷实例（不是修改）。原因：WPF 会**冻结**被 sealed Style 引用的 Freezable，直接改 `Color` 是静默 no-op（这是已修复的真实 bug）
- 所有视图引用必须是 `{DynamicResource XxxBrush}` —— 全项目已迁移（**195 处**），退回 `StaticResource` 会让主题切换失效
- 启动时 `App.OnStartup` 在窗口构造前调用 `Apply()`，避免首帧闪烁
- 深色强调色故意提亮（`#7AA7FF`），因此需要 `OnAccentBrush` 配对（浅色白字 / 深色近黑字）

**已知 UI 问题**：见 §11。

### 6.7 配置、持久化与安全

**路径**（`AppPaths.cs`）—— 便携式，根 = `<exe 目录>\Data`

| 属性 | 路径 | 内容 |
|---|---|---|
| `Root` | `<exe>\Data` | 应用数据根 |
| `Mods` | `…\Mods` | 暂存（每个 mod 一个目录） |
| `Downloads` | `…\Downloads` | 下载归档与 `.part` |
| `Backups` | `…\Backups` | 覆盖前备份 |
| `Journals` | `…\Journals` | 事务日志 |
| `Manifests` | `…\Manifests` | 已提交清单 |
| `Data` | `…\Data` | **JSON 数据库实际位置**（`games.json` / `profiles.json` / `settings.json` …） |
| `Cache` | `…\Cache` | 图片、封面映射、翻译缓存 |
| `Logs` | `…\Logs` | 日志 |
| `Games` | `…\Games` | 用户自定义游戏定义 |
| `ConfigFile` | `<Root>\settings.json` | 属性存在但**实际未用**——设置走 `JsonDocumentStore("settings")`，落在 `…\Data\settings.json` |
| `Translation` | `…\Translation` | 本地翻译运行时与语言包 |

**持久化**：`JsonDocumentStore`（`Persistence/`），JSON 文件，无数据库。`LibraryRepository` 聚合读写。

**设置模型**（`AppSettings.cs`）：`AppSettings { Version, Theme, UseLightTheme, UseSystemAccent, LogLevel, LogRetentionDays, Downloads, Nexus, Translation, Deployment, Safety, Ui, LastSelectedGameId }`。

- `Version` 字段用于迁移
- 新增设置必须用**默认值**，保证旧文件可反序列化（当前 `Translation.Engine` 默认 `Auto` 就是这样加的）

**敏感信息**（`SecretStore.cs` + `DpapiInterop.cs`）

- 文件 `<Data>\secrets.dat`
- Windows 上用 DPAPI `CryptProtectData`（绑定当前用户账户）；非 Windows 仅 XOR 混淆（明确声明不是安全边界）
- 密钥名常量在 `ISecretStore`
- **从不写日志、从不进 settings.json、不在 UI 回显**

**路径安全**（`Safety/PathSafetyService.cs`，27 KB）

```
ApproveGameRoot(gameId, installPath, modRootPath)   // 用户批准后才可写
ValidateWrite(gameId, path) → PathSafetyResult
ValidateDelete(gameId, path, isDirectory)
ValidateAppPath(path)
Canonicalize(path)                                   // 规范化（含 reparse point 处理）
IsInside / IsInsideOrEqual / ContainsForbiddenSegment / IsFileSystemRoot / IsProtectedLocation
MaxCanonicalWalkDepth = 256
```

- 所有写/删都必须先过 `ValidateWrite` / `ValidateDelete`
- `<Data>` 树整体作为允许根；其它路径拒绝
- 拒绝盘符根、系统目录、`..` 穿越、保留设备名

**日志**：`AppLogger`（内存环形 + 文件），`LogEntry` 带 `LogLevel`/`Category`/`Message`/`Detail`；启动期崩溃单独写 `Data\Logs\startup-error.log`（此时 logger 尚不存在）。

---

## 7. 关键数据模型与接口关系

```
GameInstance (持久化)  ──1:1──> GameDefinition (内置或用户定义)
      │                                  │
      │                                  └─ ModLoaderDefinition / LoadOrderDefinition
      └──1:N──> ModMetadata ──1:1──> ModManifest (DeployedFile 列表)
                     │
                     ├──NexusModId──────> NexusModSummary/Detail
                     └──Dependencies[]──> ModReference ──> DependencyResolver

Profile { ModOrder[], DisabledModIds[], ConflictOverrides{} }
DownloadRecord { State, Progress, ByteRange… }
DeploymentJournal { Entries[], UndoActions[], State }
```

**核心接口**（`Abstractions.cs` / `I*.cs`）

| 接口 | 实现 |
|---|---|
| `IAppLogger` | `AppLogger` / `NullLogger` |
| `ISecretStore` | `SecretStore` |
| `IPathSafetyService` | `PathSafetyService` |
| `IGameRepository` / `IModRepository` / `IDownloadRepository` / `IBackupRepository` / `IConflictRepository` / `ISettingsStore` | `LibraryRepository` / `JsonDocumentStore` |
| `IArchiveReader` / `IModAnalyzer` / `IModInstallService` / `IConflictService` | 同名服务类 |
| `IDeploymentPlanner` / `IDeploymentApplier` / `IJournalService` / `IVerifyService` | 同名服务类 |
| `INexusProvider` / `IDownloadManager` / `IDependencyService` / `IHealthService` / `IExternalContentService` | 同名服务类 |
| `IGameAdapter` / `IGameAdapterRegistry` / `IGameDetector` | `GenericGameAdapter` 等 / `GameAdapterRegistry` / `GameDetector` |
| `ITranslationService` | `TranslationService` |
| `IImageCache` | `ImageCacheService` |
| `INexusLibraryLinker` | `NexusLibraryLinker` |

**统一返回类型**：`OperationResult<T> { Succeeded, Value, Error }`（`SupportRecords.cs`）—— Core 服务的标准返回约定，失败带人类可读中文原因。

---

## 8. Nexus 与翻译系统的详细调用链

### 8.1 Nexus 搜索 → 展示 → 安装

```
OnlineModsViewModel.SearchAsync(page)
  └─ _host.Nexus.SearchModsAsync(NexusSearchQuery{GameDomain,SearchText,Sort,Page,IncludeAdult})
       └─ NexusProvider：GraphQL v2（或 v1 回退）
            └─ OperationResult<NexusSearchResult>{ Items, TotalCount, KeywordFilteredLocally }
  └─ Results.ReplaceAll(rows)                     // OnlineModRow 包装
  └─ LoadCoversAsync() → CoverImageLoader.LoadAsync(PictureUrl)
        └─ ImageCacheService.GetAsync(url)        // 磁盘缓存，404 记 30 天

用户点"安装"
  └─ OnlineModsViewModel.InstallAsync(row)
       └─ _host.Nexus.GetDownloadLinkAsync(...)   // ⚠️ 需 Premium
       └─ _host.Downloads.EnqueueAsync(DownloadRequest)
            └─ DownloadManager 分块下载到 <Data>\Downloads
       └─（下载完成后）ModInstallService.StageAsync → PreviewAsync → InstallAsync
```

### 8.2 翻译（本地引擎）

```
OnlineModsViewModel.TranslatePageTitlesAsync()
  └─ _host.Translation.TranslateAsync(key, title, summary, description)
       └─ TranslationService
            ├─ 计算 fingerprint = SHA256(source + engineId)
            ├─ 命中 <Data>\Cache\Translations\<key>.json → 直接返回（FromCache=true）
            ├─ 本地可用（Engine != Online && Probe().CanTranslate）
            │    └─ LocalArgosTranslationEngine.TranslateTextAsync / TranslateDescriptionAsync
            │         ├─ TranslationSegmenter.Segment(description)
            │         ├─ TranslationTextGuard.Protect(text)        // 路径/URL/版本/ID → {n}
            │         ├─ ArgosWorkerClient.TranslateAsync(texts)   // JSON-lines → Python
            │         │    └─ engine_worker.py: ct2.translate_batch(...)
            │         ├─ TranslationTextGuard.Restore(...)         // 占位符缺失/重复 → 失败并保留原文
            │         └─ TranslationSegmenter.Compose(...)
            └─ 否则在线 API（OpenAI 兼容 /chat/completions）
  └─ row.TranslatedTitle = ... / row.TranslatedSummary = ...
  └─ XAML：DisplayTitle / DisplaySummary 立即更新
```

---

## 9. UI 结构与现有设计规则

- **一页一个视觉主角**：首页=当前游戏；游戏库=表格；本地 Mod=列表；Nexus=列表+详情
- **列表用扁平行**（`TableRow`），不再每行套卡片（Nexus / 本地 Mod 已改）
- **中文优先**：Nexus 列表主标题 = `DisplayTitle`（有译文用中文），英文降为 `CaptionText` 次要行
- **状态用 `StatusPill` + 语义色**，不靠纯颜色区分（文字 + 颜色双编码）
- **按钮层级**：`PrimaryButton`（主操作）/ `SecondaryButton`（次操作）/ 图标按钮 / `DangerButton`
- **字号层级**：页面标题 22 > hero 20 > 区块 15 > 正文 13 > 辅助 12
- **画刷必须 `DynamicResource`**（主题切换的前提）
- **不用 `DataGrid`**：表格是 `Grid` 表头 + `ListBox` 行模板（避免 DataGrid 自带编辑/列宽等行为）

---

## 10. 当前功能完成度

| 模块 | 状态 | 证据/限制 |
|---|---|---|
| Steam 扫描与游戏识别 | ✅ 已实现 | `SteamLibraryScanner` + `GameSupportResolver` |
| 通用模式管理 | ✅ 已实现 | `GenericGameAdapter` |
| 7 个内置游戏定义 | ✅ | `GameCatalogue.cs` |
| 游戏封面 | ✅ 已实现 | Nexus → Steam CDN → 占位；实测覆盖 13/16（历史） |
| Mod 导入/安装/卸载 | ✅ 已实现 | `ModInstallService` + `DeploymentApplier`；`SmokeRun` 覆盖 |
| 启用/禁用 | ✅ | Profile 的 `DisabledModIds` |
| 事务与崩溃回滚 | ✅ | `JournalService.RecoverAsync` |
| 压缩格式 | ✅ zip/7z/rar(只读)/tar | SharpCompress |
| 冲突检测 | ✅ 检测 + 信息层解决 | 不自动删文件 |
| 依赖解析 | ✅ 含循环检测 | `MaxDepth=12` |
| Nexus 搜索/详情/文件 | ✅ | GraphQL v2 + REST v1 |
| Nexus 下载链接 | ⚠️ **需 Premium** | 非会员只能"前往页面" |
| 下载暂停/恢复/重试 | ✅ | `DownloadManager` |
| 本地翻译（英→中） | ✅ 引擎可用 | 真实语言包 81.7 MB 已装 |
| 翻译标题 | ✅ | `DisplayTitle` |
| 翻译简介 | ✅ 代码已改 | `TranslatedSummary` + `DisplaySummary`；**端到端未最终复验** |
| 翻译完整描述 | ✅ | `TranslateDescriptionAsync` |
| 翻译"更新说明/文件说明" | ❌ **未实现** | 无对应抓取与字段 |
| 原文/译文切换 | ✅ | `ToggleSourceCommand` |
| 浅色/深色主题 | ✅ | `AppearanceService` + 195 处 DynamicResource |
| 分类展示 | ✅ 展示真实 `Category` | 无分类筛选控件 |
| 分类筛选 | ❌ 未实现 | 需引入过滤集合 |
| 批量翻译整个列表 | ✅ | 标题 + 简介 |

---

## 11. 已知问题与风险

| 优先级 | 问题 | 影响范围 | 根因是否确认 | 相关文件 | 建议处理 | 验证方式 |
|---|---|---|---|---|---|---|
| **高** | 翻译/列表改动**未经最终端到端复验** | Nexus 浏览页 | 否（未完成验证） | `OnlineModsViewModel.cs`、`OnlineModsView.xaml` | 启动应用，点"翻译本页"，确认标题与简介都变中文且无重叠 | 真实启动 + 截图 |
| **高** | 一键翻译失败时无分批容错 | Nexus 浏览页 | 是（`foreach` 遇错即 `return`） | `TranslatePageTitlesAsync` | 改成逐条 try/catch，失败计数 | 断网/空引擎下点击 |
| 中 | 切换主题后已渲染的 `Tone`/`Fill` 徽章不刷新 | 所有列表页 | 是（`Palette` 用 `TryFindResource` 一次性解析并存到行对象） | `DashboardViewModel.Palette`、各 `*Row` | 需给行模型加主题事件或整页刷新 | 切换主题观察徽章 |
| 中 | 翻译密钥输入是明文 TextBox | 设置页 | 是 | `SettingsView.xaml` | 换 `PasswordBox` + code-behind | 目视 |
| 中 | `Slider` 未主题化（深色下发亮） | 设置页 | 是 | `SettingsView.xaml` | 在 `Controls.xaml` 加 `AppSlider` | 深色截图 |
| 中 | 卡片边框贴页面底色对比 1.43:1 | 全局 | 是（实测计算） | `Fluent.xaml` `ColorBorder` | 可改 `#BCC9DA` | 对比度计算 |
| 中 | 分类筛选缺失 | Nexus 浏览页 | 是 | — | 引入 `ICollectionView` 或过滤集合 | — |
| 中 | Nexus 测试全部用模拟，无真实 API 测试 | 测试覆盖 | 是 | `NexusTestDoubles.cs` | 需密钥与网络，暂不可自动化 | — |
| 中 | 更新说明/文件说明未翻译 | 翻译系统 | 是 | — | 需先抓取这些字段 | — |
| 低 | 项目非 Git 仓库 | 全部 | 是 | — | 建议 `git init` 建立基线 | — |
| 低 | `AppPaths.ConfigFile` 属性与实际存储不符 | 持久化 | 是 | `AppPaths.cs` | 文档标注或移除 | 读代码 |
| 已知限制 | 下载链接需 Premium | Nexus | 是 | `NexusProvider.GetDownloadLinkAsync` | 无法绕过 | — |
| 已知限制 | 冲突只在信息层解决 | 冲突页 | 是 | `ConflictService` | 按产品决策 | — |
| 已知限制 | 翻译模型质量：专有名词会被意译 | 翻译 | 是 | — | 占位符已保护技术标识符 | — |

> 表中**没有**列出"已修复且有证据"的项，因为本轮未运行验证；历史修复（如 Grid.Row 遮挡、主题冻结画刷、`{n}` 占位符）在代码中可见，但**未在本轮重新验证**。

---

## 12. 构建、测试与启动

```powershell
# 目录
cd <ModManager 仓库目录>

# 全量重建（本轮未执行）
dotnet build -t:Rebuild .\ModManager.sln --nologo -v minimal

# 完整测试（本轮未执行）
dotnet test tests\ModManager.Tests\ModManager.Tests.csproj --nologo -v minimal

# 只跑翻译测试
dotnet test tests\ModManager.Tests\ModManager.Tests.csproj --filter "FullyQualifiedName~LocalTranslationTests"

# 启动应用
.\src\ModManager.App\bin\Debug\net8.0-windows\ModManager.exe

# 引擎冒烟
dotnet run --project tools\SmokeRun\SmokeRun.csproj
dotnet run --project tools\TranslationSmoke\TranslationSmoke.csproj -- --appdata

# 截图（必须用 Windows PowerShell 5.1，UIAutomationClient 是 .NET Framework 程序集）
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\capture-ui.ps1 -OutputDirectory artifacts\shots -Pages @('设置','总览','游戏')
```

**测试是否会动用户数据？** 不会。测试用 `TemporaryTree`（在 `artifacts\tests\tmp\` 下创建独立临时目录），不触碰 `<exe>\Data`。 **例外**：`ViewLoadTests.PagesEvaluateTheirBindingsAgainstARealViewModel` 会调用 `ApplicationHost.CreateAsync()`，它在测试输出目录下创建 `Data/`。

**测试是否需要网络？** `LocalTranslationTests` 中 3 个"真实引擎"用例需要本机已安装翻译引擎；其余用 `StubHttpHandler` 模拟，不需要网络。

---

## 13. 安全约束与不能触碰的目录

**绝对不要**：

- 删除/清空 `Data`、`Games`、`Backups`、`Cache`、`Logs`、`settings.json`、`secrets.dat`
- 删除或重建 `<Data>\Translation\runtime`（Python venv）与 `packages`（语言包 81.7 MB）
- 访问/修改 **D 盘**（本机 Steam 游戏在 `d:\steam\steamapps\common\…`）
- 修改真实游戏目录、Steam 文件、已安装 Mod
- 修改 DSH Desktop 程序文件或 `resources\app.asar`
- 输出任何密钥/令牌/密码的值（只可描述存储机制）

**允许**：在 `artifacts/` 下产生测试产物与截图。

**应用自身数据根**：`src\ModManager.App\bin\Debug\net8.0-windows\Data\`（由 `AppPaths` 决定，随 exe 位置变化）。

---

## 14. 下一阶段产品与 UI 需求（来自用户，非技术结论）

以下为用户明确提出的诉求，按原意记录，供接手者作为设计输入（**尚未实施**）：

1. UI 整体缺乏美感，存在模板化、AI 生成式的界面观感。
2. 灰色辅助文字、重复说明和装饰性标签过多，希望明显删减。
3. 颜色层级不清晰，状态难以快速辨认。
4. Nexus 浏览页、本地 Mod 管理页和详情页的信息组织不够自然。
5. **Mod 名称应优先显示中文译名**，英文原文通过次要入口查看。
6. **Mod 简介、完整介绍和更新说明也需要翻译**。
7. 游戏、分类、前置依赖、冲突、更新和安装状态需要合理分组。
8. **翻译功能应融入页面**，而不是额外堆叠一块工具栏。
9. 不要默认所有内容都采用卡片布局。
10. 希望参考成熟的桌面软件设计，而不是普通网页 SaaS 后台。

**明确的反模板化要求**：不要泛型 SaaS 后台、不要装饰性统计卡片、不要每个区域都套卡片圆角阴影、不要中英文重复显示、不要一堆颜色接近无法区分的状态、不要空洞说明文字。

**参考基线**（用户指定）：`lepoco/wpfui`（Fluent 控件库，仅作参考，**未引入项目**）；`dsh-ui-ux-pro-max`（DSH 设计 skill，本机已安装，提供 `design_recommend` / `design_review` / `design_search` 三个工具，`stack` 支持 `wpf`）。

---

## 15. 建议开发顺序

1. **先验证再开发**：跑一次 Rebuild + 全量测试 + 真实启动，确认当前工作树可运行（本轮未做）。
2. **翻译闭环**（最高价值）：真实启动 Nexus 页 → 点"翻译本页" → 确认 `DisplayTitle`/`DisplaySummary` 生效、无重叠；补"更新说明/文件说明"抓取与翻译。
3. **UI 信息重构**：按 §14 取舍，先做 Nexus 浏览页 / Mod 详情 / 本地 Mod 三页，再统一其它页。
4. **分类**：加真实分类筛选（`ICollectionView`），不做凭空生成的标签。
5. **主题细节**：`Slider` 主题化、边框对比、翻译密钥掩码。
6. **工程基线**：`git init` 建立版本控制，避免再次出现"改动无 diff"的局面。

---

## 16. 接手指南（可直接复制给下一个 AI）

```
你在接手一个已有的 Windows 桌面项目，不要从零重建。

【项目】
路径：<ModManager 仓库目录>
技术栈：.NET 8 / WPF / C# / 手写 MVVM（无 DI 容器、无 UI 框架）
规模：106 个 .cs（34,455 行）+ 15 个 .xaml（4,800 行）
唯一第三方 NuGet：SharpCompress 1.0.0
项目不是 Git 仓库 —— 改动没有版本控制保护，动手前务必先记录/备份。

【架构要点】
- ModManager.Core 不得引用 WPF（csproj 是 net8.0）
- 组合根：src/ModManager.App/ApplicationHost.cs（手写，无 DI）
- MVVM：ViewModelBase + RelayCommand/RelayCommand<T>；App.xaml 用 DataTemplate 做 VM→View 映射
- 导航：ShellViewModel.PageGroups + PageDescriptor 工厂（首次导航才创建页面）
- 统一返回：OperationResult<T>（Core 所有服务）
- 所有写/删游戏目录的操作必须经 Safety/PathSafetyService

【必读文件（按顺序）】
1. PROJECT_HANDOVER.md（本文档）
2. src/ModManager.Core/AppPaths.cs、AppSettings.cs、SupportRecords.cs
3. src/ModManager.App/ApplicationHost.cs、App.xaml.cs
4. src/ModManager.App/ViewModels/ShellViewModel.cs
5. src/ModManager.Core/Services/ModInstallService.cs + DeploymentPlanner.cs + DeploymentApplier.cs
6. src/ModManager.Core/Services/NexusProvider.cs
7. src/ModManager.Core/Services/TranslationService.cs + LocalTranslation/ 全部
8. src/ModManager.App/ViewModels/OnlineModsViewModel.cs（翻译 UI 链路）
9. src/ModManager.App/Theme/Fluent.xaml + Controls.xaml + AppearanceService.cs

【翻译系统现状（重点，已核实）】
- 双层：TranslationService（缓存+调度）→ LocalArgosTranslationEngine（Argos 语言包 + CTranslate2，独立 Python 进程）
- 占位符格式必须是 {n}（实测唯一稳定），受保护内容丢失/重复时保留原文并报错
- OnlineModRow 已有：TranslatedTitle / TranslatedSummary / DisplayTitle / DisplaySummary / OriginalTitle / ShowOriginalTitle
- XAML 已绑定 DisplayTitle（中文主）/ OriginalTitle（英文次）/ DisplaySummary
- 已知缺口：更新说明与文件说明未翻译；批量翻译遇错即中止；切换主题后行内徽章颜色不刷新；这一批改动尚未经过完整端到端验收

【UI 目标（用户原话整理）】
- 去 AI 模板感：删冗余灰字/装饰标签/无意义统计卡片
- 不要默认卡片布局，列表用扁平行
- 中文优先：Mod 名显示中文，英文作为次要入口
- 翻译融入页面，不要单独一条工具栏
- 状态（分类/依赖/冲突/更新/安装）要能快速区分
- 参考桌面软件（如 Fluent / WPF UI），不是网页 SaaS 后台
- 可用工具：DSH 的 design_recommend / design_review / design_search（离线，支持 stack=wpf）

【硬约束】
- 不碰 D 盘、真实游戏目录、Steam 文件
- 不删/不清空 Data、Games、Backups、Cache、Logs、settings.json、secrets.dat
- 不删翻译语言包（81.7 MB）与 Python venv
- 不改 DSH Desktop / resources\app.asar
- 不输出任何密钥值
- 画刷一律 {DynamicResource XxxBrush}（退回 StaticResource 会让主题切换失效）

【工作方式要求】
1. 先检查现状（读代码 + 跑 Rebuild/测试），不要上来就大规模改
2. 先给方案再改代码；改动小步、可回滚
3. 每次改动后：dotnet build -t:Rebuild .\ModManager.sln --nologo -v minimal
4. 测试：dotnet test tests\ModManager.Tests\ModManager.Tests.csproj
5. 真实启动验证：.\src\ModManager.App\bin\Debug\net8.0-windows\ModManager.exe
6. 截图用 tools\capture-ui.ps1（须 powershell.exe 5.1）
7. 不得把"未运行"说成"通过"
```

---

*本文档所有路径、类名、方法名、常量均来自对当前工作区代码的直接读取。标注"未验证"的项目表示本轮未执行运行验证。*
