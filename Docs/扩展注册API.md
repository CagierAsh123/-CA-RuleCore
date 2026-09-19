# RuleCore 扩展注册 API

别的 mod 用它把新的**主谓宾**加进规则语言。加一行 = 加一种玩法，**不需要改 RuleCore 的任何代码**。

- 入口：`RuleCore.RuleCoreApi`
- 源码：`Source/RuleCore/API/RuleCoreApi.cs`
- 引用：编译期引用 `RuleCore.dll`，或在 `About.xml` 里写 `<modDependencies>` 保证加载顺序

## 什么时候登记

```csharp
[StaticConstructorOnStartup]
public static class MyModRules
{
    static MyModRules() { /* 在这里登记 */ }
}
```

`[StaticConstructorOnStartup]` 在 Def 加载完、引擎开始采样之前跑，是最稳的位置。

**加载顺序无关紧要**：登记的行先攒进待注册队列；词表已经建过就顺手重建一次。
所以你的 mod 在 RuleCore 之前或之后加载都成立。

> 太晚登记（比如玩家点了按钮才登记）也能进表，但**已经求值过的规则**的派生属性
> （权限层级、边沿语义）要等下一次加载才重算。

## 三块词表

规则语言就是三元组：`⟨实体⟩⟨谓语⟩⟨宾语⟩`。所以你要加的东西一定落在三块里：

| 你要做的事 | 加什么 | API |
|---|---|---|
| 让规则能读一个新数/新实体 | **属性**（实体表达式路径上的一步） | `Property` |
| 让规则能判断一个新现象 | **检测**（产出真/假） | `Detect` |
| 让规则能做一件新事 | **操作**（产出改变） | `Operate` |
| 让规则对另一类东西逐个跑 | **主体绑定** | `Subject` |

## 完整例子

```csharp
using RuleCore;
using RuleCore.Core;
using Verse;

[StaticConstructorOnStartup]
public static class MyModRules
{
    static MyModRules()
    {
        // ── 实体：给地图加一个"辐射强度" ──────────────────────────────
        RuleCoreApi.Property(
                key: "mymod.radiation",
                owner: RuleEntityKind.Map,
                result: RuleValueKind.Number,
                unit: "mSv",
                reader: (host, owner, out RuleValue value, out string code, out string why) =>
                {
                    var map = RuleEvalHost.MapOf(owner);
                    if (map == null)
                    {
                        // 「读不到」：必须给原因码，它会原样进时间线
                        value = RuleValue.None; code = "mymod.no_map"; why = "没有地图。";
                        return false;
                    }

                    value = RuleValue.OfNumber(MyRadiationFor(map));
                    code = null; why = null;
                    return true;
                })
            .Range(0f, 50f)          // 编辑器用它夹取输入
            .Decimals(1)
            .Register();

        // ── 检测：这个人受辐射了吗 ────────────────────────────────────
        RuleCoreApi.Detect(
                key: "mymod.irradiated",
                subject: RuleEntityKind.Pawn,
                subjectValueKind: RuleValueKind.Entity,   // 主语是一个小人（不是数值）
                argKind: RuleValueKind.None,              // 不需要宾语
                handler: (host, subject, arg, out bool passed, out string code, out string why) =>
                {
                    passed = MyIsIrradiated(RuleEvalHost.PawnOf(subject));
                    code = passed ? null : "mymod.not_irradiated";
                    why  = passed ? null : "这个人没受辐射。";
                    return true;   // 返回 true 时 passed 才是结论
                })
            .Quiet()                 // 不成立时安静让开，不算规则失败
            .Register();

        // ── 操作：Agentic（下一条 job 就撒手）────────────────────────
        RuleCoreApi.Operate(
                key: "mymod.decontaminate",
                subject: RuleEntityKind.Pawn,
                argKind: RuleValueKind.Entity,
                argEntity: RuleEntityKind.Thing,
                handler: (host, subject, arg, out string code, out string why) =>
                {
                    var pawn = RuleEvalHost.PawnOf(subject);
                    if (pawn == null)
                    {
                        code = "mymod.no_pawn"; why = "不是一个人。";
                        return RuleOperateStatus.Failed;
                    }

                    if (!MyNeedsDecontamination(pawn))
                    {
                        // **「已经满足」不是失败**：它不记冷却，也不刷红字
                        code = "mymod.not_needed"; why = "不需要净化。";
                        return RuleOperateStatus.AlreadySatisfied;
                    }

                    if (!MyTryDecontaminate(pawn))
                    {
                        // 原版不肯接这条指令：安静让开（"原版第一顺位"）
                        code = "mymod.rejected"; why = "原版拒绝了。";
                        return RuleOperateStatus.Rejected;
                    }

                    code = "mymod.done"; why = "已净化。";
                    return RuleOperateStatus.Done;
                })
            .Register();

        // ── 主体绑定：让规则对"每个囚犯"各跑一遍 ──────────────────────
        RuleCoreApi.Subject("mymod.prisoners", RuleEntityKind.Pawn,
                binder: (host, into) =>
                {
                    var map = RuleEvalHost.MapOf(host.Map);
                    if (map == null) return;
                    foreach (Pawn p in map.mapPawns.PrisonersOfColonySpawned)
                        into.Add(RuleValue.OfEntity(RuleEntityKind.Pawn, p));
                })
            .Register();
    }
}
```

