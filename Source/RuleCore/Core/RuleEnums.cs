namespace RuleCore.Core
{
    /// <summary>
    /// 规则的生效范围——跟着**主体类型**走，不是权限的函数。
    ///
    /// 玩家和开发者都能用这几种；区别只在"谁能对 God 类动作生效"，
    /// 那是 <see cref="RuleTier"/> 的事，不是这里的事。
    /// </summary>
    public enum RuleScopeKind
    {
        /// <summary>由触发器与选择器自行决定（默认）。</summary>
        Any = 0,
        Pawn = 1,
        Thing = 2,
        Room = 3,
        Map = 4
    }

    /// <summary>
    /// 触发语义。引擎在**语法上**就区分二者，不让玩家自己猜——
    /// 把电平触发写成边沿、动作又不做幂等，是初学者 90% 的 bug 来源。
    /// </summary>
    public enum RuleTriggerKind
    {
        /// <summary>边沿：状态**发生变化**的那一刻触发一次（袭击开始、小人死亡）。</summary>
        Edge = 0,

        /// <summary>电平：只要条件**持续为真**就一直想执行（餐厅温度低于 10℃）。</summary>
        Level = 1
    }

    /// <summary>
    /// 权限层级。规则的实际层级取它**所有动作的最高级**——
    /// 一条规则里只要有一个 God 动作，整条规则就是开发者级。
    /// </summary>
    public enum RuleTier
    {
        /// <summary>玩家级：只能"让谁去做"，动作可失败，失败交回原版。</summary>
        Player = 0,

        /// <summary>开发者级：可以直接改世界状态。需要显式授权。</summary>
        Developer = 1
    }
}
