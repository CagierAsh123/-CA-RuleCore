using System;

namespace RuleCore.Core
{
    /// <summary>
    /// 日志级别。Error 永远输出；其余受设置门控。
    /// </summary>
    public enum RuleLogLevel
    {
        Trace = 0,
        Info = 1,
        Warn = 2,
        Error = 3
    }

    /// <summary>
    /// 一次规则求值的结构化记录。
    ///
    /// 这是「为什么没触发」的唯一事实来源：时间线面板与落盘各自订阅同一条记录流，
    /// 谁也不持有运行态。字段设计刻意区别于普通日志文本——
    /// outcome / reasonCode / entity 是一等字段，不靠事后从字符串里猜。
    /// </summary>
    public sealed class RuleLogRecord
    {
        /// <summary>单调递增序号。面板增量拉取的游标，也是"时间窗"切片的依据。</summary>
        public readonly long Seq;

        /// <summary>游戏 tick。可存档、可推导——绝不用真实毫秒做长期判定。</summary>
        public readonly int Tick;

        public readonly DateTime Utc;
        public readonly RuleLogLevel Level;

        public readonly string RuleId;
        public readonly string StepId;
        public readonly string Outcome;

        /// <summary>机器可读的原因码（如 capability_denied）。非成功结局必须非空。</summary>
        public readonly string ReasonCode;

        public readonly string Entity;

        /// <summary>人可读的补充说明。为空不影响判定。</summary>
        public readonly string Message;

        /// <summary>相邻同内容合并次数（从 1 起）。抖动日志不会淹没时间线，但也不会被丢弃。</summary>
        public int RepeatCount = 1;

        /// <summary>本组最后一次出现的序号。</summary>
        public long LastSeq;

        public RuleLogRecord(long seq, int tick, DateTime utc, RuleLogLevel level,
            string ruleId, string stepId, string outcome, string reasonCode, string entity, string message)
        {
            Seq = seq;
            LastSeq = seq;
            Tick = tick;
            Utc = utc;
            Level = level;
            RuleId = ruleId;
            StepId = stepId;
            Outcome = outcome;
            ReasonCode = reasonCode;
            Entity = entity;
            Message = message;
        }

        /// <summary>
        /// 相邻合并的判等键：只比内容，不比序号与时间。
        /// 注意这是"相邻"合并——同一条规则相隔很久的两次失败必须各留一条记录，
        /// 永久全局去重会让第二次失败静默消失，正是要避免的哑 bug。
        /// </summary>
        public bool SameAs(RuleLogRecord other)
        {
            if (other == null) return false;
            return SameAs(other.Level, other.RuleId, other.StepId, other.Outcome,
                other.ReasonCode, other.Entity, other.Message);
        }

        /// <summary>免构造的字段版判等，供热路径直接比对。</summary>
        public bool SameAs(RuleLogLevel level, string ruleId, string stepId, string outcome,
            string reasonCode, string entity, string message)
        {
            return Level == level
                && string.Equals(RuleId, ruleId, StringComparison.Ordinal)
                && string.Equals(StepId, stepId, StringComparison.Ordinal)
                && string.Equals(Outcome, outcome, StringComparison.Ordinal)
                && string.Equals(ReasonCode, reasonCode, StringComparison.Ordinal)
                && string.Equals(Entity, entity, StringComparison.Ordinal)
                && string.Equals(Message, message, StringComparison.Ordinal);
        }

        /// <summary>渲染成单行 key=value，人机两读，也便于外部工具 grep。</summary>
        public string Render()
        {
            var sb = new System.Text.StringBuilder(160);
            sb.Append("[RuleCore] seq=").Append(Seq)
              .Append(" tick=").Append(Tick)
              .Append(' ').Append(Level.ToString().ToUpperInvariant());

            AppendField(sb, "rule", RuleId);
            AppendField(sb, "step", StepId);
            AppendField(sb, "outcome", Outcome);
            AppendField(sb, "reason", ReasonCode);
            AppendField(sb, "entity", Entity);

            if (RepeatCount > 1)
            {
                sb.Append(" x").Append(RepeatCount);
            }

            if (!string.IsNullOrEmpty(Message))
            {
                sb.Append(" | ").Append(Message);
            }

            return sb.ToString();
        }

        private static void AppendField(System.Text.StringBuilder sb, string key, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            sb.Append(' ').Append(key).Append('=').Append(value);
        }
    }
}
