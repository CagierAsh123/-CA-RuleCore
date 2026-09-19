using System.Collections.Generic;
using Verse;
using RuleCore.Core;

namespace RuleCore
{
    /// <summary>
    /// <b>RuleCore 对外注册 API</b> —— 别的 mod 通过它把新的「主谓宾」加进规则语言。
    ///
    /// 规则语言的三块词表（<b>实体</b> / <b>谓语</b> / <b>宾语</b>）在这里开放登记。
    /// 加一行 = 加一种玩法，**不需要改 RuleCore 的任何代码**：
    ///
    /// <code>
    /// [StaticConstructorOnStartup]
    /// public static class MyModRules
    /// {
    ///     static MyModRules()
    ///     {
    ///         // 实体：给地图加一个"辐射强度"
    ///         RuleCoreApi.Property("mymod.radiation", RuleEntityKind.Map,
    ///                 RuleValueKind.Number, unit: "mSv",
    ///                 reader: (host, owner, out RuleValue value, out string code, out string why) =>
    ///                 {
    ///                     var map = RuleEvalHost.MapOf(owner);
    ///                     if (map == null) { value = RuleValue.None; code = "mymod.no_map"; why = "没有地图。"; return false; }
    ///                     value = RuleValue.OfNumber(MyRadiationFor(map));
    ///                     code = null; why = null; return true;
    ///                 })
    ///             .Range(0f, 50f).Decimals(1)
    ///             .Register();
    ///
    ///         // 谓语：一个检测
    ///         RuleCoreApi.Detect("mymod.irradiated", RuleEntityKind.Pawn,
    ///                 RuleValueKind.Entity, RuleValueKind.None,
    ///                 (host, subject, arg, out bool passed, out string code, out string why) =>
    ///                 {
    ///                     passed = MyIsIrradiated(RuleEvalHost.PawnOf(subject));
    ///                     code = passed ? null : "mymod.not_irradiated";
    ///                     why = passed ? null : "这个人没受辐射。";
    ///                     return true;
    ///                 })
    ///             .Quiet()                 // 不成立时安静让开，不算规则失败
    ///             .Label("受辐射")          // 不给就用 key，但玩家会看到英文 key
    ///             .Register();
    ///
    ///         // 操作：一个 Agentic 动作
    ///         RuleCoreApi.Operate("mymod.decontaminate", RuleEntityKind.Pawn,
    ///                 RuleValueKind.Entity, RuleEntityKind.Thing,
    ///                 (host, subject, arg, out string code, out string why) =>
    ///                 {
    ///                     var pawn = RuleEvalHost.PawnOf(subject);
    ///                     if (pawn == null) { code = "mymod.no_pawn"; why = "不是一个人。"; return RuleOperateStatus.Failed; }
    ///                     if (!MyCanDecontaminate(pawn)) { code = "mymod.not_needed"; why = "不需要净化。"; return RuleOperateStatus.AlreadySatisfied; }
    ///                     MyDecontaminate(pawn);
    ///                     code = "mymod.done"; why = "已净化。"; return RuleOperateStatus.Done;
    ///                 })
    ///             .Register();
    ///
    ///         // 主体绑定：让规则对"每个囚犯"各跑一遍
    ///         RuleCoreApi.Subject("mymod.prisoners", RuleEntityKind.Pawn,
    ///                 (host, into) => { /* 往 into 里塞 RuleValue.OfEntity(Pawn, pawn) */ })
    ///             .Register();
    ///     }
    /// }
    /// </code>
    ///
    /// <b>三条"保准"的保证</b>（这是本 API 存在的意义，而不是让你直接改静态表）：
    ///
    /// 1. **加载顺序无关**。登记的行先攒起来；表已经建过就顺手重建一次。
    ///    所以你的 mod 在 RuleCore 之前或之后加载都成立。
    /// 2. **坏行进不了表**。<see cref="Register"/> 返回 false 并写进时间线，而不是
    ///    悄悄塞一行让编辑器表现异常（"下拉里有个选项，选了必然报错"是最难查的一类问题）。
    /// 3. **不需要引用内部类型**。这个文件里的方法签名只用 <c>RulePropertyInfo</c> 这类
    ///    纯数据描述符和 <c>RuleValue</c>——它们都是 Core 层的公开类型，不随 UI 改动而变。
    ///
    /// <b>登记时机</b>：<c>[StaticConstructorOnStartup]</c> 是最好的位置——
    /// 它在 Def 加载完、引擎开始采样之前跑。太晚登记（比如玩家点了按钮才登记）也能进去，
    /// 但已经求值过的规则的派生属性（权限层级、边沿语义）要等下一次加载才重算。
    /// </summary>
    public static class RuleCoreApi
    {
        // ══ 便利构造：必填项做成参数，其余走默认 ══════════════════════════

