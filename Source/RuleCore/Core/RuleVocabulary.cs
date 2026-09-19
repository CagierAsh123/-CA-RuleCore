using System;
using System.Collections.Generic;

namespace RuleCore.Core
{
    /// <summary>
    /// 属性读取器：把一个实体身上的某个属性读成一个值。
    /// 和谓词一样，**行为挂在描述对象上**——另开一张"键 → 实现"的平行表迟早会漂。
    /// </summary>
    public delegate bool RulePropertyReadHandler(IRuleEvalHost host, RuleValue owner,
        out RuleValue value, out string reasonCode, out string reason);

    /// <summary>
    /// 一个枚举取值。**不来自 Def 表的枚举**用它来声明取值域（如"身份"：殖民者/囚犯/奴隶）。
    ///
    /// 存在的原因和 <see cref="RulePropertyInfo.enumDefType"/> 一样，只是来源不同：
    /// 天气、信件类型、物品类型这些在 RimWorld 里本来就是 Def，能查出来；
    /// 而"这个人是囚犯还是奴隶"是**算出来的**，没有任何 Def 记着它。
    /// 两者都不给的话，玩家只能手打一个内部标识符——那正是要消灭的东西。
    /// </summary>
    public sealed class RuleEnumOption
    {
        /// <summary>稳定键。**序列化进存档的就是它**，所以不能随翻译变。</summary>
        public readonly string key;

        /// <summary>显示名的语言键。查不到就显示 key。</summary>
        public readonly string labelKey;

        /// <summary>
        /// **直接的显示名** —— 给"运行时候选"用（活动区、着装方案……）。
        ///
        /// 那些名字是**玩家自己起的**（"厨房"、"工人方案"），没有语言键可查，
        /// 只能在要用的时候把名字原样带过来。静态候选留空即可。
        /// </summary>
        public readonly string label;

        public RuleEnumOption(string key, string labelKey)
            : this(key, labelKey, null)
        {
        }

        public RuleEnumOption(string key, string labelKey, string label)
        {
            this.key = key;
            this.labelKey = labelKey;
            this.label = label;
        }
    }

    /// <summary>
    /// **运行时候选清单**的收集器 —— 取值域的第三种来源。
    ///
    /// 三种并列，各有各的场合：
    ///
    /// | 来源 | 谁提供 | 例子 |
    /// |---|---|---|
    /// | <see cref="RulePropertyInfo.enumDefType"/> | Def 表 | 天气、事件、物品类型 |
    /// | <see cref="RulePropertyInfo.enumOptions"/> | 作者写死的清单 | 身份、性别 |
    /// | **这个** | **存档里算出来的** | **活动区、着装方案、药物政策** |
    ///
    /// 第三种必须存在，因为管制界面那一整列东西**都不是 Def**：
    /// 它们是玩家在游戏里自己建的对象，数量与名字每次读档都可能不同。
    /// **只能在要用的时候现场问**——写死的清单第二天就对不上了。
    /// </summary>
    public delegate void RuleEnumCandidateHandler(List<RuleEnumOption> into);

    /// <summary>
    /// 一条**被挡掉的选项**——给编辑器把"为什么没有它"说出来。
    ///
    /// 这件事不是锦上添花：玩家看着一个只有三行的下拉，**看不出是这一行不存在
    /// 还是自己主体选错了**。把他缺的那一位能力指出来，他才知道下一步该改什么。
    /// </summary>
    public struct RuleRejection
    {
        /// <summary>被挡掉的属性/谓词键。</summary>
        public string key;

        /// <summary>显示名的语言键（与它没被挡掉时用同一个）。</summary>
        public string labelKey;

        /// <summary>缺的能力位。**编辑器据此显示"他缺：饱食需求"**。</summary>
        public RuleCapability missing;
    }

