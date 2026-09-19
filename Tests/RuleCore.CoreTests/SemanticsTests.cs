using System;
using System.Collections.Generic;
using RuleCore.Core;

namespace RuleCore.CoreTests
{
    /// <summary>
    /// 新语义层（实体表达式路径 + 类型过滤词表 + 嵌套检测树）的单测。
    ///
    /// **为什么这一层必须脱离游戏测**：它承载的是规则语言的语义——
    /// "集合没归约该报什么"、"筛选里读到的是元素还是主体"、"或里面有一支读不到该报谁"。
    /// 这些结论在游戏里要靠制造特定世界状态才能碰上，而在这里用一个假宿主就能穷举。
    /// 真正的游戏知识（怎么读温度、怎么下 job）在 Verse 那一侧，这里一行都不碰。
    ///
    /// 假宿主的设计要点：它只认「把手.属性名 → 值」这张平坦表，
    /// 所以"同一个模板在两个不同元素上读出不同值"这种情形可以随手造出来——
    /// 而那正是筛选与归约最容易写错的地方。
    /// </summary>
    internal static class SemanticsTests
    {
        // ── 假宿主 ────────────────────────────────────────────────────

        private sealed class FakeHost : IRuleEvalHost
        {
            public readonly Dictionary<string, RuleValue> Roots =
                new Dictionary<string, RuleValue>(StringComparer.Ordinal);

            public readonly Dictionary<string, RuleValue> Props =
                new Dictionary<string, RuleValue>(StringComparer.Ordinal);

            /// <summary>属性读取失败注入：键 → 原因码。</summary>
            public readonly Dictionary<string, string> FailProps =
                new Dictionary<string, string>(StringComparer.Ordinal);

            public RuleValue ElementValue;

            public RuleValue Subject
            {
                get
                {
                    RuleValue value;
                    return Roots.TryGetValue("Subject", out value) ? value : RuleValue.None;
                }
            }

            public RuleValue Element
            {
                get { return ElementValue; }
                set { ElementValue = value; }
            }

            /// <summary>测试里不测指名绑定。</summary>
            public string SubjectRef
            {
                get { return null; }
            }

            public RuleValue Map
            {
                get
                {
                    RuleValue value;
                    return Roots.TryGetValue("Map", out value)
                        ? value
                        : RuleValue.OfEntity(RuleEntityKind.Map, "mapA");
                }
            }

            public bool TryRoot(RuleRootKind kind, RuleValue literal, out RuleValue value,
                out string reasonCode, out string reason)
            {
                if (kind == RuleRootKind.Literal)
                {
                    value = literal;
                    reasonCode = null;
                    reason = null;
                    return true;
                }

                if (kind == RuleRootKind.Element)
                {
                    value = ElementValue;
                    reasonCode = null;
                    reason = null;
                    return true;
                }

                string key = kind.ToString();
                if (Roots.TryGetValue(key, out value))
                {
                    reasonCode = null;
                    reason = null;
                    return true;
                }

                value = RuleValue.None;
                reasonCode = "fake.no_root";
                reason = "假宿主没有这个根：" + key;
                return false;
            }

            public bool TryReduce(RuleValue set, RuleReduceKind kind, out RuleValue value,
                out string reasonCode, out string reason)
            {
                // `最近` 之外的归约都该在 Core 里算完，走到这里说明分支写错了。
                value = RuleValue.None;
                reasonCode = "fake.reduce_reached";
                reason = "假宿主不该被要求做这个归约：" + kind;
                return false;
            }
        }

        // ── 假数据源（对应 Verse 侧那些 IExposable 类）──────────────────

        private sealed class FakeStep
        {
            public RuleStepKind kind;
            public string property;
            public FakeExpr filter;
            public RuleReduceKind reduce;
        }

        private sealed class FakePath : IRulePathSource
        {
            public RuleRootKind rootKind = RuleRootKind.Subject;
            public RuleValue rootLiteral = RuleValue.None;
            public readonly List<FakeStep> Steps = new List<FakeStep>();

            // 返回具体类型而不是接口，链式调用才走得下去。
            public FakePath Property(string key)
            {
                Steps.Add(new FakeStep { kind = RuleStepKind.Property, property = key });
                return this;
            }

            public FakePath Filter(FakeExpr predicate)
            {
                Steps.Add(new FakeStep { kind = RuleStepKind.Filter, filter = predicate });
                return this;
            }

            public FakePath Reduce(RuleReduceKind reduce)
            {
                Steps.Add(new FakeStep { kind = RuleStepKind.Reduce, reduce = reduce });
                return this;
            }

            public RuleRootKind RootKind { get { return rootKind; } }
            public RuleValue RootLiteral { get { return rootLiteral; } }
            public int StepCount { get { return Steps.Count; } }

            public RuleStepKind StepKindAt(int index) { return Steps[index].kind; }
            public string PropertyKeyAt(int index) { return Steps[index].property; }
            public IRuleExprSource FilterAt(int index) { return Steps[index].filter; }
            public RuleReduceKind ReduceAt(int index) { return Steps[index].reduce; }
        }

        private sealed class FakeOperand : IRuleOperandSource
        {
            public RuleValueKind kind = RuleValueKind.None;
            public RuleValue literal = RuleValue.None;
            public FakePath path;

            public static FakeOperand OfLiteral(RuleValue value)
            {
                return new FakeOperand { kind = value.kind, literal = value };
            }

            public static FakeOperand OfPath(FakePath value)
            {
                return new FakeOperand { kind = RuleValueKind.Entity, path = value };
            }

            public static FakeOperand Number(float value)
            {
                return OfLiteral(RuleValue.OfNumber(value));
            }

            public static FakeOperand Key(string value)
            {
                return OfLiteral(RuleValue.OfKey(value));
            }

            public RuleValueKind Kind { get { return kind; } }
            public RuleValue Literal { get { return literal; } }
            public IRulePathSource Path { get { return path; } }
        }

        private sealed class FakeExpr : IRuleExprSource
        {
            public RuleExprNodeKind kind = RuleExprNodeKind.Detect;
            public readonly List<FakeExpr> Children = new List<FakeExpr>();

            public FakePath subject;
            public string verbKey;
            public FakeOperand argument;

            public static FakeExpr And(params FakeExpr[] children)
            {
                var expr = new FakeExpr { kind = RuleExprNodeKind.And };
                expr.Children.AddRange(children);
                return expr;
            }

            public static FakeExpr Or(params FakeExpr[] children)
            {
                var expr = new FakeExpr { kind = RuleExprNodeKind.Or };
                expr.Children.AddRange(children);
                return expr;
            }

