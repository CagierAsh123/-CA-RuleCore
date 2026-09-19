using System;
using System.Collections.Generic;
using RuleCore.Core;

namespace RuleCore.CoreTests
{
    /// <summary>
    /// 规则运行器的单测。它替换了旧的 8 阶段管线测试。
    ///
    /// 要钉死的四件事：
    ///   1. **短路顺序**与各自的结局码（冷却 / 权限 / 环 / 检测 / 操作）；
    ///   2. **失败不记冷却**，所以下一次采样会自然重试；
    ///   3. **安静让开**（动作已经满足、原版拒绝）既不算失败也不记冷却——
    ///      这是"条件/能力"合并之后保留区别的地方；
    ///   4. 自触发环**拦在下发之前**，不是事后发现。
    ///
    /// 全部用假宿主 + 手搭词表，脱离游戏跑。
    /// </summary>
    internal static class RunnerTests
    {
        // ── 假现场 ────────────────────────────────────────────────────

        private sealed class FakeHost : IRuleEvalHost
        {
            private RuleValue element = RuleValue.None;

            public RuleValue Subject { get { return RuleValue.OfEntity(RuleEntityKind.Pawn, "p"); } }
            public RuleValue Element { get { return element; } set { element = value; } }
            public RuleValue Map { get { return RuleValue.OfEntity(RuleEntityKind.Map, "m"); } }

            /// <summary>管线测试不测指名绑定。</summary>
            public string SubjectRef { get { return null; } }

            public bool TryRoot(RuleRootKind kind, RuleValue literal, out RuleValue value,
                out string reasonCode, out string reason)
            {
                value = kind == RuleRootKind.Literal ? literal : Subject;
                reasonCode = null;
                reason = null;
                return true;
            }

            public bool TryReduce(RuleValue set, RuleReduceKind kind, out RuleValue value,
                out string reasonCode, out string reason)
            {
                value = RuleValue.None;
                reasonCode = "reduce.none";
                reason = "测试宿主不做下放归约。";
                return false;
            }
        }

        /// <summary>可编排的规则：检测树是否成立、操作给什么结局，全都可控。</summary>
        private sealed class FakeRule : IRuleRunTarget
        {
            public string Id = "T";
            public RuleTier Tier = RuleTier.Player;
            public int Cooldown;

            /// <summary>null = 空检测树（永远成立）。</summary>
            public bool? DetectPasses = true;

            public int OperateCountValue = 1;
            public RuleOperateStatus OperateStatus = RuleOperateStatus.Done;
            public string OperateCode = "done";

            public string RuleId { get { return Id; } }
            public RuleTier EffectiveTier { get { return Tier; } }
            public int CooldownTicks { get { return Cooldown; } }
            public IRuleExprSource Detect { get { return DetectPasses.HasValue ? new Leaf() : null; } }
            public int OperateCount { get { return OperateCountValue; } }
            public IRuleOperateSource OperateAt(int index) { return new Clause(); }

            private sealed class Leaf : IRuleExprSource
            {
                public RuleExprNodeKind NodeKind { get { return RuleExprNodeKind.Detect; } }
                public int ChildCount { get { return 0; } }
                public IRuleExprSource ChildAt(int index) { return null; }
                public IRulePathSource Subject { get { return new SubjectPath(); } }
                public string VerbKey { get { return "flag"; } }
                public IRuleOperandSource Argument { get { return null; } }
            }

            private sealed class Clause : IRuleOperateSource
            {
                public IRulePathSource Subject { get { return new SubjectPath(); } }
                public string VerbKey { get { return "act"; } }
                public IRuleOperandSource Argument { get { return null; } }
            }

            private sealed class SubjectPath : IRulePathSource
            {
                public RuleRootKind RootKind { get { return RuleRootKind.Subject; } }
                public RuleValue RootLiteral { get { return RuleValue.None; } }
                public int StepCount { get { return 0; } }
                public RuleStepKind StepKindAt(int index) { return RuleStepKind.Property; }
                public string PropertyKeyAt(int index) { return null; }
                public IRuleExprSource FilterAt(int index) { return null; }
                public RuleReduceKind ReduceAt(int index) { return RuleReduceKind.First; }
                public RuleQuantifier QuantifyAt(int index) { return RuleQuantifier.All; }
            }
        }

        private static RuleVocabulary BuildVocabulary(FakeRule rule, FakeHost host,
            out int detectCalls, out int operateCalls)
        {
            var probe = new int[2];
            var vocabulary = new RuleVocabulary();

            vocabulary.Add(new RuleVerbInfo
            {
                key = "flag", category = RuleVerbCategory.Detect,
                subject = RuleEntityKind.Any, argKind = RuleValueKind.None,
                detect = delegate(IRuleEvalHost h, RuleValue subject, RuleValue arg,
                    out bool passed, out string code, out string reason)
                {
                    probe[0]++;
                    passed = rule.DetectPasses.HasValue && rule.DetectPasses.Value;
                    code = "flag.false";
                    reason = "测试用检测不成立。";
                    return true;
                }
            });

            vocabulary.Add(new RuleVerbInfo
            {
                key = "act", category = RuleVerbCategory.Operate,
                subject = RuleEntityKind.Pawn, argKind = RuleValueKind.None,
                operate = delegate(IRuleEvalHost h, RuleValue subject, RuleValue arg,
                    out string code, out string reason)
                {
                    probe[1]++;
                    code = rule.OperateCode;
                    reason = "测试用操作。";
                    return rule.OperateStatus;
                }
            });

            detectCalls = 0;
            operateCalls = 0;
            Probe = probe;
            return vocabulary;
        }

