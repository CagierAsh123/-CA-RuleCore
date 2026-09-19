using System.Collections.Generic;
using Verse;
using RuleCore.Core;

namespace RuleCore
{
    /// <summary>
    /// 规则的统一出处 —— **引擎与面板都只看这里，不看 Def、也不看设置**。
    ///
    /// 两个来源，同一个列表：
    ///   · 模组规则：从 <see cref="DefDatabase{T}"/> 里的规则包收集，载入后缓存；
    ///   · 玩家规则：住在 <see cref="RuleCoreSettings"/> 里，跟着全局配置 Scribe。
    ///
    /// <see cref="Version"/> 是给 UI 用的变更计数。
    /// </summary>
    public static class RuleLibrary
    {
        private static readonly List<Rule> modRules = new List<Rule>();
        private static readonly List<Rule> allRules = new List<Rule>();
        private static readonly List<Rule> noPlayerRules = new List<Rule>();
        private static bool modRulesBuilt;

        /// <summary>集合发生任何变化时递增。UI 缓存据此失效。</summary>
        public static int Version { get; private set; }

        public static IReadOnlyList<Rule> ModRules
        {
            get
            {
                EnsureModRules();
                return modRules;
            }
        }

        /// <summary>玩家规则。直接引用设置里的列表——改动立刻生效。</summary>
        public static List<Rule> PlayerRules
        {
            get
            {
                var settings = RuleCoreMod.Settings;
                return settings != null ? settings.playerRules : noPlayerRules;
            }
        }

        /// <summary>模组规则 + 玩家规则。引擎每轮采样遍历的就是它。</summary>
        public static IReadOnlyList<Rule> All
        {
            get
            {
                EnsureModRules();

                allRules.Clear();
                for (int i = 0; i < modRules.Count; i++)
                {
                    allRules.Add(modRules[i]);
                }

                var player = PlayerRules;
                for (int i = 0; i < player.Count; i++)
                {
                    if (player[i] != null)
                    {
                        allRules.Add(player[i]);
                    }
                }

                return allRules;
            }
        }

        /// <summary>Def 加载完之后调一次。玩家规则变化不需要重建缓存。</summary>
        public static void RebuildModRules()
        {
            modRules.Clear();

            // 词表是惰性的，这里主动建一次：Def 加载期可能早于任何一次访问，
            // 那时 Rule.Resolve() 拿到的是一张空表，层级与边沿语义都推不出来。
            RuleVocabularyCatalog.EnsureBuilt();

            var packs = DefDatabase<RulePackDef>.AllDefsListForReading;
            var problems = new List<string>();

            for (int i = 0; i < packs.Count; i++)
            {
                var pack = packs[i];
                if (pack == null || pack.rules == null) continue;

                for (int k = 0; k < pack.rules.Count; k++)
                {
                    var rule = pack.rules[k];
                    if (rule == null) continue;

                    // 词表就绪之后重新推一遍，并做一次完整校验。
                    // 载入期的 Def.ConfigErrors 可能跑在词表建起来之前，那里只能做形状检查，
                    // 类型与键的检查必须在这里补上——否则"XML 写错了"要等到跑起来才知道。
                    rule.Resolve();

                    problems.Clear();
                    rule.CollectConfigErrors(problems);
                    for (int p = 0; p < problems.Count; p++)
                    {
                        RuleLog.Error(rule.id, "Library", RuleEvalStatus.Error,
                            "rule.config_error", null, problems[p]);
                    }

                    modRules.Add(rule);
                }
            }

            modRulesBuilt = true;
            Version++;

            RuleLog.Info(null, "Library", "loaded", "mod_rules", null,
                "内置规则 " + modRules.Count + " 条，来自 " + packs.Count + " 个规则包；"
                + "玩家规则 " + PlayerRules.Count + " 条");
        }

        public static void NotifyChanged()
        {
            Version++;
        }

        public static Rule Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            EnsureModRules();
            for (int i = 0; i < modRules.Count; i++)
            {
                if (modRules[i].id == id) return modRules[i];
            }

            var player = PlayerRules;
            for (int i = 0; i < player.Count; i++)
            {
                if (player[i] != null && player[i].id == id) return player[i];
            }

            return null;
        }

        // ── 玩家规则的增删 ────────────────────────────────────────────

