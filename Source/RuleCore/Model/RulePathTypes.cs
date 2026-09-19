using RuleCore.Core;

namespace RuleCore
{
    /// <summary>
    /// 实体表达式路径的**类型推导** —— 「走到这一步手上是什么类型」。
    ///
    /// 它住在模型层而不是界面层，是因为**求值器、编辑器、载入期校验三方必须用同一套推导**：
    ///   · 编辑器靠它决定下拉里列什么（只列能用的）；
    ///   · 校验靠它在载入时报"这个谓词用错了类型"；
    ///   · 求值器靠它（间接地，通过词表声明）在跑起来时给出同一个结论。
    ///
    /// 三处各写一份的后果是"界面上能选、跑起来报错"——那正是这套东西最该消灭的体验。
    ///
    /// 推导**不读游戏状态**，只读词表元数据，所以它是纯函数。
    /// </summary>
    public static class RulePathTypes
    {
        /// <summary>根产出什么类型。</summary>
        public static void RootType(RulePath path, RuleEntityKind subjectKind, bool insideFilter,
            RuleEntityKind elementKind, out RuleValueKind kind, out RuleEntityKind entityKind)
        {
            switch (path != null ? path.rootKind : RuleRootKind.Subject)
            {
                case RuleRootKind.Subject:
                    kind = RuleValueKind.Entity;
                    entityKind = subjectKind;
                    return;

                case RuleRootKind.Element:
                    kind = RuleValueKind.Entity;
                    entityKind = insideFilter ? elementKind : RuleEntityKind.Any;
                    return;

                case RuleRootKind.Map:
                    kind = RuleValueKind.Entity;
                    entityKind = RuleEntityKind.Map;
                    return;

                case RuleRootKind.Colonists:
                    kind = RuleValueKind.EntitySet;
                    entityKind = RuleEntityKind.Pawn;
                    return;

                case RuleRootKind.PawnGroup:
                {
                    // 元素种类由**那一种绑定**声明，不是写死成 Pawn——
                    // 将来有"每张床"这类绑定，它自动就对。
                    kind = RuleValueKind.EntitySet;

                    var group = RuleVocabularyCatalog.Current.Subject(
                        path != null && !path.RootLiteral.IsMissing
                            ? path.RootLiteral.AsKey
                            : null);

                    entityKind = group != null ? group.entityKind : RuleEntityKind.Pawn;
                    return;
                }

                case RuleRootKind.AllMaps:
                    kind = RuleValueKind.EntitySet;
                    entityKind = RuleEntityKind.Map;
                    return;

                default:
                {
                    RuleValue literal = path != null ? path.RootLiteral : RuleValue.None;
                    kind = literal.kind;
                    entityKind = literal.entityKind;
                    return;
                }
            }
        }

        /// <summary>
        /// 走到第 <paramref name="stepCount"/> 步时手上是什么类型。
        /// <paramref name="lastProperty"/> 是最后一个读到的属性（阈值的单位从它来）。
        /// </summary>
        public static void AdvanceType(RulePath path, RuleEntityKind subjectKind, bool insideFilter,
            RuleEntityKind elementKind, int stepCount,
            out RuleValueKind kind, out RuleEntityKind entityKind, out RulePropertyInfo lastProperty)
        {
            RootType(path, subjectKind, insideFilter, elementKind, out kind, out entityKind);
            lastProperty = null;

            if (path == null) return;

            var vocabulary = RuleVocabularyCatalog.Current;
            int limit = stepCount < path.StepCount ? stepCount : path.StepCount;

            for (int i = 0; i < limit; i++)
            {
                var step = path.steps[i];

                if (step.kind == RuleStepKind.Property)
                {
                    var info = vocabulary.Property(step.propertyKey);
                    if (info == null)
                    {
                        // 认不出的属性：类型变成"未知"，后面就不该再给出任何可选谓词。
                        kind = RuleValueKind.None;
                        entityKind = RuleEntityKind.Any;
                        return;
                    }

                    kind = info.result;
                    entityKind = info.result == RuleValueKind.Entity
                                 || info.result == RuleValueKind.EntitySet
                        ? info.resultEntity
                        : RuleEntityKind.Any;
                    lastProperty = info;
                }
                else if (step.kind == RuleStepKind.Reduce)
                {
                    var info = RuleVocabulary.ReduceInfo(step.reduce);
                    if (info == null)
                    {
                        kind = RuleValueKind.None;
                        entityKind = RuleEntityKind.Any;
                        return;
                    }

                    kind = info.to;
                    if (kind == RuleValueKind.Number) entityKind = RuleEntityKind.Any;
                }
                else if (step.kind == RuleStepKind.Quantify)
                {
                    // 量词把一组东西合成一个布尔。集合上**没有**别的出路了——
                    // 「全都满足」之后就是一个普通布尔，可以接「为真」。
                    if (kind != RuleValueKind.EntitySet)
                    {
                        kind = RuleValueKind.None;
                        entityKind = RuleEntityKind.Any;
                        return;
                    }

                    kind = RuleValueKind.Bool;
                    entityKind = RuleEntityKind.Any;
                }

                // 筛选不改类型：还是同一组东西，只是少了一些。
            }
        }

