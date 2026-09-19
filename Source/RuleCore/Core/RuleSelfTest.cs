using System;
using System.Collections.Generic;

namespace RuleCore.Core
{
    /// <summary>一项自检的结果。</summary>
    public struct RuleSelfTestResult
    {
        public string Name;
        public bool Passed;
        public string Detail;

        public RuleSelfTestResult(string name, bool passed, string detail)
        {
            Name = name;
            Passed = passed;
            Detail = detail;
        }
    }

    /// <summary>
    /// 引擎自证 —— 面板上的「自检」按钮跑的就是它。
    ///
    /// 它和 <c>Tests/RuleCore.CoreTests</c> 的分工：那边跑得更深、覆盖更广，
    /// 但**要退出游戏、敲命令行**；这里跑的是**在玩家机器上也成立**的那部分不变式，
    /// 而且它随包发布——所以它同时验证了"这个 DLL 装对了"。
    ///
    /// 只测**纯逻辑**：比较符、值的种类、词表的类型匹配、实体表达式路径的三类错误、检测树的短路。
    /// 每一条都必须有一个只靠 Core 就能造的现场（一张手搭的小词表 + 一个假宿主），
    /// 因为这里跑的时候游戏可能正在加载，任何依赖真实世界状态的检查都会不稳定。
    /// </summary>
    public static class RuleCoreSelfTest
    {
        public static int CountPassed(List<RuleSelfTestResult> results)
        {
            if (results == null) return 0;

            int passed = 0;
            for (int i = 0; i < results.Count; i++)
            {
                if (results[i].Passed) passed++;
            }
            return passed;
        }

        public static List<RuleSelfTestResult> Run()
        {
            var results = new List<RuleSelfTestResult>();

            Compare(results);
            Values(results);
            VocabularyTests(results);
            Paths(results);
            Exprs(results);

            return results;
        }

        private static void Check(List<RuleSelfTestResult> into, string name, bool ok, string detail)
        {
            into.Add(new RuleSelfTestResult(name, ok, detail));
        }

        // ── 比较符 ────────────────────────────────────────────────────

        private static void Compare(List<RuleSelfTestResult> into)
        {
            Check(into, "比较：边界含不含端点",
                !RuleCompare.Apply(RuleOperator.Greater, 10f, 10f)
                && RuleCompare.Apply(RuleOperator.AtLeast, 10f, 10f)
                && !RuleCompare.Apply(RuleOperator.Less, 10f, 10f)
                && RuleCompare.Apply(RuleOperator.AtMost, 10f, 10f),
                "> 与 < 不含端点；>= 与 <= 含");

            Check(into, "比较：判等带浮点容差",
                RuleCompare.Apply(RuleOperator.Equal, 20.00001f, 20f)
                && !RuleCompare.Apply(RuleOperator.Equal, 20.1f, 20f),
                "温度被判等时读数几乎永远不是整齐的 20.0");

            Check(into, "比较：认不出的比较符判 false 而不抛异常",
                !RuleCompare.Apply((RuleOperator)99, 1f, 0f),
                "手改 XML 写坏枚举值不该让整个 tick 停摆");
        }

        // ── 值 ────────────────────────────────────────────────────────

        private static void Values(List<RuleSelfTestResult> into)
        {
            var number = RuleValue.OfNumber(3.5f);
            Check(into, "值：类型不对时访问器返回默认值而不是抛异常",
                number.AsKey == null && Math.Abs(number.AsNumber - 3.5f) < 0.0001f,
                "规则语言里类型对不上是玩家会犯的错，不能变成崩溃");

            var set = RuleValue.OfSet(RuleEntityKind.Thing, new List<RuleValue>
            {
                RuleValue.OfEntity(RuleEntityKind.Thing, "a"),
                RuleValue.OfEntity(RuleEntityKind.Thing, "b")
            });
            Check(into, "值：集合带元素类型与个数",
                set.IsSet && set.Count == 2 && set.entityKind == RuleEntityKind.Thing,
                "count=" + set.Count);

            Check(into, "值：文本与枚举分开（文本永不参与比较）",
                RuleValue.OfText("你好").IsText && !RuleValue.OfText("你好").IsNumber
                && RuleValue.OfKey("Rain").AsKey == "Rain",
                "写日志的文本不该被拿去比大小");
        }

        // ── 词表类型匹配 ──────────────────────────────────────────────

