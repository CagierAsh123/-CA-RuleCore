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

                    // **玩家的覆盖在这里生效。**
                    // XML 说 `enabled=true` 是**作者**的意图；玩家关掉过它才是当下的状态。
                    // 覆盖层是权威——否则"关掉内置规则"每次读档都会被 XML 顶回来。
                    rule.enabled = !IsDisabled(rule.id);

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

        // ── 玩家对「内置规则」的覆盖 ──────────────────────────────────
        //
        // 内置规则是模组带来的**定义**：不可编辑、不可删除。
        // 但"我不想让它跑 / 我不想在列表里看见它"是**玩家的偏好**，
        // 跟"它定义成什么样"完全是两件事——不该因为拿不到编辑权就连关都关不掉。
        //
        // 覆盖层住在全局配置里（和玩家规则一起），载入时套回去。
        // 「隐藏」**不删任何数据**，所以随时能恢复：那些定义在别人的模组里。

        private static readonly List<string> emptyIds = new List<string>();

        private static List<string> DisabledIds
        {
            get
            {
                var settings = RuleCoreMod.Settings;
                return settings != null ? settings.disabledRuleIds : emptyIds;
            }
        }

        private static List<string> HiddenIds
        {
            get
            {
                var settings = RuleCoreMod.Settings;
                return settings != null ? settings.hiddenRuleIds : emptyIds;
            }
        }

        public static bool IsDisabled(string id)
        {
            return !string.IsNullOrEmpty(id) && DisabledIds.Contains(id);
        }

        /// <summary>被玩家从列表里移除的规则：它不跑，也不显示。</summary>
        public static bool IsHidden(string id)
        {
            return !string.IsNullOrEmpty(id) && HiddenIds.Contains(id);
        }

        /// <summary>
        /// 这条规则现在该不该跑。**引擎用它，而不是直接看 <c>rule.enabled</c>**——
        /// "关掉"是玩家覆盖，"隐藏"是干脆不加载，两个都得算。
        /// </summary>
        public static bool IsActive(Rule rule)
        {
            if (rule == null || !rule.enabled) return false;
            return !IsHidden(rule.id);
        }

        /// <summary>关掉/打开一条规则。**内置规则也能关**（写进覆盖层，不动它的定义）。</summary>
        public static bool SetDisabled(string id, bool disabled)
        {
            if (string.IsNullOrEmpty(id)) return false;

            var list = DisabledIds;
            bool changed = disabled ? Add(list, id) : list.Remove(id);
            if (!changed) return false;

            var rule = Find(id);
            if (rule != null) rule.enabled = !disabled;

            SaveSettings();
            Version++;
            return true;
        }

        /// <summary>从列表里移除 / 恢复一条规则。只对内置规则有意义。</summary>
        public static bool SetHidden(string id, bool hidden)
        {
            if (string.IsNullOrEmpty(id)) return false;

            var list = HiddenIds;
            bool changed = hidden ? Add(list, id) : list.Remove(id);
            if (!changed) return false;

            SaveSettings();
            Version++;
            return true;
        }

        /// <summary>当前被移除的内置规则条数。面板据此决定要不要给"恢复"入口。</summary>
        public static int HiddenCount
        {
            get
            {
                var list = HiddenIds;
                int n = 0;

                for (int i = 0; i < list.Count; i++)
                {
                    for (int k = 0; k < modRules.Count; k++)
                    {
                        if (modRules[k] != null && modRules[k].id == list[i]) { n++; break; }
                    }
                }

                return n;
            }
        }

        /// <summary>把所有被移除的内置规则放回列表。</summary>
        public static bool RestoreHidden()
        {
            var list = HiddenIds;
            if (list.Count == 0) return false;

            list.Clear();
            SaveSettings();
            Version++;
            return true;
        }

        private static bool Add(List<string> list, string id)
        {
            if (list.Contains(id)) return false;
            list.Add(id);
            return true;
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

        /// <summary>
        /// 关掉/打开一条规则。**转发到 <see cref="SetDisabled"/>**，
        /// 不再自己改 <c>rule.enabled</c>。
        ///
        /// 这是踩过的坑的收口：留两个"能改 enabled"的入口，早晚会有人走错那条——
        /// 直接改字段对内置规则是**没用的**，下次读档会被 XML 里的 enabled 顶回来。
        /// 保留这个名字只是不打断已有调用方。
        /// </summary>
        public static bool SetPlayerRuleEnabled(string id, bool enabled)
        {
            return SetDisabled(id, !enabled);
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
