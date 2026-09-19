using System.Collections.Generic;
using Verse;
using Verse.AI;
using RimWorld;
using RuleCore.Core;

namespace RuleCore
{
    /// <summary>
    /// 规则语言的词表内容 —— **「都能写什么」在这一个文件里**。
    ///
    /// 这是新模型最要紧的变化：**加一种玩法不需要加一个类**。
    /// 「室外温度大于10」不再需要一个 `RuleCondition_TemperatureAbove10`，
    /// 它由三行现成的东西组合出来：属性 `map.outdoorTemp` × 谓词 `compare.gt` × 字面量 `10`。
    /// 只有"真正新的现象"才需要在这里加一行。
    ///
    /// **惰性构建**（第一次访问时才建表），理由是加载顺序不保证：
    /// Def 加载会调 <see cref="Rule.Resolve"/>，而 Mod 构造函数里注册词表的时机与之无关。
    /// 惰性之后两边谁先来都对——表本身不读游戏状态，任何时候建都安全。
    ///
    /// 每个属性 / 谓词 / 绑定方式都声明**类型、单位、范围、边沿、权限、失败模式、所需能力**，
    /// 编辑器的全部智能都来自这些声明（按类型过滤下拉、强制归约、带单位显示、
    /// 以及"机械族身上不列饱食度"）。
    /// </summary>
    public static class RuleVocabularyCatalog
    {
        private static RuleVocabulary current;

        /// <summary>
        /// 别的 mod 通过 <see cref="RuleCoreApi"/> 登记进来的行。
        ///
        /// **攒着而不是直接写进表里**，是为了让加载顺序无关紧要：
        /// 别的 mod 可能在我们建表之前登记，也可能之后。
        /// 攒着的话两种情况都成立——建表时把它们一起加进去，建完再登记就重建一次表。
        /// </summary>
        private static readonly List<RulePropertyInfo> stagedProperties = new List<RulePropertyInfo>();
        private static readonly List<RuleVerbInfo> stagedVerbs = new List<RuleVerbInfo>();
        private static readonly List<RuleSubjectInfo> stagedSubjects = new List<RuleSubjectInfo>();

        public static RuleVocabulary Current
        {
            get
            {
                if (current == null)
                {
                    current = Build();
                }
                return current;
            }
        }

        /// <summary>
        /// 登记一行。表已经建过就顺手重建一次——表只有几十行，重建比"等下次启动"便宜得多。
        /// </summary>
        public static void Stage(RulePropertyInfo info)
        {
            if (info == null) return;
            stagedProperties.Add(info);
            Reload();
        }

        public static void Stage(RuleVerbInfo info)
        {
            if (info == null) return;
            stagedVerbs.Add(info);
            Reload();
        }

        public static void Stage(RuleSubjectInfo info)
        {
            if (info == null) return;
            stagedSubjects.Add(info);
            Reload();
        }

        /// <summary>重建词表（内置 + 已登记的）。</summary>
        public static void Reload()
        {
            current = Build();
            RuleLibrary.NotifyChanged();
        }

        /// <summary>
        /// 一行词表的**显示名**。查不到就回落成 key。
        ///
        /// **校验与报错消息里必须用它，不能用 <c>key</c>。**
        /// 玩家看到的是「触发事件」，不该是 `map.incident`——
        /// 内部键是给序列化用的，把它念给玩家听等于让他去猜那是什么。
        /// （编辑器自己走 <see cref="RuleEditorView.Label"/>，那个最终也落到这里。）
        /// </summary>
        public static string LabelOf(string labelKey, string fallback)
        {
            if (string.IsNullOrEmpty(labelKey)) return fallback;

            // 别的 mod 用 RuleCoreApi.Label 覆盖过的显示名优先。
            string overridden = RuleCoreApi.Override(labelKey);
            if (!string.IsNullOrEmpty(overridden)) return overridden;

            try
            {
                // **不能写成三元表达式**：`Translate()` 返回 `TaggedString`，
                // 和 `string` 之间在 C# 7.3 里没有公共类型（CS8957）。
                // 这个坑在本项目里踩过多次，见 代码Wiki/dotnet-build/RimWorld_Mod_编译指南.md。
                if (labelKey.CanTranslate())
                {
                    string translated = labelKey.Translate();
                    return translated;
                }
                return fallback;
            }
            catch (System.Exception)
            {
                return fallback;
            }
        }

        /// <summary>实体类型的显示名（「小人」「格子」）。</summary>
        public static string EntityKindName(RuleEntityKind kind)
        {
            return LabelOf("RuleCore.Enum.RuleEntityKind." + kind, kind.ToString());
        }

        /// <summary>值类型的显示名（「数值」「名字」）。</summary>
        public static string ValueKindName(RuleValueKind kind)
        {
            return LabelOf("RuleCore.Edit.Type." + kind, kind.ToString());
        }

        /// <summary>权限层级的显示名（「玩家级」「开发者级」）。</summary>
        public static string TierName(RuleTier tier)
        {
            return LabelOf("RuleCore.Enum.RuleTier." + tier, tier.ToString());
        }

        /// <summary>让 Mod 启动时就把表建起来，顺便把"描述与实现是否对得上"暴露在启动日志里。</summary>
        public static void EnsureBuilt()
        {
            var vocabulary = Current;
            RuleLog.Info(null, "Vocabulary", "loaded", "vocabulary.built", null,
                "词表：属性 " + vocabulary.PropertyCount
                + " · 谓词 " + vocabulary.VerbCount
                + " · 主体绑定 " + vocabulary.SubjectCount
                + (vocabulary.DuplicateKeys.Count > 0
                    ? " · **有重复键** " + vocabulary.DuplicateKeys.Count + " 个"
                    : string.Empty));
        }

        // ── 建表 ──────────────────────────────────────────────────────

        private static RuleVocabulary Build()
        {
            var v = new RuleVocabulary();

            AddSubjects(v);
            AddMapProperties(v);
            AddPawnProperties(v);
            AddThingProperties(v);
            AddDetects(v);
            AddOperates(v);

            // 别的 mod 登记的行加在**内置之后**：内置的顺序是设计过的（安全的排前面），
            // 而外来行放在后面既不打乱那个顺序，也让"出问题的是哪个 mod"一眼可查。
            for (int i = 0; i < stagedSubjects.Count; i++) v.Add(stagedSubjects[i]);
            for (int i = 0; i < stagedProperties.Count; i++) v.Add(stagedProperties[i]);
            for (int i = 0; i < stagedVerbs.Count; i++) v.Add(stagedVerbs[i]);

            return v;
        }

        /// <summary>当前词表里有多少行是别的 mod 登记的。</summary>
        public static int StagedCount
        {
            get { return stagedProperties.Count + stagedVerbs.Count + stagedSubjects.Count; }
        }

        // ── 主体绑定 ──────────────────────────────────────────────────

        private delegate bool PawnTest(Pawn pawn);

        /// <summary>
        /// 一个"收出地图上所有满足条件的人"的绑定器。
        ///
        /// 六个群体绑定只差一个判据，所以只写一遍。**刻意不缓存结果**：
        /// 主体集合是每轮采样重新问的，缓存等于把上一轮的人绑到这一轮上。
        /// </summary>
        private static RuleSubjectCollectHandler GroupOf(PawnTest test)
        {
            return delegate(IRuleEvalHost host, List<RuleValue> into)
            {
                var map = RuleEvalHost.MapOf(host.Map);
                if (map == null || map.mapPawns == null) return;

                var pawns = map.mapPawns.AllPawnsSpawned;
                for (int i = 0; i < pawns.Count; i++)
                {
                    var pawn = pawns[i];
                    if (pawn == null || pawn.Dead || pawn.Destroyed) continue;
                    if (!test(pawn)) continue;
                    into.Add(RuleValue.OfEntity(RuleEntityKind.Pawn, pawn));
                }
            };
        }