            public static FakeExpr Not(FakeExpr child)
            {
                var expr = new FakeExpr { kind = RuleExprNodeKind.Not };
                expr.Children.Add(child);
                return expr;
            }

            public static FakeExpr Detect(string verb, FakePath subject, FakeOperand arg)
            {
                return new FakeExpr
                {
                    kind = RuleExprNodeKind.Detect,
                    verbKey = verb,
                    subject = subject,
                    argument = arg
                };
            }

            public RuleExprNodeKind NodeKind { get { return kind; } }
            public int ChildCount { get { return Children.Count; } }
            public IRuleExprSource ChildAt(int index) { return Children[index]; }
            public IRulePathSource Subject { get { return subject; } }
            public string VerbKey { get { return verbKey; } }
            public IRuleOperandSource Argument { get { return argument; } }
        }

        /// <summary>可编排的检测谓词：返回值与是否"读不到"都可控。</summary>
        private sealed class FakeVerb
        {
            public bool Result = true;
            public bool Readable = true;
            public int Calls;

            /// <summary>把主语把手记下来，用来断言"筛选里读到的到底是哪个元素"。</summary>
            public object LastSubjectHandle;

            public bool TryRun(IRuleEvalHost host, RuleValue subject, RuleValue arg,
                out bool passed, out string reasonCode, out string reason)
            {
                Calls++;
                LastSubjectHandle = subject.AsHandle;

                if (!Readable)
                {
                    passed = false;
                    reasonCode = "unreadable";
                    reason = "假谓词读不到。";
                    return false;
                }

                passed = Result;
                reasonCode = passed ? null : "fake.false";
                reason = passed ? null : "假谓词判定不成立。";
                return true;
            }
        }

        /// <summary>直接比较两个数值的检测谓词：让路径测试能写出真实的一句话。</summary>
        private sealed class NumberCompareVerb
        {
            public RuleOperator Operator = RuleOperator.Greater;

            public bool TryRun(IRuleEvalHost host, RuleValue subject, RuleValue arg,
                out bool passed, out string reasonCode, out string reason)
            {
                if (!subject.IsNumber)
                {
                    passed = false;
                    reasonCode = "compare.not_number";
                    reason = "主语不是一个数。";
                    return false;
                }

                if (!arg.IsNumber)
                {
                    passed = false;
                    reasonCode = "compare.no_threshold";
                    reason = "没有填参照值。";
                    return false;
                }

                passed = RuleCompare.Apply(Operator, subject.AsNumber, arg.AsNumber);
                reasonCode = passed ? null : "compare.false";
                reason = passed
                    ? null
                    : subject.AsNumber.ToString("0.##") + " 不满足 "
                      + RuleCompare.Symbol(Operator) + " " + arg.AsNumber.ToString("0.##");
                return true;
            }
        }

        /// <summary>操作谓词的假实现：只记调用次数，用来验证"操作不会被当成检测"这类边界。</summary>
        private sealed class FakeOperate
        {
            public bool Ok = true;
            public int Calls;

            public RuleOperateStatus Run(IRuleEvalHost host, RuleValue subject, RuleValue arg,
                out string reasonCode, out string reason)
            {
                Calls++;
                reasonCode = Ok ? null : "fake.operate_failed";
                reason = Ok ? null : "假操作失败。";
                return Ok ? RuleOperateStatus.Done : RuleOperateStatus.Failed;
            }
        }

        /// <summary>
        /// 属性读取现在由**词表里的 reader** 直接做（和谓词一样），所以假宿主的数据表
        /// 要通过 reader 暴露出去。测试中途会改 Props（换着装、改耐久），
        /// 所以 reader 必须每次现查，不能在注册时把值抄下来。
        /// </summary>
        private static RulePropertyReadHandler FromHost(string propertyKey)
        {
            return delegate(IRuleEvalHost h, RuleValue owner, out RuleValue value,
                out string code, out string reason)
            {
                var host = h as FakeHost;
                string id = owner.AsHandle as string ?? "(null)";
                string full = id + ":" + propertyKey;

                if (host == null)
                {
                    value = RuleValue.None;
                    code = "fake.no_host";
                    reason = "不是假宿主。";
                    return false;
                }

                string failCode;
                if (host.FailProps.TryGetValue(full, out failCode))
                {
                    value = RuleValue.None;
                    code = failCode;
                    reason = "假宿主故意读不到：" + full;
                    return false;
                }

                if (host.Props.TryGetValue(full, out value))
                {
                    code = null;
                    reason = null;
                    return true;
                }

                value = RuleValue.None;
                code = "fake.no_property";
                reason = "假宿主没有这个属性：" + full;
                return false;
            };
        }

        // ── 词表 ──────────────────────────────────────────────────────

