using System;

namespace RuleCore.Core
{
    /// <summary>操作下发后的三种结局。刻意把"已经满足了"与"失败"分开。</summary>
    public enum RuleOperateStatus
    {
        /// <summary>做成了。</summary>
        Done = 0,

        /// <summary>
        /// 要达成的状态**本来就已经满足**，所以什么都没做。
        ///
        /// 这是电平规则不刷屏的第一道防线，而且它**不是失败**：
        /// 记失败会让时间线充满红色的"他没进屋"（其实他就在屋里），
        /// 排障的人会去查一个根本不存在的 bug。
        /// </summary>
        AlreadySatisfied = 1,

        /// <summary>
        /// 原版不肯接这条指令（更高优先级的 job、玩家征召、路径不通）。
        /// 安静让开就是"原版第一顺位"，不算规则出错，但要记下来。
        /// </summary>
        Rejected = 2,

        /// <summary>真的出错了。</summary>
        Failed = 3
    }

    /// <summary>操作谓词的行为本体。</summary>
    public delegate RuleOperateStatus RuleOperateHandler(IRuleEvalHost host, RuleValue subject,
        RuleValue arg, out string reasonCode, out string reason);

    /// <summary>一条操作子句：实体 · 操作 · 宾语。</summary>
    public interface IRuleOperateSource
    {
        IRulePathSource Subject { get; }
        string VerbKey { get; }
        IRuleOperandSource Argument { get; }
    }

    /// <summary>
    /// 引擎要跑的一条规则。和 <see cref="IRuleEvalHost"/> 一样，是 Core 对 Verse 的只读视图——
    /// 这样"冷却 / 权限 / 环检测 / 短路顺序"这些引擎逻辑能脱离游戏单测。
    /// </summary>
    public interface IRuleRunTarget
    {
        string RuleId { get; }

        /// <summary>规则所有操作的最高权限级。</summary>
        RuleTier EffectiveTier { get; }

        int CooldownTicks { get; }

        /// <summary>检测树。整棵成立才算通过。</summary>
        IRuleExprSource Detect { get; }

        int OperateCount { get; }
        IRuleOperateSource OperateAt(int index);
    }

    /// <summary>
    /// 规则运行器 —— 替换了旧的 8 阶段管线。
    ///
    /// 新模型只有三件事要做，因为"触发 / 条件 / 能力"三个类别已经合并进检测树：
    ///
    /// <code>
    /// 0 冷却      Engine   冷却中          → cooldown_blocked（Warn）
    /// 1 权限      Engine   层级不足        → skipped
    /// 2 自触发环  Engine   已停用          → loop_suspected（Warn）
    /// 3 检测      Detect   树不成立        → condition_false（Trace）
    ///             含 QuietSkip 的叶子时   → capability_denied（Warn）—— 安静让开
    /// 4 操作      逐条下发，第一条不成功就停
    /// </code>
    ///
    /// 两条旧管线的设计原样保留，因为它们是对的：
    ///   · **异常不外泄**：任何一步抛异常都被就地捕获成一条 <c>error</c> 结局，
    ///     一颗坏规则不该让整个 tick 停摆（但必须响亮地记下来）。
    ///   · **自触发环拦在下发之前**，不是事后发现。
    ///
    /// 第三条保留：**失败不记冷却**，所以下一次采样会自然重试。
    /// </summary>
    public sealed class RuleRunner
    {
        public const int DefaultLoopWindowTicks = 250;
        public const int DefaultLoopIssueThreshold = 8;

        /// <summary>自触发环的观察窗口长度（tick）。</summary>
        public int LoopWindowTicks = DefaultLoopWindowTicks;

        /// <summary>窗口内下发次数达到这个数就判定为自触发环。</summary>
        public int LoopIssueThreshold = DefaultLoopIssueThreshold;