    /// <summary>
    /// 一个**属性**的元数据：`本图.室外温度`、`本主体.着装`、`本主体.位置`。
    ///
    /// 属性是纯数据描述（key + 类型 + 单位 + 范围），读它的代码在 Verse 那一侧注册时挂上来。
    /// 这样做的直接好处：**编辑器不需要为每个属性写界面**——
    /// 它读的是这张表，所以"新加一个属性"= 加一行，界面自动就有了
    /// （下拉里出现、单位正确、范围正确、"可能多件所以要归约"也自动被要求）。
    /// </summary>
    public sealed class RulePropertyInfo
    {
        /// <summary>稳定键，如 <c>map.outdoorTemp</c>。序列化存的就是它。</summary>
        public string key;

        /// <summary>挂在什么类型的实体上。决定它在编辑器里什么时候出现在下拉里。</summary>
        public RuleEntityKind owner = RuleEntityKind.Any;

        /// <summary>
        /// 产出什么类型的值。
        /// <see cref="RuleValueKind.EntitySet"/> 表示**可能多件**——编辑器会强制要求后面接归约。
        /// 刻意不额外加一个 <c>mayBeMultiple</c> 布尔：两个字段说同一件事，迟早会不一致，
        /// 而"结果类型是集合"本身就是"必须归约"的充分条件。
        /// </summary>
        public RuleValueKind result = RuleValueKind.Number;

        /// <summary>产出实体/集合时，元素是什么类型。路径上的类型检查靠它往下走。</summary>
        public RuleEntityKind resultEntity = RuleEntityKind.Any;

        public float min = float.MinValue;
        public float max = float.MaxValue;

        /// <summary>数值显示保留几位小数。</summary>
        public int decimals = 2;

        /// <summary>单位后缀，如 <c>℃</c>。空则不加。</summary>
        public string unit;

        /// <summary>以百分比显示（0.5 → 50%）。</summary>
        public bool percent;

        /// <summary>
        /// 产出枚举时，取值来自哪个 Def 类型（如 <c>WeatherDef</c>）。
        /// 编辑器据此给一个**选择器**而不是让玩家手打 defName；为 null 就只能手填。
        /// 放在属性上而不是谓词上，是因为"是哪个枚举"是**属性**的性质：
        /// 同一个「是」可以比天气，也可以比物品类型。
        /// </summary>
        public Type enumDefType;

        /// <summary>
        /// 取值不来自 Def 表时的**固定选项**（如「身份」）。
        /// 与 <see cref="enumDefType"/> 二选一，都为空就只能手填。
        /// </summary>
        public RuleEnumOption[] enumOptions;

        /// <summary>
        /// 取值来自**存档里的对象**（活动区、着装方案……）。见 <see cref="RuleEnumCandidateHandler"/>。
        /// 与 <see cref="enumOptions"/> / <see cref="enumDefType"/> 三选一。
        /// </summary>
        public RuleEnumCandidateHandler enumCandidates;

        /// <summary>
        /// 挂在什么样的宿主上才有意义。见 <see cref="RuleCapability"/>。
        ///
        /// <see cref="RuleCapability.None"/>（默认）= 不挑。给机械族也列「饱食度」是
        /// 编辑器最典型的坑，而这一行就是堵它的地方。
        /// </summary>
        public RuleCapability requires = RuleCapability.None;

        /// <summary>
        /// 行为本体，由 Verse 那一侧注册时挂上来。
        /// **和谓词一样放在同一个描述对象里**，避免"键 → 实现"的平行表漂移。
        /// </summary>
        public RulePropertyReadHandler reader;

        public string LabelKey
        {
            get { return "RuleCore.Prop." + key; }
        }

        public string DescKey
        {
            get { return "RuleCore.Prop." + key + "Desc"; }
        }
    }

    /// <summary>
    /// 一个**谓词**（检测或操作）的元数据。
    ///
    /// 这是整个设计里最要紧的一张表：**类型过滤、权限、边沿语义、失败模式全在这里**，
    /// 于是"玩家面对 20 个谓词、其中 3 个能用"这件事变成一次表查询。
    /// 不做按类型过滤，规则语言的可用性就无从谈起——这是规范里列为硬要求的一条。
    /// </summary>
    public sealed class RuleVerbInfo
    {
        /// <summary>稳定键，如 <c>compare.greater</c>、<c>op.goto</c>。序列化存的就是它。</summary>
        public string key;