        /// <summary>
        /// 新建一条玩家规则。给的是"无害骨架"：不绑定主体、只写一条日志、周期五秒。
        /// 它不读世界状态也不改世界状态，所以怎么点都不会出事。
        /// </summary>
        public static Rule CreatePlayerRule(string label)
        {
            string resolvedLabel = label;
            if (string.IsNullOrEmpty(resolvedLabel))
            {
                resolvedLabel = "RuleCore.NewRuleLabel".Translate();
            }

            var rule = new Rule
            {
                id = NewPlayerRuleId(),
                label = resolvedLabel,
                description = "RuleCore.NewRuleDesc".Translate(),
                enabled = true,
                Origin = RuleOrigin.Player,
                cooldownTicks = 5000
            };

            rule.operate.Add(RuleClause.Of(
                new RulePath { rootKind = RuleRootKind.Map },
                "op.log",
                RuleOperand.OfText("RuleCore.NewRuleActionText".Translate())));

            rule.Resolve();

            PlayerRules.Add(rule);
            SaveSettings();
            Version++;
            return rule;
        }

        // ── 编辑期：改动的归一化与落盘 ────────────────────────────────

        /// <summary>延迟落盘的合并窗口（秒）。只在面板开着时由 <see cref="TickAutosave"/> 驱动。</summary>
        private const float AutosaveDelaySeconds = 1.0f;

        private static bool pendingSave;
        private static float dirtySince = -1f;

        /// <summary>真的有改动还躺在内存里没落盘。面板据此显示提示。</summary>
        public static bool HasPendingEdits
        {
            get { return pendingSave; }
        }

        /// <summary>
        /// 结构性改动：换谓词、增删节点、换根。
        ///
        /// 立刻归一化 + 立刻落盘——这类改动是离散的，没有"打一半"的中间态，
        /// 而且换谓词会改变权限层级与边沿语义，必须马上重算。
        /// 归一化（重算 EffectiveTier / HasEdgeDetect）也只在这里做一次。
        /// </summary>
        public static void NotifyStructureChanged(Rule rule)
        {
            if (rule != null)
            {
                rule.Resolve();
            }

            Version++;
            SaveSettings();
            pendingSave = false;
            dirtySince = -1f;
        }

        /// <summary>
        /// 值改动：文本、数字、开关。只打脏标记，等防抖窗口合并写一次。
        /// 刻意**不动 <see cref="Version"/>**：规则列表显示的是操作条数之类的结构信息，
        /// 值改动不影响它，每敲一个字母就让列表重建是白费。
        /// </summary>
        public static void NotifyValueChanged(Rule rule)
        {
            if (rule == null) return;
            pendingSave = true;
        }

        /// <summary>
        /// 由面板每帧驱动。用**真实时间**而不是游戏 tick——
        /// 编辑规则时游戏多半是暂停的，用 tick 算的话防抖窗口永远不会到期。
        /// </summary>
        public static void TickAutosave(float realtimeSeconds)
        {
            if (!pendingSave)
            {
                dirtySince = -1f;
                return;
            }

            if (dirtySince < 0f)
            {
                dirtySince = realtimeSeconds;
                return;
            }

            if (realtimeSeconds - dirtySince < AutosaveDelaySeconds)
            {
                return;
            }

            FlushPendingEdits();
        }

        /// <summary>立刻落盘。面板关闭、模组设置保存、玩家手动点保存都走这里。</summary>
        public static void FlushPendingEdits()
        {
            if (!pendingSave) return;

            SaveSettings();
            pendingSave = false;
            dirtySince = -1f;
        }
        public static bool RemovePlayerRule(string id)
        {
            var player = PlayerRules;
            for (int i = 0; i < player.Count; i++)
            {
                if (player[i] != null && player[i].id == id)
                {
                    player.RemoveAt(i);
                    SaveSettings();
                    Version++;
                    return true;
                }
            }
            return false;
        }

        public static bool SetPlayerRuleEnabled(string id, bool enabled)
        {
            var rule = Find(id);
            if (rule == null || !rule.IsPlayerRule) return false;

            rule.enabled = enabled;
            SaveSettings();
            Version++;
            return true;
        }

        private static string NewPlayerRuleId()
        {
            return "player:" + System.Guid.NewGuid().ToString("N").Substring(0, 12);
        }

        private static void SaveSettings()
        {
            var settings = RuleCoreMod.Settings;
            if (settings != null)
            {
                settings.Write();
            }
        }

        private static void EnsureModRules()
        {
            if (modRulesBuilt) return;
            RebuildModRules();
        }
    }
}