        /// <summary>
        /// 登记一个**属性**（实体表达式路径上的一步读操作）。
        ///
        /// <paramref name="reader"/> 的约定：
        ///   · 返回 <c>false</c> 表示**读不到**，必须在 <c>reasonCode</c> 里给原因；
        ///   · 返回 <c>true</c> 时 <c>value</c> 才有效；
        ///   · **"读不到"和"读到了 0"必须分开**——混起来会让规则静默地一直成立。
        /// </summary>
        public static PropertyBuilder Property(string key, RuleEntityKind owner,
            RuleValueKind result, string unit = null, RulePropertyReadHandler reader = null)
        {
            var info = new RulePropertyInfo
            {
                key = key,
                owner = owner,
                result = result,
                unit = unit,
                reader = reader
            };

            return new PropertyBuilder(info);
        }

        /// <summary>登记一个**检测**（产出真/假的谓语）。</summary>
        public static VerbBuilder Detect(string key, RuleEntityKind subject,
            RuleValueKind subjectValueKind, RuleValueKind argKind, RuleDetectHandler handler,
            RuleEntityKind argEntity = RuleEntityKind.Any)
        {
            var info = new RuleVerbInfo
            {
                key = key,
                category = RuleVerbCategory.Detect,
                subject = subject,
                subjectValueKind = subjectValueKind,
                argKind = argKind,
                argEntity = argEntity,
                detect = handler
            };

            return new VerbBuilder(info);
        }

        /// <summary>登记一个**操作**（产出改变的谓语）。</summary>
        public static VerbBuilder Operate(string key, RuleEntityKind subject, RuleValueKind argKind,
            RuleOperateHandler handler, RuleEntityKind argEntity = RuleEntityKind.Any,
            RuleTier tier = RuleTier.Player)
        {
            var info = new RuleVerbInfo
            {
                key = key,
                category = RuleVerbCategory.Operate,
                subject = subject,
                argKind = argKind,
                argEntity = argEntity,
                tier = tier,
                operate = handler
            };

            return new VerbBuilder(info);
        }

        /// <summary>登记一种**主体绑定**（规则对谁各跑一遍）。</summary>
        public static SubjectBuilder Subject(string key, RuleEntityKind entityKind,
            RuleSubjectCollectHandler binder)
        {
            return new SubjectBuilder(new RuleSubjectInfo
            {
                key = key,
                entityKind = entityKind,
                binder = binder
            });
        }

        // ══ 只读查询：给别的 mod 看词表用 ══════════════════════════════

        public static bool TryGetProperty(string key, out RulePropertyInfo info)
        {
            info = RuleVocabularyCatalog.Current.Property(key);
            return info != null;
        }

        public static bool TryGetVerb(string key, out RuleVerbInfo info)
        {
            info = RuleVocabularyCatalog.Current.Verb(key);
            return info != null;
        }

        /// <summary>某个实体类型身上可用的属性（编辑器就是这么取列表的）。</summary>
        public static List<RulePropertyInfo> PropertiesFor(RuleEntityKind owner)
        {
            var list = new List<RulePropertyInfo>();
            RuleVocabularyCatalog.Current.CollectPropertiesFor(owner, list);
            return list;
        }

        /// <summary>某个类型 + 值类型的实体身上可用的谓语。</summary>
        public static List<RuleVerbInfo> VerbsFor(RuleEntityKind subject, RuleValueKind value,
            RuleVerbCategory category)
        {
            var list = new List<RuleVerbInfo>();
            RuleVocabularyCatalog.Current.CollectVerbsFor(subject, value, category, list);
            return list;
        }

        /// <summary>词表里有多少行来自别的 mod。</summary>
        public static int RegisteredFromOtherMods
        {
            get { return RuleVocabularyCatalog.StagedCount; }
        }

        // ══ 校验：坏行进不了表 ══════════════════════════════════════════

        /// <summary>
        /// 一行的自洽检查。**这是"保准"的核心**：与其让一行半成品进表、
        /// 让编辑器在下拉里摆出一个"选了必然报错"的选项，不如当场拒绝并说清原因。
        /// </summary>
        private static bool Accept(string kind, string key, out string problem)
        {
            problem = null;

            if (string.IsNullOrEmpty(key))
            {
                problem = kind + "的 key 是空的。key 是序列化进存档的东西，必须稳定且非空。";
                return false;
            }

            if (key.IndexOf(' ') >= 0)
            {
                problem = kind + "的 key「" + key + "」里有空格。建议用 <域>.<名字>，如 mymod.radiation。";
                return false;
            }

            return true;
        }

        private static void Report(string problem)
        {
            RuleLog.Error(null, "Api", RuleEvalStatus.Error, "api.rejected", null, problem);
        }

        // ══ 构造器（链式的目的只是让登记读起来像一句话）══════════════════