        public RuleVerbCategory category = RuleVerbCategory.Detect;

        /// <summary>作用于什么类型的实体。</summary>
        public RuleEntityKind subject = RuleEntityKind.Any;

        /// <summary>
        /// 主语必须是什么**值类型**。<see cref="RuleValueKind.None"/> 表示不限制。
        ///
        /// 为什么需要它：`大于` 只吃数值，但它的主语在**实体类型**上是不挑的
        /// （温度是数值、血量也是数值，都不是"实体"）。只按实体类型过滤的话，
        /// 「大于」会出现在 `本主体`（一个 Pawn）身上——玩家选了它必然报
        /// <c>compare.not_number</c>。这类"看得见但选了必错"的选项正是要消灭的东西。
        /// </summary>
        public RuleValueKind subjectValueKind = RuleValueKind.None;

        /// <summary>宾语槽接受什么类型的值。<see cref="RuleValueKind.None"/> 表示这个谓词不需要宾语。</summary>
        public RuleValueKind argKind = RuleValueKind.None;

        /// <summary>宾语是实体/集合时，要求什么类型。</summary>
        public RuleEntityKind argEntity = RuleEntityKind.Any;

        /// <summary>边沿还是电平。**是谓词作者的声明，不是玩家的选择。**</summary>
        public RuleEdge edge = RuleEdge.Level;

        /// <summary>不成立时算规则失败还是安静跳过（原来的"条件"与"能力"的区别落在这里）。</summary>
        public RuleFailureMode failure = RuleFailureMode.RuleFailed;

        /// <summary>需要什么权限层级。取规则所有操作的最高级。</summary>
        public RuleTier tier = RuleTier.Player;

        /// <summary>
        /// 行为本体，由 Verse 那一侧注册时挂上来。
        ///
        /// **刻意放在同一个描述对象里**，而不是另开一张"键 → 实现"的平行表：
        /// 平行表迟早会漂（登记了描述忘了实现，或反过来），
        /// 而放在一起之后"描述和实现对不上"这件事在类型上就不可能发生。
        /// </summary>
        public RuleDetectHandler detect;

        public RuleOperateHandler operate;

        /// <summary>
        /// 宾语是枚举时，取值来自哪个 Def 类型（如信件触发的 <c>LetterDef</c>）。
        /// 有它就出选择器，没有就手填——**不猜**：让编辑器猜"这个枚举该从哪来"，
        /// 猜错的后果是玩家在下拉里找不到想要的东西，而不知道该去改哪里。
        /// </summary>
        public Type argDefType;

        /// <summary>宾语是枚举、但取值来自"算出来的固定选项"时的清单（与 <see cref="argDefType"/> 二选一）。</summary>
        public RuleEnumOption[] argOptions;

        /// <summary>宾语取值来自**存档里的对象**（活动区、着装方案……）。见 <see cref="RuleEnumCandidateHandler"/>。</summary>
        public RuleEnumCandidateHandler argCandidates;

        /// <summary>
        /// **数值宾语的显示元数据**（单位 / 百分比 / 范围 / 小数位）。
        ///
        /// 数值宾语的显示一向是从"主语路径最后读到的那个属性"来的
        /// （`血量 小于 [50%]` 的百分号就是那么来的）。但**操作的主语是执行者**
        /// ——`本主体 充电 [__]` 的路径上什么都没有——于是玩家面对一个裸数字框，
        /// 输入 1 会被当成 100%。这里就是补那个位置。
        ///
        /// 刻意**复用 <see cref="RulePropertyInfo"/>**：它正好就是那四样元数据，
        /// 另开一个类只会多一份要同步的东西（key / owner / reader 留空即可）。
        /// </summary>
        public RulePropertyInfo argDisplay;

