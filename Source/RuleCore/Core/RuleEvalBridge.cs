using System;
using System.Collections.Generic;

namespace RuleCore.Core
{
    // ── Core 与 Verse 的接缝 ──────────────────────────────────────────
    //
    // 这一组接口是**路径语义能脱离游戏单测**的原因：
    // Core 负责"怎么走一条路径、什么时候算类型不对、集合没归约该报什么"，全是纯逻辑；
    // Verse 只负责"读一个属性"和"跑一个谓词"，全是游戏知识。
    //
    // 分工线划在"值"上：Core 在整条链路上只传递 <see cref="RuleValue"/>，
    // 从不接触 Pawn / Map / Thing。Verse 侧的实现在内部把它们来回翻译。

    /// <summary>
    /// 路径与谓词求值时的宿主。**实现在 Verse 侧，且持有当前求值上下文**
    /// （所以接口上不需要出现 RuleEvalContext，Core 也不必知道它存在）。
    /// </summary>
    public interface IRuleEvalHost
    {
        /// <summary>
        /// 当前被绑定的本主体。<see cref="RuleRootKind.Subject"/> 读它。
        /// 由引擎在每次求值前设置；筛选器不动它（筛选器用 <see cref="RuleRootKind.Element"/>）。
        /// </summary>
        RuleValue Subject { get; }

        /// <summary>筛选器正在遍历的那个元素。<see cref="RuleRootKind.Element"/> 读它。</summary>
        RuleValue Element { get; set; }

        /// <summary>
        /// 主体绑定的**参数** —— 指名绑定时就是那个实体的引用（<c>Rule.subjectRef</c>）。
        ///
        /// 绑定器读它来回答"到底指的是哪一个"。没有它的后果是绑定器只能收出整组人，
        /// 而「本主体 = 指定的某个人」这件事就无从表达。
        /// 组绑定不读它（传进来是什么都无所谓）。
        /// </summary>
        string SubjectRef { get; }

        /// <summary>
        /// 本次求值所在的地图实体。
        /// 主体绑定器要用它来枚举人——"地图上的自由殖民者"只能从地图上问。
        /// </summary>
        RuleValue Map { get; }

        /// <summary>解析一个根。失败必须给原因码——"读不到"和"读到了但是 0"是两件事。</summary>
        bool TryRoot(RuleRootKind kind, RuleValue literal, out RuleValue value,
            out string reasonCode, out string reason);


        /// <summary>
        /// 只有计算依赖游戏世界的归约才走这里（目前是 `最近`）。
        /// `第一个` 与 `数量` 是纯逻辑，Core 自己算——能在 Core 算的就不该下放，
        /// 否则脱离游戏的单测就覆盖不到它。
        /// </summary>
        bool TryReduce(RuleValue set, RuleReduceKind kind, out RuleValue value,
            out string reasonCode, out string reason);
    }

    /// <summary>
    /// 检测谓词的行为本体。返回 false 表示"读不到"，true 时 <c>passed</c> 才是结论。
    ///
    /// 用**委托**而不是接口：词表里每个谓词都是一行，为一行写一个只有一个方法的类，
    /// 收益只有"看起来像 OO"。委托让 Registration 那一段读起来就是一张表。
    /// </summary>
    public delegate bool RuleDetectHandler(IRuleEvalHost host, RuleValue subject, RuleValue arg,
        out bool passed, out string reasonCode, out string reason);



    // ── 数据视图（Verse 侧的 IExposable 类实现它们）──────────────────
    //
    // 为什么不直接让 Core 用那些类：它们必须实现 Verse 的 IExposable 才能被 Scribe 存，
    // 而 Core 引用不了 Verse。所以 Core 定义"我需要看到什么"，Verse 的类实现它。
    // 这不是两套模型——是被迫的适配层，而且它只有只读访问器，没有第二份状态。

    public interface IRulePathSource
    {
        RuleRootKind RootKind { get; }

        /// <summary>RootKind == Literal 时的值。</summary>
        RuleValue RootLiteral { get; }

        int StepCount { get; }

        RuleStepKind StepKindAt(int index);
        string PropertyKeyAt(int index);
        IRuleExprSource FilterAt(int index);
        RuleReduceKind ReduceAt(int index);
        RuleQuantifier QuantifyAt(int index);
    }

