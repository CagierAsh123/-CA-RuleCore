using Verse;
using RuleCore.Core;

namespace RuleCore
{
    /// <summary>
    /// 一次求值的运行上下文。
    ///
    /// 刻意区分三种角色（需求里反复强调的那条）：
    ///   · <see cref="Actor"/>  —— 执行者，被委派去做事的人
    ///   · <see cref="Target"/> —— 被作用者
    ///   · 条件的主体与动作的主体**不必是同一个**（"下雨导致餐厅温度归零"里，
    ///     条件是房间温度、动作是加热器开关）
    ///
    /// 上下文随管线推进被逐步填充。它只在一次求值内有效，不持有跨 tick 状态——
    /// 规则的持久化运行态另外存放，规范上只允许"上次触发 tick"这类极小结构。
    /// </summary>
    public sealed class RuleEvalContext
    {
        public Rule Rule;
        public Map Map;
        public Pawn Actor;
        public Thing Target;
        public IntVec3 Cell = IntVec3.Invalid;
        public int Tick;

        /// <summary>
        /// 主体绑定的参数（<see cref="Rule.subjectRef"/>）。指名绑定的绑定器读它。
        /// 组绑定不读——传进来是什么都无所谓。
        /// </summary>
        public string SubjectRef;

        /// <summary>本次求值允许使用的最高权限层级。God 动作在 Player 层级下会直接被挡下。</summary>
        public RuleTier AllowedTier = RuleTier.Player;

        public string RuleId
        {
            get { return Rule != null ? Rule.id : null; }
        }

        public string ActorLabel
        {
            get { return Actor != null ? Actor.LabelShort : null; }
        }

        public bool HasCell
        {
            get { return Cell.IsValid; }
        }

        public override string ToString()
        {
            var sb = new System.Text.StringBuilder(64);
            sb.Append("rule=").Append(RuleId ?? "-");
            sb.Append(" tick=").Append(Tick);
            if (Map != null) sb.Append(" map=").Append(Map.uniqueID);
            if (Actor != null) sb.Append(" actor=").Append(ActorLabel);
            if (Target != null) sb.Append(" target=").Append(Target.ThingID);
            if (HasCell) sb.Append(" cell=").Append(Cell);
            return sb.ToString();
        }
    }
}