        /// <summary>
        /// 宾语取值的**额外筛选**（可选）。参数是那个候选值（Verse 侧是一个 <c>Def</c>），
        /// 返回它**此刻**能不能用。
        ///
        /// 类型过滤管的是"这个宾语类型对不对"，管不了"这个具体的值此刻能不能用"。
        /// 「触发事件」要的正是后者：日蚀 / 太阳耀斑 / 极光的 <c>targetTags</c> 只有
        /// <c>World</c>，而从"本图"发出去会被原版第一关挡下——把它们列在菜单里，
        /// 玩家**选了才知道不行**，那正是这套东西存在的全部理由要消灭的东西。
        ///
        /// 用 <c>object</c> 而不是 <c>Def</c>：Core 不认识 Verse 的类型
        /// （和 <see cref="RuleValue.AsHandle"/> 同一个道理）。
        /// </summary>
        public System.Func<object, bool> argFilter;

        /// <summary>主语要具备什么能力才有意义（如「脱下」要求那个主体真的能穿衣服）。</summary>
        public RuleCapability requires = RuleCapability.None;

        public string LabelKey
        {
            get { return "RuleCore.Verb." + key; }
        }

        public string DescKey
        {
            get { return "RuleCore.Verb." + key + "Desc"; }
        }

        public bool NeedsArgument
        {
            get { return argKind != RuleValueKind.None; }
        }
    }

    /// <summary>
    /// 一个**归约算子**的元数据：把一个集合变回单值。
    ///
    /// 第一期只有三个（第一个 / 数量 / 最近）。实施时发现另外几个
    /// （求和 / 最大 / 最小 / 全部 / 任意 / 无）**形状不一样**：
    /// 它们不是"把集合归约成单值再比"，而是"对每个元素各做一次比较、再合并结果"，
    /// 也就是**检测上的量词**（类似 LINQ 的 All/Any/Sum），需要引入元素绑定。
    /// 那是另一个复杂度级别，而 RimWorld 里"一个槽位穿多件同类装备"极少见，
    /// 集合通常只有 0 或 1 个元素，所以第一期不做。
    /// </summary>
    public sealed class RuleReduceInfo
    {
        public readonly RuleReduceKind kind;

        /// <summary>接受什么（目前都是实体集合）。</summary>
        public readonly RuleValueKind from;

        /// <summary>产出什么。</summary>
        public readonly RuleValueKind to;

        public RuleReduceInfo(RuleReduceKind kind, RuleValueKind from, RuleValueKind to)
        {
            this.kind = kind;
            this.from = from;
            this.to = to;
        }

        public string LabelKey
        {
            get { return "RuleCore.Reduce." + kind; }
        }
    }

    /// <summary>
    /// 主体绑定器：把"本主体"落到一组具体实体上。引擎对**每一个**各跑一遍规则。
    ///
    /// 这就是原来 <c>RuleSelector</c> 的归宿。它的存在解释了为什么
    /// 「袭击来了，外面的人都回家」里每个人各自欠一次"回家"：
    /// 冷却与自触发环都是按主体分开算的，一个人的一次触发不该消耗掉别人的。
    /// </summary>
    public delegate void RuleSubjectCollectHandler(IRuleEvalHost host, List<RuleValue> into);

    /// <summary>
    /// 一个**主体绑定方式**的元数据：规则里的"本主体"到底指谁。
    ///
    /// 它和属性、谓词一样是词表里的一行，理由是同一个：编辑器要按类型过滤。
    /// 「每个自由殖民者各跑一遍」和将来的「每个囚犯」「每张床」在这里是同一种东西。
    /// </summary>
    public sealed class RuleSubjectInfo
    {
        /// <summary>稳定键，如 <c>colonists</c>。空键表示"不绑定主体，整图评估一次"。</summary>
        public string key;

        /// <summary>绑定出来的是什么东西。决定本主体身上能用哪些属性和谓词。</summary>
        public RuleEntityKind entityKind = RuleEntityKind.Pawn;

        /// <summary>
        /// 绑一个还是一群。见 <see cref="RuleSubjectScope"/>。
        ///
        /// <see cref="RuleSubjectScope.Single"/> 的绑定**必须**由规则给出
        /// <see cref="Rule.subjectRef"/>（指名是哪个人），否则一个主体都收不出来。
        /// </summary>
        public RuleSubjectScope scope = RuleSubjectScope.Group;

