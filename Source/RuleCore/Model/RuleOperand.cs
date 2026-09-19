using Verse;
using RuleCore.Core;

namespace RuleCore
{
    /// <summary>
    /// 谓词的**宾语槽**。三种填法，共用同一个类（编辑器因此只处理一种东西）：
    ///
    ///   · 字面量：`10` `50%` `下雨` `(100,100)` `袭击(点数=1000)`
    ///   · 实体引用：`本图`、`殖民者[不是 本主体]`——一条完整的实体表达式路径
    ///   · 空：一元谓词（`本主体 能动`），或者"还没填"
    ///
    /// 「还没填」和「填错了」必须分得开：前者是 <see cref="RuleValueKind.None"/>，
    /// 编辑器显示成空槽；后者是类型对不上，求值时报 <c>detect.wrong_arg</c>。
    /// 混起来的后果是编辑器把空槽显示成"类型错误"，玩家会以为自己填错了。
    /// </summary>
    public class RuleOperand : IExposable, IRuleOperandSource
    {
        /// <summary>这个宾语打算是什么类型。None = 没填。</summary>
        public RuleValueKind kind = RuleValueKind.None;

        /// <summary>kind 是字面量类型时用它。</summary>
        public RuleLiteral literal = new RuleLiteral();

        /// <summary>kind == Entity 时用它。</summary>
        public RulePath path;

        public void ExposeData()
        {
            Scribe_Values.Look(ref kind, "kind", RuleValueKind.None);
            Scribe_Deep.Look(ref literal, "literal");
            Scribe_Deep.Look(ref path, "path");
        }

        // ── IRuleOperandSource ───────────────────────────────────────

        /// <summary>
        /// 注意这里返回的是**字段**而不是原样透出：
        /// 空的实体引用（kind==Entity 但 path 为空）要报成"没填"，
        /// 否则求值器会去跑一条空路径并报 path.missing，玩家看到的是一句莫名其妙的话。
        /// </summary>
        public RuleValueKind Kind
        {
            get
            {
                if (kind == RuleValueKind.Entity && (path == null || path.IsEmpty))
                {
                    return RuleValueKind.None;
                }
                return kind;
            }
        }

        public RuleValue Literal
        {
            get { return literal != null ? literal.ToValue() : RuleValue.None; }
        }

        public IRulePathSource Path
        {
            get { return path; }
        }

        public static RuleOperand OfNumber(float value)
        {
            var operand = new RuleOperand { kind = RuleValueKind.Number };
            operand.literal.CopyFrom(RuleValue.OfNumber(value));
            return operand;
        }

        public static RuleOperand OfKey(string key)
        {
            var operand = new RuleOperand { kind = RuleValueKind.Enum };
            operand.literal.CopyFrom(RuleValue.OfKey(key));
            return operand;
        }

        public static RuleOperand OfText(string text)
        {
            var operand = new RuleOperand { kind = RuleValueKind.Text };
            operand.literal.CopyFrom(RuleValue.OfText(text));
            return operand;
        }

        public static RuleOperand OfEntity(RulePath entityPath)
        {
            return new RuleOperand { kind = RuleValueKind.Entity, path = entityPath };
        }

        public override string ToString()
        {
            switch (Kind)
            {
                case RuleValueKind.None: return string.Empty;
                case RuleValueKind.Entity: return path != null ? path.ToString() : "?";
                default: return literal != null ? literal.ToValue().ToString() : "?";
            }
        }
    }

    /// <summary>
    /// 一条**操作子句**：实体 · 操作 · 宾语。
    ///
    /// 操作侧只有 `且`（顺序下发，第一条不成功就停），所以它是一个列表而不是树。
    /// `执行A或执行B` 不是操作，是条件——要用检测写。
    /// </summary>
    public class RuleClause : IExposable, IRuleOperateSource
    {
        public RulePath subject;
        public string verbKey;
        public RuleOperand argument;

        public IRulePathSource Subject
        {
            get { return subject; }
        }

        public string VerbKey
        {
            get { return verbKey; }
        }

        public IRuleOperandSource Argument
        {
            get { return argument; }
        }

        public static RuleClause Of(RulePath subject, string verbKey, RuleOperand argument)
        {
            return new RuleClause { subject = subject, verbKey = verbKey, argument = argument };
        }

        public void ExposeData()
        {
            Scribe_Deep.Look(ref subject, "subject");
            Scribe_Values.Look(ref verbKey, "verbKey");
            Scribe_Deep.Look(ref argument, "argument");
        }

        public override string ToString()
        {
            return (subject != null ? subject.ToString() : "?")
                + " " + (verbKey ?? "?")
                + (argument != null ? " " + argument : string.Empty);
        }
    }
}