        /// <summary>
        /// 造一份"像真的"词表：地图有温度、天气；主体有血量、着装、位置；着装元素有耐久与是不是帽子。
        /// 属性顺序是设计顺序，测试会断言它被原样保持。
        /// </summary>
        private static RuleVocabulary BuildVocabulary(out FakeVerb probe,
            out NumberCompareVerb compare, out FakeVerb eventVerb)
        {
            var vocab = new RuleVocabulary();

            vocab.Add(new RulePropertyInfo
            {
                key = "map.outdoorTemp", owner = RuleEntityKind.Map,
                result = RuleValueKind.Number, unit = "C", decimals = 1,
                reader = FromHost("map.outdoorTemp")
            });
            vocab.Add(new RulePropertyInfo
            {
                key = "map.weather", owner = RuleEntityKind.Map, result = RuleValueKind.Enum,
                reader = FromHost("map.weather")
            });
            vocab.Add(new RulePropertyInfo
            {
                key = "pawn.health", owner = RuleEntityKind.Pawn,
                result = RuleValueKind.Number, percent = true,
                reader = FromHost("pawn.health")
            });
            vocab.Add(new RulePropertyInfo
            {
                key = "pawn.position", owner = RuleEntityKind.Pawn, result = RuleValueKind.Coord,
                reader = FromHost("pawn.position")
            });
            vocab.Add(new RulePropertyInfo
            {
                key = "pawn.apparel", owner = RuleEntityKind.Pawn,
                // 只有能穿衣服的宿主才有意义——"机械族身上不列着装"就靠这一行。
                requires = RuleCapability.Apparel,
                result = RuleValueKind.EntitySet, resultEntity = RuleEntityKind.Thing,
                reader = FromHost("pawn.apparel")
            });
            vocab.Add(new RulePropertyInfo
            {
                // 机械族身上不该出现的那一条。测试里就拿它当"饱食度"。
                key = "pawn.food", owner = RuleEntityKind.Pawn,
                requires = RuleCapability.NeedFood,
                result = RuleValueKind.Number, percent = true,
                reader = FromHost("pawn.food")
            });
            vocab.Add(new RulePropertyInfo
            {
                key = "thing.durability", owner = RuleEntityKind.Thing,
                result = RuleValueKind.Number, percent = true,
                reader = FromHost("thing.durability")
            });

            probe = new FakeVerb { Result = true };
            compare = new NumberCompareVerb { Operator = RuleOperator.Greater };
            eventVerb = new FakeVerb { Result = true };

            vocab.Add(new RuleVerbInfo
            {
                key = "compare.greater", category = RuleVerbCategory.Detect,
                subject = RuleEntityKind.Any, argKind = RuleValueKind.Number,
                subjectValueKind = RuleValueKind.Number,
                detect = compare.TryRun
            });
            vocab.Add(new RuleVerbInfo
            {
                key = "state.probe", category = RuleVerbCategory.Detect,
                subject = RuleEntityKind.Any, argKind = RuleValueKind.None,
                detect = probe.TryRun
            });
            vocab.Add(new RuleVerbInfo
            {
                key = "state.quiet", category = RuleVerbCategory.Detect,
                subject = RuleEntityKind.Pawn, argKind = RuleValueKind.None,
                failure = RuleFailureMode.QuietSkip,
                detect = new FakeVerb { Result = false }.TryRun
            });
            vocab.Add(new RuleVerbInfo
            {
                key = "state.unreadable", category = RuleVerbCategory.Detect,
                subject = RuleEntityKind.Any, argKind = RuleValueKind.None,
                detect = new FakeVerb { Readable = false }.TryRun
            });
            vocab.Add(new RuleVerbInfo
            {
                key = "event.arrive", category = RuleVerbCategory.Detect,
                subject = RuleEntityKind.Event, argKind = RuleValueKind.Entity,
                argEntity = RuleEntityKind.Map, edge = RuleEdge.Edge,
                detect = eventVerb.TryRun
            });
            vocab.Add(new RuleVerbInfo
            {
                key = "op.goto", category = RuleVerbCategory.Operate,
                subject = RuleEntityKind.Pawn, argKind = RuleValueKind.Entity,
                argEntity = RuleEntityKind.Cell,
                // 能力位也可以挂在谓词上：穿不了衣服的人身上不该出现"脱下"。
                requires = RuleCapability.Apparel,
                operate = new FakeOperate().Run
            });

            return vocab;
        }

        private static FakePath Subject()
        {
            return new FakePath { rootKind = RuleRootKind.Subject };
        }

        private static FakePath Map()
        {
            return new FakePath { rootKind = RuleRootKind.Map };
        }

        // ── 入口 ──────────────────────────────────────────────────────

        public static void Run()
        {
            Console.WriteLine();
            Console.WriteLine("== 词表：按类型过滤 ==");
            RunVocabularyTests();

            Console.WriteLine();
            Console.WriteLine("== 词表：按宿主能力过滤（机械族不列饱食度）==");
            RunCapabilityTests();

            Console.WriteLine();
            Console.WriteLine("== 类型匹配 ==");
            RunTypeMatchTests();

            Console.WriteLine();
            Console.WriteLine("== 实体表达式路径 ==");
            RunPathTests();

            Console.WriteLine();
            Console.WriteLine("== 嵌套检测树 ==");
            RunExprTests();

            Console.WriteLine();
            Console.WriteLine("== 数值格式化（编辑器的单位与百分号）==");
            RunFormatTests();
        }