        /// <summary>
        /// **候选清单**：编辑期列出"可以指名哪些实体"。
        ///
        /// 只有 <see cref="RuleSubjectScope.Single"/> 的绑定需要它。
        /// 有它，编辑器才能给出一个"当前地图上的人"的列表，
        /// 而不是让玩家去手抄一个内部 id —— 手抄内部 id 是绝不会被接受的交互。
        /// </summary>
        public RuleSubjectCollectHandler candidates;

        /// <summary>行为本体，由 Verse 那一侧注册时挂上来。</summary>
        public RuleSubjectCollectHandler binder;

        public string LabelKey
        {
            get { return "RuleCore.Subject." + key; }
        }

        public string DescKey
        {
            get { return "RuleCore.Subject." + key + "Desc"; }
        }
    }

    /// <summary>
    /// 规则语言的词表 —— **「都能写什么」的唯一出处**。
    ///
    /// 它是按类型索引的**扁平表**，不是继承体系。这是规范里那句
    /// "一个谓词基类 + 一张按类型索引的词表"的落地：
    /// 现在每加一种现象都要写一个类（`RuleCondition_TemperatureAbove10` 之类），
    /// 新模型里只有"真正新的谓词"才需要写，而"温度大于10"这种组合不需要任何新代码。
    ///
    /// 注册在启动期一次完成（Verse 那一侧填表），之后只读。
    /// 注册期发现重复键会记进 <see cref="DuplicateKeys"/> 而不是抛异常——
    /// 一个 mod 写坏了自己的词表，不该让整个游戏起不来，但必须能被测出来。
    /// </summary>
    public sealed class RuleVocabulary
    {
        private readonly Dictionary<string, RulePropertyInfo> properties =
            new Dictionary<string, RulePropertyInfo>(StringComparer.Ordinal);

        private readonly Dictionary<string, RuleVerbInfo> verbs =
            new Dictionary<string, RuleVerbInfo>(StringComparer.Ordinal);

        private readonly List<RulePropertyInfo> propertyList = new List<RulePropertyInfo>();
        private readonly List<RuleVerbInfo> verbList = new List<RuleVerbInfo>();
        private readonly List<RuleSubjectInfo> subjectList = new List<RuleSubjectInfo>();
        private readonly List<string> duplicateKeys = new List<string>();

        /// <summary>注册期冲突的键。自检会读它。</summary>
        public IReadOnlyList<string> DuplicateKeys
        {
            get { return duplicateKeys; }
        }

        public int PropertyCount
        {
            get { return propertyList.Count; }
        }

        public int VerbCount
        {
            get { return verbList.Count; }
        }

        public void Add(RulePropertyInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.key)) return;

            if (properties.ContainsKey(info.key))
            {
                duplicateKeys.Add("property:" + info.key);
                return;
            }

