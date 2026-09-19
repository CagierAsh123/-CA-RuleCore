using System.Collections.Generic;
using Verse;
using RimWorld;
using RuleCore.Core;

namespace RuleCore
{
    /// <summary>
    /// **RimWorld 事实 → 规则语言的问句** 的唯一翻译处。
    ///
    /// 词表里那些"囚犯 / 奴隶 / 殖民者 / 机械族"的问句全靠这里给答案，
    /// 而编辑器"机械族身上不列饱食度"的那个判断也全靠这里给能力位。
    /// 把它们集中在一个文件里，是因为它们都是**游戏知识**：
    /// 分散在几十行属性 reader 里的话，改一个原版行为就要到处找。
    ///
    /// 全部是**只读**的：这里不改变世界，只回答"他是谁、他有什么"。
    /// </summary>
    public static class RulePawnFacts
    {
        // ══ 能力位：编辑器"哪些属性能挂上去"的判据 ══════════════════════

        /// <summary>
        /// 一个实体身上具备什么。
        ///
        /// **拿不到具体的东西时返回 <see cref="RuleCapability.All"/>** ——
        /// 这是刻意的"宁可多给，不可少给"：不知道主体是什么的时候，
        /// 把有用的选项藏起来比多摆几个更糟（玩家会以为这个词表没有那个功能）。
        /// </summary>
        public static RuleCapability CapabilitiesOf(object handle)
        {
            var pawn = handle as Pawn;
            if (pawn != null) return OfPawn(pawn);

            if (handle is Map) return RuleCapability.Map;
            if (handle is Room) return RuleCapability.Room;
            if (handle is Thing) return RuleCapability.Thing;

            return RuleCapability.All;
        }

        public static RuleCapability OfPawn(Pawn pawn)
        {
            if (pawn == null) return RuleCapability.All;

            var c = RuleCapability.Pawn;

            var race = pawn.RaceProps;
            if (race != null)
            {
                if (race.Humanlike) c |= RuleCapability.Humanlike;
                if (race.IsMechanoid) c |= RuleCapability.Mechanoid;
                if (race.Animal) c |= RuleCapability.Animal;
                if (race.IsFlesh) c |= RuleCapability.Biological;
            }

            // 需求逐条问，而不是"有 needs 就算有需求"——
            // 机械族也有 needs 对象，里面只有 energy。这就是这个枚举存在的理由。
            var needs = pawn.needs;
            if (needs != null)
            {
                if (needs.mood != null) c |= RuleCapability.NeedMood;
                if (needs.food != null) c |= RuleCapability.NeedFood;
                if (needs.rest != null) c |= RuleCapability.NeedRest;
                if (needs.energy != null) c |= RuleCapability.NeedEnergy;
                if (needs.joy != null) c |= RuleCapability.NeedJoy;
                if (needs.comfort != null) c |= RuleCapability.NeedComfort;
                if (needs.beauty != null) c |= RuleCapability.NeedBeauty;
            }

            if (pawn.apparel != null) c |= RuleCapability.Apparel;
            if (pawn.equipment != null) c |= RuleCapability.Equipment;
            if (pawn.inventory != null) c |= RuleCapability.Inventory;
            if (pawn.ownership != null) c |= RuleCapability.Bed;
            if (pawn.drafter != null) c |= RuleCapability.CanDraft;
            if (pawn.skills != null) c |= RuleCapability.Skills;
            if (pawn.ideo != null) c |= RuleCapability.Ideo;
            if (pawn.ageTracker != null) c |= RuleCapability.Age;

            if (pawn.IsColonist) c |= RuleCapability.Colonist;
            if (pawn.IsPrisoner) c |= RuleCapability.Prisoner;
            if (pawn.IsSlave) c |= RuleCapability.Slave;

            // 访客：有人接待他，但既不是囚犯也不是奴隶。
            // 不用 GuestStatus——那个字段对"来参观的商队"和"来投降的逃兵"是同一个值。
            if (pawn.HostFaction != null && !pawn.IsPrisoner && !pawn.IsSlave)
            {
                c |= RuleCapability.Guest;
            }

            var faction = pawn.Faction;
            if (faction != null && faction != Faction.OfPlayer && faction.HostileTo(Faction.OfPlayer))
            {
                c |= RuleCapability.Hostile;
            }

            // 野生动物。判据和「本图野生动物」集合、「标记狩猎」**共用一份**
            // （RuleMapFacts.IsWildAnimal）——三处各写各的迟早会漂，
            // 而漂开的表现是"集合收得出来但动词被能力过滤藏掉了"。
            if (RuleMapFacts.IsWildAnimal(pawn)) c |= RuleCapability.Wild;

            return c;
        }

        // ══ 身份：一个枚举回答"他是谁" ═══════════════════════════════