        /// <summary>
        /// 按**宿主能力**过滤 —— "机械族身上不列饱食度"这件事的全部机制。
        ///
        /// 它是纯函数（能力位是数据，不是游戏对象），所以这里能穷举钉死；
        /// 一旦它错了，玩家看到的是"词表里没有这个功能"而不是"选错了"，查不出答案。
        /// </summary>
        private static void RunCapabilityTests()
        {
            FakeVerb probe;
            NumberCompareVerb compare;
            FakeVerb eventVerb;
            var vocab = BuildVocabulary(out probe, out compare, out eventVerb);

            var props = new List<RulePropertyInfo>();
            var rejects = new List<RuleRejection>();

            // ── 判据本身 ──────────────────────────────────────────────
            Program.Check("能力判据：(have & need) == need",
                RuleVocabulary.CapabilityMatches(RuleCapability.Pawn | RuleCapability.NeedFood,
                    RuleCapability.NeedFood)
                && !RuleVocabulary.CapabilityMatches(RuleCapability.Pawn, RuleCapability.NeedFood),
                "有 NeedFood 才通过 NeedFood 检查");

            Program.Check("能力判据：need=None 恒通过（这一行不挑宿主）",
                RuleVocabulary.CapabilityMatches(RuleCapability.None, RuleCapability.None)
                && RuleVocabulary.CapabilityMatches(RuleCapability.Pawn, RuleCapability.None),
                "requires 为 None 时任何宿主都通过");

            // **拿不到样本时必须全都给。** 反过来（当作"什么都没有"）会让
            // 主体还没绑定时整个属性列表是空的——玩家会以为词表里什么都没有。
            Program.Check("能力判据：have=All 恒通过（不知道宿主是谁时宁可多给）",
                RuleVocabulary.CapabilityMatches(RuleCapability.All, RuleCapability.NeedFood)
                && RuleVocabulary.CapabilityMatches(RuleCapability.All, RuleCapability.Humanlike),
                "All 通过一切检查");

            // ── 属性：人 vs 机械族 ────────────────────────────────────
            RuleCapability human = RuleCapability.Pawn | RuleCapability.Humanlike
                | RuleCapability.Biological | RuleCapability.NeedFood | RuleCapability.Apparel;

            RuleCapability mech = RuleCapability.Pawn | RuleCapability.Mechanoid
                | RuleCapability.NeedEnergy;

            vocab.CollectPropertiesFor(RuleEntityKind.Pawn, human, props, rejects);
            bool humanHasFood = false;
            bool humanHasApparel = false;
            for (int i = 0; i < props.Count; i++)
            {
                if (props[i].key == "pawn.food") humanHasFood = true;
                if (props[i].key == "pawn.apparel") humanHasApparel = true;
            }
            Program.Check("人的身上列出饱食度与着装", humanHasFood && humanHasApparel,
                "count=" + props.Count);

            vocab.CollectPropertiesFor(RuleEntityKind.Pawn, mech, props, rejects);

            bool mechHasFood = false;
            bool mechHasApparel = false;
            for (int i = 0; i < props.Count; i++)
            {
                if (props[i].key == "pawn.food") mechHasFood = true;
                if (props[i].key == "pawn.apparel") mechHasApparel = true;
            }

            // 这一条就是整个需求的那句话。
            Program.Check("机械族的属性列表里没有饱食度、没有着装",
                !mechHasFood && !mechHasApparel, "count=" + props.Count);

            // 被挡掉的**必须带原因**，否则玩家分不清"没有它"和"我选错了主体"。
            Program.Check("被挡掉的属性进拒绝清单并带上缺的能力位", rejects.Count == 2,
                "rejects=" + rejects.Count);

            bool foodExplained = false;
            bool apparelExplained = false;
            for (int i = 0; i < rejects.Count; i++)
            {
                if (rejects[i].key == "pawn.food"
                    && rejects[i].missing == RuleCapability.NeedFood) foodExplained = true;
                if (rejects[i].key == "pawn.apparel"
                    && rejects[i].missing == RuleCapability.Apparel) apparelExplained = true;
            }
            Program.Check("拒绝原因指向缺的那一位（饱食需求 / 能穿衣服）",
                foodExplained && apparelExplained, "rejects=" + rejects.Count);

            // 人身上不该有任何拒绝项——全都用得上。
            vocab.CollectPropertiesFor(RuleEntityKind.Pawn, human, props, rejects);
            Program.Check("人身上没有被挡掉的属性", rejects.Count == 0, "rejects=" + rejects.Count);

            // ── 谓语 ─────────────────────────────────────────────────
            var verbs = new List<RuleVerbInfo>();

            vocab.CollectVerbsFor(RuleEntityKind.Pawn, RuleValueKind.Entity,
                RuleVerbCategory.Operate, human, verbs, rejects);
            bool humanCanUndress = false;
            for (int i = 0; i < verbs.Count; i++)
            {
                if (verbs[i].key == "op.goto") humanCanUndress = true;
            }
            Program.Check("能穿衣服的人身上有「脱下」（测试里叫 op.goto）", humanCanUndress,
                "count=" + verbs.Count);

            vocab.CollectVerbsFor(RuleEntityKind.Pawn, RuleValueKind.Entity,
                RuleVerbCategory.Operate, mech, verbs, rejects);
            bool mechCanUndress = false;
            for (int i = 0; i < verbs.Count; i++)
            {
                if (verbs[i].key == "op.goto") mechCanUndress = true;
            }
            Program.Check("机械族身上没有「脱下」", !mechCanUndress, "count=" + verbs.Count);
            Program.Check("被挡掉的谓词也有原因", rejects.Count == 1 && rejects[0].key == "op.goto",
                "rejects=" + rejects.Count);

            // ── 老签名必须等价于"不按能力过滤" ────────────────────────
            // 它是给"拿不到样本"的调用方用的，行为不能因为这次改动而变。
            var legacy = new List<RulePropertyInfo>();
            vocab.CollectPropertiesFor(RuleEntityKind.Pawn, legacy);
            Program.Check("不带能力位的重载 = All（老调用方行为不变）", legacy.Count == 4,
                "legacy=" + legacy.Count);
        }

        private static void RunVocabularyTests()
        {
            FakeVerb probe;
            NumberCompareVerb compare;
            FakeVerb eventVerb;
            var vocab = BuildVocabulary(out probe, out compare, out eventVerb);

            var propertyList = new List<RulePropertyInfo>();
            var verbList = new List<RuleVerbInfo>();

            vocab.CollectPropertiesFor(RuleEntityKind.Pawn, propertyList);
            bool pawnOnly = propertyList.Count == 4;
            for (int i = 0; i < propertyList.Count; i++)
            {
                if (propertyList[i].owner != RuleEntityKind.Pawn) pawnOnly = false;
            }
            Program.Check("属性按所有者过滤：Pawn 只列出 Pawn 的", pawnOnly,
                "count=" + propertyList.Count);

            vocab.CollectPropertiesFor(RuleEntityKind.Map, propertyList);
            Program.Check("属性按所有者过滤：Map 只列出 Map 的",
                propertyList.Count == 2 && propertyList[0].key == "map.outdoorTemp",
                "first=" + (propertyList.Count > 0 ? propertyList[0].key : "-"));

            Program.Check("属性顺序 = 注册顺序（设计顺序，不是字典序）",
                propertyList.Count == 2 && propertyList[0].key == "map.outdoorTemp"
                && propertyList[1].key == "map.weather",
                "order=" + (propertyList.Count > 0 ? propertyList[0].key : "-"));

            vocab.CollectVerbsFor(RuleEntityKind.Pawn, RuleValueKind.Entity, RuleVerbCategory.Detect, verbList);
            bool hasQuiet = false;
            bool hasGoto = false;
            for (int i = 0; i < verbList.Count; i++)
            {
                if (verbList[i].key == "state.quiet") hasQuiet = true;
                if (verbList[i].key == "op.goto") hasGoto = true;
            }
            Program.Check("谓词按主语类型过滤：Pawn 身上有 quiet", hasQuiet,
                "count=" + verbList.Count);

            // **值类型过滤**：主语是一个 Pawn（Entity），所以只吃数值的「大于」不该出现。
            // 没有这一条，「大于」会出现在小人身上，玩家选了必然报 compare.not_number。
            bool numberVerbOnPawn = false;
            for (int i = 0; i < verbList.Count; i++)
            {
                if (verbList[i].key == "compare.greater") numberVerbOnPawn = true;
            }
            Program.Check("值类型过滤：只吃数值的谓词不出现在实体主语上", !numberVerbOnPawn,
                "count=" + verbList.Count);

            vocab.CollectVerbsFor(RuleEntityKind.Any, RuleValueKind.Number, RuleVerbCategory.Detect, verbList);
            bool numberVerbOnNumber = false;
            for (int i = 0; i < verbList.Count; i++)
            {
                if (verbList[i].key == "compare.greater") numberVerbOnNumber = true;
            }
            Program.Check("值类型过滤：数值主语上该出现数值谓词", numberVerbOnNumber,
                "count=" + verbList.Count);
            Program.Check("谓词按类别过滤：检测列表里不出现操作", !hasGoto,
                "count=" + verbList.Count);

            vocab.CollectVerbsFor(RuleEntityKind.Map, RuleValueKind.Entity, RuleVerbCategory.Detect, verbList);
            bool mapHasPawnVerb = false;
            for (int i = 0; i < verbList.Count; i++)
            {
                if (verbList[i].key == "state.quiet") mapHasPawnVerb = true;
            }
            Program.Check("只作用于 Pawn 的谓词不出现在 Map 的列表里", !mapHasPawnVerb,
                "count=" + verbList.Count);

            vocab.CollectVerbsFor(RuleEntityKind.Event, RuleValueKind.Entity, RuleVerbCategory.Operate, verbList);
            Program.Check("事件型实体身上没有可用操作（这个阶段）", verbList.Count == 0,
                "count=" + verbList.Count);

            // 登记了描述却忘了实现，是这张表最危险的失效方式：玩家看得见、选了没反应。
            bool everyVerbImplemented = true;
            var allKinds = new[]
            {
                RuleEntityKind.Map, RuleEntityKind.Pawn, RuleEntityKind.Thing,
                RuleEntityKind.Room, RuleEntityKind.Cell, RuleEntityKind.Event
            };
            for (int k = 0; k < allKinds.Length; k++)
            {
                vocab.CollectVerbsFor(allKinds[k], RuleValueKind.Entity, RuleVerbCategory.Detect, verbList);
                for (int i = 0; i < verbList.Count; i++)
                {
                    if (verbList[i].detect == null) everyVerbImplemented = false;
                }
                vocab.CollectVerbsFor(allKinds[k], RuleValueKind.Entity, RuleVerbCategory.Operate, verbList);
                for (int i = 0; i < verbList.Count; i++)
                {
                    if (verbList[i].operate == null) everyVerbImplemented = false;
                }
            }
            Program.Check("每个登记过的谓词都挂了实现（防止有描述没行为）",
                everyVerbImplemented, "detect/operate 都不为 null");

            // 重复键不覆盖、不抛异常，但要能被测出来。
            var dup = new RuleVocabulary();
            dup.Add(new RulePropertyInfo { key = "a.b" });
            dup.Add(new RulePropertyInfo { key = "a.b" });
            Program.Check("重复属性键被记录而不是覆盖",
                dup.PropertyCount == 1 && dup.DuplicateKeys.Count == 1,
                "count=" + dup.PropertyCount + " dup=" + dup.DuplicateKeys.Count);

            var dupVerb = new RuleVocabulary();
            dupVerb.Add(new RuleVerbInfo { key = "x.y" });
            dupVerb.Add(new RuleVerbInfo { key = "x.y" });
            Program.Check("重复谓词键被记录而不是覆盖",
                dupVerb.VerbCount == 1 && dupVerb.DuplicateKeys.Count == 1,
                "count=" + dupVerb.VerbCount);

            Program.Check("归约表：第一期只有 第一个/最近/数量",
                RuleVocabulary.AllReduces.Count == 3
                && RuleVocabulary.ReduceInfo(RuleReduceKind.First) != null
                && RuleVocabulary.ReduceInfo(RuleReduceKind.Count) != null
                && RuleVocabulary.ReduceInfo(RuleReduceKind.Sum) == null,
                "count=" + RuleVocabulary.AllReduces.Count);
        }