        private static void AddSubjects(RuleVocabulary v)
        {
            // 顺序即界面顺序：先"我的人"，再"我关着的人"，再"我的机械与动物"，
            // 然后才是外来者。玩家的心智模型就是这个顺序。
            v.Add(new RuleSubjectInfo
            {
                key = "freeColonists",
                entityKind = RuleEntityKind.Pawn,
                binder = GroupOf(delegate(Pawn p) { return p.IsFreeColonist; })
            });

            v.Add(new RuleSubjectInfo
            {
                key = "prisoners",
                entityKind = RuleEntityKind.Pawn,
                binder = GroupOf(delegate(Pawn p) { return p.IsPrisonerOfColony; })
            });

            v.Add(new RuleSubjectInfo
            {
                key = "slaves",
                entityKind = RuleEntityKind.Pawn,
                binder = GroupOf(delegate(Pawn p) { return p.IsSlaveOfColony; })
            });

            v.Add(new RuleSubjectInfo
            {
                key = "colonyMechs",
                entityKind = RuleEntityKind.Pawn,
                binder = GroupOf(delegate(Pawn p) { return p.IsColonyMech; })
            });

            v.Add(new RuleSubjectInfo
            {
                key = "colonyAnimals",
                entityKind = RuleEntityKind.Pawn,
                binder = GroupOf(delegate(Pawn p) { return p.IsColonyAnimal; })
            });

            v.Add(new RuleSubjectInfo
            {
                key = "guests",
                entityKind = RuleEntityKind.Pawn,
                binder = GroupOf(delegate(Pawn p)
                {
                    return p.HostFaction != null && !p.IsPrisoner && !p.IsSlave;
                })
            });

            // **指名到具体某个人**。
            //
            // 它和上面六个的区别不是"筛选条件更细"，而是**基数**：
            // 上面六个对每个成员各跑一遍，这个整条规则只跑一遍。
            // 所以它必须由规则给出 subjectRef——没有它就一个主体都收不出来，
            // 而"收不出主体"是**安静地什么都不发生**，是最难自己看出来的失效。
            // 载入期校验会因此报一条错（见 Rule.CollectConfigErrors）。
            v.Add(new RuleSubjectInfo
            {
                key = "namedPawn",
                entityKind = RuleEntityKind.Pawn,
                scope = RuleSubjectScope.Single,
                candidates = RulePawnFacts.CollectNameable,
                binder = delegate(IRuleEvalHost host, List<RuleValue> into)
                {
                    var pawn = RulePawnFacts.FindById(host != null ? host.SubjectRef : null);
                    if (pawn == null) return;
                    if (pawn.Dead || pawn.Destroyed || !pawn.Spawned) return;

                    into.Add(RuleValue.OfEntity(RuleEntityKind.Pawn, pawn));
                }
            });
        }

        // ── 属性：地图 ────────────────────────────────────────────────

        private static void AddMapProperties(RuleVocabulary v)
        {
            AddWeatherRate(v, "map.rainRate");
            AddWeatherRate(v, "map.snowRate");

            // 天空亮度 0~1：白天接近 1，夜里接近 0，日落时连续变化。
            // 它比一个"是不是夜晚"的布尔好用——「亮度 小于 0.3」顺带能表达"天快黑了"。
            v.Add(new RulePropertyInfo
            {
                key = "map.skyGlow", owner = RuleEntityKind.Map,
                requires = RuleCapability.Map,
                result = RuleValueKind.Number, min = 0f, max = 1f, decimals = 2,
                reader = delegate(IRuleEvalHost host, RuleValue owner, out RuleValue value,
                    out string code, out string reason)
                {
                    var map = RuleEvalHost.MapOf(owner);
                    if (map == null || map.skyManager == null)
                    {
                        return Fail(out value, out code, out reason, "prop.no_map", "没有地图，读不到天色。");
                    }
                    return Ok(RuleValue.OfNumber(map.skyManager.CurSkyGlow),
                        out value, out code, out reason);
                }
            });

            v.Add(new RulePropertyInfo
            {
                key = "map.wealth", owner = RuleEntityKind.Map,
                requires = RuleCapability.Map,
                result = RuleValueKind.Number, decimals = 0, min = 0f, max = 10000000f,
                reader = delegate(IRuleEvalHost host, RuleValue owner, out RuleValue value,
                    out string code, out string reason)
                {
                    var map = RuleEvalHost.MapOf(owner);
                    if (map == null || map.wealthWatcher == null)
                    {
                        return Fail(out value, out code, out reason, "prop.no_map", "没有地图，算不了财富。");
                    }
                    return Ok(RuleValue.OfNumber(map.wealthWatcher.WealthTotal),
                        out value, out code, out reason);
                }
            });

            v.Add(new RulePropertyInfo
            {
                key = "map.outdoorTemp", owner = RuleEntityKind.Map,
                requires = RuleCapability.Map,
                result = RuleValueKind.Number, unit = "℃", decimals = 1,
                min = -200f, max = 200f,
                reader = delegate(IRuleEvalHost host, RuleValue owner, out RuleValue value,
                    out string code, out string reason)
                {
                    var map = RuleEvalHost.MapOf(owner);
                    if (map == null || map.mapTemperature == null)
                    {
                        return Fail(out value, out code, out reason, "prop.no_map", "没有地图，读不到温度。");
                    }
                    return Ok(RuleValue.OfNumber(map.mapTemperature.OutdoorTemp),
                        out value, out code, out reason);
                }
            });

            v.Add(new RulePropertyInfo
            {
                key = "map.weather", owner = RuleEntityKind.Map,
                requires = RuleCapability.Map,
                result = RuleValueKind.Enum,
                // 取值来源：和信件类型同一个坑——不声明的话编辑器只能让玩家手填 defName，
                // 而天气是原版就有的完整清单（含所有 mod 加的）。
                enumDefType = typeof(WeatherDef),
                reader = delegate(IRuleEvalHost host, RuleValue owner, out RuleValue value,
                    out string code, out string reason)
                {
                    var map = RuleEvalHost.MapOf(owner);
                    if (map == null || map.weatherManager == null || map.weatherManager.curWeather == null)
                    {
                        return Fail(out value, out code, out reason, "prop.no_map", "没有地图，读不到天气。");
                    }
                    return Ok(RuleValue.OfKey(map.weatherManager.curWeather.defName),
                        out value, out code, out reason);
                }
            });

            AddMapPawnCount(v, "map.colonistCount", PawnTally.FreeColonists);
            AddMapPawnCount(v, "map.prisonerCount", PawnTally.Prisoners);
            AddMapPawnCount(v, "map.slaveCount", PawnTally.Slaves);
            AddMapPawnCount(v, "map.colonyMechCount", PawnTally.ColonyMechs);
            AddMapPawnCount(v, "map.colonyAnimalCount", PawnTally.ColonyAnimals);
            AddMapPawnCount(v, "map.pawnCount", PawnTally.AllSpawned);
        }

        private enum PawnTally
        {
            FreeColonists,
            Prisoners,
            Slaves,
            ColonyMechs,
            ColonyAnimals,
            AllSpawned
        }