        private static void VocabularyTests(List<RuleSelfTestResult> into)
        {
            Check(into, "词表：Any 匹配一切，具体类型只匹配同类",
                RuleVocabulary.EntityKindMatches(RuleEntityKind.Any, RuleEntityKind.Pawn)
                && RuleVocabulary.EntityKindMatches(RuleEntityKind.Pawn, RuleEntityKind.Pawn)
                && !RuleVocabulary.EntityKindMatches(RuleEntityKind.Map, RuleEntityKind.Pawn),
                "Map 与 Pawn 互不匹配");

            Check(into, "词表：实到类型未知时不被具体要求认下",
                !RuleVocabulary.EntityKindMatches(RuleEntityKind.Pawn, RuleEntityKind.Any),
                "否则把数字喂给只作用于小人的谓词也能过");

            var verb = new RuleVerbInfo { key = "t", argKind = RuleValueKind.Number };
            Check(into, "词表：空宾语算「还没填」而不是「类型错」",
                RuleVocabulary.ArgMatches(verb, RuleValue.None)
                && RuleVocabulary.ArgMatches(verb, RuleValue.OfNumber(1f))
                && !RuleVocabulary.ArgMatches(verb, RuleValue.OfKey("Rain")),
                "「还没填」与「填错了」必须分得开");

            Check(into, "词表：重复键被记录而不是覆盖", DuplicateKeyRecorded(),
                "重复注册会让玩家在下拉里看到两个一样的项");
        }

        private static bool DuplicateKeyRecorded()
        {
            var vocabulary = new RuleVocabulary();
            vocabulary.Add(new RulePropertyInfo { key = "a.b" });
            vocabulary.Add(new RulePropertyInfo { key = "a.b" });
            return vocabulary.PropertyCount == 1 && vocabulary.DuplicateKeys.Count == 1;
        }

        // ── 实体表达式路径 ────────────────────────────────────────────

        private static void Paths(List<RuleSelfTestResult> into)
        {
            var vocabulary = BuildPathVocabulary();
            var host = new TinyHost(RuleValue.OfEntity(RuleEntityKind.Pawn, "p"));

            var single = new TinyPath(RuleRootKind.Subject).Step("p.health");
            var outcome = RulePathEval.Evaluate(single, host, vocabulary);
            Check(into, "路径：读一个属性",
                outcome.ok && Math.Abs(outcome.value.AsNumber - 0.4f) < 0.0001f,
                outcome.ok ? outcome.value.ToString() : outcome.reasonCode);

            // 少写一个归约是玩家最常犯的错，报错必须点名。
            var needsReduce = new TinyPath(RuleRootKind.Subject).Step("p.apparel").Step("p.health");
            outcome = RulePathEval.Evaluate(needsReduce, host, vocabulary);
            Check(into, "路径：集合没归约就往下读属性 → needs_reduce",
                !outcome.ok && outcome.reasonCode == "path.needs_reduce",
                outcome.reasonCode);

            var unknown = new TinyPath(RuleRootKind.Subject).Step("p.不存在");
            outcome = RulePathEval.Evaluate(unknown, host, vocabulary);
            Check(into, "路径：词表里没有的属性 → unknown_property",
                !outcome.ok && outcome.reasonCode == "path.unknown_property",
                outcome.reasonCode);

            var wrongOwner = new TinyPath(RuleRootKind.Map).Step("p.health");
            outcome = RulePathEval.Evaluate(wrongOwner, host, vocabulary);
            Check(into, "路径：属性挂错类型 → property_wrong_type",
                !outcome.ok && outcome.reasonCode == "path.property_wrong_type",
                outcome.reasonCode);

            var emptySet = RuleValue.OfSet(RuleEntityKind.Thing, new List<RuleValue>());
            RuleValue reduced;
            string code;
            string reason;

            bool ok = RulePathEval.Reduce(emptySet, RuleReduceKind.First, host,
                out reduced, out code, out reason);
            Check(into, "路径：空集合的「第一个」是读不到，不是 0",
                !ok && code == "path.empty_set", code);

            ok = RulePathEval.Reduce(emptySet, RuleReduceKind.Count, host,
                out reduced, out code, out reason);
            Check(into, "路径：空集合的「数量」才是真的 0",
                ok && reduced.AsNumber == 0f, ok ? reduced.ToString() : code);
        }