## 四种返回值的约定

这是整套东西里最容易写错的地方，所以单列：

### `reader`（属性）

| 返回 | 含义 |
|---|---|
| `false` | **读不到**。必须在 `code` 里给原因，它会原样出现在时间线上 |
| `true` | 读到了，`value` 有效 |

**「读不到」和「读到了 0」必须分开。** 混起来的具体后果：`房间温度 小于 10`
在一个站户外的机械体上会静默成立。这是硬规矩，不是风格问题。

### `handler`（检测）

| 返回 | 含义 |
|---|---|
| `false` | **读不到**（同上，`code` 必填） |
| `true` | 读到了，`passed` 才是"成不成立" |

### `handler`（操作）

返回 `RuleOperateStatus`：

| 值 | 含义 | 记冷却？ |
|---|---|---|
| `Done` | 做成了 | 记 |
| `AlreadySatisfied` | 要达成的状态已经满足，**什么都没做** | 不记 |
| `Rejected` | 原版不肯接（被征召、更高优先级 job） | 不记 |
| `Failed` | 真的出错了 | 不记（所以下次采样会重试） |

`AlreadySatisfied` 与 `Failed` 分开，是因为电平规则不刷屏靠它：
记失败会让时间线充满红色的"他没进屋"（其实他就在屋里），
排障的人会去查一个根本不存在的 bug。

## 两条"保准"的保证

### 一、坏行进不了表

`Register()` 返回 `bool`。它会在下面这些情况下**拒绝**并把原因写进时间线：

- key 是空的，或含空格（key 要序列化进存档，必须稳定）
- 没给 handler / reader
- 产出 `Entity` 或 `EntitySet` 却没声明元素类型（`Entity(...)`）——
  没有元素类型，路径就不知道下一步能读什么，编辑器会在这里断掉
- 主体绑定没声明产出什么类型的实体
- key 撞车

与其让一行半成品进表、让编辑器在下拉里摆出一个"选了必然报错"的选项，
不如当场拒绝。**注册期的一个日志错误，好过玩家面前的一个假选项。**

### 二、类型过滤自动生效

你声明的是：

- **实体类型**（`RuleEntityKind`）—— 能不能作用在这个东西上
- **值类型**（`subjectValueKind`）—— 吃不吃得下它现在这个值
- **所需能力**（`RuleCapability`，`.Needs(...)`）—— **这个宿主身上这件事存不存在**

编辑器据此**只列出能用的**。所以：

- 写 `subjectValueKind: RuleValueKind.Number`，你的谓语就**不会**出现在 `本主体`（一个 Pawn）身上；
- 写 `subject: RuleEntityKind.Pawn`，它就只在绑定了小人的规则里出现；
- 写 `.Needs(RuleCapability.NeedFood)`，它就**不会**出现在机械族身上——
  机械族没有饱食需求，有电量需求，而这是它自己的声明，不是一句写死的 if。

不做这一步的话，玩家面对 20 个谓词、其中 3 个能用——**规则语言的可用性完全取决于它**。

## 第三维：所需能力（`.Needs`）

