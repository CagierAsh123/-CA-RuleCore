using System.Collections.Generic;
using System.Linq;
using Verse;
using RimWorld;
using RimWorld.Planet;
using RuleCore.Core;

namespace RuleCore
{
    /// <summary>
    /// 「触发事件」这条动词要用到的**游戏知识**，集中在一处。
    ///
    /// 存在的理由和 <see cref="RulePawnFacts"/> 一样：这些全是 RimWorld 的事实，
    /// 散在词表的 delegate 里就变成"改一个原版行为要到处找"。
    /// </summary>
    public static class RuleIncidentFacts
    {
        // ══ 投给谁：地图优先，其次世界 ═══════════════════════════════

        /// <summary>
        /// 从"本图"发一个事件时，到底该把它投给谁。
        ///
        /// <b>这是踩过的坑。</b> 原版的事件带**目标标签**：袭击一类投给地图，
        /// 而**日蚀 / 太阳耀斑 / 极光的目标标签只有 <c>World</c>**——它们本来就是
        /// 全世界一起发生的。直接拿 <c>map</c> 去喂，`CanFireNow` 的**第一句**
        /// <c>TargetAllowed</c> 就挡下了，而玩家拿到的解释是"原版判断现在不能发生"，
        /// 完全看不出真正的原因。
        ///
        /// 所以：地图允许就投地图，否则投世界，两边都不允许才拒绝。
        /// </summary>
        public static bool TryResolveTarget(IncidentDef def, Map map,
            out IIncidentTarget target, out string note)
        {
            target = null;
            note = null;

            if (def == null) return false;

            if (map != null && def.TargetAllowed(map))
            {
                target = map;
                return true;
            }

            var world = Find.World;
            if (world != null && def.TargetAllowed(world))
            {
                target = world;
                // 说清楚，否则"我在本图上触发的事件怎么会影响别的地图"就成了新的困惑。
                note = "它是世界层面的事件，会同时影响所有地图";
                return true;
            }

            return false;
        }

        /// <summary>
        /// 这个事件现在能不能从这张地图（或世界）发出去。
        /// 编辑器用它把"投不出去"的候选从菜单里拿掉。
        /// </summary>
        public static bool TargetAvailable(IncidentDef def, Map map)
        {
            IIncidentTarget target;
            string note;
            return TryResolveTarget(def, map, out target, out note);
        }

        /// <summary>给词表的 <c>argFilter</c> 用：参数是候选 Def。</summary>
        public static bool TargetAvailableFilter(object candidate)
        {
            return TargetAvailable(candidate as IncidentDef, Find.CurrentMap);
        }

        // ══ 为什么现在不能发生 ═══════════════════════════════════════

