using System.Collections.Generic;
using Verse;
using RuleCore.Core;

namespace RuleCore
{
    /// <summary>
    /// 检测树的一个节点：`且` / `或` / `非` / 叶子（一个三元组）。
    ///
    /// **为什么是树而不是列表**：`(A且B)或C` 和 `A且(B或C)` 是两句不同的话，
    /// 用列表加优先级只能表达其中一句，而"拆成两条规则"又绕不过去。
    /// 既然决定做嵌套，就做成真的树——显式括号，玩家不用记优先级。
    ///
    /// 叶子就是三元组本身：<see cref="subject"/> · <see cref="verbKey"/> · <see cref="argument"/>。
    /// 触发器、条件、能力在这一层没有区别——它们的区别下沉成谓词的
    /// <c>edge</c>（边沿/电平）与 <c>failure</c>（算失败/安静让开）声明。
    /// </summary>
    public class RuleExprNode : IExposable, IRuleExprSource
    {
        public RuleExprNodeKind kind = RuleExprNodeKind.Detect;

        /// <summary>And / Or / Not 的子节点。</summary>
        public List<RuleExprNode> children = new List<RuleExprNode>();

        // ── 叶子（Detect）专用 ────────────────────────────────────────

        /// <summary>主语。**筛选器里的叶子用 <see cref="RuleRootKind.Element"/> 作根。**</summary>
        public RulePath subject;

        /// <summary>词表里的谓词键。</summary>
        public string verbKey;

        /// <summary>宾语。可为空（一元谓词，如 `本主体 能动`）。</summary>
        public RuleOperand argument;

        // ── IRuleExprSource ──────────────────────────────────────────

        public RuleExprNodeKind NodeKind
        {
            get { return kind; }
        }

        public int ChildCount
        {
            get { return children != null ? children.Count : 0; }
        }

        public IRuleExprSource ChildAt(int index)
        {
            return children[index];
        }

        public IRulePathSource Subject
        {
            get { return subject; }
        }

        public string VerbKey
        {
            get { return verbKey; }
        }

        public IRuleOperandSource Argument
        {
            get { return argument; }
        }

        // ── 构造（编辑器用，也让 XML 之外的用法读起来像句子）──────────

        public static RuleExprNode Leaf(RulePath subject, string verbKey, RuleOperand argument)
        {
            return new RuleExprNode
            {
                kind = RuleExprNodeKind.Detect,
                subject = subject,
                verbKey = verbKey,
                argument = argument
            };
        }

        public static RuleExprNode All(params RuleExprNode[] nodes)
        {
            var node = new RuleExprNode { kind = RuleExprNodeKind.And };
            for (int i = 0; i < nodes.Length; i++)
            {
                if (nodes[i] != null) node.children.Add(nodes[i]);
            }
            return node;
        }

        public static RuleExprNode Any(params RuleExprNode[] nodes)
        {
            var node = new RuleExprNode { kind = RuleExprNodeKind.Or };
            for (int i = 0; i < nodes.Length; i++)
            {
                if (nodes[i] != null) node.children.Add(nodes[i]);
            }
            return node;
        }

        public static RuleExprNode Negate(RuleExprNode node)
        {
            var expr = new RuleExprNode { kind = RuleExprNodeKind.Not };
            if (node != null) expr.children.Add(node);
            return expr;
        }

        public bool IsLeaf
        {
            get { return kind == RuleExprNodeKind.Detect; }
        }

        /// <summary>
        /// 整棵树有多深。校验用：手改 XML 造出自引用（树里又指向自己）时，
        /// 求值器的深度上限会兜住，但**载入时就该报出来**，不该等跑到一半。
        /// </summary>
        public int Depth()
        {
            if (children == null || children.Count == 0) return 1;

            int deepest = 0;
            for (int i = 0; i < children.Count; i++)
            {
                var child = children[i];
                if (child == null) continue;
                int d = child.Depth();
                if (d > deepest) deepest = d;
            }
            return deepest + 1;
        }

        public void ExposeData()
        {
            Scribe_Values.Look(ref kind, "kind", RuleExprNodeKind.Detect);
            Scribe_Collections.Look(ref children, "children", LookMode.Deep);
            Scribe_Deep.Look(ref subject, "subject");
            Scribe_Values.Look(ref verbKey, "verbKey");
            Scribe_Deep.Look(ref argument, "argument");

            if (Scribe.mode == LoadSaveMode.PostLoadInit && children == null)
            {
                children = new List<RuleExprNode>();
            }
        }

        public override string ToString()
        {
            switch (kind)
            {
                case RuleExprNodeKind.And: return "(" + JoinChildren(" 且 ") + ")";
                case RuleExprNodeKind.Or: return "(" + JoinChildren(" 或 ") + ")";
                case RuleExprNodeKind.Not: return "非(" + (ChildCount > 0 ? children[0].ToString() : "?") + ")";
                default:
                    return (subject != null ? subject.ToString() : "?")
                        + " " + (verbKey ?? "?")
                        + (argument != null ? " " + argument : string.Empty);
            }
        }

        private string JoinChildren(string separator)
        {
            var sb = new System.Text.StringBuilder(64);
            for (int i = 0; i < ChildCount; i++)
            {
                if (i > 0) sb.Append(separator);
                sb.Append(children[i] != null ? children[i].ToString() : "?");
            }
            return sb.ToString();
        }
    }
}