只有实体类型这一维是不够的：机械族、动物、殖民者在词表里都叫「小人」，
但**它们身上能读的东西完全不同**。不给这一维，编辑器只能在机械族身上也列出「饱食度」，
玩家选了必然读到 `prop.no_need`——而"看得见但选了必错"正是这套东西存在的全部理由。

```csharp
// 只有机械族才有的那个"饱食度"
RuleCoreApi.Property("mymod.charge", RuleEntityKind.Pawn, RuleValueKind.Number, reader: Charged)
    .Percent()
    .Needs(RuleCapability.NeedEnergy)
    .Register();

// 只有能穿衣服的宿主才有意义
RuleCoreApi.Operate("mymod.swapOutfit", RuleEntityKind.Pawn,
        RuleValueKind.Entity, RuleEntityKind.Thing, handler: SwapOutfit)
    .Needs(RuleCapability.Apparel)
    .Register();
```

内置的能力位共 29 个，分四组（见 `Core/RuleSemantics.cs` 的 `RuleCapability`）：

| 组 | 位 |
|---|---|
| 实体大类 | `Pawn` `Map` `Thing` `Room` `Cell` |
| 生理构成 | `Humanlike` `Mechanoid` `Animal` `Biological` |
| 需求 | `NeedMood` `NeedFood` `NeedRest` `NeedEnergy` `NeedJoy` `NeedComfort` `NeedBeauty` |
| 装备/身份/能力 | `Apparel` `Equipment` `Inventory` `Bed` `Colonist` `Prisoner` `Slave` `Guest` `Hostile` `CanDraft` `Skills` `Ideo` `Age` |

判据是**含位**：`(have & need) == need`。所以 `None` 不挑宿主，
而**拿不到主体样本时会当作 `All`**（宁可多给几个选项，也不要把有用的东西藏起来）。

> **要加一位新的？** 它必须是所有 mod 都能用的通用概念，否则你应该检查现有的位够不够。
> 加位的代价是三份语言文件都要补 `RuleCore.Cap.<名字>`
> ——编辑器会用这个名字对玩家说「缺：饱食需求」。

编辑器把被挡掉的项**连同缺的那一位一起显示**：

```
这些用不上（当前主体身上没有它们要的东西）
  饱食度  —— 缺：饱食需求
```

**"没有它"和"你选错了主体"必须分开说**——玩家看到的是同一个短列表，
但该做的下一步完全不同。

## 固定取值域（`.Options`）

产出枚举时，RimWorld 里本来就有的东西用 `.Domain(typeof(WeatherDef))`——
编辑器会列出该 Def 类型的全部实例，显示当前语言的名字。

但有些枚举**不在任何 Def 表里**（"这个人是囚犯还是奴隶"是算出来的）：

```csharp
RuleCoreApi.Property("mymod.stance", RuleEntityKind.Pawn, RuleValueKind.Enum, reader: Stance)
    .Options(
        new RuleEnumOption("aggressive", "MyMod.Stance.aggressive"),
        new RuleEnumOption("defensive",  "MyMod.Stance.defensive"))
    .Register();
```

`RuleEnumOption.key` **会序列化进存档**，所以它必须稳定、不能随翻译变；
`labelKey` 才是显示名（查不到就回落成 key）。

## 主体绑定的两种基数

```csharp
// 群体：对每个成员各跑一遍（冷却也按人分开算）
RuleCoreApi.Subject("mymod.prisoners", RuleEntityKind.Pawn, binder: CollectPrisoners)
    .Register();

// 指名：整条规则只对**指定的那一个**跑一遍
RuleCoreApi.Subject("mymod.warden", RuleEntityKind.Pawn, binder: BindWarden)
    .Single()
    .Candidates(CollectWardens)   // 编辑期列出"可以指名哪些"，缺了只能让玩家手填引用
    .Register();
```

指名绑定的 `binder` 从 `host.SubjectRef` 读规则存的那个引用
（就是你存进 `Rule.subjectRef` 的字符串，比如 `ThingID`）：

```csharp
static void BindWarden(IRuleEvalHost host, List<RuleValue> into)
{
    var pawn = MyFindByRef(host.SubjectRef);
    if (pawn == null) return;          // 收不出主体 = 这条规则这一轮不跑
    into.Add(RuleValue.OfEntity(RuleEntityKind.Pawn, pawn));
}
```