        /// <summary>
        /// **尽量**说清原版为什么不让它发生。
        ///
        /// <b>放行与否始终以原版为准</b>——调用方先问 <c>IncidentWorker.CanFireNow</c>，
        /// 这个函数只在它说"不行"之后被调用来解释。所以它是
        /// `CanFireNow` 那串检查的一份**镜像**：能对的都用原版自己的方法或字段
        /// （<c>TargetAllowed</c> / <c>Worker.FiredTooRecently</c> /
        /// <c>difficulty.AllowedBy</c> …），镜像没覆盖到的情况返回 null，
        /// 由调用方回落成一句"原版没给出具体原因"——**不编一个**。
        /// </summary>
        public static string DescribeWhyNot(IncidentDef def, IncidentParms parms)
        {
            if (def == null) return null;
            if (parms == null || parms.target == null) return "没有可以承接事件的目标。";

            var target = parms.target;

            if (!def.TargetAllowed(target))
            {
                return "它不接受这个目标（targetTags 与目标对不上）。";
            }

            if (!parms.bypassStorytellerSettings)
            {
                int days = GenDate.DaysPassedSinceSettle;
                if (days < def.earliestDay)
                {
                    return "它最早要在开局第 " + def.earliestDay + " 天之后才发生，现在是第 "
                        + days + " 天。";
                }

                if (!Find.Storyteller.difficulty.AllowedBy(def.disabledWhen))
                {
                    return "当前难度把它**禁掉了**。";
                }

                if (def.category == IncidentCategoryDefOf.ThreatBig
                    && !Find.Storyteller.difficulty.allowBigThreats)
                {
                    return "当前难度里「允许大威胁」是关着的。";
                }
            }

            if (parms.points >= 0f && parms.points < def.minThreatPoints)
            {
                return "它要求威胁点数不低于 " + def.minThreatPoints.ToString("0")
                    + "，现在是 " + parms.points.ToString("0") + "。";
            }

            if (parms.points >= 0f && parms.points > def.maxThreatPoints)
            {
                return "它要求威胁点数不高于 " + def.maxThreatPoints.ToString("0")
                    + "，现在是 " + parms.points.ToString("0") + "。";
            }

            string biome = BiomeReason(def, target);
            if (biome != null) return biome;

            if (def.minPopulation > 0)
            {
                int have = PawnsFinder.AllMapsCaravansAndTravellingTransporters_Alive_FreeColonists
                    .Count();
                if (have < def.minPopulation)
                {
                    return "它要求自由殖民者不少于 " + def.minPopulation + " 个，现在有 "
                        + have + " 个。";
                }
            }

            // **最常见的那一条**：最近刚发生过。
            // 判据直接问原版（它还会连带检查 RefireCheckIncidents），
            // 天数只是拿来把话说清楚。
            if (def.Worker != null && def.Worker.FiredTooRecently(target))
            {
                float since = DaysSinceLastFire(def, target);
                string when = since >= 0f
                    ? "距上次发生只过了 " + since.ToString("0.0") + " 天"
                    : "它（或它的关联事件）最近刚发生过";

                return when + "，而它要求至少 " + def.minRefireDays + " 天。";
            }

            if (def.minGreatestPopulation > 0
                && Find.StoryWatcher != null
                && Find.StoryWatcher.statsRecord.greatestPopulation < def.minGreatestPopulation)
            {
                return "它要求历史最高人口达到 " + def.minGreatestPopulation + "。";
            }

            var map = target as Map;
            if (map != null && map.gameConditionManager != null)
            {
                var active = map.gameConditionManager.ActiveConditions;
                for (int i = 0; i < active.Count; i++)
                {
                    if (active[i] == null || active[i].def == null) continue;
                    if (!active[i].def.preventIncidents) continue;
                    return "地图上有「" + active[i].def.LabelCap + "」在生效，它**阻止一切事件**。";
                }
            }

            if (Find.GameEnder != null && Find.GameEnder.gameEnding
                && (def.category == IncidentCategoryDefOf.ThreatBig
                    || def.category == IncidentCategoryDefOf.ThreatSmall))
            {
                return "游戏已经进入终局，威胁类事件不再发生。";
            }

            // 镜像没覆盖到（mod 的 CanFireNowSub、异常内容、行星层……）。**不猜。**
            return null;
        }

        private static string BiomeReason(IncidentDef def, IIncidentTarget target)
        {
            if (target == null || !target.Tile.Valid) return null;
            if (def.allowedBiomes.NullOrEmpty() && def.disallowedBiomes.NullOrEmpty()) return null;
            if (Find.WorldGrid == null) return null;

            BiomeDef biome = Find.WorldGrid[target.Tile].PrimaryBiome;
            if (biome == null) return null;

            if (!def.allowedBiomes.NullOrEmpty() && !def.allowedBiomes.Contains(biome))
            {
                return "它不能在" + biome.LabelCap + "发生（allowedBiomes 里没有这个生物群系）。";
            }

            if (!def.disallowedBiomes.NullOrEmpty() && def.disallowedBiomes.Contains(biome))
            {
                return "它被" + biome.LabelCap + "生物群系排除了。";
            }

            return null;
        }

        /// <summary>距上次发生过了几天。没记录过返回 -1。</summary>
        private static float DaysSinceLastFire(IncidentDef def, IIncidentTarget target)
        {
            if (target == null || target.StoryState == null) return -1f;

            int last;
            if (!target.StoryState.lastFireTicks.TryGetValue(def, out last)) return -1f;

            int tick = Find.TickManager != null ? Find.TickManager.TicksGame : 0;
            return (float)(tick - last) / 60000f;
        }
    }
}
