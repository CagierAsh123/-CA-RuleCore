namespace RuleCore.Core
{
    /// <summary>
    /// 求值过程中产生结果的那一层。用于回答「是哪一步挡住了」。
    ///
    /// 旧的 Trigger / Condition / Capability / Idempotence / Action 五层已经合并成三层，
    /// 因为语法本身只有三段（实体 · 谓语 · 宾语）：触发器和条件都是检测，
    /// 能力也是检测（只是失败模式不同），幂等则是操作自己的结局之一。
    /// </summary>
    public enum RuleStage
    {
        None = 0,

        /// <summary>引擎级：冷却、权限、自触发环。不属于单条规则的业务逻辑。</summary>
        Engine = 1,

        /// <summary>检测树：整棵成立才继续。</summary>
        Detect = 2,

        /// <summary>操作：逐条下发，第一条不成功就停。</summary>
        Operate = 3
    }
}