        /// <summary>
        /// 「地图上有几个 X」。六个只差一个取数方法，所以写一遍。
        ///
        /// 它和主体绑定**不是一回事**：绑定决定"规则对谁各跑一遍"，
        /// 而这是地图的一个读数（"囚犯只有两个，太少了"）。
        /// </summary>
        private static void AddMapPawnCount(RuleVocabulary v, string key, PawnTally which)
        {
            PawnTally captured = which;

            v.Add(new RulePropertyInfo
            {
                key = key, owner = RuleEntityKind.Map,
                requires = RuleCapability.Map,
                result = RuleValueKind.Number, decimals = 0, min = 0f, max = 1000f,
                reader = delegate(IRuleEvalHost host, RuleValue owner, out RuleValue value,
                    out string code, out string reason)
                {
                    var map = RuleEvalHost.MapOf(owner);
                    if (map == null || map.mapPawns == null)
                    {
                        return Fail(out value, out code, out reason, "prop.no_map", "没有地图，数不了人。");
                    }

                    float count;
                    switch (captured)
                    {
                        case PawnTally.FreeColonists:
                            count = map.mapPawns.FreeColonistsSpawnedCount;
                            break;
                        case PawnTally.Prisoners:
                            count = map.mapPawns.PrisonersOfColonySpawnedCount;
                            break;
                        case PawnTally.Slaves:
                            count = map.mapPawns.SlavesOfColonySpawned.Count;
                            break;
                        case PawnTally.ColonyMechs:
                            count = map.mapPawns.SpawnedColonyMechs.Count;
                            break;
                        case PawnTally.ColonyAnimals:
                            count = map.mapPawns.SpawnedColonyAnimals.Count;
                            break;
                        default:
                            count = map.mapPawns.AllPawnsSpawnedCount;
                            break;
                    }

                    return Ok(RuleValue.OfNumber(count), out value, out code, out reason);
                }
            });
        }

        private static void AddWeatherRate(RuleVocabulary v, string key)
        {
            v.Add(new RulePropertyInfo
            {
                key = key, owner = RuleEntityKind.Map,
                requires = RuleCapability.Map,
                result = RuleValueKind.Number, percent = true, min = 0f, max = 1f,
                reader = delegate(IRuleEvalHost host, RuleValue owner, out RuleValue value,
                    out string code, out string reason)
                {
                    var map = RuleEvalHost.MapOf(owner);
                    if (map == null || map.weatherManager == null)
                    {
                        return Fail(out value, out code, out reason, "prop.no_map", "没有地图，读不到天气。");
                    }

                    float rate = key == "map.rainRate"
                        ? map.weatherManager.RainRate
                        : map.weatherManager.SnowRate;

                    return Ok(RuleValue.OfNumber(rate), out value, out code, out reason);
                }
            });
        }

        // ── 属性：小人 ────────────────────────────────────────────────

