using System.Collections.Generic;
using Verse;
using RuleCore.Core;

namespace RuleCore
{
    /// <summary>
    /// 运行时规则模型 —— 内置规则与玩家规则是同一个类。
    ///
    /// 形状就是规范里的那两句：
    /// <code>
    /// 当 ⟨实体⟩⟨检测⟩⟨宾语⟩ ，… ， ⟨实体⟩⟨操作⟩⟨宾语⟩ ，…
    /// </code>
    /// 于是整个类只有四个部分：**谁**（<see cref="subjectKey"/> 绑定的本主体）、
    /// **什么时候**（<see cref="detect"/> 检测树）、**做什么**（<see cref="operate"/> 操作序列）、
    /// **多久能做一次**（<see cref="cooldownTicks"/>）。
    ///
    /// 旧的 triggers / conditions / capabilities / actions 四张表已经合并进 detect：
    /// 触发器与条件都是检测，能力也是检测（只是失败模式是"安静让开"），
    /// 幂等则是操作自己的结局之一。合并的依据是语法本身只有三段，
    /// 而"三种检测"与"两种操作"的差别全都下沉成谓词自己的声明。
    ///
    /// 它同时要满足两个加载器（RimWorld 的常态要求）：
    ///   · **XML** 只认 public 字段、不认属性 → 所有可配置项都是 public 字段 + 初始化器默认值；
    ///   · **Scribe** 走 <see cref="ExposeData"/>，多态子项用 LookMode.Deep。
    /// </summary>
    public class Rule : IExposable, IRuleRunTarget
    {
        // ── 身份 ──────────────────────────────────────────────────────

        /// <summary>稳定唯一 id。**一旦生成就不能改**——它是运行态（冷却、环检测）的键。</summary>
        public string id;

        public string label;
        public string description;
        public bool enabled = true;

        // ── 谁 ────────────────────────────────────────────────────────

        /// <summary>
        /// 本主体的绑定方式（词表里的 <c>RuleSubjectInfo.key</c>）。
        /// 空表示**不绑定**，整图评估一次（那时 `本主体` 读不到，检测若用它就会失败）。
        ///
        /// 引擎对绑定出来的每一个主体**各跑一遍规则**，冷却与自触发环也按主体分开算——
        /// 否则「袭击来了，外面的人都回家」里第一个人的一次触发会把整条规则的冷却吃掉。
        /// </summary>
        public string subjectKey;

        /// <summary>
        /// 主体绑定的**参数**：指名绑定时，指的是**哪一个**实体。
        ///
        /// 存的是那个 pawn 的 <c>ThingID</c>（如 <c>Human472</c>），不是下标也不是对象引用——
        /// 下标会随删除错位，对象引用存不进 XML 也存不进配置。
        ///
        /// <b>它的一个诚实的局限</b>：玩家规则住在**全局配置**里（见 <see cref="RuleCoreSettings"/>），
        /// 而 ThingID 是**每个存档各自生成**的。所以"指名某人"的规则换一个存档就找不到那个人，
        /// 于是不触发。这不是 bug，是"规则跨存档复用"与"指名到人"之间的固有冲突；
        /// 编辑器会把它说出来（而不是让规则静默地什么都不做）。
        /// </summary>
        public string subjectRef;

        /// <summary>
        /// 主体引用对应的**显示名缓存**。**不是权威** —— 权威永远是按 <see cref="subjectRef"/>
        /// 查出来的那个活人（他改了名字就该显示新名字）。
        /// 它存在的唯一理由是：那个人死了/那个存档没有他的时候，
        /// 界面上还能写"（找不到：小明）"而不是一个光秃秃的内部 id。
        /// </summary>
        public string subjectName;

        /// <summary>这条规则是不是"指名到某一个具体的人"。</summary>
        public bool HasNamedSubject
        {
            get { return !string.IsNullOrEmpty(subjectRef); }
        }

        // ── 什么时候 ──────────────────────────────────────────────────

        /// <summary>检测树。整棵成立才继续。空树表示"永远成立"（用来定纯周期规则）。</summary>
        public RuleExprNode detect;

        // ── 做什么 ────────────────────────────────────────────────────

        /// <summary>操作序列。顺序下发，第一条不成功就停——后面的可能依赖前面的结果。</summary>
        public List<RuleClause> operate = new List<RuleClause>();