        private static void RunTypeMatchTests()
        {
            bool anyMatchesAll = RuleVocabulary.EntityKindMatches(RuleEntityKind.Any, RuleEntityKind.Pawn)
                && RuleVocabulary.EntityKindMatches(RuleEntityKind.Pawn, RuleEntityKind.Pawn);

            // **实到类型未知时不被具体要求认下**（actual == Any 只匹配 Any）。
            // 早期版本两个方向都放行，结果是"把数字喂给只作用于小人的谓词"也能过。
            Program.Check("Any 作为期望匹配一切；作为实到类型则不被具体要求认下",
                anyMatchesAll
                && !RuleVocabulary.EntityKindMatches(RuleEntityKind.Map, RuleEntityKind.Pawn)
                && !RuleVocabulary.EntityKindMatches(RuleEntityKind.Map, RuleEntityKind.Any),
                "Map vs Pawn、Map vs Any 都应当不匹配");

            var verb = new RuleVerbInfo { key = "v", argKind = RuleValueKind.Number };

            Program.Check("宾语：没填算「还没填」而不是「类型错」",
                RuleVocabulary.ArgMatches(verb, RuleValue.None),
                "空槽若算类型错，编辑器会把未填显示成填错");

            Program.Check("宾语：类型对了才通过",
                RuleVocabulary.ArgMatches(verb, RuleValue.OfNumber(1f))
                && !RuleVocabulary.ArgMatches(verb, RuleValue.OfKey("Rain")),
                "Number 槽不接受 Enum");

            var entityVerb = new RuleVerbInfo
            {
                key = "e", argKind = RuleValueKind.Entity, argEntity = RuleEntityKind.Cell
            };

            Program.Check("宾语：实体还要类型对上",
                RuleVocabulary.ArgMatches(entityVerb, RuleValue.OfEntity(RuleEntityKind.Cell))
                && !RuleVocabulary.ArgMatches(entityVerb, RuleValue.OfEntity(RuleEntityKind.Pawn)),
                "Cell 槽不接受 Pawn");

            var noArgVerb = new RuleVerbInfo { key = "n", argKind = RuleValueKind.None };
            Program.Check("不需要宾语的谓词，多填了也不报错",
                RuleVocabulary.ArgMatches(noArgVerb, RuleValue.OfNumber(5f)),
                "玩家可能先填宾语再改谓词，报错会很烦");
        }