        /// <summary>
        /// 判定一条规则。**只推进 <paramref name="state"/> 的冷却与窗口计数**，
        /// 除此之外不产生副作用（不写日志、不动世界）。要不要记录由调用方决定。
        /// </summary>
        public RuleEvaluation Evaluate(IRuleRunTarget rule, IRuleEvalHost host,
            RuleVocabulary vocabulary, RuleRuntimeState state, int tick, RuleTier allowedTier)
        {
            if (rule == null) throw new ArgumentNullException("rule");
            if (host == null) throw new ArgumentNullException("host");
            if (state == null) throw new ArgumentNullException("state");

            string ruleId = rule.RuleId;
            string entity = HostEntityLabel(host);

            // ── 0. 冷却 ──────────────────────────────────────────────
            int wait = state.TicksUntilReady(tick, rule.CooldownTicks);
            if (wait > 0)
            {
                return RuleEvaluation.Blocked(ruleId, RuleStage.Engine,
                    RuleEvalStatus.CooldownBlocked, "cooldown",
                    "冷却中：还需 " + wait + " tick", entity);
            }

            // ── 1. 权限层级 ──────────────────────────────────────────
            if ((int)rule.EffectiveTier > (int)allowedTier)
            {
                return RuleEvaluation.Blocked(ruleId, RuleStage.Engine,
                    RuleEvalStatus.Skipped, "tier.denied",
                    "需要 " + rule.EffectiveTier + " 层级，当前只允许 " + allowedTier, entity);
            }

            // ── 2. 已被判定为自触发环并停用 ──────────────────────────
            if (state.LoopSuspected)
            {
                return RuleEvaluation.Blocked(ruleId, RuleStage.Engine,
                    RuleEvalStatus.LoopSuspected, "loop.disabled",
                    "此前在 " + LoopWindowTicks + " tick 内触发超过 " + LoopIssueThreshold
                    + " 次，已自动停用", entity);
            }

            // ── 3. 检测树 ────────────────────────────────────────────
            RuleExprOutcome detect;
            if (rule.Detect == null)
            {
                // 空检测树 = 永远成立。用来写"纯周期规则"（每 N tick 做一次某事），
                // 也避免玩家在编辑器里刚建出来的空规则直接被拦下。
                detect = RuleExprOutcome.Pass();
            }
            else
            {
                try
                {
                    detect = RuleExprEval.Evaluate(rule.Detect, host, vocabulary);
                }
                catch (Exception ex)
                {
                    return ErrorAt(ruleId, RuleStage.Detect, "detect.exception", ex, entity);
                }
            }

            if (!detect.passed)
            {
                // 失败模式决定这是一条"不成立"还是"安静让开"。
                // 这就是"条件 / 能力"合并之后保留区别的全部手段——谓词自己声明。
                bool quiet = detect.mode == RuleFailureMode.QuietSkip;
                string code = WithTrace(detect.tracePath, detect.reasonCode);

                return RuleEvaluation.Blocked(ruleId, RuleStage.Detect,
                    quiet ? RuleEvalStatus.CapabilityDenied : RuleEvalStatus.ConditionFalse,
                    code, detect.reason, entity);
            }

            // ── 4. 操作序列 ──────────────────────────────────────────
            if (rule.OperateCount == 0)
            {
                // 检测成立但无事可做。这不是错误——"只在时间线上留个痕"是合法的用法，
                // 但也不能报 issued（那会骗人说做了事）。
                return RuleEvaluation.Blocked(ruleId, RuleStage.Operate,
                    RuleEvalStatus.AlreadySatisfied, "operate.none", "这条规则没有操作。", entity);
            }

            for (int i = 0; i < rule.OperateCount; i++)
            {
                var evaluation = RunOperate(rule, rule.OperateAt(i), i, host, vocabulary,
                    state, tick, entity);

                if (evaluation != null)
                {
                    return evaluation;
                }
            }

            // 全部操作都"已经满足"——记一条 Trace 级的既成事实，不记下发。
            return RuleEvaluation.Blocked(ruleId, RuleStage.Operate,
                RuleEvalStatus.AlreadySatisfied, "operate.all_satisfied",
                "要达成的状态都已经满足了。", entity);
        }

        /// <summary>求值并立刻写进日志汇。做试运行时应当只调 <see cref="Evaluate"/>。</summary>
        public RuleEvaluation EvaluateAndEmit(IRuleRunTarget rule, IRuleEvalHost host,
            RuleVocabulary vocabulary, RuleRuntimeState state, int tick, RuleTier allowedTier)
        {
            var evaluation = Evaluate(rule, host, vocabulary, state, tick, allowedTier);
            evaluation.Emit();
            return evaluation;
        }

