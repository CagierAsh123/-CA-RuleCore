using System.Collections.Generic;

namespace RuleCore.Core
{
    /// <summary>
    /// 坐标。刻意**不用 UnityEngine.IntVec3** —— Core 是纯 BCL，这正是它能脱离游戏单测的原因。
    /// 到了 Verse 那一侧再翻译成 <c>IntVec3</c>，翻译只有一处。
    /// </summary>
    public struct RuleCell
    {
        public int x;
        public int z;

        public RuleCell(int x, int z)
        {
            this.x = x;
            this.z = z;
        }

        public bool IsValid
        {
            get { return x >= 0 && z >= 0; }
        }

        public static RuleCell Invalid
        {
            get { return new RuleCell(-1, -1); }
        }

        public override string ToString()
        {
            return "(" + x + ", " + z + ")";
        }
    }

    /// <summary>
    /// 规则语言里的一个值。
    ///
    /// <b>为什么是一个"什么都能装"的结构体，而不是泛型或者继承体系。</b>
    /// 实体表达式路径是**运行期才知道类型**的（词表里的一行说"我产出数值"），
    /// 泛型在这里没有立足点：`着装` 产出集合、`第一个` 产出实体、`耐久` 产出数值，
    /// 一条路径上的类型逐段变化。做继承体系则要为每种值写一个类，
    /// 而值的种类只有六种、行为也极简单（比大小 / 取反 / 格式化）。
    ///
    /// 代价是取值要判 kind。所以每个访问器都**不抛异常、返回默认值**，
    /// 由调用方先用 <see cref="IsNumber"/> 这类判断——路径求值器本来就必须逐段判类型，
    /// 顺手判一下不增加负担；而抛异常会让"玩家写了一条类型不对的路径"变成崩溃，
    /// 那在规则语言里是不可接受的。
    /// </summary>
    public struct RuleValue
    {
        public readonly RuleValueKind kind;

        /// <summary>
        /// kind 是 Entity / EntitySet 时，实体本身的种类。
        /// 路径上的类型检查全靠它——`着装` 为什么能挂在主体上、`耐久` 为什么能挂在衣服上。
        /// </summary>
        public readonly RuleEntityKind entityKind;

        private readonly float number;
        private readonly bool boolean;
        private readonly string key;
        private readonly RuleCell cell;
        private readonly IReadOnlyList<RuleValue> items;

        /// <summary>
        /// 实体背后的**不透明把手** —— Verse 那一侧把游戏对象塞在这里，Core 只是搬运它。
        ///
        /// 为什么必须有：`RuleValue` 只带"这是个 Thing"，不带"这是**哪一个** Thing"。
        /// 少了它，宿主就没法把值翻译回游戏对象（"归约成最近的那个"要算距离，
        /// "脱下这件衣服"要知道是哪件）。而 Core 不认识 Pawn / Thing，所以只能是 object。
        /// Core 自己从不读它——`Count` / `First` 这类纯归约不需要知道是谁。
        /// </summary>
        private readonly object handle;

        private RuleValue(RuleValueKind kind, RuleEntityKind entityKind, float number,
            bool boolean, string key, RuleCell cell, IReadOnlyList<RuleValue> items, object handle)
        {
            this.kind = kind;
            this.entityKind = entityKind;
            this.number = number;
            this.boolean = boolean;
            this.key = key;
            this.cell = cell;
            this.items = items;
            this.handle = handle;
        }

        public static readonly RuleValue None = new RuleValue(
            RuleValueKind.None, RuleEntityKind.Any, 0f, false, null, RuleCell.Invalid, null, null);

        public static RuleValue OfNumber(float value)
        {
            return new RuleValue(RuleValueKind.Number, RuleEntityKind.Any, value, false, null,
                RuleCell.Invalid, null, null);
        }

        public static RuleValue OfBool(bool value)
        {
            return new RuleValue(RuleValueKind.Bool, RuleEntityKind.Any, 0f, value, null,
                RuleCell.Invalid, null, null);
        }

        public static RuleValue OfKey(string value)
        {
            return new RuleValue(RuleValueKind.Enum, RuleEntityKind.Any, 0f, false, value,
                RuleCell.Invalid, null, null);
        }

        public static RuleValue OfCell(RuleCell value)
        {
            return new RuleValue(RuleValueKind.Coord, RuleEntityKind.Cell, 0f, false, null,
                value, null, null);
        }

        public static RuleValue OfEntity(RuleEntityKind entityKind)
        {
            return OfEntity(entityKind, null);
        }

        public static RuleValue OfEntity(RuleEntityKind entityKind, object handle)
        {
            return new RuleValue(RuleValueKind.Entity, entityKind, 0f, false, null,
                RuleCell.Invalid, null, handle);
        }

        public static RuleValue OfSet(RuleEntityKind elementKind, IReadOnlyList<RuleValue> values)
        {
            return new RuleValue(RuleValueKind.EntitySet, elementKind, 0f, false, null,
                RuleCell.Invalid, values, null);
        }

        /// <summary>自由文本。只给"写日志"这类操作的参数用，**永不参与比较**。</summary>
        public static RuleValue OfText(string value)
        {
            return new RuleValue(RuleValueKind.Text, RuleEntityKind.Any, 0f, false, value,
                RuleCell.Invalid, null, null);
        }

        // ── 访问器。类型不对时返回默认值，绝不抛异常。 ────────────────

        public float AsNumber
        {
            get { return kind == RuleValueKind.Number ? number : 0f; }
        }

        public bool AsBool
        {
            get { return kind == RuleValueKind.Bool && boolean; }
        }

        public string AsKey
        {
            get { return kind == RuleValueKind.Enum ? key : null; }
        }

        /// <summary>自由文本。kind 不是 Text 时返回 null。</summary>
        public string AsText
        {
            get { return kind == RuleValueKind.Text ? (key ?? string.Empty) : null; }
        }

        public RuleCell AsCell
        {
            get { return kind == RuleValueKind.Coord ? cell : RuleCell.Invalid; }
        }

        public IReadOnlyList<RuleValue> AsItems
        {
            get { return items; }
        }

        /// <summary>实体背后的游戏对象。只有 Verse 侧的实现会读它。</summary>
        public object AsHandle
        {
            get { return handle; }
        }

        public int Count
        {
            get { return items != null ? items.Count : 0; }
        }

        public bool IsNumber
        {
            get { return kind == RuleValueKind.Number; }
        }

        public bool IsBool
        {
            get { return kind == RuleValueKind.Bool; }
        }

        public bool IsEntity
        {
            get { return kind == RuleValueKind.Entity; }
        }

        public bool IsSet
        {
            get { return kind == RuleValueKind.EntitySet; }
        }

        public bool IsMissing
        {
            get { return kind == RuleValueKind.None; }
        }

        public bool IsText
        {
            get { return kind == RuleValueKind.Text; }
        }

        /// <summary>诊断用。不进界面——界面上的显示要带单位，那是 RuleFormat 的事。</summary>
        public override string ToString()
        {
            switch (kind)
            {
                case RuleValueKind.Number: return number.ToString("0.###");
                case RuleValueKind.Bool: return boolean ? "true" : "false";
                case RuleValueKind.Enum: return key ?? "(enum?)";
                case RuleValueKind.Coord: return cell.ToString();
                case RuleValueKind.Entity: return "entity:" + entityKind;
                case RuleValueKind.EntitySet: return "set<" + entityKind + ">[" + Count + "]";
                default: return "(none)";
            }
        }
    }
}