    public interface IRuleOperandSource
    {
        /// <summary>这个宾语打算是什么类型。None 表示"没填"。</summary>
        RuleValueKind Kind { get; }

        /// <summary>字面量（数值 / 布尔 / 枚举 / 坐标 / 文本）。</summary>
        RuleValue Literal { get; }

        /// <summary>实体引用。Kind 为 Entity 时非空。</summary>
        IRulePathSource Path { get; }
    }

    public interface IRuleExprSource
    {
        RuleExprNodeKind NodeKind { get; }
        int ChildCount { get; }
        IRuleExprSource ChildAt(int index);

        /// <summary>叶子（Detect）用：主语路径。</summary>
        IRulePathSource Subject { get; }

        /// <summary>叶子（Detect）用：谓词键。</summary>
        string VerbKey { get; }

        /// <summary>叶子（Detect）用：宾语。可为空。</summary>
        IRuleOperandSource Argument { get; }
    }

    // ── 求值结果 ──────────────────────────────────────────────────────

    /// <summary>
    /// 一次检测树求值的结果。
    ///
    /// 带上 <see cref="mode"/> 是因为"条件"与"能力"合并之后，
    /// "算规则失败"和"安静让开"的区别只能由**失败的那个叶子**决定，
    /// 所以它必须一路传上来，不能在中间被抹平成布尔值。
    /// </summary>
    public struct RuleExprOutcome
    {
        public bool passed;

        /// <summary>不通过时，出错的那个叶子的语义。</summary>
        public RuleFailureMode mode;

        public string reasonCode;
        public string reason;

        /// <summary>
        /// 出错叶子的路径，如 <c>and[1].or[0]</c>。
        /// 时间线上据此能一眼看出是**哪一支**挡住了——
        /// 嵌套之后"规则没触发"这句话本身已经不含信息了。
        /// </summary>
        public string tracePath;

        public static RuleExprOutcome Pass()
        {
            RuleExprOutcome outcome;
            outcome.passed = true;
            outcome.mode = RuleFailureMode.RuleFailed;
            outcome.reasonCode = null;
            outcome.reason = null;
            outcome.tracePath = null;
            return outcome;
        }

        public static RuleExprOutcome Fail(RuleFailureMode mode, string code, string reason,
            string tracePath)
        {
            RuleExprOutcome outcome;
            outcome.passed = false;
            outcome.mode = mode;
            outcome.reasonCode = code;
            outcome.reason = reason;
            outcome.tracePath = tracePath;
            return outcome;
        }
    }

    /// <summary>位置 + 原因。路径求值用。</summary>
    public struct RulePathOutcome
    {
        public bool ok;
        public RuleValue value;
        public string reasonCode;
        public string reason;

        public static RulePathOutcome Ok(RuleValue value)
        {
            RulePathOutcome outcome;
            outcome.ok = true;
            outcome.value = value;
            outcome.reasonCode = null;
            outcome.reason = null;
            return outcome;
        }

        public static RulePathOutcome Fail(string code, string reason)
        {
            RulePathOutcome outcome;
            outcome.ok = false;
            outcome.value = RuleValue.None;
            outcome.reasonCode = code;
            outcome.reason = reason;
            return outcome;
        }
    }

    // ── 路径求值 ──────────────────────────────────────────────────────

    /// <summary>
    /// 实体表达式路径的求值器。**规则语言里最容易出错的一段，所以它是纯的。**
    ///
    /// 三类错误必须分得清清楚楚，因为它们对应的玩家错误完全不同：
    ///   · <c>path.needs_reduce</c>  —— 手上是个集合就往下读属性（少写了一个归约）
    ///   · <c>path.filter_on_scalar</c> —— 对单个东西写筛选
    ///   · <c>path.property_wrong_type</c> —— 把"耐久"挂在了一张地图上
    /// 混成一个"路径无效"，玩家就只能猜。
    /// </summary>
    public static class RulePathEval
    {
        /// <summary>嵌套筛选的深度上限。防止手改 XML 造出一个自引用死循环。</summary>
        public const int MaxFilterDepth = 6;

        public static RulePathOutcome Evaluate(IRulePathSource path, IRuleEvalHost host,
            RuleVocabulary vocabulary)
        {
            return EvaluateWithDepth(path, host, vocabulary, 0);
        }