        public sealed class PropertyBuilder
        {
            private readonly RulePropertyInfo info;

            internal PropertyBuilder(RulePropertyInfo info)
            {
                this.info = info;
            }

            /// <summary>数值的范围（编辑器用它夹取输入，也用来判断该给多大步长）。</summary>
            public PropertyBuilder Range(float min, float max)
            {
                info.min = min;
                info.max = max;
                return this;
            }

            public PropertyBuilder Decimals(int digits)
            {
                info.decimals = digits;
                return this;
            }

            /// <summary>按百分比显示（值是 0~1，界面显示成 50%）。</summary>
            public PropertyBuilder Percent()
            {
                info.percent = true;
                info.min = 0f;
                info.max = 1f;
                return this;
            }

            /// <summary>
            /// 产出实体或集合时，元素是什么类型。
            /// **产出集合必须给元素类型**——不给的话路径没法继续往下走（读不出下一步能读什么）。
            /// </summary>
            public PropertyBuilder Entity(RuleEntityKind element)
            {
                info.resultEntity = element;
                return this;
            }

            /// <summary>产出枚举时，取值来自哪个 Def 类型（给了编辑器就出选择器）。</summary>
            public PropertyBuilder Domain(System.Type defType)
            {
                info.enumDefType = defType;
                return this;
            }

            /// <summary>
            /// 产出枚举、但取值**不来自 Def 表**时，直接给出清单（如"身份"：殖民者/囚犯/奴隶）。
            /// 与 <see cref="Domain"/> 二选一。
            /// </summary>
            public PropertyBuilder Options(params RuleEnumOption[] options)
            {
                info.enumOptions = options;
                return this;
            }

            /// <summary>
            /// 这个属性**只在具备这些能力的宿主身上**才出现。
            ///
            /// 这是"机械族身上不列饱食度"的开放版：不写的话，别的 mod 加的需求类属性
            /// 会出现在每一种小人身上，玩家选了必然读到失败。
            /// 典型的用法是 <c>.Needs(RuleCapability.NeedFood)</c>。
            /// </summary>
            public PropertyBuilder Needs(RuleCapability required)
            {
                info.requires |= required;
                return this;
            }

            public bool Register()
            {
                string problem;
                if (!Accept("属性", info.key, out problem))
                {
                    Report(problem);
                    return false;
                }

                if (info.reader == null)
                {
                    Report("属性「" + info.key + "」没有给 reader——登记了它也没人会读。");
                    return false;
                }

                if ((info.result == RuleValueKind.Entity || info.result == RuleValueKind.EntitySet)
                    && info.resultEntity == RuleEntityKind.Any)
                {
                    Report("属性「" + info.key + "」产出 " + info.result
                        + "，但没声明元素类型（Entity(...)）。"
                        + "没有元素类型，路径就不知道下一步能读什么，编辑器会在这里断掉。");
                    return false;
                }

                if (RuleVocabularyCatalog.Current.Property(info.key) != null)
                {
                    Report("属性「" + info.key + "」已经被登记过了（可能是 key 撞车）。");
                    return false;
                }

                RuleVocabularyCatalog.Stage(info);
                return true;
            }
        }

        public sealed class VerbBuilder
        {
            private readonly RuleVerbInfo info;

            internal VerbBuilder(RuleVerbInfo info)
            {
                this.info = info;
            }

            /// <summary>边沿语义（发生的那一刻为真），而不是持续为真。</summary>
            public VerbBuilder Edge()
            {
                info.edge = RuleEdge.Edge;
                return this;
            }

            /// <summary>不成立时**安静让开**，不算规则失败（"做不了"用这个）。</summary>
            public VerbBuilder Quiet()
            {
                info.failure = RuleFailureMode.QuietSkip;
                return this;
            }

            /// <summary>
            /// 用中文（或任意语言）的名字覆盖显示名。
            /// 不调它就显示 key——**能跑，但玩家看到的是英文标识符**，
            /// 所以正式发布前应该用 Keyed 语言文件而不是这个方法。
            /// </summary>
            public VerbBuilder Label(string label)
            {
                RuleCoreApi.Label(info.LabelKey, label);
                RuleCoreApi.Label(info.DescKey, null);
                return this;
            }

            /// <summary>工具提示。</summary>
            public VerbBuilder Describe(string text)
            {
                RuleCoreApi.Label(info.DescKey, text);
                return this;
            }

            /// <summary>这个谓词只对具备这些能力的主体成立（见 <see cref="RuleCapability"/>）。</summary>
            public VerbBuilder Needs(RuleCapability required)
            {
                info.requires |= required;
                return this;
            }

            /// <summary>宾语是枚举、取值不来自 Def 表时直接给清单。</summary>
            public VerbBuilder ArgOptions(params RuleEnumOption[] options)
            {
                info.argOptions = options;
                return this;
            }

