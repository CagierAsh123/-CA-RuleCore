using System.Collections.Generic;
using Verse;
using RuleCore.Core;

namespace RuleCore
{
    /// <summary>
    /// Core 与游戏之间的**翻译层**：Core 在整条链路上只传 <see cref="RuleValue"/>，
    /// 这里负责把它和 Pawn / Map / Thing / Room 互相翻译。
    ///
    /// <b>为什么值是"不透明把手 + 种类"而不是直接存对象</b>：
    /// Core 必须是纯 BCL 才能脱离游戏单测。所以实体值里那个 <c>object</c> 对 Core 是黑盒，
    /// 只有本类知道它其实是个 Pawn。
    ///
    /// 一次求值建一个实例、持有上下文。**刻意不缓存跨 tick**：
    /// 上下文里的 Actor 每个采样点都可能不同，缓存等于把上一个主体绑到这一轮上。
    /// </summary>
    public sealed class RuleEvalHost : IRuleEvalHost
    {
        private readonly RuleEvalContext context;

        private RuleValue subject = RuleValue.None;
        private RuleValue element = RuleValue.None;

        /// <summary>枚举用的一次性缓冲。每次借用都清空，避免把上一次的残留带进来。</summary>
        private readonly List<RuleValue> scratch = new List<RuleValue>();

        public RuleEvalHost(RuleEvalContext context)
        {
            this.context = context;
            SetSubject(context != null ? context.Actor : null);
        }

        public RuleEvalContext Context
        {
            get { return context; }
        }

        // ── 翻译 ──────────────────────────────────────────────────────

        public static RuleValue Entity(object handle, RuleEntityKind kind)
        {
            return handle == null ? RuleValue.None : RuleValue.OfEntity(kind, handle);
        }

        public static Map MapOf(RuleValue value)
        {
            return value.AsHandle as Map;
        }

        public static Pawn PawnOf(RuleValue value)
        {
            return value.AsHandle as Pawn;
        }

        public static Thing ThingOf(RuleValue value)
        {
            return value.AsHandle as Thing;
        }

        public static Room RoomOf(RuleValue value)
        {
            return value.AsHandle as Room;
        }

        public static Def DefOf(RuleValue value)
        {
            return value.AsHandle as Def;
        }

        // ── IRuleEvalHost ─────────────────────────────────────────────

        public RuleValue Subject
        {
            get { return subject; }
        }

        public RuleValue Element
        {
            get { return element; }
            set { element = value; }
        }

        public RuleValue Map
        {
            get { return Entity(context != null ? context.Map : null, RuleEntityKind.Map); }
        }

        /// <summary>
        /// 主体绑定的参数。**从上下文读，而不是从宿主的状态读**——
        /// 绑定器是在"还没定下主体"的时刻跑的，那时宿主里根本没有主体可言。
        /// </summary>
        public string SubjectRef
        {
            get { return context != null ? context.SubjectRef : null; }
        }

        /// <summary>把本主体绑到一个具体的 pawn 上。引擎按主体拆分求值时用它。</summary>
        public void SetSubject(Pawn pawn)
        {
            subject = Entity(pawn, RuleEntityKind.Pawn);
        }

        public bool TryRoot(RuleRootKind kind, RuleValue literal, out RuleValue value,
            out string reasonCode, out string reason)
        {
            switch (kind)
            {
                case RuleRootKind.Literal:
                    value = literal;
                    return Done(out reasonCode, out reason);

                case RuleRootKind.Subject:
                    if (subject.IsMissing)
                    {
                        value = RuleValue.None;
                        reasonCode = "root.no_subject";
                        reason = "这条规则没有主体绑定方式，所以没有「本主体」可读。";
                        return false;
                    }
                    value = subject;
                    return Done(out reasonCode, out reason);

                case RuleRootKind.Element:
                    if (element.IsMissing)
                    {
                        value = RuleValue.None;
                        reasonCode = "root.no_element";
                        reason = "这里不是筛选器内部，没有「当前元素」。";
                        return false;
                    }
                    value = element;
                    return Done(out reasonCode, out reason);

                case RuleRootKind.Map:
                    value = Map;
                    if (value.IsMissing)
                    {
                        reasonCode = "root.no_map";
                        reason = "这次求值没有地图。";
                        return false;
                    }
                    return Done(out reasonCode, out reason);

                case RuleRootKind.Colonists:
                    // 老数据里的「全部自由殖民者」。它等于 PawnGroup + freeColonists，
                    // 留着这个分支是为了让已经存下来的规则继续能跑。
                    return CollectGroup("freeColonists", out value, out reasonCode, out reason);

                case RuleRootKind.PawnGroup:
                    // **一群同类实体**：是哪一群写在 literal 里（Enum 值，键就是绑定表的 key）。
                    //
                    // 复用**绑定器本体**，而不是在这里另写一份"地图上所有囚犯"——
                    // 两份判据迟早会漂。于是"绑定表加一行"就自动多出一个根。
                    return CollectGroup(literal.IsMissing ? null : literal.AsKey,
                        out value, out reasonCode, out reason);

                case RuleRootKind.AllMaps:
                {
                    scratch.Clear();
                    var maps = Find.Maps;
                    if (maps != null)
                    {
                        for (int i = 0; i < maps.Count; i++)
                        {
                            if (maps[i] != null)
                            {
                                scratch.Add(RuleValue.OfEntity(RuleEntityKind.Map, maps[i]));
                            }
                        }
                    }

                    value = RuleValue.OfSet(RuleEntityKind.Map, new List<RuleValue>(scratch));
                    return Done(out reasonCode, out reason);
                }

                default:
                    value = RuleValue.None;
                    reasonCode = "root.unknown";
                    reason = "认不出的根：" + kind;
                    return false;
            }
        }

