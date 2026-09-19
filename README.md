# 【CA】指令核心 - RuleCore

RimWorld 1.6 的**通用反应式自动化规则引擎**。把殖民地行为写成可以直接分享的规则：

```
当 ⟨实体表达式⟩⟨谓语⟩⟨宾语⟩ ，… ， ⟨实体表达式⟩⟨操作⟩⟨宾语⟩ ，…
```

逗号之前全是**检测**，之后全是**操作**。

**不做规划器**——路径怎么走、战斗怎么打，全部交回原版 AI；规则只回答「现在该让谁去做哪件事」。
内置游戏内规则编辑器与调试时间线，**每一次求值都能回答「为什么没触发」**。

---

## 三个设计决定

### 1. 词表是数据，不是类

「室外温度大于 10」不是一种条件类型，它是 `属性 × 比较符 × 阈值` 的组合。
所以加一种玩法**不需要加一个类**，只需要在词表里加一行：

```csharp
RuleCoreApi.Property("mymod.radiation", RuleEntityKind.Map, RuleValueKind.Number, reader: Read)
    .Range(0f, 50f).Needs(RuleCapability.Map).Register();
```

别的 mod 通过 `RuleCore.Api.RuleCoreApi` 登记，**加载顺序无关**，坏行会被拒绝并说清原因。
见 `Docs/扩展注册API.md`。

### 2. 绑定就是身份

身份由**主体绑定**决定，不在规则语言里判断。

绑成「囚犯」，那一轮里每个人的身份就都是囚犯；点了名，那个人的身份就是定死的那一个。
所以 `本主体.是囚犯` 对任何一条规则都是**恒真句**——它不提供信息，只占列表位置。
要「囚犯做某事」就把主体绑成囚犯。想知道「这个主体到底是什么」，编辑器直接写在他脸上。

规则语言里因此**没有身份判断词**。

### 3. 只列能用的（四维过滤）

编辑器列出的每个选项都经过四道过滤，**看不见但选了必错的东西一个都不出现**：

| 维度 | 决定 | 例子 |
|---|---|---|
| 实体类型 | 能不能作用在这个东西上 | `耐久` 不出现在地图上 |
| 值类型 | 这个谓语吃不吃得下它现在的值 | `大于` 不出现在小人身上（小人不是数） |
| **能力位** | **这个宿主身上这件事存不存在** | **`饱食度` 不出现在机械族身上，出现的是电量** |
| 权限层级 | 现在放不放行 | 未授权的开发者级动作会标出来 |

被挡掉的项会**连同缺的那一位一起显示**：

```
这些用不上（当前主体身上没有它们要的东西）
  饱食度  —— 缺：饱食需求
```

「没有它」和「你选错了主体」必须分开说——玩家看到的是同一个短列表，但该做的下一步完全不同。

---

## 目录

| 路径 | 内容 |
|---|---|
| `About/` | 模组元数据 |
| `1.6/Assemblies/` | 编译产物（`RuleCore.dll`） |
| `1.6/Defs/` | 内置规则包（`RulePackDef`） |
| `Languages/` | 简体中文 / 繁体中文 / English |
| `Source/RuleCore/Core/` | **纯 BCL 层**：语义、路径求值、词表结构、日志。不引用 Verse / UnityEngine，可脱离游戏单测 |
| `Source/RuleCore/Model/` | 规则模型与词表内容（`Rule` / `RulePath` / `RuleVocabularyCatalog`） |
| `Source/RuleCore/Engine/` | 引擎宿主与「RimWorld 事实 → 规则语言问句」的翻译层 |
| `Source/RuleCore/UI/` | 编辑器、面板、选择器 |
| `Source/RuleCore/API/` | 对外注册 API |
| `Tests/RuleCore.CoreTests/` | Core 层无头测试 |
| `Docs/` | 语义规范、词表、扩展注册 API、可点原型 |
| `log/` | 运行期落盘的调试日志（不进版本库） |

---

## 编译

需要 .NET SDK 与 RimWorld。`Source/RuleCore/RuleCore.csproj` 里的 `HintPath` 指向本机的
RimWorld 与 Unity 托管程序集，**换机器要按自己的安装路径改**：

```
<RimWorld>\RimWorldWin64_Data\Managed\Assembly-CSharp.dll
<RimWorld>\RimWorldWin64_Data\Managed\UnityEngine.CoreModule.dll
<RimWorld>\RimWorldWin64_Data\Managed\UnityEngine.IMGUIModule.dll
<RimWorld>\RimWorldWin64_Data\Managed\UnityEngine.TextRenderingModule.dll
```

```powershell
dotnet build "Source\RuleCore\RuleCore.csproj" -c Release
```

产物落在 `1.6/Assemblies/RuleCore.dll`。

**不依赖 Harmony**（全项目没有一处 `HarmonyLib` / `HarmonyPatch`），
所以 `About.xml` 里也没有 `<modDependencies>`。

## 测试

```powershell
dotnet run --project "Tests\RuleCore.CoreTests\RuleCore.CoreTests.csproj" -c Release
```

退出码 0 = 全通过。跑的是和游戏内「自检」按钮**同一套** `RuleCoreSelfTest`，
外加只有脱离游戏才好测的部分（词表过滤、路径求值、嵌套检测树、环形日志缓冲、落盘 sink）。

## 部署

开发目录是权威。把 `About` / `Languages` / `1.6` / `LoadFolders.xml` 拷进
`<RimWorld>\Mods\【CA】指令核心 - RuleCore`。
**DLL 被游戏映射住，复制前必须先关游戏**（否则报 `ERROR 1224 ... user-mapped section open`）。

---

## 文档

| 文件 | 内容 |
|---|---|
| `Docs/语义规范.md` | 语法与语义的正式规范（结论已锁定） |
| `Docs/词表.md` | 当前**全部**主谓宾 + 组合规则 + 写得出／写不出清单 |
| `Docs/扩展注册API.md` | 给别的 mod 的注册 API 与约定 |
| `Docs/编辑器原型.html` | 可点的界面原型（含能力过滤与诊断面板，不需开游戏） |
| `Docs/我的世界谓词系统介绍.md` | 最初的设计来源 |

---

## 许可证

**GNU Lesser General Public License v3.0 or later**（`LGPL-3.0-or-later`）。

- `LICENSE` —— LGPL-3.0 全文
- `COPYING` —— GPL-3.0 全文

LGPL-3.0 是「GPL-3.0 **加上**一组附加许可」，所以两份文本都要附。

Copyright (C) 2026 Cagier（GitHub: [CagierAsh123](https://github.com/CagierAsh123) / Steam: Cagier.阳）

本仓库包含**完整源码**，因此满足 LGPL-3.0 §4(d)(0) 关于「用户能够把本程序与修改过的
库版本重新组合或重新链接」的要求——你拿到的是可编译的全部源文件，而不只是一个 DLL。