        private static int[] Probe;

        private static int DetectCalls
        {
            get { return Probe != null ? Probe[0] : 0; }
        }

        private static int OperateCalls
        {
            get { return Probe != null ? Probe[1] : 0; }
        }

        // ── 入口 ──────────────────────────────────────────────────────

        public static void Run()
        {
            Console.WriteLine();
            Console.WriteLine("== 规则运行器 ==");

            RuleLog.Boot(256);
            RuleLog.EnabledProvider = () => true;
            RuleLog.VerboseProvider = () => true;
            RuleLog.TickProvider = () => 0;

            Blocks();
            Detects();
            Operates();
            Loops();
        }

        private static void Blocks()
        {
            var host = new FakeHost();
            var rule = new FakeRule();
            int ignoredA;
            int ignoredB;
            var vocabulary = BuildVocabulary(rule, host, out ignoredA, out ignoredB);
            var runner = new RuleRunner();

            // 冷却
            var state = new RuleRuntimeState();
            state.LastFiredTick = 100;
            rule.Cooldown = 50;
            var evaluation = runner.Evaluate(rule, host, vocabulary, state, 120, RuleTier.Player);
            Program.Check("运行器：冷却中 → cooldown_blocked，且不碰检测",
                evaluation.Status == RuleEvalStatus.CooldownBlocked && DetectCalls == 0,
                evaluation.Status + " detectCalls=" + DetectCalls);

            // 权限
            rule.Cooldown = 0;
            rule.Tier = RuleTier.Developer;
            evaluation = runner.Evaluate(rule, host, vocabulary, new RuleRuntimeState(), 120,
                RuleTier.Player);
            Program.Check("运行器：层级不足 → skipped，且不碰检测",
                evaluation.Status == RuleEvalStatus.Skipped && DetectCalls == 0,
                evaluation.Status + " detectCalls=" + DetectCalls);

            rule.Tier = RuleTier.Player;

            // 空检测树 = 永远成立
            rule.DetectPasses = null;
            evaluation = runner.Evaluate(rule, host, vocabulary, new RuleRuntimeState(), 120,
                RuleTier.Player);
            Program.Check("运行器：空检测树视作恒真（写纯周期规则用）",
                evaluation.Succeeded, evaluation.Status + " " + evaluation.ReasonCode);

            rule.DetectPasses = true;
        }

        private static void Detects()
        {
            var host = new FakeHost();
            var rule = new FakeRule();
            int ignoredA;
            int ignoredB;
            var vocabulary = BuildVocabulary(rule, host, out ignoredA, out ignoredB);
            var runner = new RuleRunner();

            rule.DetectPasses = false;
            var evaluation = runner.Evaluate(rule, host, vocabulary, new RuleRuntimeState(), 1,
                RuleTier.Player);
            Program.Check("运行器：检测不成立 → condition_false，且不下发操作",
                evaluation.Status == RuleEvalStatus.ConditionFalse
                && evaluation.Stage == RuleStage.Detect
                && OperateCalls == 0,
                evaluation.Status + " opCalls=" + OperateCalls);
            Program.Check("运行器：检测失败的原因码带上了是哪一支",
                evaluation.ReasonCode != null && evaluation.ReasonCode.StartsWith("flag", StringComparison.Ordinal),
                evaluation.ReasonCode);

            rule.DetectPasses = true;
            evaluation = runner.Evaluate(rule, host, vocabulary, new RuleRuntimeState(), 1,
                RuleTier.Player);
            Program.Check("运行器：检测成立 → 下发操作并通过",
                evaluation.Succeeded && OperateCalls > 0,
                evaluation.Status + " opCalls=" + OperateCalls);
        }