        /// <summary>
        /// 一个"一群"的根：跑那一种主体绑定的绑定器，把结果当成集合。
        ///
        /// **复用绑定器本体**（不是在这里重写一份判据）—— 于是"绑定表加一行"
        /// 自动在根列表里多出一个可用的根，两处不会漂。
        /// </summary>
        private bool CollectGroup(string groupKey, out RuleValue value,
            out string reasonCode, out string reason)
        {
            value = RuleValue.None;

            if (string.IsNullOrEmpty(groupKey))
            {
                reasonCode = "root.no_group";
                reason = "这个「一群」没有说清是哪一群。";
                return false;
            }

            var info = RuleVocabularyCatalog.Current.Subject(groupKey);
            if (info == null || info.binder == null)
            {
                reasonCode = "root.unknown_group";
                reason = "词表里没有「" + groupKey + "」这个群体。";
                return false;
            }

            if (context != null && context.Map == null)
            {
                reasonCode = "root.no_map";
                reason = "这次求值没有地图，收不出「" + groupKey + "」。";
                return false;
            }

            scratch.Clear();
            info.binder(this, scratch);

            // 复制的元素种类由绑定自己声明——不假设它一定是小人。
            value = RuleValue.OfSet(info.entityKind, new List<RuleValue>(scratch));
            return Done(out reasonCode, out reason);
        }

        public bool TryReduce(RuleValue set, RuleReduceKind kind, out RuleValue value,
            out string reasonCode, out string reason)
        {
            value = RuleValue.None;

            switch (kind)
            {
                case RuleReduceKind.Nearest:
                {
                    var origin = PawnOf(subject);
                    if (origin == null)
                    {
                        reasonCode = "reduce.no_origin";
                        reason = "「最近」需要一个主体来算距离，但这次求值没有主体。";
                        return false;
                    }

                    // 确定性：并列时取下标更小的那个。**不用随机**——
                    // 同一个世界状态必须解析出同一个目标，否则读档、重放、排障全都会漂。
                    int bestIndex = -1;
                    int bestDistance = int.MaxValue;

                    for (int i = 0; i < set.Count; i++)
                    {
                        var candidate = set.AsItems[i];
                        int distance = DistanceSquared(origin, candidate);
                        if (distance < 0) continue;
                        if (distance < bestDistance)
                        {
                            bestDistance = distance;
                            bestIndex = i;
                        }
                    }

                    if (bestIndex < 0)
                    {
                        reasonCode = "reduce.no_reachable";
                        reason = "这一组里没有一个能算出距离的目标。";
                        return false;
                    }

                    value = set.AsItems[bestIndex];
                    return Done(out reasonCode, out reason);
                }

                default:
                    reasonCode = "reduce.unsupported";
                    reason = "这个归约还没实现：" + kind;
                    return false;
            }
        }

        private static int DistanceSquared(Pawn origin, RuleValue candidate)
        {
            var thing = ThingOf(candidate);
            if (thing != null && thing.Spawned)
            {
                return (thing.Position - origin.Position).LengthHorizontalSquared;
            }

            var cell = candidate.AsCell;
            if (cell.IsValid)
            {
                return (new IntVec3(cell.x, 0, cell.z) - origin.Position).LengthHorizontalSquared;
            }

            return -1;
        }

        private static bool Done(out string reasonCode, out string reason)
        {
            reasonCode = null;
            reason = null;
            return true;
        }
    }
}
