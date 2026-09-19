using System.Collections.Generic;

namespace RuleCore.Core
{
    /// <summary>
    /// 求值结局的词表，以及围绕它的一条硬不变式。
    ///
    /// 硬不变式：**非成功结局必须携带原因码**（<see cref="RequiresReason"/>）。
    /// 这条不是文档约定，而是运行时保证——见 <see cref="RuleEvaluation.Emit"/>。
    ///
    /// 另一条纪律：<see cref="Canonicalize"/> 遇到不认识的词表**必须显式返回 Unknown**，
    /// 绝不静默降级成 Failed。静默降级会让一个拼错的 "cooldown_blocked" 变成"失败"，
    /// 排障时能把人耗死。
    /// </summary>
    public static class RuleEvalStatus
    {
        /// <summary>唯一算作成功的结局：动作已下发。</summary>
        public const string Issued = "issued";

        /// <summary>条件不成立。这是最常见的结局，不是错误。</summary>
        public const string ConditionFalse = "condition_false";

        /// <summary>能力不匹配——不做动作，交回原版 AI。</summary>
        public const string CapabilityDenied = "capability_denied";

        /// <summary>动作要达成的状态已经满足，无需动作（幂等命中）。</summary>
        public const string AlreadySatisfied = "already_satisfied";

        /// <summary>规则级冷却未过。</summary>
        public const string CooldownBlocked = "cooldown_blocked";

        /// <summary>自触发环嫌疑：本规则的动作用改动了自己的输入，被自动禁用。</summary>
        public const string LoopSuspected = "loop_suspected";

        /// <summary>动作执行失败（有明确返回值，非异常）。</summary>
        public const string Failed = "failed";

        /// <summary>求值过程抛出异常。</summary>
        public const string Error = "error";

        /// <summary>被有意跳过（例如规则未启用、作用域不匹配）。</summary>
        public const string Skipped = "skipped";

        /// <summary>词表外的值。出现它就意味着有缺陷，必须显式暴露。</summary>
        public const string Unknown = "unknown";

        /// <summary>引擎内部自检使用的结局码。</summary>
        public const string SelfTest = "self_test";

        private static readonly string[] knownValues =
        {
            Issued, ConditionFalse, CapabilityDenied, AlreadySatisfied,
            CooldownBlocked, LoopSuspected, Failed, Error, Skipped, Unknown, SelfTest
        };

        private static readonly HashSet<string> knownSet = new HashSet<string>(knownValues);

        public static IReadOnlyList<string> Known
        {
            get { return knownValues; }
        }

        public static bool IsKnown(string status)
        {
            return !string.IsNullOrEmpty(status) && knownSet.Contains(status);
        }

        /// <summary>唯一算作成功的结局。</summary>
        public static bool IsSuccess(string status)
        {
            return string.Equals(status, Issued, System.StringComparison.Ordinal);
        }

        /// <summary>
        /// 非成功结局必须给出原因码。这条不变式是「为什么没触发」能被回答的前提。
        /// </summary>
        public static bool RequiresReason(string status)
        {
            return !IsSuccess(status);
        }

        /// <summary>
        /// 规范化。不认识的词表返回 <see cref="Unknown"/> 并置 <paramref name="wasUnknown"/>，
        /// 让调用方能据此刻意地记一条警告——而不是让它悄悄变成别的什么。
        /// </summary>
        public static string Canonicalize(string raw, out bool wasUnknown)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                wasUnknown = true;
                return Unknown;
            }

            string normalized = raw.Trim().ToLowerInvariant();
            if (knownSet.Contains(normalized))
            {
                wasUnknown = false;
                return normalized;
            }

            wasUnknown = true;
            return Unknown;
        }

        public static string Canonicalize(string raw)
        {
            bool ignored;
            return Canonicalize(raw, out ignored);
        }

        /// <summary>
        /// 结局到日志级别的映射。时间线的颜色直接来自这里——
        /// 一条从不命中的规则会呈现为一片灰色 Trace，而命中的那一下是白色 Info。
        /// </summary>
        public static RuleLogLevel LevelFor(string status)
        {
            if (IsSuccess(status)) return RuleLogLevel.Info;

            switch (status)
            {
                case Failed:
                case Error:
                    return RuleLogLevel.Error;

                case CapabilityDenied:
                case CooldownBlocked:
                case LoopSuspected:
                case Unknown:
                    return RuleLogLevel.Warn;

                case ConditionFalse:
                case AlreadySatisfied:
                case Skipped:
                    return RuleLogLevel.Trace;

                default:
                    // 词表外的一律按注意处理，不按错误——但也不隐藏。
                    return RuleLogLevel.Warn;
            }
        }
    }
}