        // ── 时序 ──────────────────────────────────────────────────────

        public int cooldownTicks;
        public int priority;

        // ── 来源（运行期推导，不进 XML 也不进存档）────────────────────

        public RuleOrigin Origin { get; set; }
        public string SourcePack { get; set; }

        public bool IsPlayerRule
        {
            get { return Origin == RuleOrigin.Player; }
        }

        public bool IsEditable
        {
            get { return Origin == RuleOrigin.Player; }
        }

        // ── 载入后推导 ────────────────────────────────────────────────

        /// <summary>本规则实际需要的权限层级 = 它所有**操作**里的最高级。</summary>
        public RuleTier EffectiveTier { get; private set; }

        /// <summary>检测树里有没有边沿谓词（`来到` 那种）。</summary>
        public bool HasEdgeDetect { get; private set; }

        /// <summary>检测树里有没有认不出的谓词键。有就说明 XML 写错了或词表缺了一行。</summary>
        public bool HasUnknownVerb { get; private set; }

        public bool RequiresDeveloperTier
        {
            get { return EffectiveTier == RuleTier.Developer; }
        }

        public bool AllowedAt(RuleTier allowedTier)
        {
            return (int)EffectiveTier <= (int)allowedTier;
        }

        public string DisplayLabel
        {
            get
            {
                if (!string.IsNullOrEmpty(label)) return label;
                return string.IsNullOrEmpty(id) ? "(无标题规则)" : id;
            }
        }

        /// <summary>
        /// 归一化 + 推导。载入后、创建后、编辑后都要调一次。
        ///
        /// 刻意不放在构造函数里：XML 加载是先建空对象再逐字段填，构造期算出来的是错的。
        /// 也刻意**不依赖"词表已经注册好了"**——Def 加载与词表注册的先后顺序不保证，
        /// 所以词表是惰性构建的（<see cref="RuleVocabularyCatalog.Current"/>）。
        /// </summary>
        public void Resolve()
        {
            if (operate == null) operate = new List<RuleClause>();

            for (int i = operate.Count - 1; i >= 0; i--)
            {
                if (operate[i] == null) operate.RemoveAt(i);
            }

            var vocabulary = RuleVocabularyCatalog.Current;

            // 归一化主体引用：**组绑定不该带着一个指名引用**（换了绑定方式没清干净）。
            // 留着它只会在界面上显示一个不生效的名字，玩家会以为规则还锁在那个人身上。
            // 在 Resolve 里清而不是报错，是因为它没有"错"——它只是一份过期的残留。
            var boundInfo = vocabulary.Subject(subjectKey);
            if (boundInfo == null || boundInfo.scope != RuleSubjectScope.Single)
            {
                subjectRef = null;
                subjectName = null;
            }

            EffectiveTier = RuleTier.Player;
            HasEdgeDetect = false;
            HasUnknownVerb = false;

            for (int i = 0; i < operate.Count; i++)
            {
                if (string.IsNullOrEmpty(operate[i].verbKey)) continue;

                var verb = vocabulary.Verb(operate[i].verbKey);
                if (verb == null)
                {
                    HasUnknownVerb = true;
                    continue;
                }

                if ((int)verb.tier > (int)EffectiveTier)
                {
                    EffectiveTier = verb.tier;
                }
            }

            ForEachLeaf(detect, delegate(RuleExprNode leaf)
            {
                if (string.IsNullOrEmpty(leaf.verbKey)) return;

                var verb = vocabulary.Verb(leaf.verbKey);
                if (verb == null)
                {
                    HasUnknownVerb = true;
                    return;
                }

                if (verb.edge == RuleEdge.Edge)
                {
                    HasEdgeDetect = true;
                }
            });
        }

        /// <summary>深度优先遍历所有节点（含连接词节点）。元数校验靠它。</summary>
        public static void ForEachNode(RuleExprNode node, System.Action<RuleExprNode> action)
        {
            if (node == null || action == null) return;

            action(node);

            for (int i = 0; i < node.ChildCount; i++)
            {
                ForEachNode(node.children[i], action);
            }
        }

        /// <summary>深度优先遍历所有叶子。校验、推导、编辑器都靠它。</summary>
        public static void ForEachLeaf(RuleExprNode node, System.Action<RuleExprNode> action)
        {
            if (node == null || action == null) return;

            if (node.IsLeaf)
            {
                action(node);
                return;
            }

            for (int i = 0; i < node.ChildCount; i++)
            {
                ForEachLeaf(node.children[i], action);
            }
        }