        /// <summary>路径最后一个读到的属性。</summary>
        public static RulePropertyInfo LastPropertyOf(RulePath path, RuleEntityKind subjectKind,
            bool insideFilter, RuleEntityKind elementKind)
        {
            RuleValueKind kind;
            RuleEntityKind entityKind;
            RulePropertyInfo last;
            AdvanceType(path, subjectKind, insideFilter, elementKind,
                path != null ? path.StepCount : 0, out kind, out entityKind, out last);
            return last;
        }

        /// <summary>
        /// 路径第一步就违反类型的地方，返回原因；没问题返回 null。
        /// 校验用它把"载入时就能看出来的错"提前报出来，而不是等跑起来。
        /// </summary>
        public static string FindTypeError(RulePath path, RuleEntityKind subjectKind,
            bool insideFilter, RuleEntityKind elementKind)
        {
            if (path == null) return null;

            var vocabulary = RuleVocabularyCatalog.Current;
            RuleValueKind kind;
            RuleEntityKind entityKind;
            RootType(path, subjectKind, insideFilter, elementKind, out kind, out entityKind);

            // 「全部某群」的根要说清是哪一群，而且那一群得真的在词表里。
            // 不查的话，手改 XML / 删掉一个绑定之后，这句话会变成"读不到任何东西"，
            // 而界面上只有一句笼统的"这条路走到头了"。
            if (path.rootKind == RuleRootKind.PawnGroup)
            {
                string groupKey = path.RootLiteral.IsMissing ? null : path.RootLiteral.AsKey;

                if (string.IsNullOrEmpty(groupKey))
                {
                    return "这一句的根是一个「全部…」，但没说清是哪一群。";
                }

                if (vocabulary.Subject(groupKey) == null)
                {
                    return "「全部" + groupKey + "」不在词表里（主体绑定表里没有这一群）。";
                }
            }

            for (int i = 0; i < path.StepCount; i++)
            {
                var step = path.steps[i];

                if (step.kind == RuleStepKind.Property)
                {
                    if (kind == RuleValueKind.EntitySet)
                    {
                        return "步骤 " + (i + 1) + " 在读属性，但它前面还是一组东西——先接一个归约。";
                    }

                    var info = vocabulary.Property(step.propertyKey);
                    if (info == null)
                    {
                        return "步骤 " + (i + 1) + " 的属性「" + (step.propertyKey ?? "(空)")
                            + "」不在词表里。";
                    }

                    if (!RuleVocabulary.EntityKindMatches(info.owner, entityKind))
                    {
                        return "「" + info.key + "」不能挂在 " + entityKind + " 上。";
                    }

                    kind = info.result;
                    entityKind = info.result == RuleValueKind.Entity
                                 || info.result == RuleValueKind.EntitySet
                        ? info.resultEntity
                        : RuleEntityKind.Any;
                }
                else if (step.kind == RuleStepKind.Reduce)
                {
                    var info = RuleVocabulary.ReduceInfo(step.reduce);
                    if (info == null)
                    {
                        return "步骤 " + (i + 1) + " 的归约认不出来。";
                    }

                    if (info.from != kind)
                    {
                        return "归约「" + step.reduce + "」不能用在 " + kind + " 上。";
                    }

                    kind = info.to;
                    if (kind == RuleValueKind.Number) entityKind = RuleEntityKind.Any;
                }
                else if (step.kind == RuleStepKind.Quantify)
                {
                    if (kind != RuleValueKind.EntitySet)
                    {
                        return "步骤 " + (i + 1) + " 是一个量词，但它前面不是一组东西（是 "
                            + kind + "）——量词只能问「这一组里的每个怎么样」。";
                    }

                    if (step.filter == null)
                    {
                        return "步骤 " + (i + 1) + " 的量词还没写条件（「全都满足」什么？）。";
                    }

                    kind = RuleValueKind.Bool;
                    entityKind = RuleEntityKind.Any;
                }
            }

            return null;
        }
    }
}
