using System.Collections.Generic;
using Verse;
using RimWorld;
using RuleCore.Core;

namespace RuleCore
{
    /// <summary>
    /// 地图层面要用到的**游戏知识**，集中在一处。
    ///
    /// 存在的理由和 <see cref="RulePawnFacts"/> / <see cref="RuleIncidentFacts"/> 一样：
    /// 散在词表的 delegate 里就变成"改一个原版行为要到处找"。
    /// </summary>
    public static class RuleMapFacts
    {
        // ══ 野生动物 / 狩猎 ═══════════════════════════════════════════

        /// <summary>
        /// **它是不是一头野生动物** —— 只看种类和归属，不看它此刻活着没有。
        ///
        /// 拆出这一层是因为需要它的地方有两处，而它们问的是**同一件事**：
        /// <list type="bullet">
        /// <item><see cref="Huntable"/>（集合与动词的判据）——再加"活着、在地图上"</item>
        /// <item><c>RulePawnFacts.OfPawn</c> 的 <see cref="RuleCapability.Wild"/> 能力位</item>
        /// </list>
        /// 能力位必须和集合的判据一致，否则会出现"集合收得出来，但编辑器里
        /// 这个动词被能力过滤藏掉了"。两处各写一份判据迟早会漂，所以只写一份。
        ///
        /// <b>为什么要求 <c>RaceProps.Animal</c> 而不是原版的 <c>AnimalOrWildMan</c>。</b>
        /// 原版的狩猎设计器也接受"野人"。这里刻意不收，因为"野人"是**人形**
        /// （<c>RaceProps.Animal</c> 为 false），收进来之后「本图野生动物」
        /// 就会包含没有 <see cref="RuleCapability.Animal"/> 位的成员，
        /// 而动词的能力要求是"每一位都得有"。**一个更窄但自洽的集合，
        /// 好过一个会让动词时隐时现的集合**——何况野人本来也不叫野生动物。
        /// </summary>
        public static bool IsWildAnimal(Pawn pawn)
        {
            if (pawn == null || pawn.RaceProps == null) return false;
            if (!pawn.RaceProps.Animal) return false;

            // 驯化过的有派系，而玩家派系的 def.humanlikeFaction 是 true，
            // 所以这一条把"我的动物"排除掉——原版也是这么排除的。
            if (pawn.Faction != null && pawn.Faction.def.humanlikeFaction) return false;

            return true;
        }

        /// <summary>
        /// 这只动物**能不能被狩猎** —— <see cref="IsWildAnimal"/> 再加上"活着、在地图上"。
        ///
        /// 判据**照抄** <c>Designator_Hunt.CanDesignateThing</c> 的结构，
        /// 因为这套东西里已经栽过三次同一个跟头（「脱下」出现在机械族身上、
        /// 「前往」的宾语预填成小人、日蚀投给地图）：**过滤只做了"类型对不对"，
        /// 没做"这个值此刻能不能用"**，于是玩家看到的东西选了必错。
        ///
        /// 保证两件事同时成立：
        ///   · 「本图野生动物」列出来的，玩家手动点狩猎也能标记；
        ///   · 「标记狩猎」对集合里的**每一个**成员都真的能成功。
        ///
        /// **不含"是否已经带了狩猎标记"**——那是幂等，归动词管，不归集合管
        /// （集合回答"图上有哪些野生动物"，不是"哪些还没被标记"）。
        /// </summary>
        public static bool Huntable(Pawn pawn)
        {
            if (pawn == null || pawn.Dead || pawn.Destroyed) return false;
            if (!pawn.Spawned) return false;
            return IsWildAnimal(pawn);
        }

        /// <summary>图上能狩猎的野生动物有几只。</summary>
        public static int WildAnimalCount(Map map)
        {
            if (map == null || map.mapPawns == null) return 0;

            int count = 0;
            var pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                if (Huntable(pawns[i])) count++;
            }
            return count;
        }

