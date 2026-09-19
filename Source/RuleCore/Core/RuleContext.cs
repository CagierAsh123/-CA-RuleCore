using System;
using System.Threading;

namespace RuleCore.Core
{
    /// <summary>
    /// 环绕上下文的快照。不可变。
    /// </summary>
    public sealed class RuleContextSnapshot
    {
        public static readonly RuleContextSnapshot Empty =
            new RuleContextSnapshot(null, RuleStage.None, null);

        public readonly string RuleId;
        public readonly RuleStage Stage;
        public readonly string OperationId;

        public RuleContextSnapshot(string ruleId, RuleStage stage, string operationId)
        {
            RuleId = ruleId;
            Stage = stage;
            OperationId = operationId;
        }

        public bool IsEmpty
        {
            get { return RuleId == null && Stage == RuleStage.None && OperationId == null; }
        }

        public RuleContextSnapshot WithRule(string ruleId)
        {
            return new RuleContextSnapshot(ruleId, Stage, OperationId);
        }

        public RuleContextSnapshot WithStage(RuleStage stage)
        {
            return new RuleContextSnapshot(RuleId, stage, OperationId);
        }

        public RuleContextSnapshot WithOperation(string operationId)
        {
            return new RuleContextSnapshot(RuleId, Stage, operationId);
        }

        public string Describe()
        {
            if (IsEmpty) return "-";

            var parts = new System.Text.StringBuilder(48);
            if (!string.IsNullOrEmpty(RuleId)) parts.Append("rule=").Append(RuleId);
            if (Stage != RuleStage.None)
            {
                if (parts.Length > 0) parts.Append(' ');
                parts.Append("stage=").Append(RuleStageNames.NameOf(Stage));
            }
            if (!string.IsNullOrEmpty(OperationId))
            {
                if (parts.Length > 0) parts.Append(' ');
                parts.Append("op=").Append(OperationId);
            }
            return parts.ToString();
        }
    }

    /// <summary>
    /// 三级环绕上下文：规则 → 阶段 → 操作。
    ///
    /// 存在的理由只有一个：让**任意深度的代码零参数地知道自己此刻属于哪条规则的哪一步**。
    /// 引擎调用条件求值、能力校验、动作下发时压入作用域，异常路径也被 using 保证还原。
    /// 被 Harmony 补丁捕获的日志同样能读到它——因为 AsyncLocal 会跟着同线程的同步回调走。
    ///
    /// 用法：
    /// <code>
    /// using (RuleContext.Push(rule.defName, RuleStage.Condition, null))
    /// {
    ///     // 这里面的任何日志/异常都自动带上归属
    /// }
    /// </code>
    /// </summary>
    public static class RuleContext
    {
        private static readonly AsyncLocal<RuleContextSnapshot> current =
            new AsyncLocal<RuleContextSnapshot>();

        public static RuleContextSnapshot Current
        {
            get
            {
                var snapshot = current.Value;
                return snapshot ?? RuleContextSnapshot.Empty;
            }
        }

        public static IDisposable PushRule(string ruleId)
        {
            return Push(Current.WithRule(ruleId));
        }

        public static IDisposable PushStage(RuleStage stage)
        {
            return Push(Current.WithStage(stage));
        }

        public static IDisposable PushOperation(string operationId)
        {
            return Push(Current.WithOperation(operationId));
        }

        public static IDisposable Push(string ruleId, RuleStage stage, string operationId)
        {
            var next = Current;
            if (ruleId != null) next = next.WithRule(ruleId);
            if (stage != RuleStage.None) next = next.WithStage(stage);
            if (operationId != null) next = next.WithOperation(operationId);
            return Push(next);
        }

        private static IDisposable Push(RuleContextSnapshot next)
        {
            var previous = current.Value;
            current.Value = next;
            return new Scope(previous);
        }

        private sealed class Scope : IDisposable
        {
            private readonly RuleContextSnapshot previous;
            private bool disposed;

            public Scope(RuleContextSnapshot previous)
            {
                this.previous = previous;
            }

            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                current.Value = previous;
            }
        }
    }
}