        private static RuleVocabulary BuildPathVocabulary()
        {
            var vocabulary = new RuleVocabulary();
            vocabulary.Add(new RulePropertyInfo
            {
                key = "p.health", owner = RuleEntityKind.Pawn,
                result = RuleValueKind.Number, reader = SimpleNumber(0.4f)
            });
            vocabulary.Add(new RulePropertyInfo
            {
                key = "p.apparel", owner = RuleEntityKind.Pawn,
                result = RuleValueKind.EntitySet, resultEntity = RuleEntityKind.Thing,
                // 空集合也要有个 reader —— 属性读取由词表负责之后，
                // "登记了属性忘了实现"会以 path.no_reader 的形式在路径上暴露出来。
                reader = delegate(IRuleEvalHost h, RuleValue owner, out RuleValue value,
                    out string code, out string reason)
                {
                    value = RuleValue.OfSet(RuleEntityKind.Thing, new List<RuleValue>());
                    code = null;
                    reason = null;
                    return true;
                }
            });
            return vocabulary;
        }

        // ── 检测树 ────────────────────────────────────────────────────

        private static void Exprs(List<RuleSelfTestResult> into)
        {
            var vocabulary = BuildPathVocabulary();

            vocabulary.Add(new RuleVerbInfo
            {
                key = "gt", category = RuleVerbCategory.Detect,
                subject = RuleEntityKind.Any, argKind = RuleValueKind.Number,
                detect = delegate(IRuleEvalHost h, RuleValue subject, RuleValue arg,
                    out bool passed, out string reasonCode, out string reason)
                {
                    passed = RuleCompare.Apply(RuleOperator.Greater, subject.AsNumber, arg.AsNumber);
                    reasonCode = passed ? null : "gt.false";
                    reason = passed ? null : "不成立";
                    return true;
                }
            });

            vocabulary.Add(new RuleVerbInfo
            {
                key = "quiet", category = RuleVerbCategory.Detect,
                subject = RuleEntityKind.Pawn, argKind = RuleValueKind.None,
                failure = RuleFailureMode.QuietSkip,
                detect = delegate(IRuleEvalHost h, RuleValue subject, RuleValue arg,
                    out bool passed, out string reasonCode, out string reason)
                {
                    passed = false;
                    reasonCode = "quiet.false";
                    reason = "做不了";
                    return true;
                }
            });

            var host = new TinyHost(RuleValue.OfEntity(RuleEntityKind.Pawn, "p"));

            var passing = Leaf(new TinyPath(RuleRootKind.Subject).Step("p.health"), "gt", 0.1f);
            var failing = Leaf(new TinyPath(RuleRootKind.Subject).Step("p.health"), "gt", 1f);

            var outcome = RuleExprEval.Evaluate(passing, host, vocabulary);
            Check(into, "检测树：单叶成立", outcome.passed, outcome.reasonCode);

            outcome = RuleExprEval.Evaluate(failing, host, vocabulary);
            Check(into, "检测树：单叶不成立并带出原因码",
                !outcome.passed && outcome.reasonCode == "gt.false", outcome.reasonCode);

            var andNode = new TinyExpr { kind = RuleExprNodeKind.And };
            andNode.children.Add(failing);
            andNode.children.Add(passing);

            outcome = RuleExprEval.Evaluate(andNode, host, vocabulary);
            Check(into, "检测树：且短路在第一句，带出下标 and[0]",
                !outcome.passed && outcome.tracePath == "and[0]",
                outcome.tracePath + " | " + outcome.reasonCode);

            var orNode = new TinyExpr { kind = RuleExprNodeKind.Or };
            orNode.children.Add(passing);
            orNode.children.Add(failing);

            outcome = RuleExprEval.Evaluate(orNode, host, vocabulary);
            Check(into, "检测树：或有一支成立即通过", outcome.passed, outcome.reasonCode);

            outcome = RuleExprEval.Evaluate(Leaf(new TinyPath(RuleRootKind.Subject), "quiet", null),
                host, vocabulary);
            Check(into, "检测树：安静跳过一路带到顶层（能力不成立不算规则失败）",
                !outcome.passed && outcome.mode == RuleFailureMode.QuietSkip,
                outcome.mode.ToString());
        }