            properties.Add(info.key, info);
            propertyList.Add(info);
        }

        public void Add(RuleVerbInfo info)
        {
            if (info == null || string.IsNullOrEmpty(info.key)) return;

            if (verbs.ContainsKey(info.key))
            {
                duplicateKeys.Add("verb:" + info.key);
                return;
            }

            verbs.Add(info.key, info);
            verbList.Add(info);
        }

        public RulePropertyInfo Property(string key)
        {
            RulePropertyInfo info;
            if (!string.IsNullOrEmpty(key) && properties.TryGetValue(key, out info))
            {
                return info;
            }
            return null;
        }

        public RuleVerbInfo Verb(string key)
        {
            RuleVerbInfo info;
            if (!string.IsNullOrEmpty(key) && verbs.TryGetValue(key, out info))
            {
                return info;
            }
            return null;
        }

        // ── 主体绑定方式 ──────────────────────────────────────────────

        public void Add(RuleSubjectInfo info)
        {
            if (info == null) return;

            subjectList.Add(info);
        }

        /// <summary>键为空/不认识的绑定方式 → 返回 null，表示"整图评估一次"。</summary>
        public RuleSubjectInfo Subject(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;

            for (int i = 0; i < subjectList.Count; i++)
            {
                if (subjectList[i].key == key) return subjectList[i];
            }
            return null;
        }

        public void CollectSubjects(List<RuleSubjectInfo> into)
        {
            if (into == null) return;
            into.Clear();
            into.AddRange(subjectList);
        }

        public int SubjectCount
        {
            get { return subjectList.Count; }
        }

        // ── 按类型过滤：编辑器就靠这两个方法决定"下拉里有什么" ─────────

        /// <summary>某个类型的实体身上可用的属性。**顺序即注册顺序 = 设计顺序**。</summary>
        public void CollectPropertiesFor(RuleEntityKind owner, List<RulePropertyInfo> into)
        {
            CollectPropertiesFor(owner, RuleCapability.All, into, null);
        }

        /// <summary>
        /// 带上**宿主能力**的版本。这是"机械族身上不列饱食度"落地的地方。
        /// <paramref name="have"/> 传 <see cref="RuleCapability.All"/> 表示"不知道宿主是什么，全都给"。
        /// </summary>
        public void CollectPropertiesFor(RuleEntityKind owner, RuleCapability have,
            List<RulePropertyInfo> into)
        {
            CollectPropertiesFor(owner, have, into, null);
        }

        /// <summary>
        /// 带**拒绝清单**的版本。被挡掉的项连同"缺哪一位能力"一起返回，
        /// 编辑器据此把"没有它"和"你选错了主体"分开说——这两件事在玩家看来
        /// 都是"列表里没有"，但该做的下一步完全不同。
        /// </summary>
        public void CollectPropertiesFor(RuleEntityKind owner, RuleCapability have,
            List<RulePropertyInfo> into, List<RuleRejection> rejected)
        {
            if (into == null) return;
            into.Clear();
            if (rejected != null) rejected.Clear();

            for (int i = 0; i < propertyList.Count; i++)
            {
                var info = propertyList[i];
                if (!EntityKindMatches(info.owner, owner)) continue;

                if (!CapabilityMatches(have, info.requires))
                {
                    if (rejected != null)
                    {
                        rejected.Add(new RuleRejection
                        {
                            key = info.key,
                            labelKey = info.LabelKey,
                            missing = info.requires
                        });
                    }
                    continue;
                }

                into.Add(info);
            }
        }

        /// <summary>
        /// 某个类型的实体（且当前值是某个值类型）身上可用的谓词。
        ///
        /// **三个维度都要对**：实体类型决定"能不能作用在这个东西上"，
        /// 值类型决定"这个谓词吃不吃得下它现在这个值"，
        /// 能力位决定"这个宿主身上这件事存不存在"。
        /// 编辑器把它们一起传进来，于是「大于」不会出现在小人身上，
        /// 「脱下」也不会出现在一个穿不了衣服的机械族身上。
        /// </summary>
        public void CollectVerbsFor(RuleEntityKind subject, RuleValueKind subjectValue,
            RuleVerbCategory category, List<RuleVerbInfo> into)
        {
            CollectVerbsFor(subject, subjectValue, category, RuleCapability.All, into, null);
        }

        public void CollectVerbsFor(RuleEntityKind subject, RuleValueKind subjectValue,
            RuleVerbCategory category, RuleCapability have, List<RuleVerbInfo> into,
            List<RuleRejection> rejected)
        {
            if (into == null) return;
            into.Clear();
            if (rejected != null) rejected.Clear();

            for (int i = 0; i < verbList.Count; i++)
            {
                var info = verbList[i];
                if (info.category != category) continue;
                if (!EntityKindMatches(info.subject, subject)) continue;

                if (info.subjectValueKind != RuleValueKind.None
                    && info.subjectValueKind != subjectValue)
                {
                    continue;
                }

                // **集合永远不能被"只作用于单个实体"的谓词接住。**
                // 它必须先归约——这是"归约强制"在**过滤**这一侧的对应物。
                //
                // 之前只挡住了"往下读属性"，没挡住"选谓语"，于是能写出
                // 「全部自由殖民者 前往 本主体」这种胡话：把一组人当成一个人用。
                // 求值器当场就会以 needs_reduce 拒掉，但玩家是**选了才发现**——
                // 而"看得见但选了必错"正是要消灭的东西。
                if (subjectValue == RuleValueKind.EntitySet
                    && info.subjectValueKind != RuleValueKind.EntitySet)
                {
                    continue;
                }

                if (!CapabilityMatches(have, info.requires))
                {
                    if (rejected != null)
                    {
                        rejected.Add(new RuleRejection
                        {
                            key = info.key,
                            labelKey = info.LabelKey,
                            missing = info.requires
                        });
                    }
                    continue;
                }

                into.Add(info);
            }
        }

        /// <summary>
        /// 宿主是否具备要求的能力。判据是**含位**，所以：
        ///   · <c>need == None</c>（不挑）恒成立；
        ///   · <c>have == All</c>（不知道宿主是什么）恒成立——宁可多给，不可少给；
        ///   · 其余必须每一位都有。
        /// </summary>
        public static bool CapabilityMatches(RuleCapability have, RuleCapability need)
        {
            return (have & need) == need;
        }

        // ── 类型匹配 ──────────────────────────────────────────────────

        /// <summary>
        /// 期望类型与实到类型是否相容。
        ///
        /// <c>expected == Any</c> 匹配一切（"这个谓词不挑食"）；
        /// 但 <c>actual == Any</c> **只匹配 Any** —— 实到值不知道自己的实体类型时
        /// （数值、枚举、文本），不该被一个要求 Pawn 的谓词认下来。
        /// 早期版本两个方向都放行，结果是"把数字喂给了只作用于小人的谓词"也能过。
        /// </summary>
        public static bool EntityKindMatches(RuleEntityKind expected, RuleEntityKind actual)
        {
            if (expected == RuleEntityKind.Any) return true;
            if (actual == RuleEntityKind.Any) return false;
            return expected == actual;
        }

        /// <summary>
        /// 宾语是否满足谓词的要求。**编辑器用它把"选了必然报错"的组合挡在下拉之外**，
        /// 求值器用它做开跑前的最后一次检查（因为 XML 可以被手改）。
        /// </summary>
        public static bool ArgMatches(RuleVerbInfo verb, RuleValue arg)
        {
            if (verb == null) return false;

            if (verb.argKind == RuleValueKind.None)
            {
                // 不需要宾语。给了也不算错（存着但不看），因为玩家可能先把宾语填上再改谓词。
                return true;
            }

            // 宾语没填：不算"类型不匹配"，这是"还没填"。分开表达，
            // 否则编辑器会把"空槽"显示成"类型错误"，玩家会以为自己填错了。
            if (arg.IsMissing) return true;

            if (arg.kind != verb.argKind) return false;

            if (arg.kind == RuleValueKind.Entity || arg.kind == RuleValueKind.EntitySet)
            {
                return EntityKindMatches(verb.argEntity, arg.entityKind);
            }

            return true;
        }

        // ── 归约 ──────────────────────────────────────────────────────

        private static readonly RuleReduceInfo[] reduceTable =
        {
            new RuleReduceInfo(RuleReduceKind.First, RuleValueKind.EntitySet, RuleValueKind.Entity),
            new RuleReduceInfo(RuleReduceKind.Nearest, RuleValueKind.EntitySet, RuleValueKind.Entity),
            new RuleReduceInfo(RuleReduceKind.Count, RuleValueKind.EntitySet, RuleValueKind.Number)
        };

        public static IReadOnlyList<RuleReduceInfo> AllReduces
        {
            get { return reduceTable; }
        }

        public static RuleReduceInfo ReduceInfo(RuleReduceKind kind)
        {
            for (int i = 0; i < reduceTable.Length; i++)
            {
                if (reduceTable[i].kind == kind) return reduceTable[i];
            }
            return null;
        }

        /// <summary>这个归约能不能用在当下的值上。</summary>
        public static bool ReduceAvailable(RuleReduceKind kind, RuleValue set)
        {
            var info = ReduceInfo(kind);
            if (info == null) return false;
            return info.from == set.kind;
        }
    }
}
