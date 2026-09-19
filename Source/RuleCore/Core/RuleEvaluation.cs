namespace RuleCore.Core
{
    /// <summary>
    /// 一次规则求值的完整结局：卡在哪一层、什么结局、为什么、哪个实体。
    ///
    /// 这是引擎对外唯一的「发生了什么」表达。日志、时间线面板、将来的试运行
    /// 都消费它。它的存在本身就是对那句需求的回答：
    /// **每一次求值都要能说清"哪条规则、哪一步、为什么、哪个实体"**。
    /// </summary>
    public sealed class RuleEvaluation
    {
        /// <summary>非成功结局却缺原因码时，引擎自动补上的诊断码。出现它就说明有缺陷。</summary>
        public const string ReasonMissingCode = "reason_missing";

        private const string ReasonMissingNote = "（引擎未提供原因码——这是缺陷，请连同本条记录一起报告）";

        public readonly string RuleId;
        public readonly RuleStage Stage;
        public readonly string Status;
        public readonly string ReasonCode;
        public readonly string Reason;
        public readonly string Entity;

        private RuleEvaluation(string ruleId, RuleStage stage, string status,
            string reasonCode, string reason, string entity)
        {
            RuleId = ruleId;
            Stage = stage;
            Status = status;
            ReasonCode = reasonCode;
            Reason = reason;
            Entity = entity;
        }

        /// <summary>唯一算作成功的结局：动作已下发。</summary>
        public bool Succeeded
        {
            get { return RuleEvalStatus.IsSuccess(Status); }
        }

        public static RuleEvaluation Create(string ruleId, RuleStage stage, string status,
            string reasonCode, string reason, string entity)
        {
            bool wasUnknown;
            string canonical = RuleEvalStatus.Canonicalize(status, out wasUnknown);

            if (wasUnknown)
            {
                // 不静默降级：把原始值原样带进原因，让它在时间线上无法被忽略。
                string detail = "结局码不在词表内：\"" + (status ?? "null") + "\"";
                if (!string.IsNullOrEmpty(reasonCode))
                {
                    detail = detail + "（原始原因码：" + reasonCode + "）";
                }
                return new RuleEvaluation(ruleId, stage, RuleEvalStatus.Unknown,
                    "status.unknown", detail, entity);
            }

            return new RuleEvaluation(ruleId, stage, canonical, reasonCode, reason, entity);
        }

        public static RuleEvaluation Issued(string ruleId, RuleStage stage, string reasonCode,
            string reason, string entity)
        {
            return new RuleEvaluation(ruleId, stage, RuleEvalStatus.Issued, reasonCode, reason, entity);
        }

        public static RuleEvaluation Blocked(string ruleId, RuleStage stage, string status,
            string reasonCode, string reason, string entity)
        {
            return Create(ruleId, stage, status, reasonCode, reason, entity);
        }

        public static RuleEvaluation Failed(string ruleId, RuleStage stage, string reasonCode,
            string reason, string entity)
        {
            return new RuleEvaluation(ruleId, stage, RuleEvalStatus.Failed, reasonCode, reason, entity);
        }

        /// <summary>
        /// 不变式的纯函数形态：非成功结局必须有原因码，缺了就补上诊断码。
        ///
        /// 刻意抽成纯函数——这样验证它只需要调用一个函数，
        /// 而不必往时间线里写一条伪装成真缺陷的记录（那种夹具会让人每次自检都以为引擎坏了）。
        /// </summary>
        public static string ResolveReasonCode(string status, string reasonCode, out bool wasMissing)
        {
            if (!RuleEvalStatus.RequiresReason(status) || !string.IsNullOrEmpty(reasonCode))
            {
                wasMissing = false;
                return reasonCode;
            }

            wasMissing = true;
            return ReasonMissingCode;
        }

        /// <summary>
        /// 写入日志汇。**不变式在这里被强制执行**：
        /// 任何非成功结局若没带原因码，会被补上 <see cref="ReasonMissingCode"/> 并注明是缺陷，
        /// 而不是让它作为一条无解释的"失败"流过去。
        /// </summary>
        public void Emit()
        {
            bool wasMissing;
            string code = ResolveReasonCode(Status, ReasonCode, out wasMissing);

            string text = Reason;
            if (wasMissing)
            {
                text = string.IsNullOrEmpty(text) ? ReasonMissingNote : text + " " + ReasonMissingNote;
            }

            RuleLog.Write(RuleEvalStatus.LevelFor(Status), RuleId, RuleStageNames.NameOf(Stage),
                Status, code, Entity, text);
        }

        public override string ToString()
        {
            return (RuleId ?? "-") + " " + RuleStageNames.NameOf(Stage) + " → " + Status
                + " (" + (ReasonCode ?? "-") + ")";
        }
    }
}