        // ── IRuleRunTarget ────────────────────────────────────────────

        string IRuleRunTarget.RuleId
        {
            get { return id; }
        }

        RuleTier IRuleRunTarget.EffectiveTier
        {
            get { return EffectiveTier; }
        }

        int IRuleRunTarget.CooldownTicks
        {
            get { return cooldownTicks; }
        }

        IRuleExprSource IRuleRunTarget.Detect
        {
            get { return detect; }
        }

        int IRuleRunTarget.OperateCount
        {
            get { return operate != null ? operate.Count : 0; }
        }

        IRuleOperateSource IRuleRunTarget.OperateAt(int index)
        {
            return operate[index];
        }

        // ── 存档 ──────────────────────────────────────────────────────

        public void ExposeData()
        {
            Scribe_Values.Look(ref id, "id");
            Scribe_Values.Look(ref label, "label");
            Scribe_Values.Look(ref description, "description");
            Scribe_Values.Look(ref enabled, "enabled", true);

            Scribe_Values.Look(ref subjectKey, "subjectKey");
            Scribe_Values.Look(ref subjectRef, "subjectRef");
            Scribe_Values.Look(ref subjectName, "subjectName");
            Scribe_Deep.Look(ref detect, "detect");
            Scribe_Collections.Look(ref operate, "operate", LookMode.Deep);

            Scribe_Values.Look(ref cooldownTicks, "cooldownTicks", 0);
            Scribe_Values.Look(ref priority, "priority", 0);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                Origin = RuleOrigin.Player;
                Resolve();
            }
        }

        // ── 校验 ──────────────────────────────────────────────────────

