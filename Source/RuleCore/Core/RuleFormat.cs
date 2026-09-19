using System;

namespace RuleCore.Core
{
    /// <summary>
    /// 数值的**显示与输入**格式化。
    ///
    /// 这一层决定了规则读起来是"人话"还是"一堆小数"：
    /// `耐久 小于 0.5` 谁都不爱看，`耐久 小于 50%` 才是一句话。
    /// 单位、范围、小数位、是不是百分比**全都来自属性自己的元数据**——
    /// 编辑器不知道"耐久"是什么，它只是照着 <see cref="RulePropertyInfo"/> 念。
    ///
    /// 放在 Core 因为它没有 Verse、没有 Unity，可以脱离游戏单测
    /// （而"0.5 显示成 50%"这种事恰恰是最容易写反的）。
    /// </summary>
    public static class RuleFormat
    {
        /// <summary>带单位/百分号显示一个数。元数据为 null 时退化成两位小数。</summary>
        public static string Format(RulePropertyInfo info, float value)
        {
            if (info == null)
            {
                return value.ToString("0.##");
            }

            return FormatNumber(value, info.decimals, info.unit, info.percent);
        }

        public static string FormatNumber(float value, int decimals, string unit, bool percent)
        {
            if (percent)
            {
                // 0.5 → "50%"。percent 属性的值是 0~1 的比例，玩家想的是百分数。
                float shown = value * 100f;
                return shown.ToString("0.#") + "%";
            }

            int digits = decimals < 0 ? 2 : decimals;
            string text = value.ToString("0." + new string('#', Math.Max(1, digits)));

            // 空单位不补空格：补了会变成"12 "，复制出去是个带尾空格的字符串。
            return string.IsNullOrEmpty(unit) ? text : text + unit;
        }

        /// <summary>把值翻译成"输入框里该显示的数"。百分比属性要乘 100。</summary>
        public static float ToEditValue(float value, bool percent)
        {
            return percent ? value * 100f : value;
        }

        /// <summary>把输入框里的数翻译回真实值。百分比要除回去。</summary>
        public static float FromEditValue(float shown, bool percent)
        {
            return percent ? shown / 100f : shown;
        }

        /// <summary>输入框右侧那截后缀（单位或百分号）。</summary>
        public static string EditSuffix(RulePropertyInfo info)
        {
            if (info == null) return null;
            if (info.percent) return "%";
            return string.IsNullOrEmpty(info.unit) ? null : info.unit;
        }

        /// <summary>
        /// 数值输入的合理范围。百分比固定 0~100（输入侧），其余取元数据声明的范围；
        /// 没声明范围的属性给一个宽松但有限的区间——**不给 float 的极值**，
        /// 否则输入框里打出个 1e30 之后路径照样求值，只是永远不成立，很难查。
        /// </summary>
        public static void EditRange(RulePropertyInfo info, out float min, out float max)
        {
            if (info != null && info.percent)
            {
                min = 0f;
                max = 100f;
                return;
            }

            if (info == null)
            {
                min = -100000f;
                max = 100000f;
                return;
            }

            min = info.min;
            max = info.max;

            if (float.IsNegativeInfinity(min) || min == float.MinValue) min = -100000f;
            if (float.IsPositiveInfinity(max) || max == float.MaxValue) max = 100000f;
        }
    }
}
