using Verse;
using RuleCore.Core;

namespace RuleCore
{
    /// <summary>
    /// 一个**字面量值**的可序列化形态。
    ///
    /// <see cref="RuleValue"/> 是 Core 里的值类型（含不透明把手），Scribe 不认识它；
    /// 而字面量（10 / 50% / 下雨 / (100,100) / 一句日志文本）本来就不带把手。
    /// 所以序列化的是这份"扁平壳"，读出来再翻译成 <see cref="RuleValue"/>。
    ///
    /// 反过来的方向也有用：编辑器改了字面量之后要写回壳里，走 <see cref="CopyFrom"/>。
    /// </summary>
    public class RuleLiteral : IExposable
    {
        public RuleValueKind kind = RuleValueKind.None;

        public float number;
        public bool boolean;
        public string key;
        public int cellX = -1;
        public int cellZ = -1;

        public RuleValue ToValue()
        {
            switch (kind)
            {
                case RuleValueKind.Number: return RuleValue.OfNumber(number);
                case RuleValueKind.Bool: return RuleValue.OfBool(boolean);
                case RuleValueKind.Enum: return RuleValue.OfKey(key);
                case RuleValueKind.Text: return RuleValue.OfText(key);
                case RuleValueKind.Coord: return RuleValue.OfCell(new RuleCell(cellX, cellZ));
                default: return RuleValue.None;
            }
        }

        public void CopyFrom(RuleValue value)
        {
            kind = value.kind;
            number = value.AsNumber;
            boolean = value.AsBool;
            key = value.kind == RuleValueKind.Enum ? value.AsKey : value.AsText;
            var cell = value.AsCell;
            cellX = cell.IsValid ? cell.x : -1;
            cellZ = cell.IsValid ? cell.z : -1;
        }

        public static RuleLiteral From(RuleValue value)
        {
            var literal = new RuleLiteral();
            literal.CopyFrom(value);
            return literal;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref kind, "kind", RuleValueKind.None);
            Scribe_Values.Look(ref number, "number", 0f);
            Scribe_Values.Look(ref boolean, "boolean", false);
            Scribe_Values.Look(ref key, "key");
            Scribe_Values.Look(ref cellX, "cellX", -1);
            Scribe_Values.Look(ref cellZ, "cellZ", -1);
        }
    }

    /// <summary>
    /// 实体表达式路径上的一步：读属性 / 筛选 / 归约。
    ///
    /// 三种步骤塞在同一个类里（而不是三个子类），是因为它们**共享同一个位置语义**：
    /// 都是"在路径的这一点上做一件小事"。分成三个类只为了三个字段，
    /// 而收益是编辑器遍历路径时不用做类型判断——它看 <see cref="kind"/> 就够了。
    /// </summary>
    public class RulePathStep : IExposable
    {
        public RuleStepKind kind = RuleStepKind.Property;

        /// <summary>kind == Property：词表里的属性键。</summary>
        public string propertyKey;

        /// <summary>kind == Filter：筛选条件（一棵完整的检测树）。</summary>
        public RuleExprNode filter;

        /// <summary>kind == Reduce：归约算子。</summary>
        public RuleReduceKind reduce = RuleReduceKind.First;

        public void ExposeData()
        {
            Scribe_Values.Look(ref kind, "kind", RuleStepKind.Property);
            Scribe_Values.Look(ref propertyKey, "propertyKey");
            Scribe_Deep.Look(ref filter, "filter");
            Scribe_Values.Look(ref reduce, "reduce", RuleReduceKind.First);
        }

        public override string ToString()
        {
            switch (kind)
            {
                case RuleStepKind.Property: return "." + (propertyKey ?? "?");
                case RuleStepKind.Filter: return "[" + (filter != null ? filter.ToString() : "?") + "]";
                case RuleStepKind.Reduce: return "." + reduce;
                default: return "?";
            }
        }
    }

    /// <summary>
    /// 实体表达式 —— 规则语言的主语与宾语都是它。
    ///
    /// <code>
    /// 实体表达式 := 根 ( .属性 | [筛选] | .归约 )*
    /// </code>
    ///
    /// 「打开子属性」不是特例，就是这条路径上的一次 <c>.属性</c> 或 <c>[筛选]</c>：
    /// 有子类就继续展开，没有就到此为止。编辑器因此可以做成通用渲染器——
    /// 它只需要知道"当前这一步产出什么类型"，然后去词表里问"这个类型上有什么可走的下一步"。
    ///
    /// 归约必须显式写出来。`第一个` 与 `数量` 是三件不同的事里的两件，
    /// 不给默认值才能逼玩家说清"我说的是哪一件"。
    /// </summary>
    public class RulePath : IExposable, IRulePathSource
    {
        public RuleRootKind rootKind = RuleRootKind.Subject;

        /// <summary>rootKind == Literal 时的值。</summary>
        public RuleLiteral rootLiteral = new RuleLiteral();

        public System.Collections.Generic.List<RulePathStep> steps =
            new System.Collections.Generic.List<RulePathStep>();

        // ── IRulePathSource ──────────────────────────────────────────

        public RuleRootKind RootKind
        {
            get { return rootKind; }
        }

        public RuleValue RootLiteral
        {
            get { return rootLiteral != null ? rootLiteral.ToValue() : RuleValue.None; }
        }

        public int StepCount
        {
            get { return steps != null ? steps.Count : 0; }
        }

        public RuleStepKind StepKindAt(int index)
        {
            return steps[index].kind;
        }

        public string PropertyKeyAt(int index)
        {
            return steps[index].propertyKey;
        }

        public IRuleExprSource FilterAt(int index)
        {
            return steps[index].filter;
        }

        public RuleReduceKind ReduceAt(int index)
        {
            return steps[index].reduce;
        }

        // ── 编辑期的小工具（编辑器与校验都直接用）────────────────────

        public bool IsEmpty
        {
            get { return rootKind == RuleRootKind.Literal && rootLiteral != null
                && rootLiteral.kind == RuleValueKind.None; }
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref rootKind, "rootKind", RuleRootKind.Subject);
            Scribe_Deep.Look(ref rootLiteral, "rootLiteral");
            Scribe_Collections.Look(ref steps, "steps", LookMode.Deep);

            if (Scribe.mode == LoadSaveMode.PostLoadInit && steps == null)
            {
                steps = new System.Collections.Generic.List<RulePathStep>();
            }
        }

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder(48);
            sb.Append(rootKind);

            if (rootKind == RuleRootKind.Literal && rootLiteral != null)
            {
                sb.Append('(').Append(rootLiteral.ToValue()).Append(')');
            }

            for (int i = 0; i < StepCount; i++)
            {
                sb.Append(steps[i].ToString());
            }

            return sb.ToString();
        }
    }
}