        /// <summary>
        /// 载入期校验。**不假设词表已经就绪**——词表是惰性的，真到用的时候自然会建。
        /// 走的是和求值器同一套类型规则，所以"载入时不报、跑起来才失败"这个断层被压到最小。
        /// </summary>
        public void CollectConfigErrors(List<string> into)
        {
            if (into == null) return;

            var vocabulary = RuleVocabularyCatalog.Current;

            if (string.IsNullOrEmpty(id))
            {
                into.Add("缺少 id——它是运行态的键，必须唯一且稳定。");
            }

            if (detect == null && (operate == null || operate.Count == 0))
            {
                into.Add("既没有检测也没有操作——这条规则什么也不做。");
            }

            if (cooldownTicks < 0)
            {
                into.Add("cooldownTicks 不能为负（当前 " + cooldownTicks + "）。");
            }

            if (!string.IsNullOrEmpty(subjectKey) && vocabulary.Subject(subjectKey) == null)
            {
                into.Add("主体绑定方式「" + subjectKey + "」不在词表里。");
            }

            // 主体绑定产出什么类型——路径推导与谓词过滤的依据。
            var subjectInfo = vocabulary.Subject(subjectKey);
            RuleEntityKind subjectKind = subjectInfo != null
                ? subjectInfo.entityKind
                : RuleEntityKind.Any;

            // 指名绑定却没指名：一个主体都收不出来，规则会**安静地永不触发**。
            // 这是最难自己看出来的错——语法完全正确，检测全对，就是什么都不发生。
            if (subjectInfo != null && subjectInfo.scope == RuleSubjectScope.Single
                && string.IsNullOrEmpty(subjectRef))
            {
                into.Add("主体绑定「" + subjectKey + "」是指名绑定，但还没选是哪一位"
                    + "（subjectRef 为空）——这条规则一个主体都收不出来，永远不会触发。");
            }

            if (detect != null)
            {
                int depth = detect.Depth();
                if (depth > RuleExprEval.MaxNodeDepth)
                {
                    into.Add("检测树深 " + depth + " 层，超过求值器上限 "
                        + RuleExprEval.MaxNodeDepth + "——载入后会被判 detect.too_deep。");
                }

                // 元数校验。空的「且」在数学上是恒真，但那不是玩家想要的东西：
                // 他刚点了「+ 且」还没填，而规则会静默地永远成立。
                // **"空集合"在布尔运算里不能靠"没填"来表达"全都通过"**——和空筛选是同一个道理。
                ForEachNode(detect, delegate(RuleExprNode node)
                {
                    if (node.kind == RuleExprNodeKind.Not)
                    {
                        if (node.ChildCount != 1)
                        {
                            into.Add("「非」必须正好作用在一句检测上（当前 "
                                + node.ChildCount + " 句）。");
                        }
                        return;
                    }

                    if ((node.kind == RuleExprNodeKind.And || node.kind == RuleExprNodeKind.Or)
                        && node.ChildCount == 0)
                    {
                        into.Add("空的「" + (node.kind == RuleExprNodeKind.And ? "且" : "或")
                            + "」：还没往里加东西。"
                            + (node.kind == RuleExprNodeKind.And
                                ? "空的「且」会被判为永远成立。"
                                : "空的「或」会被判为永远不成立。"));
                    }
                });

                bool anyLevel = false;

                ForEachLeaf(detect, delegate(RuleExprNode leaf)
                {
                    // 「还没选谓语」不是错误：编辑器里它就是一个灰色的空槽。
                    // 把它报成"谓词不在词表里"等于把"没填"说成"填错了"。
                    if (string.IsNullOrEmpty(leaf.verbKey)) return;

                    var verb = vocabulary.Verb(leaf.verbKey);
                    if (verb == null)
                    {
                        into.Add("检测里的谓词「" + leaf.verbKey
                            + "」不在词表里——多半是键写错了，或者词表少了一行。");
                        return;
                    }

                    if (verb.category != RuleVerbCategory.Detect)
                    {
                        into.Add("「" + verb.key + "」是操作，不能出现在检测里。");
                        return;
                    }

                    if (verb.edge == RuleEdge.Level) anyLevel = true;

                    ValidateOperand(vocabulary, verb, leaf.argument,
                        "检测「" + verb.key + "」", subjectKind, into);

                    if (leaf.subject == null)
                    {
                        into.Add("检测「" + verb.key + "」没有主语。");
                        return;
                    }

                    // 路径本身的类型错（把「耐久」挂在地图上、集合没归约就往下读）——
                    // 载入时就报，而不是等跑起来在时间线里找。
                    string pathError = RulePathTypes.FindTypeError(leaf.subject, subjectKind,
                        false, RuleEntityKind.Any);
                    if (pathError != null)
                    {
                        into.Add("检测「" + verb.key + "」的主语路径： " + pathError);
                        return;
                    }

                    // 谓词吃不吃得下这个值类型（「大于」用在一个人身上）。
                    if (verb.subjectValueKind != RuleValueKind.None)
                    {
                        RuleValueKind valueKind;
                        RuleEntityKind valueEntity;
                        RulePropertyInfo lastProperty;
                        RulePathTypes.AdvanceType(leaf.subject, subjectKind, false,
                            RuleEntityKind.Any, leaf.subject.StepCount,
                            out valueKind, out valueEntity, out lastProperty);

                        if (valueKind != verb.subjectValueKind)
                        {
                            into.Add("检测「" + verb.key + "」要 " + verb.subjectValueKind
                                + "，但主语产出的是 " + valueKind + "。");
                        }
                    }

                    // 主语绑的是"本主体"，但规则没有主体绑定 → 这条检测永远读不到。
                    // 这是最难自己看出来的错：规则一条日志都不写，而检测本身看起来完全正常。
                    if (leaf.subject.rootKind == RuleRootKind.Subject
                        && string.IsNullOrEmpty(subjectKey))
                    {
                        into.Add("检测「" + verb.key + "」读的是本主体，但规则没有主体绑定方式"
                            + "——这一句永远不成立。");
                    }
                });

                // 防呆：电平检测 + 零冷却 = 每 tick 重复下发，初学者最典型的刷屏 bug。
                if (anyLevel && cooldownTicks <= 0 && operate != null && operate.Count > 0
                    && !HasEdgeDetect)
                {
                    into.Add("全是电平检测但 cooldownTicks 为 0：动作若不做幂等，会被每 tick 重复下发。");
                }
            }

            for (int i = 0; i < operate.Count; i++)
            {
                var clause = operate[i];
                string where = "操作[" + i + "]";

                var verb = vocabulary.Verb(clause.verbKey);
                if (verb == null)
                {
                    // **「还没选」不是「填错了」。**
                    //
                    // 空槽在编辑器里就是一个灰色的占位符。把它报成"不在词表里"，
                    // 玩家会去翻自己填过的那个词——而他根本没填过。
                    // （检测那一侧早就有这条判断，操作这一侧漏了。）
                    if (string.IsNullOrEmpty(clause.verbKey))
                    {
                        into.Add(where + " 还没选操作——这条子句现在什么也不做。");
                        continue;
                    }

                    into.Add(where + " 的谓词「" + clause.verbKey + "」不在词表里。");
                    continue;
                }

                if (verb.category != RuleVerbCategory.Operate)
                {
                    into.Add(where + " 用的是检测「" + verb.key + "」——操作位里只能放操作。");
                    continue;
                }

                if (clause.subject == null)
                {
                    into.Add(where + " 没有主语。");
                }

                ValidateOperand(vocabulary, verb, clause.argument,
                    where + "「" + verb.key + "」", subjectKind, into);
            }
        }