        /// <summary>带筛选深度的版本。跨类可调用——筛选器与检测树会互相递归。</summary>
        public static RulePathOutcome EvaluateWithDepth(IRulePathSource path, IRuleEvalHost host,
            RuleVocabulary vocabulary, int filterDepth)
        {
            if (path == null)
            {
                return RulePathOutcome.Fail("path.missing", "没有配置实体。");
            }

            if (filterDepth > MaxFilterDepth)
            {
                return RulePathOutcome.Fail("path.too_deep", "筛选嵌套太深（可能有自引用）。");
            }

            RuleValue value;
            string code;
            string reason;
            if (!host.TryRoot(path.RootKind, path.RootLiteral, out value, out code, out reason))
            {
                return RulePathOutcome.Fail(code, reason);
            }

            for (int i = 0; i < path.StepCount; i++)
            {
                switch (path.StepKindAt(i))
                {
                    case RuleStepKind.Property:
                    {
                        if (value.IsSet)
                        {
                            return RulePathOutcome.Fail("path.needs_reduce",
                                "手上还是一组东西（" + value + "），先选一个或数个数，再往下读属性。");
                        }

                        string key = path.PropertyKeyAt(i);
                        var info = vocabulary != null ? vocabulary.Property(key) : null;
                        if (info == null)
                        {
                            return RulePathOutcome.Fail("path.unknown_property",
                                "词表里没有这个属性：" + (key ?? "(空)"));
                        }

                        if (!RuleVocabulary.EntityKindMatches(info.owner, value.entityKind))
                        {
                            return RulePathOutcome.Fail("path.property_wrong_type",
                                "「" + key + "」不能挂在 " + value.entityKind + " 上。");
                        }

                        // 属性读取**由词表里的 reader 直接做**，不经过宿主——
                        // 和谓词一样，"行为挂在描述对象上"就不会出现
                        // "登记了属性忘了实现"这种只在跑起来才发现的漏洞。
                        if (info.reader == null)
                        {
                            return RulePathOutcome.Fail("path.no_reader",
                                "「" + key + "」登记了但没有实现。");
                        }

                        RuleValue read;
                        if (!info.reader(host, value, out read, out code, out reason))
                        {
                            return RulePathOutcome.Fail(code, reason);
                        }

                        // 词表说产出什么，实现就得产出什么。不一致是**注册表写错了**，
                        // 属于编程错误——但也不能让玩家崩，报出来让人看得见。
                        if (read.kind != info.result)
                        {
                            return RulePathOutcome.Fail("path.property_type_mismatch",
                                "「" + key + "」声明产出 " + info.result + "，实际产出 " + read.kind
                                + "——词表登记错了。");
                        }

                        value = read;
                        break;
                    }

                    case RuleStepKind.Filter:
                    {
                        if (!value.IsSet)
                        {
                            return RulePathOutcome.Fail("path.filter_on_scalar",
                                "「筛选」只能用在成组的东西上，当前的 " + value + " 是单个的。");
                        }

                        var predicate = path.FilterAt(i);
                        if (predicate == null)
                        {
                            return RulePathOutcome.Fail("path.empty_filter", "筛选条件没有配置。");
                        }

                        var kept = new List<RuleValue>();
                        var previousElement = host.Element;

                        for (int k = 0; k < value.Count; k++)
                        {
                            var element = value.AsItems[k];
                            host.Element = element;

                            var inner = RuleExprEval.EvaluateWithDepth(predicate, host, vocabulary, filterDepth + 1);
                            if (inner.passed)
                            {
                                kept.Add(element);
                            }
                        }

                        host.Element = previousElement;

                        value = RuleValue.OfSet(value.entityKind, kept);
                        break;
                    }

                    case RuleStepKind.Reduce:
                    {
                        if (!value.IsSet)
                        {
                            return RulePathOutcome.Fail("path.reduce_on_scalar",
                                "「归约」只能用在成组的东西上，当前的 " + value + " 是单个的。");
                        }

                        RuleValue reduced;
                        if (!Reduce(value, path.ReduceAt(i), host, out reduced, out code, out reason))
                        {
                            return RulePathOutcome.Fail(code, reason);
                        }

                        value = reduced;
                        break;
                    }

                    case RuleStepKind.Quantify:
                    {
                        if (!value.IsSet)
                        {
                            return RulePathOutcome.Fail("path.quantify_on_scalar",
                                "「全都满足 / 有一个满足 / 一个都不满足」只能用在成组的东西上，当前的 "
                                + value + " 是单个的。");
                        }

                        var condition = path.FilterAt(i);
                        if (condition == null)
                        {
                            return RulePathOutcome.Fail("path.empty_quantifier",
                                "这一句量词还没有条件。");
                        }

                        var quantifier = path.QuantifyAt(i);
                        int matched = 0;
                        var restoreElement = host.Element;

                        for (int k = 0; k < value.Count; k++)
                        {
                            host.Element = value.AsItems[k];

                            var inner = RuleExprEval.EvaluateWithDepth(condition, host, vocabulary, filterDepth + 1);
                            if (inner.passed)
                            {
                                matched++;
                            }
                        }

                        host.Element = restoreElement;

                        bool held;
                        switch (quantifier)
                        {
                            case RuleQuantifier.All:
                                // 空集合上成立（空真）。理由写在 RuleQuantifier 的注释里。
                                held = matched == value.Count;
                                break;
                            case RuleQuantifier.Any:
                                held = matched > 0;
                                break;
                            default:
                                held = matched == 0;
                                break;
                        }

                        value = RuleValue.OfBool(held);
                        break;
                    }

                    default:
                        return RulePathOutcome.Fail("path.unknown_step", "路径上有认不出的步骤。");
                }
            }

            return RulePathOutcome.Ok(value);
        }