        private static void RunPathTests()
        {
            FakeVerb probe;
            NumberCompareVerb compare;
            FakeVerb eventVerb;
            var vocab = BuildVocabulary(out probe, out compare, out eventVerb);

            var host = new FakeHost();
            host.Roots["Subject"] = RuleValue.OfEntity(RuleEntityKind.Pawn, "pawnA");
            host.Roots["Map"] = RuleValue.OfEntity(RuleEntityKind.Map, "mapA");
            host.Props["pawnA:pawn.health"] = RuleValue.OfNumber(0.4f);
            host.Props["mapA:map.outdoorTemp"] = RuleValue.OfNumber(-5f);
            host.Props["mapA:map.weather"] = RuleValue.OfKey("Rain");

            var health = Subject().Property("pawn.health");
            var outcome = RulePathEval.Evaluate(health, host, vocab);
            Program.Check("路径：读一个数",
                outcome.ok && outcome.value.IsNumber
                && Math.Abs(outcome.value.AsNumber - 0.4f) < 0.0001f,
                outcome.ok ? outcome.value.ToString() : outcome.reasonCode);

            var temp = Map().Property("map.outdoorTemp");
            outcome = RulePathEval.Evaluate(temp, host, vocab);
            Program.Check("路径：换一个根（本图）",
                outcome.ok && Math.Abs(outcome.value.AsNumber + 5f) < 0.0001f,
                outcome.ok ? outcome.value.ToString() : outcome.reasonCode);

            // 挂着两件衣服：一件帽子（耐久 0.3），一件大衣（耐久 0.9）。
            var hat = RuleValue.OfEntity(RuleEntityKind.Thing, "hat");
            var coat = RuleValue.OfEntity(RuleEntityKind.Thing, "coat");
            host.Props["pawnA:pawn.apparel"] = RuleValue.OfSet(RuleEntityKind.Thing,
                new List<RuleValue> { hat, coat });
            host.Props["hat:thing.durability"] = RuleValue.OfNumber(0.3f);
            host.Props["coat:thing.durability"] = RuleValue.OfNumber(0.9f);

            // "少写了一个归约"：手上还是集合就往下读属性。
            var noReduce = Subject().Property("pawn.apparel").Property("thing.durability");
            outcome = RulePathEval.Evaluate(noReduce, host, vocab);
            Program.Check("路径：集合没归约就往下读属性 → needs_reduce",
                !outcome.ok && outcome.reasonCode == "path.needs_reduce",
                outcome.reasonCode);

            // 对单个东西写筛选。
            var filterOnScalar = Subject().Property("pawn.health").Filter(
                FakeExpr.Detect("state.probe", new FakePath { rootKind = RuleRootKind.Element }, null));
            outcome = RulePathEval.Evaluate(filterOnScalar, host, vocab);
            Program.Check("路径：对单值写筛选 → filter_on_scalar",
                !outcome.ok && outcome.reasonCode == "path.filter_on_scalar",
                outcome.reasonCode);

            // 属性挂错类型。
            var wrongOwner = Map().Property("pawn.health");
            outcome = RulePathEval.Evaluate(wrongOwner, host, vocab);
            Program.Check("路径：把「血量」挂在图上 → property_wrong_type",
                !outcome.ok && outcome.reasonCode == "path.property_wrong_type",
                outcome.reasonCode);

            var unknown = Subject().Property("pawn.不存在");
            outcome = RulePathEval.Evaluate(unknown, host, vocab);
            Program.Check("路径：词表里没有的属性 → unknown_property",
                !outcome.ok && outcome.reasonCode == "path.unknown_property",
                outcome.reasonCode);

            // 数量归约。
            var count = Subject().Property("pawn.apparel").Reduce(RuleReduceKind.Count);
            outcome = RulePathEval.Evaluate(count, host, vocab);
            Program.Check("路径：数量归约 → 2",
                outcome.ok && Math.Abs(outcome.value.AsNumber - 2f) < 0.0001f,
                outcome.ok ? outcome.value.ToString() : outcome.reasonCode);

            // 第一个 + 属性：确定性地取第一件。
            var firstDurability = Subject().Property("pawn.apparel")
                .Reduce(RuleReduceKind.First).Property("thing.durability");
            outcome = RulePathEval.Evaluate(firstDurability, host, vocab);
            Program.Check("路径：第一个 → 读它的耐久（确定性，取第一件）",
                outcome.ok && Math.Abs(outcome.value.AsNumber - 0.3f) < 0.0001f,
                outcome.ok ? outcome.value.ToString() : outcome.reasonCode);

            // 空集合：是"读不到"，不是"读到了 0"。
            host.Props["pawnA:pawn.apparel"] = RuleValue.OfSet(RuleEntityKind.Thing,
                new List<RuleValue>());
            var emptyFirst = Subject().Property("pawn.apparel").Reduce(RuleReduceKind.First);
            outcome = RulePathEval.Evaluate(emptyFirst, host, vocab);
            Program.Check("路径：空集合的「第一个」→ empty_set（不是 0）",
                !outcome.ok && outcome.reasonCode == "path.empty_set",
                outcome.reasonCode);

            var emptyCount = Subject().Property("pawn.apparel").Reduce(RuleReduceKind.Count);
            outcome = RulePathEval.Evaluate(emptyCount, host, vocab);
            Program.Check("路径：空集合的「数量」→ 0（这个才是真的 0）",
                outcome.ok && outcome.value.AsNumber == 0f,
                outcome.ok ? outcome.value.ToString() : outcome.reasonCode);

            // ── 筛选：这里是最容易错的地方 ──────────────────────────

            host.Props["pawnA:pawn.apparel"] = RuleValue.OfSet(RuleEntityKind.Thing,
                new List<RuleValue> { hat, coat });

            // 戴着帽子吗：着装[耐久 < 0.5].数量
            var filterExpr = FakeExpr.Not(FakeExpr.Detect("compare.greater",
                new FakePath { rootKind = RuleRootKind.Element }.Property("thing.durability"),
                FakeOperand.Number(0.5f)));

            var filtered = Subject().Property("pawn.apparel").Filter(filterExpr)
                .Reduce(RuleReduceKind.Count);
            outcome = RulePathEval.Evaluate(filtered, host, vocab);
            Program.Check("筛选：[非 耐久 > 0.5] 只剩帽子那件 → 数量 1",
                outcome.ok && Math.Abs(outcome.value.AsNumber - 1f) < 0.0001f,
                outcome.ok ? outcome.value.ToString() : outcome.reasonCode);

            // 筛选里读到的是**元素**，不是主体：探针把主语把手记下来就能验证。
            probe.LastSubjectHandle = null;
            var probeFilter = Subject().Property("pawn.apparel").Filter(
                FakeExpr.Detect("state.probe", new FakePath { rootKind = RuleRootKind.Element }, null))
                .Reduce(RuleReduceKind.Count);
            RulePathEval.Evaluate(probeFilter, host, vocab);
            Program.Check("筛选里 Element 根读到的是元素（不是本主体）",
                probe.Calls >= 2 && (string)probe.LastSubjectHandle == "coat",
                "last=" + (probe.LastSubjectHandle ?? "null"));

            // 筛选之后主体必须复原，否则后面的兄弟节点会读到上一个元素。
            outcome = RulePathEval.Evaluate(Subject().Property("pawn.health"), host, vocab);
            Program.Check("筛选结束后本主体被复原（不残留元素绑定）",
                outcome.ok && Math.Abs(outcome.value.AsNumber - 0.4f) < 0.0001f,
                outcome.ok ? outcome.value.ToString() : outcome.reasonCode);

            // 注册表写错：词表说产出 Number，实现给了 Enum。
            host.Props["mapA:map.outdoorTemp"] = RuleValue.OfKey("Rain");
            outcome = RulePathEval.Evaluate(Map().Property("map.outdoorTemp"), host, vocab);
            Program.Check("路径：词表声明与实际产出不一致 → property_type_mismatch",
                !outcome.ok && outcome.reasonCode == "path.property_type_mismatch",
                outcome.reasonCode);
            host.Props["mapA:map.outdoorTemp"] = RuleValue.OfNumber(-5f);

            // 宿主读不到时要原样带出原因，不能变成"不成立"。
            host.FailProps["pawnA:pawn.health"] = "health.no_data";
            outcome = RulePathEval.Evaluate(Subject().Property("pawn.health"), host, vocab);
            Program.Check("路径：宿主读不到 → 原样带出原因码",
                !outcome.ok && outcome.reasonCode == "health.no_data",
                outcome.reasonCode);
            host.FailProps.Clear();

            // 归约下放：假宿主不支持额外归约，必须报出来而不是静默通过。
            outcome = RulePathEval.Evaluate(
                Subject().Property("pawn.apparel").Reduce(RuleReduceKind.Nearest), host, vocab);
            Program.Check("路径：宿主没实现的下放归约 → 报错不静默",
                !outcome.ok && outcome.reasonCode == "fake.reduce_reached",
                outcome.reasonCode);
        }