        /// <summary>
        /// 自检里的最小叶子与宾语。**刻意不用 Verse 侧的 RuleExprNode / RuleOperand** ——
        /// 这一层必须纯 BCL，否则自检就跑不起来（而它恰恰要在游戏刚启动时跑）。
        /// </summary>
        private static TinyExpr Leaf(TinyPath path, string verbKey, float? threshold)
        {
            return new TinyExpr
            {
                kind = RuleExprNodeKind.Detect,
                subject = path,
                verbKey = verbKey,
                argument = threshold.HasValue ? TinyOperand.Number(threshold.Value) : null
            };
        }

        private sealed class TinyOperand : IRuleOperandSource
        {
            public RuleValueKind kind = RuleValueKind.None;
            public RuleValue literal = RuleValue.None;

            public static TinyOperand Number(float value)
            {
                return new TinyOperand { kind = RuleValueKind.Number, literal = RuleValue.OfNumber(value) };
            }

            public RuleValueKind Kind { get { return kind; } }
            public RuleValue Literal { get { return literal; } }
            public IRulePathSource Path { get { return null; } }
        }

        private sealed class TinyExpr : IRuleExprSource
        {
            public RuleExprNodeKind kind = RuleExprNodeKind.And;
            public readonly List<TinyExpr> children = new List<TinyExpr>();

            public IRulePathSource subject;
            public string verbKey;
            public TinyOperand argument;

            public RuleExprNodeKind NodeKind { get { return kind; } }
            public int ChildCount { get { return children.Count; } }
            public IRuleExprSource ChildAt(int index) { return children[index]; }
            public IRulePathSource Subject { get { return subject; } }
            public string VerbKey { get { return verbKey; } }
            public IRuleOperandSource Argument { get { return argument; } }
        }

        // ── 只为自检存在的两个最小实现 ────────────────────────────────

        private static RulePropertyReadHandler SimpleNumber(float value)
        {
            return delegate(IRuleEvalHost h, RuleValue owner, out RuleValue result,
                out string reasonCode, out string reason)
            {
                result = RuleValue.OfNumber(value);
                reasonCode = null;
                reason = null;
                return true;
            };
        }

        private sealed class TinyHost : IRuleEvalHost
        {
            private readonly RuleValue subject;
            private RuleValue element = RuleValue.None;

            public TinyHost(RuleValue subject)
            {
                this.subject = subject;
            }

            public RuleValue Subject { get { return subject; } }
            public RuleValue Element { get { return element; } set { element = value; } }
            public RuleValue Map { get { return RuleValue.OfEntity(RuleEntityKind.Map, "m"); } }

            /// <summary>自检里不测指名绑定，所以永远没有引用。</summary>
            public string SubjectRef { get { return null; } }

            public bool TryRoot(RuleRootKind kind, RuleValue literal, out RuleValue value,
                out string reasonCode, out string reason)
            {
                switch (kind)
                {
                    case RuleRootKind.Subject: value = subject; break;
                    case RuleRootKind.Map: value = Map; break;
                    case RuleRootKind.Literal: value = literal; break;
                    case RuleRootKind.Element: value = element; break;
                    default:
                        value = RuleValue.None;
                        reasonCode = "root.none";
                        reason = "自检宿主不提供这个根。";
                        return false;
                }

                reasonCode = null;
                reason = null;
                return true;
            }

            public bool TryReduce(RuleValue set, RuleReduceKind kind, out RuleValue value,
                out string reasonCode, out string reason)
            {
                value = RuleValue.None;
                reasonCode = "reduce.none";
                reason = "自检宿主不做下放归约。";
                return false;
            }
        }

        private sealed class TinyPath : IRulePathSource
        {
            private readonly RuleRootKind rootKind;
            private readonly List<string> properties = new List<string>();

            public TinyPath(RuleRootKind rootKind)
            {
                this.rootKind = rootKind;
            }

            public TinyPath Step(string propertyKey)
            {
                properties.Add(propertyKey);
                return this;
            }

            public RuleRootKind RootKind { get { return rootKind; } }
            public RuleValue RootLiteral { get { return RuleValue.None; } }
            public int StepCount { get { return properties.Count; } }
            public RuleStepKind StepKindAt(int index) { return RuleStepKind.Property; }
            public string PropertyKeyAt(int index) { return properties[index]; }
            public IRuleExprSource FilterAt(int index) { return null; }
            public RuleReduceKind ReduceAt(int index) { return RuleReduceKind.First; }
            public RuleQuantifier QuantifyAt(int index) { return RuleQuantifier.All; }
        }
    }
}