        /// <summary>
        /// 小人的属性。
        ///
        /// <b>顺序是设计过的</b>：玩家最常问的排前面（身份 / 血量 / 需求），
        /// 位置与房间这类"给动作指路用"的排后面，身份布尔垫底。
        ///
        /// <b>每一条的 <c>requires</c> 才是这个版本的要点</b>：
        /// 机械族没有饱食度、有电量；动物没有理念也没有征召。
        /// 这些差别不是靠一个 if 写死的，而是声明在行上、由编辑器读出来——
        /// 于是别的 mod 加一行时也自动获得同样的待遇。
        /// </summary>
        private static void AddPawnProperties(RuleVocabulary v)
        {
            v.Add(new RulePropertyInfo
            {
                key = "pawn.health", owner = RuleEntityKind.Pawn,
                requires = RuleCapability.Pawn,
                result = RuleValueKind.Number, percent = true, min = 0f, max = 1f,
                reader = delegate(IRuleEvalHost host, RuleValue owner, out RuleValue value,
                    out string code, out string reason)
                {
                    var pawn = RuleEvalHost.PawnOf(owner);
                    if (pawn == null || pawn.health == null || pawn.health.summaryHealth == null)
                    {
                        return Fail(out value, out code, out reason, "prop.no_health",
                            "读不到这个人的健康状态。");
                    }
                    return Ok(RuleValue.OfNumber(pawn.health.summaryHealth.SummaryHealthPercent),
                        out value, out code, out reason);
                }
            });

            AddNeed(v, "pawn.mood", NeedKind.Mood);
            AddNeed(v, "pawn.food", NeedKind.Food);
            AddNeed(v, "pawn.rest", NeedKind.Rest);

            // 电量。**机械族的"饱食度"** —— 同一个槽位上的同一件事，
            // 只是名字和需求对象不同。玩家在机械族身上看到的就是它，看不到饱食度。
            AddNeed(v, "pawn.energy", NeedKind.Energy);

            v.Add(new RulePropertyInfo
            {
                key = "pawn.age", owner = RuleEntityKind.Pawn,
                requires = RuleCapability.Age,
                result = RuleValueKind.Number, unit = "岁", decimals = 0,
                min = 0f, max = 10000f,
                reader = delegate(IRuleEvalHost host, RuleValue owner, out RuleValue value,
                    out string code, out string reason)
                {
                    var pawn = RuleEvalHost.PawnOf(owner);
                    if (pawn == null || pawn.ageTracker == null)
                    {
                        return Fail(out value, out code, out reason, "prop.no_age",
                            "读不到这个人的年龄。");
                    }
                    return Ok(RuleValue.OfNumber(pawn.ageTracker.AgeBiologicalYearsFloat),
                        out value, out code, out reason);
                }
            });

            v.Add(new RulePropertyInfo
            {
                key = "pawn.position", owner = RuleEntityKind.Pawn,
                requires = RuleCapability.Pawn,
                result = RuleValueKind.Coord,
                reader = delegate(IRuleEvalHost host, RuleValue owner, out RuleValue value,
                    out string code, out string reason)
                {
                    var pawn = RuleEvalHost.PawnOf(owner);
                    if (pawn == null || !pawn.Spawned)
                    {
                        return Fail(out value, out code, out reason, "prop.not_spawned",
                            "这个人不在任何地图上。");
                    }
                    var p = pawn.Position;
                    return Ok(RuleValue.OfCell(new RuleCell(p.x, p.z)), out value, out code, out reason);
                }
            });

            v.Add(new RulePropertyInfo
            {
                key = "pawn.locationTemp", owner = RuleEntityKind.Pawn,
                requires = RuleCapability.Pawn,
                result = RuleValueKind.Number, unit = "℃", decimals = 1,
                min = -200f, max = 200f,
                reader = delegate(IRuleEvalHost host, RuleValue owner, out RuleValue value,
                    out string code, out string reason)
                {
                    var pawn = RuleEvalHost.PawnOf(owner);
                    if (pawn == null || !pawn.Spawned || pawn.Map == null)
                    {
                        return Fail(out value, out code, out reason, "prop.not_spawned",
                            "这个人不在任何地图上。");
                    }

                    var room = pawn.GetRoom();
                    if (room != null)
                    {
                        return Ok(RuleValue.OfNumber(room.Temperature), out value, out code, out reason);
                    }

                    if (pawn.Map.mapTemperature == null)
                    {
                        return Fail(out value, out code, out reason, "prop.no_map", "读不到温度。");
                    }

                    // 在户外不是错误——"他在的地方多冷"才是真正想问的问题。
                    return Ok(RuleValue.OfNumber(pawn.Map.mapTemperature.OutdoorTemp),
                        out value, out code, out reason);
                }
            });

            // 房间：从"人所在的地方"出发，而不是凭空给一个房间选择器。
            // 于是「他那个房间多冷 / 多大」都能写，而且不需要先解决"怎么选一个房间"。
            v.Add(new RulePropertyInfo
            {
                key = "pawn.room", owner = RuleEntityKind.Pawn,
                requires = RuleCapability.Pawn,
                result = RuleValueKind.Entity, resultEntity = RuleEntityKind.Room,
                reader = delegate(IRuleEvalHost host, RuleValue owner, out RuleValue value,
                    out string code, out string reason)
                {
                    var pawn = RuleEvalHost.PawnOf(owner);
                    if (pawn == null || !pawn.Spawned || pawn.Map == null)
                    {
                        return Fail(out value, out code, out reason, "prop.not_spawned",
                            "这个人不在任何地图上。");
                    }

                    var room = pawn.GetRoom();
                    if (room == null)
                    {
                        // 在户外不是错误，但"他没有房间"是**读不到**而不是"房间属性为零"——
                        // 混起来会让「房间温度 小于 10」在户外静默地按 0 度判成立。
                        return Fail(out value, out code, out reason, "prop.no_room",
                            "他在户外，没有房间。");
                    }

                    return Ok(RuleValue.OfEntity(RuleEntityKind.Room, room),
                        out value, out code, out reason);
                }
            });

            v.Add(new RulePropertyInfo
            {
                key = "pawn.apparel", owner = RuleEntityKind.Pawn,
                requires = RuleCapability.Apparel,
                result = RuleValueKind.EntitySet, resultEntity = RuleEntityKind.Thing,
                reader = delegate(IRuleEvalHost host, RuleValue owner, out RuleValue value,
                    out string code, out string reason)
                {
                    var pawn = RuleEvalHost.PawnOf(owner);
                    if (pawn == null)
                    {
                        return Fail(out value, out code, out reason, "prop.no_pawn", "这不是一个人。");
                    }

                    var items = new List<RuleValue>();
                    var worn = pawn.apparel != null ? pawn.apparel.WornApparel : null;
                    if (worn != null)
                    {
                        for (int i = 0; i < worn.Count; i++)
                        {
                            if (worn[i] != null)
                            {
                                items.Add(RuleValue.OfEntity(RuleEntityKind.Thing, worn[i]));
                            }
                        }
                    }

                    // 集合是 0 个也是合法值——"他什么都没穿"必须能表达，
                    // 用「数量 等于 0」而不是"读不到"。
                    return Ok(RuleValue.OfSet(RuleEntityKind.Thing, items),
                        out value, out code, out reason);
                }
            });

            v.Add(new RulePropertyInfo
            {
                key = "pawn.equipment", owner = RuleEntityKind.Pawn,
                requires = RuleCapability.Equipment,
                result = RuleValueKind.Entity, resultEntity = RuleEntityKind.Thing,
                reader = delegate(IRuleEvalHost host, RuleValue owner, out RuleValue value,
                    out string code, out string reason)
                {
                    var pawn = RuleEvalHost.PawnOf(owner);
                    if (pawn == null || pawn.equipment == null)
                    {
                        return Fail(out value, out code, out reason, "prop.no_equipment",
                            "这不是一个能拿东西的人。");
                    }

                    var primary = pawn.equipment.Primary;
                    if (primary == null)
                    {
                        // 空手是**读不到**，不是"拿了一件空的东西"——
                        // 混起来会让「手持武器.耐久 小于 50%」在空手时静默地按 0 判成立。
                        return Fail(out value, out code, out reason, "prop.no_primary",
                            "他手里没拿东西。");
                    }

                    return Ok(RuleValue.OfEntity(RuleEntityKind.Thing, primary),
                        out value, out code, out reason);
                }
            });

            v.Add(new RulePropertyInfo
            {
                key = "pawn.shelterCell", owner = RuleEntityKind.Pawn,
                requires = RuleCapability.Pawn,
                result = RuleValueKind.Entity, resultEntity = RuleEntityKind.Cell,
                reader = delegate(IRuleEvalHost host, RuleValue owner, out RuleValue value,
                    out string code, out string reason)
                {
                    var pawn = RuleEvalHost.PawnOf(owner);
                    var map = pawn != null ? pawn.Map : null;
                    if (pawn == null || map == null || !pawn.Spawned)
                    {
                        return Fail(out value, out code, out reason, "prop.not_spawned",
                            "这个人不在任何地图上。");
                    }

                    IntVec3 cell;
                    string reasonCode;
                    string reason2;
                    if (!TryFindShelter(pawn, map, out cell, out reasonCode, out reason2))
                    {
                        return Fail(out value, out code, out reason, reasonCode, reason2);
                    }

                    return Ok(RuleValue.OfEntity(RuleEntityKind.Cell,
                        new RuleCell(cell.x, cell.z)), out value, out code, out reason);
                }
            });

            // ── 状态布尔 ──────────────────────────────────────────────
            //
            // **这里没有「是囚犯 / 是机械族 / 是殖民者」这类身份判断，那是刻意的。**
            //
            // 身份**完全由主体绑定决定**：主体绑成「囚犯」，那一轮里每个人的身份就都是囚犯。
            // 于是 `本主体.是囚犯` 对任何一条规则都是**恒真句**——它不提供信息，
            // 只占属性列表的位置（原本 21 行小人属性里有 6 行是这种）。
            //
            // 想写"囚犯做某事"，就把主体绑成囚犯。想知道"现在这个主体到底是什么"，
            // 编辑器直接写在他脸上（主体绑定检查器里的「小明 现在是：殖民者」），
            // 不需要玩家在规则里再问一遍。
            //
            // 什么时候该把它们加回来：**当出现一个宽到需要再区分的绑定时**
            // （比如"这张图上的所有人"）。现在没有那种绑定，所以现在它们只能是常数。
            AddBool(v, "pawn.isDowned", PawnBool.Downed, RuleCapability.Pawn);
            AddBool(v, "pawn.isDrafted", PawnBool.Drafted, RuleCapability.CanDraft);
        }

        /// <summary>
        /// 最近的、可站立的、有屋顶的、**真的走得到**的格子。
        ///
        /// 确定性矩形扫描 + 按距离排序取最近 N 个候选再逐个试可达性。
        /// **不用 <c>CellFinder.TryFindRandomCellNear</c>** —— 随机取样会让同一个世界状态
        /// 解析出不同格子，读档、重放、排障全部会漂。
        /// </summary>
        private static bool TryFindShelter(Pawn pawn, Map map, out IntVec3 cell,
            out string reasonCode, out string reason)
        {
            const int radius = 30;
            const int maxCandidates = 12;

            cell = IntVec3.Invalid;
            reasonCode = null;
            reason = null;

            var candidates = new IntVec3[maxCandidates];
            var distances = new int[maxCandidates];
            int count = 0;

            IntVec3 origin = pawn.Position;
            int x0 = UnityEngine.Mathf.Max(0, origin.x - radius);
            int x1 = UnityEngine.Mathf.Min(map.Size.x - 1, origin.x + radius);
            int z0 = UnityEngine.Mathf.Max(0, origin.z - radius);
            int z1 = UnityEngine.Mathf.Min(map.Size.z - 1, origin.z + radius);

            for (int x = x0; x <= x1; x++)
            {
                for (int z = z0; z <= z1; z++)
                {
                    var candidate = new IntVec3(x, 0, z);
                    if (!candidate.Roofed(map)) continue;
                    if (!candidate.Standable(map)) continue;

                    int distance = (candidate - origin).LengthHorizontalSquared;
                    if (distance == 0) continue;

                    if (count == maxCandidates && distance >= distances[count - 1]) continue;

                    int at = count < maxCandidates ? count : maxCandidates - 1;
                    while (at > 0 && distances[at - 1] > distance)
                    {
                        distances[at] = distances[at - 1];
                        candidates[at] = candidates[at - 1];
                        at--;
                    }

                    distances[at] = distance;
                    candidates[at] = candidate;
                    if (count < maxCandidates) count++;
                }
            }

            if (count == 0)
            {
                reasonCode = "shelter.no_roofed_cell";
                reason = "半径 " + radius + " 内没有可站立的屋顶格。";
                return false;
            }

            var parms = TraverseParms.For(pawn, Danger.Deadly, TraverseMode.PassDoors);
            for (int i = 0; i < count; i++)
            {
                if (map.reachability.CanReach(origin, candidates[i], PathEndMode.OnCell, parms))
                {
                    cell = candidates[i];
                    return true;
                }
            }

            reasonCode = "shelter.unreachable";
            reason = "找到 " + count + " 个屋顶格，但一个都到不了（路径被堵？）。";
            return false;
        }

