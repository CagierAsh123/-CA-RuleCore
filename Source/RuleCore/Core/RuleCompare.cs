using System;

namespace RuleCore.Core
{
    /// <summary>
    /// 比较符 —— 「判断」那一半的词汇。
    ///
    /// 规则语言的骨架是**检测 → 判断 → 执行**三段：
    ///   · 检测给出一个数（室外温度 = 12.4）
    ///   · 判断说它够不够（12.4 &gt; 10）
    ///   · 执行给出后果（让某人去做某事）
    ///
    /// 把"判断"独立成枚举而不是给每种情形写一个条件类
    /// （`RuleCondition_TemperatureAbove10` 之类），是因为前者是 3 个词的组合，
    /// 后者是无穷多个类。用户要的是「{{实体}} {{怎么样}}」，
    /// 那么"怎么样"就该是一张表，不是一堆类型。
    /// </summary>
    public enum RuleOperator
    {
        /// <summary>&gt;</summary>
        Greater = 0,

        /// <summary>≥</summary>
        AtLeast = 1,

        /// <summary>&lt;</summary>
        Less = 2,

        /// <summary>≤</summary>
        AtMost = 3,

        /// <summary>=</summary>
        Equal = 4,

        /// <summary>≠</summary>
        NotEqual = 5
    }

    /// <summary>
    /// 比较本身。放在 Core 里是因为它是**纯函数**——没有游戏状态、没有 Verse，
    /// 所以可以脱离游戏直接单测，而它恰恰是最不该出错的一段（判错了整条规则就判错了）。
    /// </summary>
    public static class RuleCompare
    {
        /// <summary>
        /// 判等的容差。
        ///
        /// 拿浮点直接 `==` 判"温度等于 20"是不可靠的：温度每 tick 都在
        /// 被加热器、天气、季节一点点推着走，读数几乎永远不是整齐的 20.0。
        /// 但也不能太宽——0.0001 度远小于任何有意义的温差，不会把 20.1 判成 20。
        /// </summary>
        public const float Epsilon = 0.0001f;

        public static bool Apply(RuleOperator op, float left, float right)
        {
            switch (op)
            {
                case RuleOperator.Greater: return left > right;
                case RuleOperator.AtLeast: return left >= right;
                case RuleOperator.Less: return left < right;
                case RuleOperator.AtMost: return left <= right;
                case RuleOperator.Equal: return Math.Abs(left - right) <= Epsilon;
                case RuleOperator.NotEqual: return Math.Abs(left - right) > Epsilon;

                // 手改 XML 写出一个没定义的枚举值时走这里。**返回 false 而不是抛异常**：
                // 一颗坏规则不该让整个 tick 停摆（和管线其它阶段的策略一致）。
                // 但也不能装作没事——条件那边会把这个当成"不成立"，时间线上看得见。
                default: return false;
            }
        }

        public static bool IsKnown(RuleOperator op)
        {
            return op >= RuleOperator.Greater && op <= RuleOperator.NotEqual;
        }

        /// <summary>符号。给日志与列表摘要用，比枚举名短且不依赖翻译。</summary>
        public static string Symbol(RuleOperator op)
        {
            switch (op)
            {
                case RuleOperator.Greater: return ">";
                case RuleOperator.AtLeast: return ">=";
                case RuleOperator.Less: return "<";
                case RuleOperator.AtMost: return "<=";
                case RuleOperator.Equal: return "=";
                case RuleOperator.NotEqual: return "!=";
                default: return "?";
            }
        }
    }
}
