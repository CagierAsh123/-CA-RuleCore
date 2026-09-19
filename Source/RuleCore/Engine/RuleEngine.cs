using System.Collections.Generic;
using Verse;
using RuleCore.Core;

namespace RuleCore
{
    /// <summary>
    /// 规则引擎的宿主：按档位采样、**按主体拆分求值**、持有每规则每主体的最小运行态。
    ///
    /// **当前是采样驱动，不是订阅驱动。** 这是刻意的阶段性选择，也符合已经定下的
    /// "有边沿语义 + 原版没有扩展点 → patch；只是电平条件 → 采样"这条判据。
    /// 采样档位就是架构里真正的性能预算项——以后做订阅式重评估时，
    /// 替换的是**"什么时候求值"**，检测与操作本身一行都不用动。
    ///
    /// **运行态按主体分开**：冷却与自触发环都是 per (规则, 主体)。
    /// 否则「袭击来了，外面的人都回家」里，第一个人的一次触发会把整条规则的冷却吃掉，
    /// 剩下的人全都不动了。
    ///
    /// 规则来源统一走 <see cref="RuleLibrary"/>——引擎不关心它来自模组 XML 还是玩家手写。
    ///
    /// 挂在 <see cref="MapComponent"/> 上——原版会自动扫描并实例化（无需 Def）。
    /// </summary>
    public sealed class RuleEngine : MapComponent
    {
        /// <summary>采样间隔（tick）。250 是原版的 rare tick 档位。</summary>
        public const int SampleIntervalTicks = 250;

        private readonly RuleRunner runner = new RuleRunner();

        /// <summary>规则 id → 主体 ID → 运行态。主体 ID 用 thingIDNumber，整图评估用 0。</summary>
        private readonly Dictionary<string, Dictionary<int, RuleRuntimeState>> states =
            new Dictionary<string, Dictionary<int, RuleRuntimeState>>();

        /// <summary>复用同一个列表，避免每个采样周期都为绑定器分配一次。</summary>
        private readonly List<RuleValue> subjects = new List<RuleValue>();

        public RuleEngine(Map map) : base(map)
        {
        }

        /// <summary>
        /// 当前允许的权限层级。
        ///
        /// 开发者级 = "能直接改世界状态"（改天气、触发事件）。两条路都能通到它：
        ///   · 开了原版开发者模式（调试时不想再翻一遍设置）；
        ///   · 在模组设置里勾了「允许开发者级操作」。
        ///
        /// 后者是给正常玩家的：**这类操作强，但"强"应该是玩家的选择，
        /// 不该是"你有没有按开调试菜单"的副产品。**
        /// </summary>
        public static RuleTier AllowedTier
        {
            get
            {
                if (Prefs.DevMode) return RuleTier.Developer;

                var settings = RuleCoreMod.Settings;
                return settings != null && settings.allowDeveloperOps
                    ? RuleTier.Developer
                    : RuleTier.Player;
            }
        }

        /// <summary>取（或建）某个主体在某条规则下的运行态。</summary>
        public RuleRuntimeState StateFor(Rule rule, Pawn actor)
        {
            Dictionary<int, RuleRuntimeState> bySubject;
            if (!states.TryGetValue(rule.id, out bySubject))
            {
                bySubject = new Dictionary<int, RuleRuntimeState>();
                states.Add(rule.id, bySubject);
            }

            int subjectId = actor != null ? actor.thingIDNumber : 0;

            RuleRuntimeState state;
            if (!bySubject.TryGetValue(subjectId, out state))
            {
                state = new RuleRuntimeState();
                bySubject.Add(subjectId, state);
            }

            return state;
        }

        /// <summary>规则被删掉后清掉它的运行态，避免字典随编辑次数无限长大。</summary>
        public void ForgetRule(string ruleId)
        {
            if (!string.IsNullOrEmpty(ruleId))
            {
                states.Remove(ruleId);
            }
        }

        public override void MapComponentTick()
        {
            int tick = Find.TickManager.TicksGame;
            if (tick % SampleIntervalTicks != 0)
            {
                return;
            }

            EvaluateAll(tick);
        }

        private void EvaluateAll(int tick)
        {
            var rules = RuleLibrary.All;
            var vocabulary = RuleVocabularyCatalog.Current;
            RuleTier allowed = AllowedTier;

            for (int i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                if (rule == null || !rule.enabled)
                {
                    continue;
                }

                var subjectInfo = vocabulary.Subject(rule.subjectKey);

                // 没有绑定方式（或绑定器没实现）：整图评估一次，actor 为空。
                if (subjectInfo == null || subjectInfo.binder == null)
                {
                    var context = new RuleEvalContext
                    {
                        Rule = rule, Map = map, Tick = tick, AllowedTier = allowed
                    };

                    runner.EvaluateAndEmit(rule, new RuleEvalHost(context), vocabulary,
                        StateFor(rule, null), tick, allowed);
                    continue;
                }

                var bindContext = new RuleEvalContext
                {
                    Rule = rule, Map = map, Tick = tick, AllowedTier = allowed,
                    SubjectRef = rule.subjectRef
                };

                subjects.Clear();
                subjectInfo.binder(new RuleEvalHost(bindContext), subjects);

                for (int s = 0; s < subjects.Count; s++)
                {
                    var subject = subjects[s];
                    var actor = RuleEvalHost.PawnOf(subject);
                    if (actor == null) continue;

                    var context = new RuleEvalContext
                    {
                        Rule = rule, Map = map, Actor = actor, Tick = tick, AllowedTier = allowed
                    };

                    runner.EvaluateAndEmit(rule, new RuleEvalHost(context), vocabulary,
                        StateFor(rule, actor), tick, allowed);
                }
            }
        }
    }
}