        /// <summary>给「标记狩猎」用：现在能不能对这只动物下手。不能就说清为什么。</summary>
        public static bool CanMarkHunt(Pawn pawn, out string code, out string reason)
        {
            code = null;
            reason = null;

            if (pawn == null)
            {
                code = "hunt.no_pawn";
                reason = "这不是一只动物。";
                return false;
            }

            if (pawn.Dead || pawn.Destroyed)
            {
                code = "hunt.dead";
                reason = "「" + pawn.LabelShort + "」已经死了或没了。";
                return false;
            }

            if (!pawn.Spawned || pawn.Map == null)
            {
                code = "hunt.not_spawned";
                reason = "「" + pawn.LabelShort + "」不在任何地图上，标记不了。";
                return false;
            }

            if (!Huntable(pawn))
            {
                code = "hunt.not_huntable";
                reason = "「" + pawn.LabelShort + "」不是能狩猎的野生动物"
                    + "（已经驯化、或者不是动物）。";
                return false;
            }

            return true;
        }

        /// <summary>
        /// 给它加上狩猎标记，**走原版的 designation 系统**。
        ///
        /// 返回 false 表示"这次没有改动"（已经标过了）——由调用方翻成
        /// <c>AlreadySatisfied</c>。幂等靠这个，不靠调用方自己判断。
        /// </summary>
        public static bool TryMarkHunt(Pawn pawn)
        {
            var map = pawn.Map;
            if (map == null || map.designationManager == null) return false;

            if (map.designationManager.DesignationOn(pawn, DesignationDefOf.Hunt) != null)
            {
                return false;
            }

            // 和原版 Designator_Hunt.DesignateThing 一模一样：先清掉这只动物身上
            // 其他标记，再加狩猎。不清的话"先标了屠宰、再标狩猎"会同时挂着两个，
            // 而原版点一次狩猎是会把屠宰顶掉的——**行为和手动点必须一致**。
            map.designationManager.RemoveAllDesignationsOn(pawn);
            map.designationManager.AddDesignation(new Designation(pawn, DesignationDefOf.Hunt));
            return true;
        }

        // ══ 地图状态（GameCondition）══════════════════════════════════

        /// <summary>
        /// 这张地图上现在生效的、<b>所有</b>该 Def 的状态，装进 <paramref name="into"/>。
        ///
        /// 用 <c>GetAllGameConditionsAffectingMap</c> 而不是只看本地那张表，
        /// 是因为**状态分两层**：心灵低语这类挂在<em>地图</em>上，
        /// 而日蚀 / 极光这类挂在<em>世界</em>上（原版 `GameConditionManager.Parent`）。
        /// 只看本地表的话，"清掉日蚀"会静默地什么都不做——
        /// 而那正是这个项目最不能接受的一种失败。
        /// </summary>
        public static void CollectActive(Map map, GameConditionDef def, List<GameCondition> into)
        {
            if (into == null) return;
            into.Clear();

            if (map == null || map.gameConditionManager == null || def == null) return;

            var all = new List<GameCondition>();
            map.gameConditionManager.GetAllGameConditionsAffectingMap(map, all);

            for (int i = 0; i < all.Count; i++)
            {
                if (all[i] == null || all[i].def != def) continue;
                into.Add(all[i]);
            }
        }

        /// <summary>这个状态此刻在不在图上生效。给 <c>argFilter</c> 用。</summary>
        public static bool IsActiveOn(Map map, GameConditionDef def)
        {
            var found = new List<GameCondition>();
            CollectActive(map, def, found);
            return found.Count > 0;
        }

        /// <summary>
        /// 给词表的 <c>argFilter</c> 用：参数是候选 Def。
        ///
        /// 和 <see cref="RuleIncidentFacts.TargetAvailableFilter"/> 一样，
        /// 过滤器只拿得到候选本身，拿不到"玩家在编辑哪条规则"，
        /// 所以用 <c>Find.CurrentMap</c> 当上下文——玩家正看着的那张图。
        /// </summary>
        public static bool ActiveOnCurrentMapFilter(object candidate)
        {
            return IsActiveOn(Find.CurrentMap, candidate as GameConditionDef);
        }

        /// <summary>
        /// 清掉图上所有该 Def 的状态。返回清掉了几个。
        ///
        /// **走 <c>GameCondition.End()</c>**，因为它才是原版的收尾流程：
        /// 它会发结束提示（`def.endMessage`），并且交给**拥有它的那个管理器**
        /// 去 `OnConditionEnd` 从表里摘掉。自己从 `ActiveConditions` 里
        /// `Remove` 会漏掉提示和 `Notify_GameConditionRemoved`。
        /// </summary>
        public static int EndAll(Map map, GameConditionDef def)
        {
            var found = new List<GameCondition>();
            CollectActive(map, def, found);

            for (int i = 0; i < found.Count; i++)
            {
                found[i].End();
            }

            return found.Count;
        }
    }
}