        private static void Operates()
        {
            var host = new FakeHost();
            var rule = new FakeRule();
            int ignoredA;
            int ignoredB;
            var vocabulary = BuildVocabulary(rule, host, out ignoredA, out ignoredB);
            var runner = new RuleRunner();

            // 成功：记冷却
            var state = new RuleRuntimeState();
            rule.OperateStatus = RuleOperateStatus.Done;
            rule.Cooldown = 100;
            var evaluation = runner.Evaluate(rule, host, vocabulary, state, 500, RuleTier.Player);
            Program.Check("运行器：操作成功 → issued，且记下冷却",
                evaluation.Status == RuleEvalStatus.Issued && state.LastFiredTick == 500,
                evaluation.Status + " lastFired=" + state.LastFiredTick);

            // 失败：不记冷却（下一次采样自然重试）
            state = new RuleRuntimeState();
            rule.OperateStatus = RuleOperateStatus.Failed;
            evaluation = runner.Evaluate(rule, host, vocabulary, state, 500, RuleTier.Player);
            Program.Check("运行器：操作失败 → failed，且**不**记冷却（下次立刻重试）",
                evaluation.Status == RuleEvalStatus.Failed && state.LastFiredTick == -1,
                evaluation.Status + " lastFired=" + state.LastFiredTick);

            // 已经满足：既不算失败也不记冷却
            state = new RuleRuntimeState();
            rule.OperateStatus = RuleOperateStatus.AlreadySatisfied;
            evaluation = runner.Evaluate(rule, host, vocabulary, state, 500, RuleTier.Player);
            Program.Check("运行器：已经满足 → already_satisfied，且不记冷却",
                evaluation.Status == RuleEvalStatus.AlreadySatisfied && state.LastFiredTick == -1,
                evaluation.Status + " lastFired=" + state.LastFiredTick);

            // 原版拒绝：安静让开
            state = new RuleRuntimeState();
            rule.OperateStatus = RuleOperateStatus.Rejected;
            evaluation = runner.Evaluate(rule, host, vocabulary, state, 500, RuleTier.Player);
            Program.Check("运行器：原版拒绝 → capability_denied（原版第一顺位），且不记冷却",
                evaluation.Status == RuleEvalStatus.CapabilityDenied && state.LastFiredTick == -1,
                evaluation.Status + " lastFired=" + state.LastFiredTick);

            // 没有操作
            rule.OperateStatus = RuleOperateStatus.Done;
            rule.OperateCountValue = 0;
            evaluation = runner.Evaluate(rule, host, vocabulary, new RuleRuntimeState(), 500,
                RuleTier.Player);
            Program.Check("运行器：检测成立但没有操作 → already_satisfied 而不是 issued",
                evaluation.Status == RuleEvalStatus.AlreadySatisfied,
                evaluation.Status);
            rule.OperateCountValue = 1;
        }

        private static void Loops()
        {
            var host = new FakeHost();
            var rule = new FakeRule();
            int ignoredA;
            int ignoredB;
            var vocabulary = BuildVocabulary(rule, host, out ignoredA, out ignoredB);

            var runner = new RuleRunner
            {
                LoopWindowTicks = 250,
                LoopIssueThreshold = 3
            };

            var state = new RuleRuntimeState();
            rule.OperateStatus = RuleOperateStatus.Done;
            rule.Cooldown = 0;

            // 窗口内下发 3 次后，第 4 次应当被拦下并自动停用。
            for (int i = 0; i < 3; i++)
            {
                runner.Evaluate(rule, host, vocabulary, state, 10 + i, RuleTier.Player);
            }

            var evaluation = runner.Evaluate(rule, host, vocabulary, state, 20, RuleTier.Player);
            Program.Check("运行器：窗口内下发超限 → loop_suspected，且**本次不下发**",
                evaluation.Status == RuleEvalStatus.LoopSuspected
                && evaluation.ReasonCode == "loop.detected"
                && state.LoopSuspected,
                evaluation.ReasonCode + " issues=" + state.IssuesInWindow);

            evaluation = runner.Evaluate(rule, host, vocabulary, state, 30, RuleTier.Player);
            Program.Check("运行器：判定为环之后持续停用",
                evaluation.Status == RuleEvalStatus.LoopSuspected
                && evaluation.ReasonCode == "loop.disabled",
                evaluation.ReasonCode);

            // 窗口过期后计数重置，不会累积成误判
            state = new RuleRuntimeState();
            runner.Evaluate(rule, host, vocabulary, state, 1000, RuleTier.Player);
            runner.Evaluate(rule, host, vocabulary, state, 1400, RuleTier.Player);
            runner.Evaluate(rule, host, vocabulary, state, 1800, RuleTier.Player);
            Program.Check("运行器：窗口过期后计数重置，不会累积成误判",
                !state.LoopSuspected && state.IssuesInWindow == 1,
                "issues=" + state.IssuesInWindow + " suspected=" + state.LoopSuspected);

            // 结局进日志汇
            RuleLog.Boot(64);
            var emitRule = new FakeRule { Id = "T.Emit" };
            int a;
            int b;
            var emitVocabulary = BuildVocabulary(emitRule, host, out a, out b);
            emitRule.OperateStatus = RuleOperateStatus.Done;
            new RuleRunner().EvaluateAndEmit(emitRule, host, emitVocabulary,
                new RuleRuntimeState(), 7, RuleTier.Player);

            var snapshot = RuleLog.Snapshot();
            var last = snapshot.Length > 0 ? snapshot[snapshot.Length - 1] : null;
            Program.Check("运行器：EvaluateAndEmit 把结局写进日志汇",
                last != null && last.RuleId == "T.Emit" && last.Outcome == RuleEvalStatus.Issued,
                last == null ? "时间线为空" : last.RuleId + "/" + last.Outcome);
        }
    }
}