        /// <summary>
        /// 归约。`第一个` 与 `数量` 在 Core 里算（纯逻辑、可单测），
        /// 其余下放给宿主——它们的答案依赖游戏世界。
        /// </summary>
        public static bool Reduce(RuleValue set, RuleReduceKind kind, IRuleEvalHost host,
            out RuleValue value, out string reasonCode, out string reason)
        {
            value = RuleValue.None;

            switch (kind)
            {
                case RuleReduceKind.Count:
                    value = RuleValue.OfNumber(set.Count);
                    reasonCode = null;
                    reason = null;
                    return true;

                case RuleReduceKind.First:
                    if (set.Count == 0)
                    {
                        // 空集合是**读不到**，不是"读到了 0"。混起来会让
                        // "一件帽子都没有"被当成"耐久的第一个值等于 0"。
                        reasonCode = "path.empty_set";
                        reason = "这一组东西是空的——一件都没有。";
                        return false;
                    }

                    value = set.AsItems[0];
                    reasonCode = null;
                    reason = null;
                    return true;

                default:
                    if (host.TryReduce(set, kind, out value, out reasonCode, out reason))
                    {
                        return true;
                    }

                    if (string.IsNullOrEmpty(reasonCode))
                    {
                        reasonCode = "path.reduce_unsupported";
                        reason = "这个归约还没实现：" + kind;
                    }
                    return false;
            }
        }
    }

    // ── 检测树求值 ────────────────────────────────────────────────────

    /// <summary>
    /// 布尔检测树。短路顺序是设计的一部分，不是优化：
    /// `且` 报**第一个**失败的，`或` 报**第一个**失败的分支——
    /// 因为排障时想看到的是"最左边那句为什么没成立"，而不是一堆原因。
    /// </summary>
    public static class RuleExprEval
    {
        /// <summary>
        /// 表达式树的深度上限。
        ///
        /// 与 <see cref="RulePathEval.MaxFilterDepth"/> 是两个不同的东西：
        /// 那个管"筛选套筛选"，这个管"且套或套非"。
        /// 32 层远超任何人手写得出的规则，它存在的唯一目的是**别让手改的 XML 把栈打爆**——
        /// 载入期校验会先报出来，但校验只是记日志，规则照样会进引擎。
        /// </summary>
        public const int MaxNodeDepth = 32;

        public static RuleExprOutcome Evaluate(IRuleExprSource expr, IRuleEvalHost host,
            RuleVocabulary vocabulary)
        {
            return EvaluateWithDepth(expr, host, vocabulary, 0);
        }