> **没有 `.Candidates(...)` 会记一条警告**（不是拒绝）：编辑器只能让玩家手填内部 id，
> 而那是一种绝不会被接受的交互。

## 加绑定之前：身份应该落在绑定上，不是落在规则语言里

**别为了"能判断某类人"而加一串 `isXxx` 布尔属性。** 加一个绑定。

理由是能证明的：绑定一旦确定，那一轮里每个人的身份就跟着确定了。所以
`本主体.是囚犯` 对**任何**一条规则都是恒真句——不提供信息，只占列表位置
（RuleCore 自己就因为这个删掉了 7 条这样的属性）。而编辑器会把当前主体的身份
直接写在玩家脸上，他不需要在规则里再问一遍。

```csharp
// 对：加一个绑定，身份随绑定而来
RuleCoreApi.Subject("mymod.andoids", RuleEntityKind.Pawn, binder: CollectAndroids)
    .Register();

// 错：加一个"是机器人吗"的布尔属性，让玩家在每条规则里自己去判断
```

**什么时候才该加身份属性**：当你的**绑定宽到不足以确定身份**时
（比如"这张图上的所有人"）。那时它才真的是个问题而不是常数。

## 显示名与翻译

**正经做法**：在你的 mod 里放语言文件，键名与 RuleCore 的约定一致：

```
你的mod/Languages/ChineseSimplified/Keyed/你的包名.xml
```

| 键 | 对应 |
|---|---|
| `RuleCore.Prop.<你的 key>` | 属性显示名 |
| `RuleCore.Prop.<你的 key>Desc` | 属性工具提示 |
| `RuleCore.Verb.<你的 key>` | 谓语显示名 |
| `RuleCore.Verb.<你的 key>Desc` | 谓语工具提示 |
| `RuleCore.Subject.<你的 key>` | 主体绑定显示名 |

**兜底做法**：不想打包语言文件时，用 `.Label("受辐射")` / `.Describe("...")`。
能跑，但**不能翻译**——只在原型阶段用。

## 只读查询

给"想在运行时看看词表里有什么"的 mod：

```csharp
RulePropertyInfo info;
if (RuleCoreApi.TryGetProperty("map.outdoorTemp", out info)) { /* ... */ }

List<RulePropertyInfo> props = RuleCoreApi.PropertiesFor(RuleEntityKind.Pawn);
List<RuleVerbInfo> verbs = RuleCoreApi.VerbsFor(
    RuleEntityKind.Pawn, RuleValueKind.Entity, RuleVerbCategory.Detect);

int foreign = RuleCoreApi.RegisteredFromOtherMods;   // 有多少行来自别的 mod
```

上面两个便利方法按**类型**过滤（等价于"不按能力过滤"）。
要连能力位一起算——比如做一个自己的编辑器——直接用词表本身：

```csharp
var vocab = RuleVocabularyCatalog.Current;
var usable  = new List<RulePropertyInfo>();
var blocked = new List<RuleRejection>();       // 被挡掉的**连同缺的能力位**一起给你

vocab.CollectPropertiesFor(RuleEntityKind.Pawn, RuleCapability.NeedFood, usable, blocked);
```

`RuleRejection.missing` 就是"缺哪一位"。拿它去查 `RuleCore.Cap.<名字>` 就能对玩家说人话。

## 别做这些

- **别直接改 `RuleVocabularyCatalog` 的静态表**：那会绕过校验与重建，加载顺序一变就失效
- **别在 `reader` 里改游戏状态**：属性只读。要改状态用**操作**
- **别在 `handler`（检测）里有副作用**：条件只回答"现在是否满足 P"
- **别用 `RuleValue` 之外的类型传值**：Core 只认 `RuleValue`，它是跨 mod 的稳定接口

## API 稳定性

`RuleCoreApi` 里的公开方法签名**承诺兼容**：只增不改，要改就加新方法并保留旧的。
`RuleValue` / `RulePropertyInfo` / `RuleVerbInfo` 的公开字段同理。

**不承诺稳定**的是 UI 相关的一切（`RuleEditorView` 等）——
那是内部实现，会随界面改版而变。你的扩展不该引用它们。
