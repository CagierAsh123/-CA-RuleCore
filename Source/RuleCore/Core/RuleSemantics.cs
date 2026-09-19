namespace RuleCore.Core
{
    // ── 规则语言的语义基础类型 ────────────────────────────────────────
    //
    // 这一组枚举定义的是「规则语言里都有什么」，而不是「RimWorld 里都有什么」。
    // 所以它们住在 Core：没有 Verse、没有 UnityEngine，可以脱离游戏单测。
    // 逐条依据见 Docs/语义规范.md。

    /// <summary>
    /// 值的种类。实体表达式路径上每一步的产出、以及谓词宾语槽接受什么，都用它描述。
    ///
    /// 刻意**没有** String：规则语言里没有字符串运算，"名字"这类东西是枚举或 Def 引用，
    /// 放进 String 只会让"能比大小吗"这种问题变得没法回答。
    /// 唯一用到自由文本的地方是「写日志」的文本，那是操作自己的参数，不是值。
    /// </summary>
    public enum RuleValueKind
    {
        /// <summary>没有值。谓词的宾语槽用这个表示"我不需要宾语"。</summary>
        None = 0,

        Number = 1,
        Bool = 2,

        /// <summary>枚举成员，用 key 表示（如天气的 <c>Rain</c>、派系关系的 <c>Hostile</c>）。</summary>
        Enum = 3,

        /// <summary>坐标。</summary>
        Coord = 4,

        /// <summary>单个实体（一个小人 / 一件物品 / 一张地图 / 一个房间）。</summary>
        Entity = 5,

        /// <summary>一组实体。**没有归约就不能参与比较**——见 RulePathEval。</summary>
        EntitySet = 6,

        /// <summary>
        /// 自由文本。**不可比较、不可参与运算**，只给"写日志"这类操作的参数用。
        ///
        /// 它在值类型表里是唯一的例外，所以规则很简单：任何谓词都**不能**声明
        /// "我的宾语是 Text 并且我要对它做运算"。它的存在只是为了让
        /// 「写日志」不必为了一句人话另开一套参数机制。
        /// </summary>
        Text = 7
    }

    /// <summary>
    /// 实体的种类。比原来的 <c>RuleScopeKind</c> 宽：多了事件型与坐标。
    ///
    /// 它决定**哪些谓词和属性是可用的**——这是编辑器"傻瓜易用"的全部机制。
    /// 不做按类型过滤的话，玩家面对 20 个谓词、其中 3 个能用，剩下 17 个选了之后报错。
    /// </summary>
    public enum RuleEntityKind
    {
        /// <summary>任何类型。只用在"这个谓词不挑食"的场合。</summary>
        Any = 0,

        Map = 1,
        Pawn = 2,
        Thing = 3,
        Room = 4,

        /// <summary>格子。</summary>
        Cell = 5,

        /// <summary>事件型（袭击 / 虫巢 / 精神崩溃）。只在时间上发生，没有位置。</summary>
        Event = 6,

        /// <summary>集合。**尚未归约**的中间态，不能直接喂给谓词。</summary>
        Set = 7
    }

    /// <summary>
    /// 归约算子：把一个集合变回单值。
    ///
    /// <b>"全部 / 任意 / 无"不在这里</b>，这一条是实施时才发现并纠正的：
    /// `.全部.耐久 小于 50%` 不是"把耐久归约成一个数再比"，
    /// 而是"对每个元素都做一次比较，全部成立"。形状完全不同——
    /// 前者是路径上的一步，后者是**检测上的量词**（类似 LINQ 的 All/Any）。
    ///
    /// 所以归约只管"选出单值"，量词留给检测树。
    /// 而 RimWorld 里"一个槽位穿多件同类装备"极少见，集合通常是 0 或 1 个元素，
    /// 因此第一期用 <see cref="First"/> + <see cref="Count"/> 就能写出绝大多数话，
    /// 量词节点等真有需求再加（它要引入"元素绑定"，是另一个复杂度级别）。
    /// </summary>
    public enum RuleReduceKind
    {
        /// <summary>第一个匹配项。确定性的——按属性表声明的稳定顺序取，不随机。</summary>
        First = 0,

        /// <summary>个数。产出数值。</summary>
        Count = 1,

        /// <summary>求和。产出数值。</summary>
        Sum = 2,

        /// <summary>最大值。产出数值。</summary>
        Max = 3,

        /// <summary>最小值。产出数值。</summary>
        Min = 4,

        /// <summary>离本主体最近的。产出单个实体。</summary>
        Nearest = 5
    }

    /// <summary>谓词的两大类。区别只在语义：检测产出真/假，操作产出改变。</summary>
    public enum RuleVerbCategory
    {
        Detect = 0,
        Operate = 1
    }

    /// <summary>
    /// 触发语义。**是谓词自己的属性，不是玩家要选的东西。**
    ///
    /// 这也是"触发器"这个类别消失的原因：`袭击来到本图` 就是一条普通检测，
    /// "会不会重复触发"由谓词作者声明，不该让玩家在一个叫"触发方式"的下拉框里替他决定。
    /// </summary>
    public enum RuleEdge
    {
        /// <summary>电平：只要状态持续为真就持续成立。</summary>
        Level = 0,

        /// <summary>边沿：只在发生的那一刻成立。</summary>
        Edge = 1
    }

    /// <summary>
    /// 谓词不成立时的语义。这是"条件 / 能力"合并之后保留区别的**唯一**手段。
    ///
    /// 原来条件与能力分两张表的理由是：能力不匹配要**安静让开**——
    /// "小明在精神崩溃，是该回家但做不了"，引擎该安静地不动作，而不是把规则判成失败。
    /// 合并成一张检测表之后，这个区别下沉到谓词自己声明。
    /// </summary>
    public enum RuleFailureMode
    {
        /// <summary>算规则失败，记 <c>failed</c>。</summary>
        RuleFailed = 0,

        /// <summary>安静跳过，记 <c>capability_denied</c>，交回原版，**不算规则出错**。</summary>
        QuietSkip = 1
    }

    /// <summary>
    /// 实体表达式路径的**根**——"从哪儿开始"。
    ///
    /// 只有两个隐含绑定：本主体（选择器选出来的那个）和本图（当前被评估的地图）。
    /// 不做实体命名，因为这个阶段没有"同一规则里要区分两张地图"的需求，
    /// 而名字只会多出一张要玩家自己维护的表。详见 Docs/语义规范.md §5.4。
    /// </summary>
    public enum RuleRootKind
    {
        /// <summary>本次评估的执行者。类型由主体绑定方式决定。</summary>
        Subject = 0,

        /// <summary>
        /// **筛选器内部的当前元素**。只在 <c>[ 筛选 ]</c> 里有效。
        ///
        /// 刻意与 <see cref="Subject"/> 分开，而不是让筛选把主体"遮蔽"掉：
        /// 遮蔽之后，"小人的血量比这件衣服的耐久高"这句话就写不出来了——
        /// 筛选里的 `本主体` 已经变成那件衣服。两个名字换来的是没有歧义。
        ///
        /// 界面上它看起来是**隐式**的：在 `[ ]` 里写 `耐久 小于 50%`，
        /// 编辑器自动给谓词的主语挂上 Element 根。玩家不需要知道它叫什么。
        /// </summary>
        Element = 1,

        /// <summary>本次评估的地图。引擎按地图实例化，所以规则对每张地图各跑一遍。</summary>
        Map = 2,

        /// <summary>地图上全部自由殖民者。**是一个集合**，必须归约。</summary>
        Colonists = 3,

        /// <summary>全部地图。**是一个集合**。</summary>
        AllMaps = 4,

        /// <summary>字面量。值在 <c>RulePath.rootLiteral</c> 里。</summary>
        Literal = 5,

        /// <summary>
        /// **任意"一群同类实体"的根** —— 具体是哪一群写在
        /// <c>RulePath.rootLiteral</c>（一个 Enum 值，键 = 主体绑定表的 key）。
        ///
        /// 为什么不给每种群体各加一个枚举值：那样"加一种主体绑定"就要同时加一个根，
        /// 两处都得改、迟早漏一处。共用这一个值之后，**绑定表里多一行，
        /// 根列表里就自动多一行**。
        ///
        /// 产出的是**集合**，必须归约才能用（`.数量` / `.第一个`）。
        ///
        /// <b>它表达不了"对每个成员各判一次"</b>——那是主体绑定的事。
        /// `全部机械族.电量 小于 50%` 的字面意思是"这一组东西的电量小于 50%"，
        /// 而不是"每个机械族的电量都小于 50%"。想要后者就把**主体**绑成那个群体。
        /// </summary>
        PawnGroup = 6
    }

    /// <summary>路径上一步的种类。</summary>
    public enum RuleStepKind
    {
        /// <summary>读一个属性：<c>.耐久</c> <c>.天气</c> <c>.位置</c></summary>
        Property = 0,

        /// <summary>筛掉不满足条件的元素：<c>[是 帽子]</c>。产出集合。</summary>
        Filter = 1,

        /// <summary>归约：<c>.第一个</c> <c>.数量</c></summary>
        Reduce = 2,

        /// <summary>
        /// 量词：对集合里**每个元素各判一次**同一个条件，再把结果合成一个布尔。
        ///
        /// 它和 <see cref="Reduce"/> 的区别是形状，不是省事：
        /// 归约产出的是"这一组里的某一个/个数"，而量词产出的是"**关于这一组的一句话**"。
        /// 少了它，「每件帽子耐久都低于 50%」永远写不出来——那需要否定，
        /// 而语言里没有否定（`数量[非P] 等于 0` 是同一件事，但玩家得自己做那道逻辑题）。
        /// </summary>
        Quantify = 3
    }

    /// <summary>
    /// 量词的三种。都产出布尔。
    ///
    /// <b>空集合上「全都满足」是成立的</b>（空真），这是刻意的、也是数学上的标准读法：
    /// 「我的帽子都没问题」在没有帽子时是句实话。副作用要记住：
    /// `全部囚犯 全都满足 …` 在殖民地一个囚犯都没有时也成立——
    /// 所以**"一共几个"和"每个怎么样"是两件事**，前者用 `.数量`。
    /// </summary>
    public enum RuleQuantifier
    {
        /// <summary>每个元素都满足。空集合算成立。</summary>
        All = 0,

        /// <summary>至少有一个满足。空集合算不成立。</summary>
        Any = 1,

        /// <summary>一个都不满足。空集合算成立。</summary>
        None = 2
    }

    /// <summary>量词在界面与日志里的名字。<c>RuleCore.Step.Quantify.*</c></summary>
    public static class RuleQuantifiers
    {
        public static string LabelKey(RuleQuantifier kind)
        {
            return "RuleCore.Step.Quantify." + kind;
        }

        public static readonly RuleQuantifier[] All =
        {
            RuleQuantifier.All, RuleQuantifier.Any, RuleQuantifier.None
        };
    }

    /// <summary>
    /// 一个实体**具备什么**的能力位 —— 属性与谓词的"能不能挂在这东西上"。
    ///
    /// <b>为什么需要它。</b> 只有实体种类（<see cref="RuleEntityKind.Pawn"/>）这一维是不够的：
    /// 机械族、动物、殖民者在词表里都叫「小人」，但它们身上能读的东西完全不同——
    /// 机械族没有饱食度、有电量；动物没有理念、有性别。不给这一维，编辑器就只能在
    /// 机械族身上也列出「饱食度」，玩家选了必然读到 <c>prop.no_need</c>，
    /// 而"看得见但选了必错"正是这套东西存在的全部理由。
    ///
    /// <b>它不是"玩家能选的东西"</b>，而是作者在词表里对一行属性的声明
    /// （<see cref="RulePropertyInfo.requires"/>）："我这行只有在宿主有这些能力时才有意义"。
    /// 编辑器拿当前主体的**实际**能力位去比，缺哪一位就把它从列表里拿掉，并说清缺的是什么。
    ///
    /// <b>为什么用位而不是委托。</b> 位是纯数据，所以过滤是纯函数——
    /// 可以脱离游戏穷举测试（"机械族不该看见饱食度"这种断言必须能被钉死），
    /// 也能给玩家一句人话的原因（"他缺：饱食需求"）。委托则只能在游戏里跑。
    /// </summary>
    [System.Flags]
    public enum RuleCapability
    {
        None = 0,

        // ── 实体大类 ──────────────────────────────────────────────────
        Pawn = 1 << 0,
        Map = 1 << 1,
        Thing = 1 << 2,
        Room = 1 << 3,
        Cell = 1 << 4,

        // ── 小人的生理构成 ────────────────────────────────────────────
        /// <summary>人形（能穿衣服、有技能、有心情、能被征召）。</summary>
        Humanlike = 1 << 5,

        /// <summary>机械族：有电量、没有生理需求。</summary>
        Mechanoid = 1 << 6,

        /// <summary>动物。</summary>
        Animal = 1 << 7,

        /// <summary>有血肉之躯（会饿、会累）。人和动物都有，机械族没有。</summary>
        Biological = 1 << 8,

        // ── 需求。逐条声明而不是笼统一个「有需求」──
        // 因为"这个人有没有心情"和"有没有饱食度"正是玩家要分辨的东西。
        NeedMood = 1 << 9,
        NeedFood = 1 << 10,
        NeedRest = 1 << 11,

        /// <summary>电量（<c>Need_MechEnergy</c>）。机械族专属。</summary>
        NeedEnergy = 1 << 12,

        NeedJoy = 1 << 13,
        NeedComfort = 1 << 14,
        NeedBeauty = 1 << 15,

        // ── 装备与所有物 ──────────────────────────────────────────────
        Apparel = 1 << 16,
        Equipment = 1 << 17,
        Inventory = 1 << 18,

        /// <summary>有归属信息（能拥有床/床铺位）。</summary>
        Bed = 1 << 19,

        // ── 身份。**不用于挡掉"是不是囚犯"这类问题**（那对任何小人都有意义），
        // 只用于挡掉"只有囚犯才有意义"的属性与操作。 ────────────────────
        Colonist = 1 << 20,
        Prisoner = 1 << 21,
        Slave = 1 << 22,
        Guest = 1 << 23,

        /// <summary>与玩家敌对。</summary>
        Hostile = 1 << 24,

        /// <summary>
        /// **野生动物**——是动物，而且不属于任何有人形的派系（也就是没人驯化它）。
        ///
        /// 为什么要单独一位，而不是让「标记狩猎」只要求 <see cref="Animal"/>：
        /// 驯化过的殖民地动物**也有** <see cref="Animal"/>，于是玩家绑上「殖民地动物」时
        /// 会看到「标记狩猎」，而它对每一只都必然失败（原版也不让猎自家动物）。
        /// 那就是"看得见但选了必错"——本项目已经栽过三次的那个形状。
        ///
        /// 它和 <see cref="Colonist"/> / <see cref="Prisoner"/> / <see cref="Slave"/> /
        /// <see cref="Guest"/> / <see cref="Hostile"/> 是同一类：不是生理构造，
        /// 而是**这个宿主属于哪个类别**。所以它排在身份组里。
        ///
        /// 判据 <see cref="RuleMapFacts.IsWildAnimal"/> 和「标记狩猎」共用一份。
        /// </summary>
        Wild = 1 << 29,

        // ── 能力 ──────────────────────────────────────────────────────
        /// <summary>能被征召（有人形或机械族的指挥链）。</summary>
        CanDraft = 1 << 25,

        Skills = 1 << 26,
        Ideo = 1 << 27,

        /// <summary>有年龄（能读生物年龄）。</summary>
        Age = 1 << 28,

        /// <summary>
        /// **什么都有可能** —— 拿不到样本时的答案。
        ///
        /// 语义上是"宁可多给，不可少给"：不知道主体是什么的时候，
        /// 把全部选项摆出来让玩家自己判断，比因为查不到而把有用的东西藏起来强。
        /// 所以过滤的判据是 <c>(have &amp; need) == need</c>，
        /// <see cref="All"/> 自然通过一切检查。
        /// </summary>
        All = -1
    }

    /// <summary>
    /// 主体绑定的**基数**：这条规则是"对每个人各跑一遍"，还是"只对指定的那一个跑"。
    ///
    /// 它决定 <see cref="Rule.subjectRef"/> 是不是必填，也决定编辑器该显示
    /// 「全部囚犯 (3)」还是一个名字。
    /// </summary>
    public enum RuleSubjectScope
    {
        /// <summary>绑定出一组实体，引擎对**每一个**各跑一遍（冷却按人分开算）。</summary>
        Group = 0,

        /// <summary>绑定到一个**指定**的实体（<see cref="Rule.subjectRef"/>），整条规则只跑一遍。</summary>
        Single = 1
    }

    /// <summary>布尔检测树的节点种类。</summary>
    public enum RuleExprNodeKind
    {
        /// <summary>叶子：一个三元组（实体 · 检测 · 宾语）。</summary>
        Detect = 0,

        /// <summary>全部成立。</summary>
        And = 1,

        /// <summary>任一成立。</summary>
        Or = 2,

        /// <summary>取反。</summary>
        Not = 3
    }
}