        private static void RunExprTests()
        {
            FakeVerb probe;
            NumberCompareVerb compare;
            FakeVerb eventVerb;
            var vocab = BuildVocabulary(out probe, out compare, out eventVerb);

            var host = new FakeHost();
            host.Roots["Subject"] = RuleValue.OfEntity(RuleEntityKind.Pawn, "pawnA");
            host.Roots["Map"] = RuleValue.OfEntity(RuleEntityKind.Map, "mapA");
            host.Props["pawnA:pawn.health"] = RuleValue.OfNumber(0.4f);
            host.Props["mapA:map.outdoorTemp"] = RuleValue.OfNumber(-5f);

            var healthLow = FakeExpr.Detect("compare.greater", Subject().Property("pawn.health"),
                FakeOperand.Number(1f));

            var outcome = RuleExprEval.Evaluate(healthLow, host, vocab);
            Program.Check("检测：单叶成立（0.4 > 1 不成立）→ 不通过",
                !outcome.passed && outcome.reasonCode == "compare.false",
                outcome.reasonCode + " | " + outcome.reason);

            var healthOk = FakeExpr.Detect("compare.greater", Subject().Property("pawn.health"),
                FakeOperand.Number(0.1f));
            outcome = RuleExprEval.Evaluate(healthOk, host, vocab);
            Program.Check("检测：单叶成立（0.4 > 0.1）→ 通过", outcome.passed,
                outcome.reasonCode);

            // 且：全成立
            var andPass = FakeExpr.And(healthOk,
                FakeExpr.Detect("compare.greater", Map().Property("map.outdoorTemp"),
                    FakeOperand.Number(-10f)));
            outcome = RuleExprEval.Evaluate(andPass, host, vocab);
            Program.Check("且：两句都成立 → 通过", outcome.passed, outcome.reasonCode);

            // 且：短路并带出是第几句
            var andFail = FakeExpr.And(
                FakeExpr.Detect("compare.greater", Map().Property("map.outdoorTemp"),
                    FakeOperand.Number(0f)),
                healthOk);
            outcome = RuleExprEval.Evaluate(andFail, host, vocab);
            Program.Check("且：报第一句失败，且带出下标 and[0]",
                !outcome.passed && outcome.tracePath == "and[0]"
                && outcome.reasonCode == "compare.false",
                outcome.tracePath + " | " + outcome.reasonCode);

            // 或：任一成立
            var orPass = FakeExpr.Or(andFail, healthOk);
            outcome = RuleExprEval.Evaluate(orPass, host, vocab);
            Program.Check("或：有一支成立 → 通过", outcome.passed, outcome.reasonCode);

            // 或：全失败时报第一支，并带下标
            var orFail = FakeExpr.Or(
                FakeExpr.Detect("compare.greater", Map().Property("map.outdoorTemp"),
                    FakeOperand.Number(0f)),
                FakeExpr.Detect("compare.greater", Subject().Property("pawn.health"),
                    FakeOperand.Number(1f)));
            outcome = RuleExprEval.Evaluate(orFail, host, vocab);
            Program.Check("或：全失败时报第一支 or[0]", !outcome.passed && outcome.tracePath == "or[0]",
                outcome.tracePath);

            // 或：有一支"读不到"时优先报读不到的那支——把读不到的那支放在第二位，
            // 才能证明它真的在挑，而不是碰巧报了第一支。
            var orReadFail = FakeExpr.Or(
                FakeExpr.Detect("compare.greater", Subject().Property("pawn.health"),
                    FakeOperand.Number(1f)),
                FakeExpr.Detect("state.unreadable", Subject(), null));
            outcome = RuleExprEval.Evaluate(orReadFail, host, vocab);
            Program.Check("或：有一支读不到时优先报它（or[1] 而不是 or[0]）",
                !outcome.passed && outcome.tracePath == "or[1]"
                && outcome.reasonCode == "detect.read.unreadable",
                outcome.tracePath + " | " + outcome.reasonCode);

            // 非
            outcome = RuleExprEval.Evaluate(FakeExpr.Not(andFail), host, vocab);
            Program.Check("非：内层不成立 → 外层成立", outcome.passed, outcome.reasonCode);

            outcome = RuleExprEval.Evaluate(FakeExpr.Not(healthOk), host, vocab);
            Program.Check("非：内层成立 → 外层不成立，原因码 not.false",
                !outcome.passed && outcome.reasonCode == "not.false", outcome.reasonCode);

            // 嵌套的 tracePath 形状
            var nested = FakeExpr.And(healthOk, FakeExpr.Or(andFail, andFail));
            outcome = RuleExprEval.Evaluate(nested, host, vocab);
            // 路径要**一路走到最里面那个叶子**，不能停在中间层：
            // "and[1] 那一支不成立" 对排障没用，"and[1].or[0].and[0] 那句不成立" 才有用。
            Program.Check("嵌套失败路径一路走到叶子 = and[1].or[0].and[0]",
                !outcome.passed && outcome.tracePath == "and[1].or[0].and[0]",
                outcome.tracePath);

            // 把操作当检测用
            outcome = RuleExprEval.Evaluate(FakeExpr.Detect("op.goto", Subject(), null), host, vocab);
            Program.Check("把操作当检测用 → not_a_detect",
                !outcome.passed && outcome.reasonCode == "detect.not_a_detect", outcome.reasonCode);

            // 主语类型不对
            outcome = RuleExprEval.Evaluate(FakeExpr.Detect("state.quiet", Map(), null), host, vocab);
            Program.Check("谓词主语类型不对 → wrong_subject",
                !outcome.passed && outcome.reasonCode == "detect.wrong_subject", outcome.reasonCode);

            // 宾语类型不对
            outcome = RuleExprEval.Evaluate(
                FakeExpr.Detect("compare.greater", Subject().Property("pawn.health"),
                    FakeOperand.Key("Rain")), host, vocab);
            Program.Check("宾语类型不对 → wrong_arg",
                !outcome.passed && outcome.reasonCode == "detect.wrong_arg", outcome.reasonCode);

            // 未知谓词
            outcome = RuleExprEval.Evaluate(FakeExpr.Detect("nope.nope", Subject(), null), host, vocab);
            Program.Check("词表里没有的谓词 → unknown_verb",
                !outcome.passed && outcome.reasonCode == "detect.unknown_verb", outcome.reasonCode);

            // QuietSkip 必须一路上传（这是"条件/能力"合并后保留区别的唯一手段）
            outcome = RuleExprEval.Evaluate(FakeExpr.Detect("state.quiet", Subject(), null), host, vocab);
            Program.Check("QuietSkip 的不成立一路带到顶层（能力合并后的关键）",
                !outcome.passed && outcome.mode == RuleFailureMode.QuietSkip,
                outcome.mode.ToString());

            var quietNested = FakeExpr.And(healthOk,
                FakeExpr.Detect("state.quiet", Subject(), null));
            outcome = RuleExprEval.Evaluate(quietNested, host, vocab);
            Program.Check("嵌套里 QuietSkip 也能带上来（不会在 And 里被抹平）",
                !outcome.passed && outcome.mode == RuleFailureMode.QuietSkip
                && outcome.tracePath == "and[1]",
                outcome.mode + " | " + outcome.tracePath);

            // 事件型：谓语是边沿，宾语是另一个实体（袭击来到本图）
            var arrive = FakeExpr.Detect("event.arrive",
                new FakePath { rootKind = RuleRootKind.Literal, rootLiteral = RuleValue.OfEntity(RuleEntityKind.Event, "raid") },
                FakeOperand.OfPath(Map()));
            outcome = RuleExprEval.Evaluate(arrive, host, vocab);
            Program.Check("事件型检测：宾语是另一条实体路径（袭击来到本图）",
                outcome.passed, outcome.reasonCode + " | " + outcome.reason);

            // 空「或」
            outcome = RuleExprEval.Evaluate(FakeExpr.Or(), host, vocab);
            Program.Check("空「或」→ or.empty（不是静默通过）",
                !outcome.passed && outcome.reasonCode == "or.empty", outcome.reasonCode);

            // 空「且」在数学上是恒真。**这条测试是为了把"静默恒真"钉在明面上**：
            // 它是对的语义，但它也是"玩家刚点了 + 且 还没填"时的表现，
            // 所以载入期校验必须报出来（见 Rule.CollectConfigErrors）。
            outcome = RuleExprEval.Evaluate(FakeExpr.And(), host, vocab);
            Program.Check("空「且」→ 恒真（语义如此，而校验会报「还没往里加东西」）",
                outcome.passed, outcome.reasonCode);

            // 深度上限：手改 XML 造出的深链不能把栈打爆。
            var deep = FakeExpr.And();
            var cursorNode = deep;
            for (int i = 0; i < RuleExprEval.MaxNodeDepth + 4; i++)
            {
                var child = FakeExpr.And();
                cursorNode.Children.Add(child);
                cursorNode = child;
            }
            cursorNode.Children.Add(healthOk);

            outcome = RuleExprEval.Evaluate(deep, host, vocab);
            Program.Check("检测树超过深度上限 → too_deep（不爆栈）",
                !outcome.passed && outcome.reasonCode == "detect.too_deep",
                outcome.reasonCode);

            // 空表达式
            outcome = RuleExprEval.Evaluate(null, host, vocab);
            Program.Check("空检测树 → detect.missing",
                !outcome.passed && outcome.reasonCode == "detect.missing", outcome.reasonCode);
        }
        /// <summary>
        /// 格式化是编辑器"读起来是人话"的全部依据，也是最容易写反的一处
        /// （0.5 该显示成 50% 而不是 0.5，输入框里该填 50 而不是 0.5）。
        /// </summary>
        private static void RunFormatTests()
        {
            var percent = new RulePropertyInfo { percent = true, min = 0f, max = 1f };
            var temp = new RulePropertyInfo { unit = "℃", decimals = 1, min = -200f, max = 200f };
            var plain = new RulePropertyInfo { decimals = 0 };

            Program.Check("格式化：比例显示成百分号",
                RuleFormat.FormatNumber(0.5f, 2, null, true) == "50%"
                && RuleFormat.FormatNumber(0.123f, 2, null, true) == "12.3%",
                RuleFormat.FormatNumber(0.5f, 2, null, true));

            Program.Check("格式化：带单位时数值与单位之间不留空",
                RuleFormat.FormatNumber(-5f, 1, "℃", false) == "-5℃",
                RuleFormat.FormatNumber(-5f, 1, "℃", false));

            Program.Check("格式化：没有单位就不补空格",
                RuleFormat.FormatNumber(3f, 0, null, false) == "3",
                "[" + RuleFormat.FormatNumber(3f, 0, null, false) + "]");

            Program.Check("格式化：输入值双向转换（显示 50，存 0.5）",
                Math.Abs(RuleFormat.ToEditValue(0.5f, true) - 50f) < 0.0001f
                && Math.Abs(RuleFormat.FromEditValue(50f, true) - 0.5f) < 0.0001f,
                "写反了这个方向，玩家填 50 会存成 50 倍");

            float min;
            float max;

            RuleFormat.EditRange(percent, out min, out max);
            Program.Check("格式化：比例的输入范围是 0~100（不是 0~1）",
                min == 0f && max == 100f, min + "~" + max);

            RuleFormat.EditRange(temp, out min, out max);
            Program.Check("格式化：有声明范围就用声明的",
                min == -200f && max == 200f, min + "~" + max);

            RuleFormat.EditRange(plain, out min, out max);
            Program.Check("格式化：没声明范围时给一个有限区间（不是 float 极值）",
                min > -1e9f && max < 1e9f, min + "~" + max);

            Program.Check("格式化：输入框后缀来自元数据",
                RuleFormat.EditSuffix(percent) == "%"
                && RuleFormat.EditSuffix(temp) == "℃"
                && RuleFormat.EditSuffix(plain) == null,
                "百分号/单位/无");
        }    }
}