        /// <summary>带筛选深度的版本。跨类可调用——检测树与路径筛选会互相递归。</summary>
        public static RuleExprOutcome EvaluateWithDepth(IRuleExprSource expr, IRuleEvalHost host,
            RuleVocabulary vocabulary, int filterDepth)
        {
            return EvaluateNode(expr, host, vocabulary, filterDepth, 0);
        }

        private static RuleExprOutcome EvaluateNode(IRuleExprSource expr, IRuleEvalHost host,
            RuleVocabulary vocabulary, int filterDepth, int nodeDepth)
        {
            if (nodeDepth > MaxNodeDepth)
            {
                return RuleExprOutcome.Fail(RuleFailureMode.RuleFailed, "detect.too_deep",
                    "检测树嵌套超过 " + MaxNodeDepth + " 层——多半是手改 XML 写坏了。", null);
            }
            if (expr == null)
            {
                return RuleExprOutcome.Fail(RuleFailureMode.RuleFailed, "detect.missing",
                    "这一支检测是空的。", null);
            }

            switch (expr.NodeKind)
            {
                case RuleExprNodeKind.And:
                {
                    for (int i = 0; i < expr.ChildCount; i++)
                    {
                        var child = EvaluateNode(expr.ChildAt(i), host, vocabulary, filterDepth, nodeDepth + 1);
                        if (!child.passed)
                        {
                            return RuleExprOutcome.Fail(child.mode, child.reasonCode, child.reason,
                                Join("and", i, child.tracePath));
                        }
                    }
                    return RuleExprOutcome.Pass();
                }

                case RuleExprNodeKind.Or:
                {
                    RuleExprOutcome firstFailure = RuleExprOutcome.Pass();
                    int firstFailureIndex = -1;
                    bool anyFailed = false;

                    RuleExprOutcome readFailure = RuleExprOutcome.Pass();
                    int readFailureIndex = -1;
                    bool anyReadFailed = false;

                    for (int i = 0; i < expr.ChildCount; i++)
                    {
                        var child = EvaluateNode(expr.ChildAt(i), host, vocabulary, filterDepth, nodeDepth + 1);
                        if (child.passed)
                        {
                            return RuleExprOutcome.Pass();
                        }

                        // 区分"读了但不成立"和"根本读不到"：
                        // 只要有一支**读不到**，就很可能不是"条件不满足"而是"环境不对"，
                        // 那个原因比"第一支不成立"更值得报出来。
                        bool readFailed = child.reasonCode != null
                            && child.reasonCode.StartsWith("detect.read", StringComparison.Ordinal);

                        if (readFailed && !anyReadFailed)
                        {
                            anyReadFailed = true;
                            readFailure = child;
                            readFailureIndex = i;
                        }

                        if (!anyFailed)
                        {
                            anyFailed = true;
                            firstFailure = child;
                            firstFailureIndex = i;
                        }
                    }

                    if (expr.ChildCount == 0)
                    {
                        return RuleExprOutcome.Fail(RuleFailureMode.RuleFailed, "or.empty",
                            "「或」里面一句检测都没有。", null);
                    }

                    var reported = anyReadFailed ? readFailure : firstFailure;
                    int index = anyReadFailed ? readFailureIndex : firstFailureIndex;
                    return RuleExprOutcome.Fail(reported.mode, reported.reasonCode, reported.reason,
                        Join("or", index, reported.tracePath));
                }

                case RuleExprNodeKind.Not:
                {
                    if (expr.ChildCount != 1)
                    {
                        return RuleExprOutcome.Fail(RuleFailureMode.RuleFailed, "not.arity",
                            "「非」只能作用于一句检测。", null);
                    }

                    var child = EvaluateNode(expr.ChildAt(0), host, vocabulary, filterDepth, nodeDepth + 1);
                    if (child.passed)
                    {
                        return RuleExprOutcome.Fail(RuleFailureMode.RuleFailed, "not.false",
                            "「非」里面的那句成立了，所以整句不成立。", Join("not", -1, child.tracePath));
                    }

                    return RuleExprOutcome.Pass();
                }

                default:
                    return EvaluateDetect(expr, host, vocabulary, filterDepth);
            }
        }