        // ── 属性：房间与物品 ──────────────────────────────────────────

        private static void AddThingProperties(RuleVocabulary v)
        {
            v.Add(new RulePropertyInfo
            {
                key = "room.temperature", owner = RuleEntityKind.Room,
                requires = RuleCapability.Room,
                result = RuleValueKind.Number, unit = "℃", decimals = 1,
                min = -200f, max = 200f,
                reader = delegate(IRuleEvalHost host, RuleValue owner, out RuleValue value,
                    out string code, out string reason)
                {
                    var room = RuleEvalHost.RoomOf(owner);
                    if (room == null)
                    {
                        return Fail(out value, out code, out reason, "prop.no_room", "这不是一个房间。");
                    }
                    return Ok(RuleValue.OfNumber(room.Temperature), out value, out code, out reason);
                }
            });

            v.Add(new RulePropertyInfo
            {
                key = "room.cellCount", owner = RuleEntityKind.Room,
                requires = RuleCapability.Room,
                result = RuleValueKind.Number, decimals = 0, min = 0f, max = 10000f,
                reader = delegate(IRuleEvalHost host, RuleValue owner, out RuleValue value,
                    out string code, out string reason)
                {
                    var room = RuleEvalHost.RoomOf(owner);
                    if (room == null)
                    {
                        return Fail(out value, out code, out reason, "prop.no_room", "这不是一个房间。");
                    }
                    return Ok(RuleValue.OfNumber(room.CellCount), out value, out code, out reason);
                }
            });

            v.Add(new RulePropertyInfo
            {
                key = "thing.hitPoints", owner = RuleEntityKind.Thing,
                requires = RuleCapability.Thing,
                result = RuleValueKind.Number, decimals = 0, min = 0f, max = 100000f,
                reader = delegate(IRuleEvalHost host, RuleValue owner, out RuleValue value,
                    out string code, out string reason)
                {
                    var thing = RuleEvalHost.ThingOf(owner);
                    if (thing == null)
                    {
                        return Fail(out value, out code, out reason, "prop.no_thing", "这不是一件物品。");
                    }
                    return Ok(RuleValue.OfNumber(thing.HitPoints), out value, out code, out reason);
                }
            });

            v.Add(new RulePropertyInfo
            {
                key = "thing.durability", owner = RuleEntityKind.Thing,
                requires = RuleCapability.Thing,
                result = RuleValueKind.Number, percent = true, min = 0f, max = 1f,
                reader = delegate(IRuleEvalHost host, RuleValue owner, out RuleValue value,
                    out string code, out string reason)
                {
                    var thing = RuleEvalHost.ThingOf(owner);
                    if (thing == null)
                    {
                        return Fail(out value, out code, out reason, "prop.no_thing", "这不是一件物品。");
                    }

                    var hitPoints = thing.HitPoints;
                    int max = thing.MaxHitPoints;
                    if (max <= 0)
                    {
                        return Fail(out value, out code, out reason, "prop.no_durability",
                            "这件东西没有耐久。");
                    }

                    return Ok(RuleValue.OfNumber((float)hitPoints / max), out value, out code, out reason);
                }
            });

            v.Add(new RulePropertyInfo
            {
                key = "thing.defName", owner = RuleEntityKind.Thing,
                requires = RuleCapability.Thing,
                result = RuleValueKind.Enum,
                // 声明取值来源 → 编辑器给带搜索的选择器。
                // 没有它，「他穿的是不是帽子」这句话就写不出来（两千个 ThingDef 里挑不出来）。
                enumDefType = typeof(ThingDef),
                reader = delegate(IRuleEvalHost host, RuleValue owner, out RuleValue value,
                    out string code, out string reason)
                {
                    var thing = RuleEvalHost.ThingOf(owner);
                    if (thing == null || thing.def == null)
                    {
                        return Fail(out value, out code, out reason, "prop.no_thing", "这不是一件物品。");
                    }
                    return Ok(RuleValue.OfKey(thing.def.defName), out value, out code, out reason);
                }
            });
        }

        // ── 检测 ──────────────────────────────────────────────────────
        //
        // 同一个词"等于"会出现两次（一次收数值、一次收枚举）。
        // 这是刻意的：**玩家看到的字一样，但类型不同，按类型过滤时只会出现该用的那个**。
        // 比造一个新词（"等于值"/"等于枚举"）更符合人话。