            /// <summary>
            /// 宾语是枚举、取值来自某个 Def 表（如 <c>IncidentDef</c>）。
            /// 有它编辑器才给得出**带搜索的选择器**；没有就只能让玩家手填 defName。
            /// </summary>
            public VerbBuilder Domain(System.Type defType)
            {
                info.argDefType = defType;
                return this;
            }

            public bool Register()
            {
                string problem;
                if (!Accept("谓语", info.key, out problem))
                {
                    Report(problem);
                    return false;
                }

                if (info.category == RuleVerbCategory.Detect && info.detect == null)
                {
                    Report("检测「" + info.key + "」没有给 handler。");
                    return false;
                }

                if (info.category == RuleVerbCategory.Operate && info.operate == null)
                {
                    Report("操作「" + info.key + "」没有给 handler。");
                    return false;
                }

                if (info.argKind == RuleValueKind.Entity && info.argEntity == RuleEntityKind.Any)
                {
                    // 允许，但值得提醒：宾语实体没类型等于"什么都接受"，
                    // 编辑器会从所有属性里挑，多半不是作者的意图。
                    RuleLog.Warn(null, "Api", RuleEvalStatus.Skipped, "api.loose_arg", null,
                        "谓语「" + info.key + "」的宾语是实体但没声明类型，"
                        + "编辑器会列出所有实体属性——通常不是想要的。");
                }

                if (RuleVocabularyCatalog.Current.Verb(info.key) != null)
                {
                    Report("谓语「" + info.key + "」已经被登记过了（可能是 key 撞车）。");
                    return false;
                }

                RuleVocabularyCatalog.Stage(info);
                return true;
            }
        }

        public sealed class SubjectBuilder
        {
            private readonly RuleSubjectInfo info;

            internal SubjectBuilder(RuleSubjectInfo info)
            {
                this.info = info;
            }

            /// <summary>
            /// 这个绑定**指名到一个具体实体**（整条规则只跑一遍），而不是"对每个成员各跑一遍"。
            ///
            /// 用了它就必须再给 <see cref="Candidates"/>：玩家要在界面上从一份清单里挑人，
            /// 而不是去手打一个内部 id —— 手打内部 id 是绝不会被接受的交互。
            /// </summary>
            public SubjectBuilder Single()
            {
                info.scope = RuleSubjectScope.Single;
                return this;
            }

            /// <summary>指名绑定的候选清单（只有 <see cref="Single"/> 需要）。</summary>
            public SubjectBuilder Candidates(RuleSubjectCollectHandler handler)
            {
                info.candidates = handler;
                return this;
            }

            public bool Register()
            {
                string problem;
                if (!Accept("主体绑定", info.key, out problem))
                {
                    Report(problem);
                    return false;
                }

                if (info.binder == null)
                {
                    Report("主体绑定「" + info.key + "」没有给 binder——登记了它一个主体都收不出来。");
                    return false;
                }

                if (info.entityKind == RuleEntityKind.Any)
                {
                    Report("主体绑定「" + info.key + "」没声明产出什么类型的实体。"
                        + "没有它，编辑器不知道这个主体身上能用哪些谓词。");
                    return false;
                }

                if (info.scope == RuleSubjectScope.Single && info.candidates == null)
                {
                    // 允许但不推荐：没有候选清单，编辑器只能让玩家手填引用。
                    // 这条不是错误，但**几乎一定是作者忘了**，所以必须说出来。
                    RuleLog.Warn(null, "Api", RuleEvalStatus.Skipped, "api.no_candidates", null,
                        "主体绑定「" + info.key + "」是指名绑定但没给 Candidates——"
                        + "编辑器只能让玩家手填实体引用，请补上候选清单。");
                }

                RuleVocabularyCatalog.Stage(info);
                return true;
            }
        }

        // ══ 显示名覆盖（给不想写语言文件的 mod 兜底）══════════════════════

        private static readonly Dictionary<string, string> labelOverrides =
            new Dictionary<string, string>();

        /// <summary>
        /// 覆盖一个显示名。**这是给"不想打包语言文件"的 mod 兜底的**，
        /// 正经做法是在你的 mod 里放 <c>Languages/&lt;语言&gt;/Keyed/&lt;你的包名&gt;.xml</c>
        /// 并写上同样的键——那才是能翻译的形态。
        /// </summary>
        public static void Label(string key, string text)
        {
            if (string.IsNullOrEmpty(key) || text == null) return;
            labelOverrides[key] = text;
        }

        /// <summary>查覆盖过的显示名（没有就返回 null）。词表与编辑器会先查这里。</summary>
        public static string Override(string key)
        {
            string text;
            return !string.IsNullOrEmpty(key) && labelOverrides.TryGetValue(key, out text)
                ? text
                : null;
        }
    }
}