        private static void ValidateOperand(RuleVocabulary vocabulary, RuleVerbInfo verb,
            RuleOperand operand, string where, RuleEntityKind subjectKind, List<string> into)
        {
            if (verb.NeedsArgument && (operand == null || operand.Kind == RuleValueKind.None))
            {
                // 「还没填」不是错误，是"没配完"。报出来是因为一条半成品规则跑起来一定失败，
                // 与其让玩家在时间线里找，不如在表单上直接说。
                into.Add(where + " 的宾语没有配置（需要 " + verb.argKind + "）。");
                return;
            }

            if (operand == null) return;

            if (operand.Kind == RuleValueKind.Entity)
            {
                if (operand.path == null)
                {
                    into.Add(where + " 的宾语写成「实体」却没有任何路径。");
                    return;
                }

                // 宾语路径的**类型**也要对得上谓词要的宾语类型。
                // 不查的话，`前往 本主体`（要格子、给了个人）这种话要到跑起来才失败。
                RuleValueKind pathKind;
                RuleEntityKind pathEntity;
                RulePropertyInfo pathLast;
                RulePathTypes.AdvanceType(operand.path, subjectKind, false,
                    RuleEntityKind.Any, operand.path.StepCount,
                    out pathKind, out pathEntity, out pathLast);

                if (pathKind == RuleValueKind.EntitySet)
                {
                    into.Add(where + " 的宾语是一组东西——先接一个归约。");
                }
                else if (pathKind == RuleValueKind.Entity
                    && !RuleVocabulary.EntityKindMatches(verb.argEntity, pathEntity))
                {
                    into.Add(where + " 的宾语类型不对：要 " + verb.argEntity
                        + "，给的是 " + pathEntity + "。");
                }
                return;
            }

            if (operand.Kind != RuleValueKind.None && operand.Kind != verb.argKind)
            {
                into.Add(where + " 的宾语类型不对：要 " + verb.argKind + "，给的是 " + operand.Kind + "。");
                return;
            }

            // 枚举宾语还没选值：kind 定下来了但 key 是空的。
            // 这时求值会以"没有指定要匹配哪一类"失败，所以载入时就该说。
            if (operand.Kind == RuleValueKind.Enum && string.IsNullOrEmpty(operand.literal.key))
            {
                into.Add(where + " 的宾语还没选值。");
            }
        }

        public override string ToString()
        {
            return (id ?? "(无 id)")
                + " [" + Origin + "/" + EffectiveTier + "]"
                + " 主体=" + (string.IsNullOrEmpty(subjectKey) ? "整图" : subjectKey)
                + (HasNamedSubject ? "(" + (subjectName ?? subjectRef) + ")" : string.Empty)
                + " 检测" + CountLeaves(detect)
                + " 操作" + (operate != null ? operate.Count : 0);
        }

        private static int CountLeaves(RuleExprNode node)
        {
            if (node == null) return 0;
            if (node.IsLeaf) return 1;

            int total = 0;
            for (int i = 0; i < node.ChildCount; i++)
            {
                total += CountLeaves(node.children[i]);
            }
            return total;
        }
    }

    /// <summary>规则从哪来。UI 面板据此打徽章、决定能不能编辑。</summary>
    public enum RuleOrigin
    {
        /// <summary>模组内置，写在 XML 里。只读。</summary>
        Mod = 0,

        /// <summary>玩家在游戏内创建，存在全局配置里。可编辑、可删。</summary>
        Player = 1
    }
}