        private static void AddDetects(RuleVocabulary v)
        {
            AddNumberCompare(v, "compare.gt", RuleOperator.Greater);
            AddNumberCompare(v, "compare.lt", RuleOperator.Less);
            AddNumberCompare(v, "compare.gte", RuleOperator.AtLeast);
            AddNumberCompare(v, "compare.lte", RuleOperator.AtMost);
            AddNumberCompare(v, "compare.eq", RuleOperator.Equal);
            AddNumberCompare(v, "compare.ne", RuleOperator.NotEqual);

            v.Add(new RuleVerbInfo
            {
                key = "match.is", category = RuleVerbCategory.Detect,
                subject = RuleEntityKind.Any, argKind = RuleValueKind.Enum,
                subjectValueKind = RuleValueKind.Enum,
                detect = delegate(IRuleEvalHost host, RuleValue subject, RuleValue arg,
                    out bool passed, out string code, out string reason)
                {
                    if (!arg.IsMissing && arg.kind != RuleValueKind.Enum)
                    {
                        passed = false;
                        code = "match.bad_arg";
                        reason = "要比的是一个名字，给的不是。";
                        return false;
                    }

                    passed = subject.kind == RuleValueKind.Enum && subject.AsKey == arg.AsKey;
                    code = passed ? null : "match.false";
                    reason = passed ? null : "是「" + (subject.AsKey ?? "?") + "」，不是「"
                        + (arg.AsKey ?? "?") + "」。";
                    return true;
                }
            });

            v.Add(new RuleVerbInfo
            {
                key = "state.always", category = RuleVerbCategory.Detect,
                subject = RuleEntityKind.Any, argKind = RuleValueKind.None,
                detect = delegate(IRuleEvalHost host, RuleValue subject, RuleValue arg,
                    out bool passed, out string code, out string reason)
                {
                    passed = true;
                    code = null;
                    reason = null;
                    return true;
                }
            });

            // 「为真」：把布尔属性直接当检测用。
            // 有了它，"被征召 / 倒地 / 在户外"这类事实不需要各自长一个谓词——
            // 加一个事实只是加一行**属性**。
            v.Add(new RuleVerbInfo
            {
                key = "state.holds", category = RuleVerbCategory.Detect,
                subject = RuleEntityKind.Any, subjectValueKind = RuleValueKind.Bool,
                argKind = RuleValueKind.None,
                detect = delegate(IRuleEvalHost host, RuleValue subject, RuleValue arg,
                    out bool passed, out string code, out string reason)
                {
                    passed = subject.AsBool;
                    code = passed ? null : "holds.false";
                    reason = passed ? null : "这个事实不成立。";
                    return true;
                }
            });

            AddPawnState(v, "state.able", false);
            AddPawnState(v, "state.idle", true);

            // 为什么主语是"本图"而不是"袭击"：事件型实体没有把手（它只是个类型名），
            // 而主语路径的每一步都要能读出真实值。把信件类型放进宾语之后，
            // 「本图 收到 ThreatBig」既合语法又不需要一个只有名字的假实体。
            v.Add(new RuleVerbInfo
            {
                key = "event.letter", category = RuleVerbCategory.Detect,
                subject = RuleEntityKind.Map, argKind = RuleValueKind.Enum,
                edge = RuleEdge.Edge,
                // **取值来源**。没有它，编辑器只能让玩家手填 defName——
                // 而信件类型是原版就有的完整清单，凭什么要玩家背英文标识符。
                // 有它之后编辑器列出全部 LetterDef，显示的是**当前语言的名字**
                // （Def.LabelCap 走翻译，没有翻译才回落 defName）。
                argDefType = typeof(LetterDef),
                detect = delegate(IRuleEvalHost host, RuleValue subject, RuleValue arg,
                    out bool passed, out string code, out string reason)
                {
                    int tick = host is RuleEvalHost ? ((RuleEvalHost)host).Context.Tick : 0;
                    return LetterWindow.TryMatch(tick, arg.AsKey, out passed, out code, out reason);
                }
            });

            // 「本图 处于 日蚀」——**读**游戏的持续状态（日蚀、毒雾、寒潮、心灵低语）。
            //
            // 它是「触发事件」的读取侧。原版那些状态本来就由事件造成
            // （IncidentWorker_MakeGameCondition），所以这一对读/写是对称的：
            // 用「触发」起，用「处于」监视。
            v.Add(new RuleVerbInfo
            {
                key = "map.hasCondition", category = RuleVerbCategory.Detect,
                subject = RuleEntityKind.Map,
                argKind = RuleValueKind.Enum,
                argDefType = typeof(GameConditionDef),
                detect = delegate(IRuleEvalHost host, RuleValue subject, RuleValue arg,
                    out bool passed, out string code, out string reason)
                {
                    var map = RuleEvalHost.MapOf(subject);
                    if (map == null || map.gameConditionManager == null)
                    {
                        passed = false;
                        code = "condition.no_map";
                        reason = "没有地图，读不到游戏状态。";
                        return false;
                    }

                    GameConditionDef def;
                    if (!TryDefArg(arg, out def, out code, out reason))
                    {
                        passed = false;
                        return false;
                    }

                    passed = map.gameConditionManager.ConditionIsActive(def);
                    code = passed ? null : "condition.not_active";
                    reason = passed ? null : "「" + def.LabelCap + "」现在没有在生效。";
                    return true;
                }
            });
        }

        private static void AddNumberCompare(RuleVocabulary v, string key, RuleOperator op)
        {
            RuleOperator captured = op;

            v.Add(new RuleVerbInfo
            {
                key = key, category = RuleVerbCategory.Detect,
                subject = RuleEntityKind.Any, argKind = RuleValueKind.Number,
                // 只吃数值。没有这一条，「大于」会出现在 `本主体`（一个 Pawn）身上——
                // 玩家选了必然报 compare.not_number。
                subjectValueKind = RuleValueKind.Number,
                detect = delegate(IRuleEvalHost host, RuleValue subject, RuleValue arg,
                    out bool passed, out string code, out string reason)
                {
                    if (!subject.IsNumber)
                    {
                        passed = false;
                        code = "compare.not_number";
                        reason = "要比较的不是一个数。";
                        return false;
                    }

                    if (arg.IsMissing)
                    {
                        passed = false;
                        code = "compare.no_threshold";
                        reason = "没有填参照值。";
                        return false;
                    }

                    passed = RuleCompare.Apply(captured, subject.AsNumber, arg.AsNumber);
                    code = passed ? null : "compare.false";
                    reason = passed ? null : "实测 " + subject.AsNumber.ToString("0.##")
                        + "，不满足 " + RuleCompare.Symbol(captured) + " "
                        + arg.AsNumber.ToString("0.##");
                    return true;
                }
            });
        }

        /// <summary>需求种类。各自读同一个 <c>Need.CurLevelPercentage</c>，所以合成一个工厂。</summary>
        private enum NeedKind
        {
            Mood,
            Food,
            Rest,

            /// <summary>电量。机械族的"饱食度"。</summary>
            Energy
        }

        private static void AddNeed(RuleVocabulary v, string key, NeedKind which)
        {
            NeedKind captured = which;

            // 需求**逐条声明所需能力**，而不是笼统一个"有需求"：
            // 这正是"机械族身上不列饱食度、列电量"落地的地方。
            RuleCapability required;
            switch (captured)
            {
                case NeedKind.Mood: required = RuleCapability.NeedMood; break;
                case NeedKind.Food: required = RuleCapability.NeedFood; break;
                case NeedKind.Rest: required = RuleCapability.NeedRest; break;
                default: required = RuleCapability.NeedEnergy; break;
            }

            v.Add(new RulePropertyInfo
            {
                key = key, owner = RuleEntityKind.Pawn,
                requires = required,
                result = RuleValueKind.Number, percent = true, min = 0f, max = 1f,
                reader = delegate(IRuleEvalHost host, RuleValue owner, out RuleValue value,
                    out string code, out string reason)
                {
                    var pawn = RuleEvalHost.PawnOf(owner);
                    if (pawn == null || pawn.needs == null)
                    {
                        return Fail(out value, out code, out reason, "prop.no_needs",
                            "读不到这个人的需求（不是生物？）。");
                    }

                    Need need;
                    switch (captured)
                    {
                        case NeedKind.Mood: need = pawn.needs.mood; break;
                        case NeedKind.Food: need = pawn.needs.food; break;
                        case NeedKind.Rest: need = pawn.needs.rest; break;
                        default: need = pawn.needs.energy; break;
                    }

                    if (need == null)
                    {
                        // 没有这个需求是**读不到**，不是"需求为零"——
                        // 混起来会让「心情 小于 10%」在一个没有心情的机械体上静默成立。
                        return Fail(out value, out code, out reason, "prop.no_need",
                            "这个人没有这一类需求。");
                    }

                    return Ok(RuleValue.OfNumber(need.CurLevelPercentage),
                        out value, out code, out reason);
                }
            });
        }

        /// <summary>状态布尔。取值方式在这里集中，加一个事实只是加一行。</summary>
        private enum PawnBool
        {
            Downed,
            Drafted
        }

        private static void AddBool(RuleVocabulary v, string key, PawnBool which,
            RuleCapability required)
        {
            PawnBool captured = which;

            v.Add(new RulePropertyInfo
            {
                key = key, owner = RuleEntityKind.Pawn,
                requires = required,
                result = RuleValueKind.Bool,
                reader = delegate(IRuleEvalHost host, RuleValue owner, out RuleValue value,
                    out string code, out string reason)
                {
                    var pawn = RuleEvalHost.PawnOf(owner);
                    if (pawn == null)
                    {
                        return Fail(out value, out code, out reason, "prop.no_pawn", "这不是一个人。");
                    }

                    bool result = captured == PawnBool.Downed ? pawn.Downed : pawn.Drafted;

                    return Ok(RuleValue.OfBool(result), out value, out code, out reason);
                }
            });
        }