        /// <summary>
        /// 「身份」的取值键。**互斥**，从上往下第一个成立的就是答案。
        ///
        /// 顺序是设计过的一部分，不是随手排的：
        /// 一个"被俘的机械族"应该显示成机械族而不是囚犯（原版也抓不了机械族），
        /// 一个"殖民地奴隶"应该显示成奴隶而不是殖民者（它确实是殖民地的一员，
        /// 但玩家问"他是谁"时想知道的是前者）。
        /// </summary>
        public static string CategoryKeyOf(Pawn pawn)
        {
            if (pawn == null) return "other";

            var race = pawn.RaceProps;

            // 身份优先于族群：殖民地的囚犯/奴隶就是囚犯/奴隶，
            // 即便他是个异象亚人也一样——他此刻的处境比他的种族更该被回答。
            if (pawn.IsPrisonerOfColony) return "prisoner";
            if (pawn.IsSlaveOfColony) return "slave";
            if (pawn.IsColonist) return "colonist";

            if (race != null && race.IsMechanoid) return "mechanoid";
            if (pawn.IsEntity) return "entity";

            // 亚人（食尸鬼、变形体）在"是不是动物"之前判：它们原版也不当动物算。
            if (pawn.IsSubhuman) return "subhuman";

            if (race != null && race.Animal) return "animal";

            if (race != null && race.Humanlike)
            {
                if (pawn.Faction == null) return "wildman";

                if (pawn.Faction != Faction.OfPlayer
                    && pawn.Faction.HostileTo(Faction.OfPlayer))
                {
                    return "hostile";
                }

                if (pawn.HostFaction != null) return "guest";
            }

            return "other";
        }

        // ══ 枚举取值域 ════════════════════════════════════════════════

        /// <summary>「身份」能取哪些值。**顺序即界面顺序**。</summary>
        public static readonly RuleEnumOption[] CategoryOptions =
        {
            new RuleEnumOption("colonist", "RuleCore.Enum.PawnCategory.colonist"),
            new RuleEnumOption("slave", "RuleCore.Enum.PawnCategory.slave"),
            new RuleEnumOption("prisoner", "RuleCore.Enum.PawnCategory.prisoner"),
            new RuleEnumOption("guest", "RuleCore.Enum.PawnCategory.guest"),
            new RuleEnumOption("hostile", "RuleCore.Enum.PawnCategory.hostile"),
            new RuleEnumOption("mechanoid", "RuleCore.Enum.PawnCategory.mechanoid"),
            new RuleEnumOption("animal", "RuleCore.Enum.PawnCategory.animal"),
            new RuleEnumOption("entity", "RuleCore.Enum.PawnCategory.entity"),
            new RuleEnumOption("subhuman", "RuleCore.Enum.PawnCategory.subhuman"),
            new RuleEnumOption("wildman", "RuleCore.Enum.PawnCategory.wildman"),
            new RuleEnumOption("other", "RuleCore.Enum.PawnCategory.other")
        };

        /// <summary>能力位的显示名键。编辑器拿它把"他缺：饱食需求"说出来。</summary>
        public static string CapabilityLabelKey(RuleCapability bit)
        {
            return "RuleCore.Cap." + bit;
        }

        /// <summary>
        /// 身份的显示名键。**只给界面用** ——
        /// 身份不是规则语言里的一个词（它是主体绑定的属性，锁定了就没有"是不是"可问），
        /// 但界面上每一处出现名字的地方都该带上它："小明（殖民者）"。
        /// </summary>
        public static string CategoryLabelKey(string key)
        {
            return "RuleCore.Enum.PawnCategory." + (string.IsNullOrEmpty(key) ? "other" : key);
        }

        // ══ 按引用找人 ════════════════════════════════════════════════

        /// <summary>
        /// 按 <c>ThingID</c> 找一个人。
        ///
        /// **只在已生成的人里找**，不翻存档也不翻尸体：
        /// 一条指向死人的规则应当**收不出主体**（于是不触发），
        /// 而不是绑上一具尸体让"他能动"这种检测静默地不成立。
        /// </summary>
        public static Pawn FindById(string thingId)
        {
            if (string.IsNullOrEmpty(thingId)) return null;

            var maps = Find.Maps;
            if (maps == null) return null;

            for (int m = 0; m < maps.Count; m++)
            {
                var map = maps[m];
                if (map == null || map.mapPawns == null) continue;

                var pawns = map.mapPawns.AllPawnsSpawned;
                for (int i = 0; i < pawns.Count; i++)
                {
                    var pawn = pawns[i];
                    if (pawn != null && pawn.ThingID == thingId) return pawn;
                }
            }

            return null;
        }

        /// <summary>
        /// 指名绑定的**候选清单**：当前地图上"说得上是个人物"的东西。
        ///
        /// 只列人形与机械族，不列动物——两百头牦牛会把列表淹掉，
        /// 而"指名一头牦牛"是个真实但罕见的需求，靠手写 XML 也能做。
        /// </summary>
        public static void CollectNameable(IRuleEvalHost host, List<RuleValue> into)
        {
            if (into == null) return;

            var map = RuleEvalHost.MapOf(host != null ? host.Map : RuleValue.None);
            if (map == null || map.mapPawns == null) return;

            var pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                var pawn = pawns[i];
                if (pawn == null || pawn.Dead || pawn.Destroyed) continue;

                var race = pawn.RaceProps;
                if (race == null) continue;
                if (!race.Humanlike && !race.IsMechanoid) continue;

                into.Add(RuleValue.OfEntity(RuleEntityKind.Pawn, pawn));
            }
        }
    }
}