        private static RuleExprOutcome EvaluateDetect(IRuleExprSource leaf, IRuleEvalHost host,
            RuleVocabulary vocabulary, int filterDepth)
        {
            var verb = vocabulary != null ? vocabulary.Verb(leaf.VerbKey) : null;
            if (verb == null)
            {
                return RuleExprOutcome.Fail(RuleFailureMode.RuleFailed, "detect.unknown_verb",
                    "词表里没有这个检测：" + (leaf.VerbKey ?? "(空)"), null);
            }

            if (verb.category != RuleVerbCategory.Detect)
            {
                return RuleExprOutcome.Fail(RuleFailureMode.RuleFailed, "detect.not_a_detect",
                    "「" + verb.key + "」是操作，不能当检测用。", null);
            }

            var subjectOutcome = RulePathEval.EvaluateWithDepth(leaf.Subject, host, vocabulary, filterDepth);
            if (!subjectOutcome.ok)
            {
                return RuleExprOutcome.Fail(verb.failure, "detect.read." + subjectOutcome.reasonCode,
                    subjectOutcome.reason, "subject");
            }

            var subject = subjectOutcome.value;
            if (subject.IsSet)
            {
                return RuleExprOutcome.Fail(RuleFailureMode.RuleFailed, "detect.needs_reduce",
                    "主语还是一组东西，先选一个或数个数。", "subject");
            }

            if (!RuleVocabulary.EntityKindMatches(verb.subject, subject.entityKind))
            {
                return RuleExprOutcome.Fail(RuleFailureMode.RuleFailed, "detect.wrong_subject",
                    "「" + verb.key + "」不能用在 " + subject.entityKind + " 上。", "subject");
            }

            // 值类型也要对：`大于` 只吃数值。手改 XML 能绕过编辑器的过滤，
            // 所以这里是最后一道——而它给出的原因码与编辑器灰显的依据是同一条声明。
            if (verb.subjectValueKind != RuleValueKind.None
                && verb.subjectValueKind != subject.kind)
            {
                return RuleExprOutcome.Fail(RuleFailureMode.RuleFailed, "detect.wrong_value_kind",
                    "「" + verb.key + "」要 " + verb.subjectValueKind
                    + "，但主语产出的是 " + subject.kind + "。", "subject");
            }

            var arg = ResolveOperand(leaf.Argument, host, vocabulary, filterDepth);
            if (!arg.ok)
            {
                return RuleExprOutcome.Fail(verb.failure, "detect.read." + arg.reasonCode,
                    arg.reason, "arg");
            }

            if (!RuleVocabulary.ArgMatches(verb, arg.value))
            {
                return RuleExprOutcome.Fail(RuleFailureMode.RuleFailed, "detect.wrong_arg",
                    "「" + verb.key + "」的宾语类型不对：要 " + verb.argKind
                    + "，给的是 " + arg.value.kind + "。", "arg");
            }

            if (verb.detect == null)
            {
                return RuleExprOutcome.Fail(RuleFailureMode.RuleFailed, "detect.no_runner",
                    "「" + verb.key + "」登记了但没有实现。", null);
            }

            bool passed;
            string code;
            string reason;
            if (!verb.detect(host, subject, arg.value, out passed, out code, out reason))
            {
                return RuleExprOutcome.Fail(verb.failure, "detect.read." + (code ?? "failed"),
                    reason, null);
            }

            if (passed)
            {
                return RuleExprOutcome.Pass();
            }

            return RuleExprOutcome.Fail(verb.failure, code ?? "detect.false", reason, null);
        }

        /// <summary>把宾语落地成一个值。实体引用要走一遍路径求值。</summary>
        public static RulePathOutcome ResolveOperand(IRuleOperandSource operand, IRuleEvalHost host,
            RuleVocabulary vocabulary, int filterDepth)
        {
            if (operand == null || operand.Kind == RuleValueKind.None)
            {
                // 「没填」和「填错了」必须分开：前者是"还没配"，
                // 编辑器会显示成空槽；后者才是类型错误。
                return RulePathOutcome.Ok(RuleValue.None);
            }

            if (operand.Kind == RuleValueKind.Entity)
            {
                return RulePathEval.EvaluateWithDepth(operand.Path, host, vocabulary, filterDepth);
            }

            return RulePathOutcome.Ok(operand.Literal);
        }

        private static string Join(string node, int index, string child)
        {
            string head = index >= 0 ? node + "[" + index + "]" : node;
            return string.IsNullOrEmpty(child) ? head : head + "." + child;
        }
    }
}