        private static void AddPawnState(RuleVocabulary v, string key, bool idle)
        {
            bool wantsIdle = idle;

            v.Add(new RuleVerbInfo
            {
                key = key, category = RuleVerbCategory.Detect,
                subject = RuleEntityKind.Pawn, argKind = RuleValueKind.None,
                // 能力类的检测**不成立时安静让开**，不算规则失败——
                // 这是"条件 / 能力"合并之后保留区别的唯一手段。
                failure = RuleFailureMode.QuietSkip,
                detect = delegate(IRuleEvalHost host, RuleValue subject, RuleValue arg,
                    out bool passed, out string code, out string reason)
                {
                    var pawn = RuleEvalHost.PawnOf(subject);
                    if (pawn == null)
                    {
                        passed = false;
                        code = "state.no_pawn";
                        reason = "这不是一个人。";
                        return false;
                    }

                    if (wantsIdle)
                    {
                        return PawnStates.TryIdle(pawn, out passed, out code, out reason);
                    }

                    return PawnStates.TryAble(pawn, out passed, out code, out reason);
                }
            });
        }

        // ── 操作 ──────────────────────────────────────────────────────

        // ── 操作：地图 ────────────────────────────────────────────────
        //
        // 这一组补的是"地图上一个谓词都没有"那个洞。
        //
        // 地图是**唯一一个不通过小人就能改变世界的地方**（天气、事件），
        // 而它原来只有一个「写日志」——玩家点开地图的谓语列表，看到的是一行。
        //
        // 它们几乎全是 <see cref="RuleTier.Developer"/>：这是"直接改世界状态"，
        // 不是"让谁去做"。放不放行由玩家在模组设置里决定（allowDeveloperOps），
        // **不由他有没有开原版开发者模式决定**——把功能藏在调试菜单后面，
        // 等于让玩家去猜"是不是我规则写错了"。

        private static void AddMapOperates(RuleVocabulary v)
        {
            // ── 变成（天气）──────────────────────────────────────────
            v.Add(new RuleVerbInfo
            {
                key = "map.setWeather", category = RuleVerbCategory.Operate,
                subject = RuleEntityKind.Map,
                argKind = RuleValueKind.Enum,
                // 取值来源。没有它，宾语只能手填 "Rain" 这种内部标识符。
                argDefType = typeof(WeatherDef),
                tier = RuleTier.Developer,
                operate = delegate(IRuleEvalHost host, RuleValue subject, RuleValue arg,
                    out string code, out string reason)
                {
                    var map = RuleEvalHost.MapOf(subject);
                    if (map == null || map.weatherManager == null)
                    {
                        code = "weather.no_map";
                        reason = "没有地图，改不了天气。";
                        return RuleOperateStatus.Failed;
                    }

                    WeatherDef def;
                    if (!TryDefArg(arg, out def, out code, out reason)) return RuleOperateStatus.Failed;

                    if (map.weatherManager.curWeather == def)
                    {
                        code = "weather.already";
                        reason = "天色已经是「" + def.LabelCap + "」。";
                        return RuleOperateStatus.AlreadySatisfied;
                    }

                    map.weatherManager.TransitionTo(def);
                    code = "weather.changed";
                    reason = "天气已变成「" + def.LabelCap + "」。";
                    return RuleOperateStatus.Done;
                }
            });

            // ── 触发（事件）──────────────────────────────────────────
            //
            // **走原版的事件系统，不自己拼效果。** 这样时长、信件、难度限制、
            // 剧本禁用、"最近刚发生过"这些玩家自己设的规则全都还在。
            // 自己塞 GameCondition 就得自己猜时长——那个字段根本不在
            // GameConditionDef 上，而在 IncidentDef.durationDays 里。
            v.Add(new RuleVerbInfo
            {
                key = "map.incident", category = RuleVerbCategory.Operate,
                subject = RuleEntityKind.Map,
                argKind = RuleValueKind.Enum,
                argDefType = typeof(IncidentDef),
                // **把投不出去的事件从菜单里拿掉。**
                // 日蚀 / 太阳耀斑 / 极光的目标标签只有 World，从地图上发出去会被
                // 原版第一关挡下；列出来只会让玩家选了才发现不行。
                argFilter = RuleIncidentFacts.TargetAvailableFilter,
                tier = RuleTier.Developer,
                operate = delegate(IRuleEvalHost host, RuleValue subject, RuleValue arg,
                    out string code, out string reason)
                {
                    var map = RuleEvalHost.MapOf(subject);
                    if (map == null)
                    {
                        code = "incident.no_map";
                        reason = "没有地图，触发不了事件。";
                        return RuleOperateStatus.Failed;
                    }

                    IncidentDef def;
                    if (!TryDefArg(arg, out def, out code, out reason)) return RuleOperateStatus.Failed;

                    if (def.Worker == null)
                    {
                        code = "incident.no_worker";
                        reason = "「" + def.LabelCap + "」没有执行体，触发不了。";
                        return RuleOperateStatus.Failed;
                    }

                    // **目标要选对。** 地图优先，其次世界——日蚀/太阳耀斑/极光
                    // 的目标标签只有 World，拿地图去喂会让 CanFireNow 第一句就返回 false，
                    // 而那时的报错完全看不出真正原因（玩家报过的那个 bug）。
                    IIncidentTarget target;
                    string targetNote;
                    if (!RuleIncidentFacts.TryResolveTarget(def, map, out target, out targetNote))
                    {
                        code = "incident.no_target";
                        reason = "「" + def.LabelCap + "」的目标既不是这张地图也不是世界"
                            + "（targetTags 两边都不匹配），从地图上发不出去。";
                        return RuleOperateStatus.Rejected;
                    }

                    var parms = StorytellerUtility.DefaultParmsNow(def.category, target);

                    // **原版第一顺位**：先问"现在能不能发生"。
                    // 不硬塞——硬塞会把 minRefireDays、难度里的"禁止大威胁"、
                    // 剧本禁用这些玩家自己设的东西全部踩掉。
                    if (!def.Worker.CanFireNow(parms))
                    {
                        code = "incident.not_now";

                        // **说清是哪一个条件挡的。** 原来只列四种可能，
                        // 玩家据此没法做任何决定——而这条规则到底为什么不动，
                        // 恰恰是时间线存在的全部意义。
                        string why = RuleIncidentFacts.DescribeWhyNot(def, parms);
                        reason = why != null
                            ? "「" + def.LabelCap + "」现在不能发生：" + why
                            : "原版判断现在不能发生「" + def.LabelCap
                              + "」，但没落在常见原因里（可能是 mod 的 CanFireNowSub、"
                              + "异常内容、或行星层限制）。";
                        return RuleOperateStatus.Rejected;
                    }

                    if (!def.Worker.TryExecute(parms))
                    {
                        code = "incident.failed";
                        reason = "原版试着执行「" + def.LabelCap + "」但没做成。";
                        return RuleOperateStatus.Rejected;
                    }

                    code = "incident.fired";
                    reason = "已触发「" + def.LabelCap + "」"
                        + (targetNote != null ? "（" + targetNote + "）" : string.Empty) + "。";
                    return RuleOperateStatus.Done;
                }
            });

            // ── 发信件 ───────────────────────────────────────────────
            //
            // 玩家级：它不改世界，只是**把一件事告诉玩家**。
            // 一条规则如果只是安静地做完了，玩家永远不知道它在跑——
            // 而"我看不出它到底有没有生效"是这类自动化最常见的不满。
            v.Add(new RuleVerbInfo
            {
                key = "map.sendLetter", category = RuleVerbCategory.Operate,
                subject = RuleEntityKind.Map,
                argKind = RuleValueKind.Enum,
                argDefType = typeof(LetterDef),
                operate = delegate(IRuleEvalHost host, RuleValue subject, RuleValue arg,
                    out string code, out string reason)
                {
                    LetterDef def;
                    if (!TryDefArg(arg, out def, out code, out reason)) return RuleOperateStatus.Failed;

                    var context = host is RuleEvalHost ? ((RuleEvalHost)host).Context : null;
                    var rule = context != null ? context.Rule : null;

                    string label = rule != null ? rule.DisplayLabel : "RuleCore";

                    // 信件的正文用规则的说明，没有就用标题——
                    // 玩家写规则时那句"这条是干什么的"正好就是这里该显示的东西。
                    string text = rule != null && !string.IsNullOrEmpty(rule.description)
                        ? rule.description
                        : label;

                    // 显式给 LookTargets.Invalid：传 null 会让两个重载二义，
                    // 而原版自己也是这么调的（IncidentWorker_PsychicDrone 等）。
                    Find.LetterStack.ReceiveLetter(label, text, def, LookTargets.Invalid);
                    code = "letter.sent";
                    reason = "已发信件（" + def.LabelCap + "）。";
                    return RuleOperateStatus.Done;
                }
            });
        }