        /// <summary>
        /// 跑一条操作子句。返回 null 表示"这条已经满足，继续下一条"。
        /// </summary>
        private RuleEvaluation RunOperate(IRuleRunTarget rule, IRuleOperateSource clause, int index,
            IRuleEvalHost host, RuleVocabulary vocabulary, RuleRuntimeState state, int tick,
            string entity)
        {
            string ruleId = rule.RuleId;
            string prefix = "operate[" + index + "].";

            var verb = vocabulary != null ? vocabulary.Verb(clause.VerbKey) : null;
            if (verb == null)
            {
                return RuleEvaluation.Failed(ruleId, RuleStage.Operate,
                    prefix + "unknown_verb", "词表里没有这个操作：" + (clause.VerbKey ?? "(空)"), entity);
            }

            if (verb.category != RuleVerbCategory.Operate)
            {
                return RuleEvaluation.Failed(ruleId, RuleStage.Operate,
                    prefix + "not_an_operate", "「" + verb.key + "」是检测，不能当操作用。", entity);
            }

            if (verb.operate == null)
            {
                return RuleEvaluation.Failed(ruleId, RuleStage.Operate,
                    prefix + "no_runner", "「" + verb.key + "」登记了但没有实现。", entity);
            }

            var subjectOutcome = RulePathEval.EvaluateWithDepth(clause.Subject, host, vocabulary, 0);
            if (!subjectOutcome.ok)
            {
                return RuleEvaluation.Failed(ruleId, RuleStage.Operate,
                    prefix + subjectOutcome.reasonCode, subjectOutcome.reason, entity);
            }

            var subject = subjectOutcome.value;
            if (subject.IsSet)
            {
                return RuleEvaluation.Failed(ruleId, RuleStage.Operate,
                    prefix + "needs_reduce", "主语还是一组东西，先选一个或数个数。", entity);
            }

            if (!RuleVocabulary.EntityKindMatches(verb.subject, subject.entityKind))
            {
                return RuleEvaluation.Failed(ruleId, RuleStage.Operate,
                    prefix + "wrong_subject",
                    "「" + verb.key + "」不能用在 " + subject.entityKind + " 上。", entity);
            }

            var argOutcome = RuleExprEval.ResolveOperand(clause.Argument, host, vocabulary, 0);
            if (!argOutcome.ok)
            {
                return RuleEvaluation.Failed(ruleId, RuleStage.Operate,
                    prefix + argOutcome.reasonCode, argOutcome.reason, entity);
            }

            if (!RuleVocabulary.ArgMatches(verb, argOutcome.value))
            {
                return RuleEvaluation.Failed(ruleId, RuleStage.Operate,
                    prefix + "wrong_arg",
                    "「" + verb.key + "」的宾语类型不对：要 " + verb.argKind
                    + "，给的是 " + argOutcome.value.kind + "。", entity);
            }

            // 环窗口：在**下发之前**拦。
            RollWindowIfStale(state, tick);
            if (state.IssuesInWindow >= LoopIssueThreshold)
            {
                state.LoopSuspected = true;
                return RuleEvaluation.Blocked(ruleId, RuleStage.Engine,
                    RuleEvalStatus.LoopSuspected, "loop.detected",
                    "在 " + LoopWindowTicks + " tick 内已下发 " + state.IssuesInWindow
                    + " 次，疑似自触发环——本次不下发，并自动停用该规则", entity);
            }

            RuleOperateStatus status;
            string reasonCode;
            string reason;
            try
            {
                status = verb.operate(host, subject, argOutcome.value, out reasonCode, out reason);
            }
            catch (Exception ex)
            {
                return ErrorAt(ruleId, RuleStage.Operate, prefix + "exception", ex, entity);
            }

            switch (status)
            {
                case RuleOperateStatus.Done:
                    state.LastFiredTick = tick;
                    state.IssuesInWindow++;
                    return RuleEvaluation.Issued(ruleId, RuleStage.Operate,
                        prefix + (reasonCode ?? "done"), reason, entity);

                case RuleOperateStatus.AlreadySatisfied:
                    // 不记冷却、不记窗口：什么都没做，也就不该付出代价。
                    return null;

                case RuleOperateStatus.Rejected:
                    return RuleEvaluation.Blocked(ruleId, RuleStage.Operate,
                        RuleEvalStatus.CapabilityDenied, prefix + (reasonCode ?? "rejected"),
                        reason, entity);

                default:
                    // 失败**不记冷却**，下一次采样会自然重试。
                    return RuleEvaluation.Failed(ruleId, RuleStage.Operate,
                        prefix + (reasonCode ?? "failed"), reason, entity);
            }
        }

        private void RollWindowIfStale(RuleRuntimeState state, int tick)
        {
            if (state.WindowStartTick < 0 || tick - state.WindowStartTick > LoopWindowTicks)
            {
                state.WindowStartTick = tick;
                state.IssuesInWindow = 0;
            }
        }

        private static string WithTrace(string tracePath, string reasonCode)
        {
            string code = string.IsNullOrEmpty(reasonCode) ? "detect.false" : reasonCode;
            return string.IsNullOrEmpty(tracePath) ? code : tracePath + "." + code;
        }

        private static string HostEntityLabel(IRuleEvalHost host)
        {
            var subject = host.Subject;
            return subject.IsMissing ? null : subject.ToString();
        }

        private static RuleEvaluation ErrorAt(string ruleId, RuleStage stage, string reasonCode,
            Exception ex, string entity)
        {
            string detail = ex.GetType().Name + ": " + ex.Message;
            return RuleEvaluation.Create(ruleId, stage, RuleEvalStatus.Error, reasonCode, detail, entity);
        }
    }
}