        /// <summary>
        /// 宾语是一个 Def 引用（天气 / 事件 / 游戏状态 / 信件类型）。
        ///
        /// 几个谓词都要做这件事，而且都要把**"还没选"和"选了但认不出"分开**——
        /// 混起来玩家会看到"认不出的天气：(空)"，那是在把他没填的东西说成填错了。
        /// </summary>
        private static bool TryDefArg<T>(RuleValue arg, out T def, out string code, out string reason)
            where T : Def
        {
            def = null;

            if (arg.IsMissing || string.IsNullOrEmpty(arg.AsKey))
            {
                code = "arg.empty";
                reason = "宾语还没选是哪一个。";
                return false;
            }

            def = DefDatabase<T>.GetNamedSilentFail(arg.AsKey);
            if (def == null)
            {
                code = "arg.unknown";
                reason = "认不出的取值：" + arg.AsKey;
                return false;
            }

            code = null;
            reason = null;
            return true;
        }

        private static void AddOperates(RuleVocabulary v)
        {
            AddMapOperates(v);

            v.Add(new RuleVerbInfo
            {
                key = "op.log", category = RuleVerbCategory.Operate,
                subject = RuleEntityKind.Any, argKind = RuleValueKind.Text,
                operate = delegate(IRuleEvalHost host, RuleValue subject, RuleValue arg,
                    out string code, out string reason)
                {
                    code = "log.written";
                    reason = arg.IsText && !string.IsNullOrEmpty(arg.AsText)
                        ? arg.AsText
                        : "（没写文本）";
                    return RuleOperateStatus.Done;
                }
            });

            v.Add(new RuleVerbInfo
            {
                key = "op.goto", category = RuleVerbCategory.Operate,
                subject = RuleEntityKind.Pawn, argKind = RuleValueKind.Entity,
                argEntity = RuleEntityKind.Cell,
                operate = delegate(IRuleEvalHost host, RuleValue subject, RuleValue arg,
                    out string code, out string reason)
                {
                    var pawn = RuleEvalHost.PawnOf(subject);
                    if (pawn == null || !pawn.Spawned || pawn.Map == null)
                    {
                        code = "goto.not_spawned";
                        reason = "执行者不在任何地图上。";
                        return RuleOperateStatus.Failed;
                    }

                    var cell = arg.AsCell;
                    if (!cell.IsValid)
                    {
                        code = "goto.no_cell";
                        reason = "没有解析出目的地。";
                        return RuleOperateStatus.Failed;
                    }

                    var target = new IntVec3(cell.x, 0, cell.z);
                    if ((pawn.Position - target).LengthHorizontalSquared <= 4)
                    {
                        code = "goto.already_there";
                        reason = "已经在目的地附近。";
                        return RuleOperateStatus.AlreadySatisfied;
                    }

                    var parms = TraverseParms.For(pawn, Danger.Deadly, TraverseMode.PassDoors);
                    if (!pawn.Map.reachability.CanReach(pawn.Position, target, PathEndMode.OnCell, parms))
                    {
                        code = "goto.unreachable";
                        reason = "到目的地的路不通。";
                        return RuleOperateStatus.Rejected;
                    }

                    var job = JobMaker.MakeJob(JobDefOf.Goto, target);
                    job.locomotionUrgency = LocomotionUrgency.Jog;
                    job.expiryInterval = 2000;

                    if (!pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc))
                    {
                        // 原版第一顺位：玩家征召、更高优先级的 job 占着，就规规矩矩让开。
                        code = "goto.job_rejected";
                        reason = "原版拒绝了这条指令（可能被更高优先级的 job 占着，或被玩家征召）。";
                        return RuleOperateStatus.Rejected;
                    }

                    code = "goto.issued";
                    reason = "已下发前往 " + target + " 的 job。";
                    return RuleOperateStatus.Done;
                }
            });

            v.Add(new RuleVerbInfo
            {
                // 「脱下别人的帽子」原版没有对应的 job（JobDefOf.Strip 只对尸体和倒地的人生效），
                // 所以它只能是 God 操作：原版不肯替你做的事，只能自己动手。
                key = "op.removeApparel", category = RuleVerbCategory.Operate,
                subject = RuleEntityKind.Pawn, argKind = RuleValueKind.Entity,
                argEntity = RuleEntityKind.Thing,
                // 穿不了衣服的主体（机械族、动物）身上不该出现"脱下"。
                requires = RuleCapability.Apparel,
                tier = RuleTier.Developer,
                operate = delegate(IRuleEvalHost host, RuleValue subject, RuleValue arg,
                    out string code, out string reason)
                {
                    var pawn = RuleEvalHost.PawnOf(subject);
                    var apparel = RuleEvalHost.ThingOf(arg) as Apparel;

                    if (pawn == null || pawn.apparel == null)
                    {
                        code = "apparel.no_pawn";
                        reason = "这不是一个能穿衣服的人。";
                        return RuleOperateStatus.Failed;
                    }

                    if (apparel == null)
                    {
                        code = "apparel.not_apparel";
                        reason = "要脱的不是一件衣服。";
                        return RuleOperateStatus.Failed;
                    }

                    if (!pawn.apparel.WornApparel.Contains(apparel))
                    {
                        code = "apparel.not_worn";
                        reason = "他没穿着这件。";
                        return RuleOperateStatus.AlreadySatisfied;
                    }

                    pawn.apparel.Remove(apparel);
                    code = "apparel.removed";
                    reason = "已脱下 " + apparel.LabelShort + "。";
                    return RuleOperateStatus.Done;
                }
            });
        }

        // ── 小工具 ────────────────────────────────────────────────────

        private static bool Fail(out RuleValue value, out string code, out string reason,
            string reasonCode, string text)
        {
            value = RuleValue.None;
            code = reasonCode;
            reason = text;
            return false;
        }

        private static bool Ok(RuleValue result, out RuleValue value, out string code, out string reason)
        {
            value = result;
            code = null;
            reason = null;
            return true;
        }
    }
}
