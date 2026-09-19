using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;
using RuleCore.Core;

namespace RuleCore
{
    /// <summary>
    /// 规则编辑器 —— **句子 + 检查器** 两栏。
    ///
    /// 这个形态是照着用户给的 HTML 原型重做的，核心差别只有一句话：
    /// **点槽位 → 右侧检查器列出选项，全程不弹菜单。**
    ///
    /// 上一版把每个槽位都做成"点开一个 FloatMenu"，三件事同时坏了：
    ///   · 菜单盖住面板，改完还得等它关掉才能看结果；
    ///   · 每个槽位长得一模一样，主语/谓语/宾语分不出来；
    ///   · 删除按钮在路径内部和行尾各有一个，视觉上分不清删的是哪一层。
    ///
    /// 原型给的解法是"用颜色和边框把语法画出来"：
    ///   · 子句 = 一个带左边框的盒子，**青色=检测 / 橙色=操作**；
    ///   · 主语=紫、谓语=按类别着色、宾语=土黄；
    ///   · 一个子句**只有一个** ×，在行尾；
    ///   · 空槽显示成灰字"选择实体…"，一眼看出哪里还没填。
    /// </summary>
    public sealed class RuleEditorView
    {
        private static readonly Color DetectColor = new Color(0.31f, 0.76f, 0.97f);
        private static readonly Color OperateColor = new Color(1f, 0.72f, 0.30f);
        private static readonly Color EntityColor = new Color(0.70f, 0.53f, 1f);
        private static readonly Color ReduceColor = new Color(0.40f, 0.73f, 0.42f);
        private static readonly Color ObjectColor = new Color(0.83f, 0.71f, 0.51f);
        private static readonly Color MutedColor = new Color(0.54f, 0.57f, 0.65f);
        private static readonly Color DangerColor = new Color(0.94f, 0.33f, 0.31f);
        private static readonly Color PanelColor = new Color(0.063f, 0.071f, 0.094f);
        private static readonly Color BoxColor = new Color(0.118f, 0.133f, 0.176f);
        private static readonly Color SelectedBg = new Color(0.70f, 0.53f, 1f, 0.18f);
        private static readonly Color HoverBg = new Color(1f, 1f, 1f, 0.07f);
        private static readonly Color OptionColor = new Color(0.118f, 0.133f, 0.176f);
        private static readonly Color OptionHover = new Color(0.137f, 0.157f, 0.212f);

        private const float ClauseHeight = 28f;
        private const float LineGap = 6f;
        private const float SlotPadding = 7f;
        private const float KeywordWidth = 20f;
        private const float LineStep = ClauseHeight + LineGap;
        private const float InspectorWidth = 286f;
        private const float GeneratedHeight = 92f;
        private const float OptionHeight = 26f;
        private const float OptionGap = 3f;
        private const float ScrollBarAllowance = 18f;
        private const float IndentWidth = 16f;

        // 选中**按对象引用**，不按下标：下标在"删掉上面一个子句"之后就全错位了。
        private enum Slot
        {
            None,
            Subject,
            Predicate,
            Object
        }

        private sealed class Selection
        {
            public RuleExprNode Leaf;
            public RuleClause Clause;
            public Slot Slot;
            public bool InsideFilter;

            /// <summary>
            /// 正在编辑**规则的主体绑定**（"本主体"到底指谁），而不是某个子句的主语。
            /// 它是整条规则的前提：不选它，规则里每一句 `本主体 …` 都不会成立。
            /// </summary>
            public bool Binding;

            public RulePath FilterPath;
            public int FilterStepIndex = -1;
        }

        private Selection selection;
        private Rule currentRule;

        private Vector2 sentenceScroll;
        private Vector2 inspectorScroll;
        private float sentenceHeight = 300f;
        private float inspectorHeight = 400f;

        private readonly List<string> issues = new List<string>();
        private readonly Dictionary<string, string> buffers = new Dictionary<string, string>();

        // ── 主体样本：能力过滤的输入 ──────────────────────────────────
        //
        // "机械族身上不列饱食度"这件事没法靠猜。编辑器必须**真的看一眼**当前主体是谁，
        // 拿他的实际能力位去比词表里每一行声明的 requires。
        //
        // 样本的取法：能绑出主体就取绑出来的（指名绑定就是他本人；
        // 群体绑定取成员的并集）；绑不出来就当作"什么都有可能"，
        // **宁可多给几个选项，也不要把有用的东西藏起来**——玩家会以为词表里没有它。
        private string sampleKey;
        private int sampleFrame = -1000;
        private RuleCapability sampleCaps = RuleCapability.All;

        /// <summary>一次样本解析的结果：能力位，外加收出了几个主体（界面要显示"囚犯 (3)"）。</summary>
        private int sampleCount;

        /// <summary>
        /// 每种群体绑定这一轮各收出几个主体。
        ///
        /// **"0 个"本身就是最该被看见的信息**：绑定成"囚犯"而殖民地一个囚犯都没有时，
        /// 规则安静地什么都不做，玩家只会觉得"我写对了它怎么不动"。
        /// 数字直接写在选项上，不用点进去看。
        ///
        /// 它跟在样本缓存里一起算（每 30 帧一次），所以每帧读它也不花钱。
        /// </summary>
        private readonly Dictionary<string, int> subjectCounts = new Dictionary<string, int>();

        /// <summary>
        /// 当前主体的能力位（带缓存）。
        ///
        /// 缓存不是因为算一次很贵（收一遍人也就几十项），而是因为
        /// <see cref="Draw"/> 每帧都跑、每次都要问一遍"那个人有什么"。
        /// 缓存键是 (绑定方式, 指名引用, 地图)，另外每 30 帧强制重算一次——
        /// 人会变（机械族被造出来、囚犯被释放），不能永远钉在打开面板那一刻。
        /// </summary>
        private RuleCapability SubjectCapabilities()
        {
            var map = Find.CurrentMap;
            int mapId = map != null ? map.uniqueID : -1;

            string key = (currentRule != null ? currentRule.subjectKey : null) + "|"
                + (currentRule != null ? currentRule.subjectRef : null) + "|" + mapId;

            if (key == sampleKey && Time.frameCount - sampleFrame <= 30)
            {
                return sampleCaps;
            }

            sampleKey = key;
            sampleFrame = Time.frameCount;
            sampleCaps = ComputeSubjectCapabilities(map);
            return sampleCaps;
        }

        private RuleCapability ComputeSubjectCapabilities(Map map)
        {
            sampleCount = 0;
            subjectCounts.Clear();

            if (currentRule == null || map == null) return RuleCapability.All;

            var vocabulary = RuleVocabularyCatalog.Current;
            var info = vocabulary.Subject(currentRule.subjectKey);

            // 每种群体绑定各收一遍，人数写进缓存。
            // **用的是绑定器本体**，不是在这里重写一份判据——两份判据迟早会漂。
            //
            // 注意这**不能**因为"当前绑的是点名"就跳过：那些人数就写在绑定列表上，
            // 跳过的话它们全变成 0，而"自由殖民者 0 个"是一句彻头彻尾的假话
            // （玩家就坐在一个殖民地里）。未知和 0 是两件事，宁可不算也不要给假数。
            {
                var all = new List<RuleSubjectInfo>();
                var bucket = new List<RuleValue>();
                vocabulary.CollectSubjects(all);

                for (int i = 0; i < all.Count; i++)
                {
                    if (all[i].scope == RuleSubjectScope.Single || all[i].binder == null) continue;

                    bucket.Clear();
                    all[i].binder(MakeHost(map), bucket);
                    subjectCounts[all[i].key ?? string.Empty] = bucket.Count;
                }
            }

            if (info == null || info.binder == null) return RuleCapability.All;

            var bound = new List<RuleValue>();
            info.binder(MakeHost(map), bound);
            sampleCount = bound.Count;

            if (bound.Count == 0) return RuleCapability.All;

            // **并集**，取前 12 个成员。
            //
            // 取并集而不是交集：群体里只要有一个成员具备某能力，那个属性就值得摆出来。
            // 取交集的话，一个动物混进殖民地动物群就会把「着装」藏掉，
            // 而玩家看到的是"这个词表没有着装"——一个查不出答案的问题。
            RuleCapability caps = RuleCapability.None;
            int limit = bound.Count < 12 ? bound.Count : 12;
            for (int i = 0; i < limit; i++)
            {
                caps |= RulePawnFacts.CapabilitiesOf(bound[i].AsHandle);
            }

            return caps;
        }

        /// <summary>
        /// 给绑定器用的宿主。指名绑定时必须带上 <c>SubjectRef</c>，否则它一个主体都收不出来。
        /// </summary>
        private RuleEvalHost MakeHost(Map map)
        {
            return new RuleEvalHost(new RuleEvalContext
            {
                Rule = currentRule,
                Map = map,
                Tick = Find.TickManager != null ? Find.TickManager.TicksGame : 0,
                SubjectRef = currentRule != null ? currentRule.subjectRef : null
            });
        }

        /// <summary>
        /// 一条路径的宿主能力位。**只有根是「本主体」时才做能力过滤**——
        /// 路径可以从「本图」或「殖民者」出发，那时主体是谁跟这条路无关，
        /// 拿主体的能力去砍它只会砍掉本来能用的选项。
        /// </summary>
        private RuleCapability PathCapabilities(RulePath path)
        {
            if (path == null) return RuleCapability.All;
            if (path.rootKind != RuleRootKind.Subject) return RuleCapability.All;
            return SubjectCapabilities();
        }

        public bool StructureChanged;
        public bool ValueChanged;

        public void ClearChanged()
        {
            StructureChanged = false;
            ValueChanged = false;
        }

        /// <summary>
        /// 一次绘制。外面套一层 <see cref="TextStateScope"/>：
        /// 这里的每条分支都会改字体/折行（"这一行不折行"是 <c>WordWrap = false</c>），
        /// 而提前 return 的分支不少——手写还原一定会漏，漏了原版就会每帧刷一条
        /// "Word wrap was false at end of frame"。
        /// </summary>
        public void Draw(Rect rect, Rule rule)
        {
            using (new TextStateScope())
            {
                DrawInner(rect, rule);
            }
        }

        private void DrawInner(Rect rect, Rule rule)
        {
            Widgets.DrawMenuSection(rect);
            var inner = rect.ContractedBy(6f);

            if (rule == null)
            {
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.UpperLeft;
                Text.WordWrap = false;
                selection = null;
                currentRule = null;
                Widgets.Label(new Rect(inner.x + 4f, inner.y + 6f,
                        Mathf.Max(inner.width - 8f, 16f), Mathf.Max(inner.height, 16f)),
                    "RuleCore.Form.NoSelection".Translate());
                return;
            }

            currentRule = rule;
            bool readOnly = !rule.IsEditable;

            // 顶上一条**横跨整栏**的身份栏：名称 / 冷却 / 主体绑定。
            // 它不属于句子也不属于检查器——规则的身份是全局信息，
            // 塞进任何一栏都会在窄的时候被压变形（而"主体绑定"尤其塞不下）。
            const float headerHeight = 30f;
            DrawHeader(new Rect(inner.x, inner.y, inner.width, headerHeight), rule, readOnly);

            var body = new Rect(inner.x, inner.y + headerHeight + 4f, inner.width,
                Mathf.Max(inner.height - headerHeight - 4f, 40f));

            // 面板被挤窄时（玩家把窗口拉小），两栏必须都还在正数宽度上——
            // 负宽度的 Rect 会让 IMGUI 每帧报错，而那正是玩家第一次拉窗口时会发生的事。
            // 窄到放不下两栏就只画句子：检查器可以下次再点，报错不行。
            float inspectorWidth = Mathf.Min(InspectorWidth, Mathf.Max(180f, body.width * 0.42f));
            float leftWidth = body.width - inspectorWidth - 6f;

            if (leftWidth < 140f || inspectorWidth < 120f)
            {
                DrawSentence(body, rule, readOnly);
                return;
            }

            var sentenceRect = new Rect(body.x, body.y, leftWidth, body.height);
            var inspectorRect = new Rect(sentenceRect.xMax + 6f, body.y,
                body.width - leftWidth - 6f, body.height);

            DrawSentence(sentenceRect, rule, readOnly);
            DrawInspector(inspectorRect, rule, readOnly);
        }

        // ══ 左栏：句子 ════════════════════════════════════════════════

        private void DrawSentence(Rect rect, Rule rule, bool readOnly)
        {
            Widgets.DrawBoxSolid(rect, PanelColor);

            var listRect = new Rect(rect.x, rect.y, rect.width, rect.height - GeneratedHeight - 4f);
            var generatedRect = new Rect(rect.x, listRect.yMax + 4f, rect.width, GeneratedHeight);

            DrawClauseList(listRect, rule, readOnly);
            DrawGeneratedText(generatedRect, rule);
        }

        private void DrawClauseList(Rect rect, Rule rule, bool readOnly)
        {
            float width = Mathf.Max(rect.width - ScrollBarAllowance, 1f);
            var viewRect = new Rect(0f, 0f, width, Mathf.Max(sentenceHeight, rect.height));

            Widgets.BeginScrollView(rect.ContractedBy(6f), ref sentenceScroll, viewRect);

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            Text.WordWrap = false;

            float y = 0f;
            bool first = true;

            if (rule.detect == null && (rule.operate == null || rule.operate.Count == 0))
            {
                GUI.color = MutedColor;
                Widgets.Label(new Rect(4f, 10f, width - 8f, 24f), "RuleCore.Edit.ClauseEmpty".Translate());
                GUI.color = Color.white;
                y = 40f;
            }

            if (rule.detect != null)
            {
                y = DrawDetectNode(rule.detect, y, width, 0, readOnly, delegate { first = false; }, first);
            }

            if (rule.operate != null)
            {
                for (int i = 0; i < rule.operate.Count; i++)
                {
                    string keyword;
                    if (first)
                    {
                        keyword = "RuleCore.Edit.KwWhen".Translate();
                        first = false;
                    }
                    else if (i == 0)
                    {
                        keyword = "RuleCore.Edit.KwComma".Translate();
                    }
                    else
                    {
                        keyword = "RuleCore.Edit.KwAnd".Translate();
                    }

                    y = DrawClauseLine(rule.operate[i], keyword, y, width, 0, readOnly, false);
                }
            }

            if (!readOnly)
            {
                if (Button(new Rect(4f, y + 8f, 78f, 26f),
                        "RuleCore.Edit.AddDetect".Translate(), DetectColor))
                {
                    AddDetectClause(rule);
                }

                if (Button(new Rect(86f, y + 8f, 78f, 26f),
                        "RuleCore.Edit.AddOperate".Translate(), OperateColor))
                {
                    if (rule.operate == null) rule.operate = new List<RuleClause>();
                    rule.operate.Add(RuleClause.Of(null, null, null));
                    StructureChanged = true;
                }

                y += 42f;
            }

            sentenceHeight = y + 8f;

            Widgets.EndScrollView();

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        /// <summary>
        /// 画一棵检测树。**顶层的 `且` 摊平成一串连续的句子行**（原型的样子），
        /// `或` / `非` / 更深的分组画成带标题的缩进块。
        /// </summary>
        private float DrawDetectNode(RuleExprNode node, float y, float width, int depth,
            bool readOnly, Action onFirst, bool isFirstClause)
        {
            if (node == null) return y;

            if (node.IsLeaf)
            {
                string keyword = isFirstClause
                    ? "RuleCore.Edit.KwWhen".Translate()
                    : "RuleCore.Edit.KwAnd".Translate();

                float bottom = DrawClauseLine(node, keyword, y, width, depth, readOnly, false);
                if (onFirst != null) onFirst();
                return bottom;
            }

            if (node.kind == RuleExprNodeKind.And)
            {
                float cursor = y;
                for (int i = 0; i < node.ChildCount; i++)
                {
                    cursor = DrawDetectNode(node.children[i], cursor, width, depth, readOnly,
                        onFirst, isFirstClause && i == 0);
                }
                return cursor;
            }

            string title = node.kind == RuleExprNodeKind.Or
                ? "RuleCore.Edit.KwOr".Translate()
                : "RuleCore.Edit.KwNot".Translate();

            float hx = depth * IndentWidth;

            Text.Anchor = TextAnchor.MiddleCenter;
            var prev = GUI.color;
            GUI.color = node.kind == RuleExprNodeKind.Or ? OperateColor : MutedColor;
            Widgets.Label(new Rect(hx, y, KeywordWidth, ClauseHeight), title);
            GUI.color = prev;
            Text.Anchor = TextAnchor.MiddleLeft;

            float bx = hx + KeywordWidth + 14f;

            if (!readOnly)
            {
                bool needChild = node.kind == RuleExprNodeKind.Not && node.ChildCount == 0;
                if (needChild || node.kind != RuleExprNodeKind.Not)
                {
                    if (Button(new Rect(bx, y + 3f, 56f, ClauseHeight - 6f),
                            "RuleCore.Edit.AddDetect".Translate(), DetectColor))
                    {
                        node.children.Add(NewLeaf());
                        StructureChanged = true;
                    }
                }

                if (node.kind != RuleExprNodeKind.Not)
                {
                    bx += 60f;
                    if (Button(new Rect(bx, y + 3f, 46f, ClauseHeight - 6f),
                            "RuleCore.Edit.AddOr", OperateColor))
                    {
                        var child = new RuleExprNode { kind = RuleExprNodeKind.Or };
                        child.children.Add(NewLeaf());
                        node.children.Add(child);
                        StructureChanged = true;
                    }

                    bx += 50f;
                    if (Button(new Rect(bx, y + 3f, 48f, ClauseHeight - 6f),
                            "RuleCore.Edit.AddNot", OperateColor))
                    {
                        node.children.Add(new RuleExprNode
                        {
                            kind = RuleExprNodeKind.Not,
                            children = new List<RuleExprNode>()
                        });
                        StructureChanged = true;
                    }
                }
            }

            float cursor2 = y + LineStep;

            for (int i = 0; i < node.ChildCount; i++)
            {
                var child = node.children[i];
                float before = cursor2;
                cursor2 = DrawDetectNode(child, cursor2, width, depth + 1, readOnly, onFirst, false);

                if (!readOnly)
                {
                    var delRect = new Rect(width - 22f, before + 3f, 18f, ClauseHeight - 6f);
                    if (DeleteButton(delRect))
                    {
                        node.children.RemoveAt(i);
                        if (selection != null && selection.Leaf == child) selection = null;
                        StructureChanged = true;
                        return cursor2;
                    }
                }
            }

            if (node.kind == RuleExprNodeKind.Not && node.ChildCount == 0)
            {
                GUI.color = MutedColor;
                Widgets.Label(new Rect((depth + 1) * IndentWidth + 8f, cursor2, width - 40f, 22f),
                    "RuleCore.Edit.NotEmpty".Translate());
                GUI.color = Color.white;
                cursor2 += LineStep;
            }

            return cursor2;
        }

        /// <summary>
        /// 一个子句（三元组）占整整一行：连接词 + 盒子 + 行尾的 ×。
        /// **一个子句只有一个 ×**——上一版在路径内部还有一个，根本分不清删哪一层。
        /// </summary>
        private float DrawClauseLine(object clause, string keyword, float y, float width,
            int depth, bool readOnly, bool isFilterClause)
        {
            RuleExprNode leaf = clause as RuleExprNode;
            RuleClause op = clause as RuleClause;

            float indent = depth * IndentWidth;

            if (!string.IsNullOrEmpty(keyword))
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                var prevKeyword = GUI.color;
                GUI.color = keyword == "，" ? OperateColor : MutedColor;
                Widgets.Label(new Rect(indent, y, KeywordWidth, ClauseHeight), keyword);
                GUI.color = prevKeyword;
                Text.Anchor = TextAnchor.MiddleLeft;
            }

            float boxX = indent + (string.IsNullOrEmpty(keyword) ? 0f : KeywordWidth + 2f);
            float boxWidth = width - boxX - 22f;
            if (boxWidth < 60f) boxWidth = 60f;
            // 槽位宽度合计超过盒子时也只是画出去一点，不会负宽度——
            // SlotWidth 的下限是 min，所以这里不需要再夹。

            var box = new Rect(boxX, y, boxWidth, ClauseHeight);

            Widgets.DrawBoxSolid(box, BoxColor);

            bool isDetect = leaf != null && !isFilterClause;
            Widgets.DrawBoxSolid(new Rect(box.x, box.y, 3f, box.height),
                isDetect || isFilterClause ? DetectColor : OperateColor);

            RulePath subject = leaf != null ? leaf.subject : (op != null ? op.subject : null);
            string verbKey = leaf != null ? leaf.verbKey : (op != null ? op.verbKey : null);
            RuleOperand argument = leaf != null ? leaf.argument : (op != null ? op.argument : null);

            var verb = RuleVocabularyCatalog.Current.Verb(verbKey);

            float cursor = box.x + 8f;
            float limit = box.xMax - 22f;

            string subjectText = subject != null ? PathText(subject, currentRule) : null;
            float subjectWidth = SlotWidth(subjectText, "RuleCore.Edit.SlotEntity".Translate(),
                70f, limit - cursor);
            if (SlotButton(new Rect(cursor, box.y + 2f, subjectWidth, box.height - 4f),
                    subjectText, EntityColor, IsSelected(leaf, op, Slot.Subject),
                    "RuleCore.Edit.SlotEntity".Translate()))
            {
                Select(leaf, op, Slot.Subject, isFilterClause);
            }
            cursor += subjectWidth + 4f;

            string verbText = verb != null ? Label(verb.LabelKey, verb.key) : null;
            float verbWidth = SlotWidth(verbText, "RuleCore.Edit.SlotPredicate".Translate(),
                54f, limit - cursor);
            var verbColor = isDetect || isFilterClause ? DetectColor : OperateColor;

            if (SlotButton(new Rect(cursor, box.y + 2f, verbWidth, box.height - 4f),
                    verbText, verbColor, IsSelected(leaf, op, Slot.Predicate),
                    "RuleCore.Edit.SlotPredicate".Translate()))
            {
                Select(leaf, op, Slot.Predicate, isFilterClause);
            }
            cursor += verbWidth + 4f;

            bool needsObject = verb == null || verb.NeedsArgument;
            if (needsObject)
            {
                string objectText = argument != null ? OperandText(argument, currentRule) : null;
                float objectWidth = Mathf.Max(40f, limit - cursor);
                if (SlotButton(new Rect(cursor, box.y + 2f, objectWidth, box.height - 4f),
                        objectText, ObjectColor, IsSelected(leaf, op, Slot.Object),
                        "RuleCore.Edit.SlotObject".Translate()))
                {
                    Select(leaf, op, Slot.Object, isFilterClause);
                }
            }

            if (!readOnly)
            {
                var delRect = new Rect(box.xMax - 20f, box.y + 2f, 18f, box.height - 4f);
                if (DeleteButton(delRect))
                {
                    Delete(leaf, op, isFilterClause);
                }
            }

            return y + LineStep;
        }

        private void Delete(RuleExprNode leaf, RuleClause op, bool isFilterClause)
        {
            if (isFilterClause)
            {
                if (selection != null && selection.FilterPath != null
                    && selection.FilterStepIndex >= 0
                    && selection.FilterStepIndex < selection.FilterPath.StepCount)
                {
                    selection.FilterPath.steps.RemoveAt(selection.FilterStepIndex);
                }
                selection = null;
                StructureChanged = true;
                return;
            }

            if (op != null)
            {
                if (currentRule != null && currentRule.operate != null)
                {
                    currentRule.operate.Remove(op);
                    if (selection != null && selection.Clause == op) selection = null;
                    StructureChanged = true;
                }
                return;
            }

            if (leaf != null && currentRule != null)
            {
                if (currentRule.detect == leaf) currentRule.detect = null;
                else RemoveLeafFromTree(currentRule.detect, leaf);

                if (selection != null && selection.Leaf == leaf) selection = null;
                StructureChanged = true;
            }
        }

        private static bool RemoveLeafFromTree(RuleExprNode node, RuleExprNode target)
        {
            if (node == null || target == null) return false;

            for (int i = 0; i < node.ChildCount; i++)
            {
                if (node.children[i] == target)
                {
                    node.children.RemoveAt(i);
                    return true;
                }

                if (RemoveLeafFromTree(node.children[i], target)) return true;
            }

            return false;
        }

        private void DrawGeneratedText(Rect rect, Rule rule)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.047f, 0.055f, 0.075f));

            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;

            var prev = GUI.color;
            GUI.color = MutedColor;
            // Tiny 也要给够 16：汉字的下沿（"成""本"这类）比拉丁字母低，
            // 只按字母高度留位就会切掉一点。
            Widgets.Label(new Rect(rect.x + 8f, rect.y + 4f, rect.width - 16f, 16f),
                "RuleCore.Edit.Generated".Translate());
            GUI.color = new Color(0.66f, 0.85f, 0.73f);
            // 正文从标题下面 22 开始，别贴着——原来 20 会和加高后的标题叠半个字。
            Widgets.Label(new Rect(rect.x + 8f, rect.y + 22f,
                Mathf.Max(rect.width - 16f, 16f), Mathf.Max(rect.height - 26f, 16f)),
                RuleText(rule));
            GUI.color = prev;

            Text.Font = GameFont.Small;
            Text.WordWrap = false;
        }

        // ══ 右栏：检查器 ══════════════════════════════════════════════

        private void DrawInspector(Rect rect, Rule rule, bool readOnly)
        {
            Widgets.DrawBoxSolid(rect, new Color(0.09f, 0.10f, 0.13f));

            if (selection == null)
            {
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.UpperLeft;
                Text.WordWrap = false;
                GUI.color = MutedColor;
                Widgets.Label(new Rect(rect.x + 10f, rect.y + 16f, rect.width - 20f, 60f),
                    "RuleCore.Edit.InspectorHint".Translate());
                GUI.color = Color.white;
                return;
            }

            float width = Mathf.Max(rect.width - ScrollBarAllowance, 1f);
            var viewRect = new Rect(0f, 0f, width, Mathf.Max(inspectorHeight, rect.height));

            Widgets.BeginScrollView(rect.ContractedBy(6f), ref inspectorScroll, viewRect);

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = false;

            float y = 0f;

            // 面包屑：22 而不是 18。Small 字体的行高约 22，给 18 会切掉汉字下沿
            // （玩家截图里「检测子句 · 宾语」下半截就是没的）。
            GUI.color = MutedColor;
            Widgets.LabelEllipses(new Rect(0f, y, width, 22f), CrumbText());
            GUI.color = Color.white;
            y += 28f;

            if (selection.Binding)
            {
                y = DrawBindingInspector(y, width, readOnly);
            }
            else if (selection.Slot == Slot.Subject)
            {
                y = DrawSubjectInspector(y, width, readOnly);
            }
            else if (selection.Slot == Slot.Predicate)
            {
                y = DrawPredicateInspector(y, width, readOnly);
            }
            else
            {
                y = DrawObjectInspector(y, width, readOnly);
            }

            y = DrawIssues(y, width, rule);

            inspectorHeight = y + 8f;

            Widgets.EndScrollView();

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        /// <summary>
        /// 身份栏：名称 / 冷却 / 主体绑定 / 启用。
        ///
        /// **主体绑定是这里最要紧的一项**：不选它，规则里每一句 `本主体 …`
        /// 都会在求值时读不到主体而不成立。没有这个入口的话，
        /// 玩家能写出语法完全正确、却永远不会触发的规则，而且看不出为什么。
        /// </summary>
        private void DrawHeader(Rect rect, Rule rule, bool readOnly)
        {
            Widgets.DrawBoxSolid(rect, BoxColor);

            float x = rect.x + 6f;
            float h = rect.height - 6f;
            float y = rect.y + 3f;

            // 名称
            var nameRect = new Rect(x, y, 150f, h);
            if (readOnly)
            {
                Widgets.LabelEllipses(nameRect, rule.DisplayLabel);
            }
            else
            {
                string current = rule.label ?? string.Empty;
                string edited = Widgets.TextField(nameRect, current);
                if (edited != current)
                {
                    rule.label = edited;
                    ValueChanged = true;
                }
            }
            x += 156f;

            // 冷却
            var cooldownLabel = new Rect(x, y, 34f, h);
            var previous = GUI.color;
            GUI.color = MutedColor;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.LabelEllipses(cooldownLabel, "RuleCore.Edit.Field.Cooldown".Translate());
            GUI.color = previous;
            x += 36f;

            var cooldownRect = new Rect(x, y, 56f, h);
            if (readOnly)
            {
                Widgets.LabelEllipses(cooldownRect, rule.cooldownTicks.ToString());
            }
            else
            {
                int value = rule.cooldownTicks;
                string buffer;
                buffers.TryGetValue("cooldown", out buffer);
                Widgets.TextFieldNumeric(cooldownRect, ref value, ref buffer, 0f, 60000f);
                buffers["cooldown"] = buffer;

                if (value != rule.cooldownTicks)
                {
                    rule.cooldownTicks = value;
                    ValueChanged = true;
                }
            }
            x += 58f;

            GUI.color = MutedColor;
            Widgets.LabelEllipses(new Rect(x, y, 14f, h), "t");
            GUI.color = previous;
            x += 18f;

            // 主体绑定。**指名绑定时显示那个人的名字**，而不是"本主体"这个占位符——
            // "这条规则说的是谁"是这个界面最该一眼看出来的事。
            var info = RuleVocabularyCatalog.Current.Subject(rule.subjectKey);

            string bindingText = SubjectDisplay(rule);

            // 群体绑定顺手报出"收出了几个主体"——**0 也要报**，
            // 因为"绑定成囚犯而殖民地没有囚犯"正是最该被看见的状态：
            // 规则会安静地什么都不做，而玩家只会以为自己条件写错了。
            if (info != null && info.scope != RuleSubjectScope.Single)
            {
                SubjectCapabilities();
                bindingText = bindingText + " (" + sampleCount + ")";
            }

            string bindingTag = info != null
                ? TypeTagOf(RuleValueKind.Entity, info.entityKind)
                : null;

            var chipRect = new Rect(x, y, 150f, h);
            if (SlotButton(chipRect, bindingText, EntityColor,
                    selection != null && selection.Binding, "RuleCore.Edit.SlotSubject".Translate()))
            {
                selection = new Selection { Binding = true, Slot = Slot.Subject };
            }
            x += 156f;

            if (!string.IsNullOrEmpty(bindingTag))
            {
                GUI.color = MutedColor;
                Widgets.LabelEllipses(new Rect(x, y, 60f, h), "(" + bindingTag + ")");
                GUI.color = previous;
                x += 64f;
            }

            // 启用 —— **两种规则都给这个开关。**
            //
            // 内置规则不可编辑，但"我不想让它跑"是**玩家的偏好**，不是编辑它的定义。
            // 原来这里只对玩家规则显示，内置规则连关都关不掉——那个不对称是错的。
            {
                // **标签用短的，并给一个够宽的框。**
                // 原来写的是「启用这条规则」+ 100px 宽：六个汉字约 78px，加上复选框
                // 24px 就超过 100 了，于是折成两行而那一行只有 24px 高——
                // 第二行被切掉（玩家截图里「启用这条规 / 则」就是这么来的）。
                // 完整解释放进悬停提示，那里没有宽度限制。
                var enabledRect = new Rect(Mathf.Max(x, rect.xMax - 104f), y, 100f, h);
                bool enabled = rule.enabled;
                Widgets.CheckboxLabeled(enabledRect, "RuleCore.Panel.Enable".Translate(), ref enabled);
                TooltipHandler.TipRegion(enabledRect, "RuleCore.Edit.Field.Enabled".Translate());

                if (enabled != rule.enabled)
                {
                    // 走库的入口而不是直接改字段：内置规则的开关要写进覆盖层，
                    // 否则下次读档会被 XML 里的 enabled 顶回来。
                    RuleLibrary.SetDisabled(rule.id, !enabled);
                }
            }

            Text.Anchor = TextAnchor.UpperLeft;
        }

        /// <summary>
        /// 主体绑定的选项。
        ///
        /// **分两段，因为这是两件不同的事**（也是这一版最要紧的一处纠正）：
        ///   · 点名一个人 —— 整条规则只对他跑一遍，句子里直接写他的名字；
        ///   · 让一类人各做一遍 —— 对每个成员各跑一遍，冷却按人分开算。
        ///
        /// 而**身份不在这里选第二遍**：绑成"囚犯"，那一轮里每个人的身份就都是囚犯。
        /// 所以规则语言里根本没有「是囚犯」这种问句——**锁定了就不需要判断**。
        /// 玩家想知道"现在这个主体到底是什么"，这里直接写给他看。
        /// </summary>
        private float DrawBindingInspector(float y, float width, bool readOnly)
        {
            var vocabulary = RuleVocabularyCatalog.Current;
            var subjects = new List<RuleSubjectInfo>();
            vocabulary.CollectSubjects(subjects);

            SubjectCapabilities();   // 顺手刷新样本与各群体的人数

            y = Section(y, width, "RuleCore.Edit.Section.Subject".Translate());

            if (readOnly)
            {
                return Hint(y, width, SubjectDisplay(currentRule));
            }

            if (Option(y, width, "RuleCore.Edit.Subject.None".Translate(),
                    "RuleCore.Edit.Subject.NoneTag".Translate(), MutedColor,
                    currentRule != null && string.IsNullOrEmpty(currentRule.subjectKey)))
            {
                SetSubjectKey(null);
            }
            y += OptionHeight + OptionGap;

            // ── 第一段：点名一个人 ────────────────────────────────────
            y = Section(y, width, "RuleCore.Edit.Section.SubjectNamed".Translate());

            var named = vocabulary.Subject(currentRule != null ? currentRule.subjectKey : null);

            for (int i = 0; i < subjects.Count; i++)
            {
                var info = subjects[i];
                if (info.scope != RuleSubjectScope.Single) continue;

                bool active = currentRule != null && currentRule.subjectKey == info.key;

                // 点这一行 = 切到指名绑定**并立刻问是哪一位**。
                // 分两步（先选"指名"再选人）是多余的：这个绑定存在的意义就是那个人。
                if (Option(y, width, Label(info.LabelKey, info.key),
                        "RuleCore.Edit.ScopeSingle".Translate(), ObjectColor, active))
                {
                    SetSubjectKey(info.key);
                    OpenPawnPicker(info);
                    return y;   // 选择器要开了，别在同一帧里接着画底下那些
                }
                y += OptionHeight + OptionGap;
            }

            if (named != null && named.scope == RuleSubjectScope.Single)
            {
                var pawn = RulePawnFacts.FindById(currentRule.subjectRef);

                if (Option(y, width, SubjectDisplay(currentRule), null,
                        pawn != null ? EntityColor : DangerColor))
                {
                    OpenPawnPicker(named);
                    return y;
                }
                y += OptionHeight + OptionGap;

                if (pawn != null)
                {
                    // 选中的人**此刻是什么**，直接摊在脸上——
                    // 它就是上面"哪些属性会出现"的全部依据，也是"你不用在规则里判断它"的理由。
                    y = Hint(y, width, "RuleCore.Edit.Subject.IsNow".Translate(
                        pawn.LabelShort, CategoryLabel(pawn)));
                }
                else
                {
                    // 跨存档时这条必然发生（playerRules 住在全局配置里，ThingID 是每档各自发的）。
                    // 不解释的话，玩家只会看到"规则明明写对了却什么都不做"。
                    y = Hint(y, width, "RuleCore.Edit.Subject.MissingHint".Translate(
                        currentRule.subjectName ?? currentRule.subjectRef ?? "?"));
                }
            }
            else
            {
                y = Hint(y, width, "RuleCore.Edit.Subject.NamedHint".Translate());
            }

            // ── 第二段：让一类人各做一遍 ──────────────────────────────
            y = Section(y, width, "RuleCore.Edit.Section.SubjectGroup".Translate());

            int activeGroupCount = -1;

            for (int i = 0; i < subjects.Count; i++)
            {
                var info = subjects[i];
                if (info.scope == RuleSubjectScope.Single) continue;

                bool active = currentRule != null && currentRule.subjectKey == info.key;

                int count;
                bool known = subjectCounts.TryGetValue(info.key ?? string.Empty, out count);

                if (active) activeGroupCount = known ? count : -1;

                // 人数直接写在选项上：**"0 个"本身就是信息**。
                // 绑定成"囚犯"而殖民地一个囚犯都没有时规则安静地什么都不做，
                // 而玩家会以为是自己写错了条件。
                //
                // **拿不到人数就不写**。写"0"是把"不知道"说成"没有"——
                // 而"自由殖民者 0 个"在玩家就坐在一个殖民地里的时候是一句假话。
                // 标签也只放"0 个"这么短：它会被 ellipses 截断，
                // 而"0 个——规则不会…"被截一半比没有还难看（下面用一整句说清楚）。
                string tag = !known
                    ? null
                    : (count > 0
                        ? "RuleCore.Edit.Subject.MemberCountShort".Translate(count)
                        : "RuleCore.Edit.Subject.NoMemberShort".Translate());

                if (Option(y, width, Label(info.LabelKey, info.key), tag, EntityColor, active))
                {
                    SetSubjectKey(info.key);
                }
                y += OptionHeight + OptionGap;
            }

            if (activeGroupCount == 0)
            {
                // 最要紧的一条：规则会安静地什么都不做。放在这里而不是塞进标签里，
                // 因为标签只有一行、会被截断，而这句话必须完整。
                y = Hint(y, width, "RuleCore.Edit.Subject.NoMember".Translate());
            }
            else if (named != null && named.scope != RuleSubjectScope.Single)
            {
                y = Hint(y, width, "RuleCore.Edit.Subject.GroupIdentityHint".Translate());
            }

            y = Hint(y + 4f, width, "RuleCore.Edit.Subject.Hint".Translate());

            // **给一个"怎么离开这个面板"的出口说明。**
            //
            // 这个面板只能靠"点句子里的槽位"离开，而它看上去是一个独立的页面——
            // 玩家会以为要在这里找到"确定"或者"返回"。说一句，成本一行。
            return Hint(y, width, "RuleCore.Edit.BackToSentence".Translate());
        }

        /// <summary>身份的显示名。界面上每一处出现名字的地方都该带上它。</summary>
        private static string CategoryLabel(Pawn pawn)
        {
            string key = RulePawnFacts.CategoryKeyOf(pawn);
            return Label(RulePawnFacts.CategoryLabelKey(key), key);
        }

        /// <summary>
        /// 换绑定方式。**顺手清掉指名引用**：从"指名某人"切到"全部囚犯"却留着
        /// <c>subjectRef</c>，界面上就会出现一个不生效的名字。
        /// （<see cref="Rule.Resolve"/> 里还有一道同样的清理，那是给手改 XML 兜底的。）
        /// </summary>
        private void SetSubjectKey(string key)
        {
            if (currentRule == null) return;

            currentRule.subjectKey = key;

            var info = RuleVocabularyCatalog.Current.Subject(key);
            if (info == null || info.scope != RuleSubjectScope.Single)
            {
                currentRule.subjectRef = null;
                currentRule.subjectName = null;
            }

            sampleKey = null;   // 缓存失效，下一帧重新看主体是谁
            StructureChanged = true;
        }

        private void OpenPawnPicker(RuleSubjectInfo info)
        {
            if (info == null || info.candidates == null || currentRule == null) return;

            var map = Find.CurrentMap;
            if (map == null) return;

            var context = new RuleEvalContext
            {
                Rule = currentRule,
                Map = map,
                Tick = Find.TickManager != null ? Find.TickManager.TicksGame : 0
            };

            var list = new List<RuleValue>();
            info.candidates(new RuleEvalHost(context), list);

            var pawns = new List<Pawn>();
            for (int i = 0; i < list.Count; i++)
            {
                var pawn = RuleEvalHost.PawnOf(list[i]);
                if (pawn != null) pawns.Add(pawn);
            }

            if (pawns.Count == 0)
            {
                Messages.Message("RuleCore.Edit.Subject.NoCandidate".Translate(),
                    MessageTypeDefOf.NeutralEvent, false);
                return;
            }

            Find.WindowStack.Add(new Dialog_PawnPicker(pawns, currentRule.subjectRef,
                delegate(Pawn picked)
                {
                    if (picked == null) return;
                    currentRule.subjectRef = picked.ThingID;
                    currentRule.subjectName = picked.LabelShort;
                    sampleKey = null;
                    StructureChanged = true;
                }));
        }

        /// <summary>
        /// 「本主体」在句子与界面上显示成什么。
        ///
        /// **指名绑定显示那个人的名字** —— 这正是"本主体改为具体的某个 pawn"的落地：
        /// 句子里写的不是"本主体.饱食度"，而是"小明.饱食度"。
        /// 名字永远是从**活人**上现取的（他改了名就该显示新名），
        /// 缓存只在人找不到时兜底，并明确写成"（找不到：小明）"而不是假装还在。
        /// </summary>
        public static string SubjectDisplay(Rule rule)
        {
            if (rule == null || string.IsNullOrEmpty(rule.subjectKey))
            {
                return "RuleCore.Edit.Root.Subject".Translate();
            }

            var info = RuleVocabularyCatalog.Current.Subject(rule.subjectKey);
            if (info == null) return rule.subjectKey;

            if (info.scope == RuleSubjectScope.Single)
            {
                var pawn = RulePawnFacts.FindById(rule.subjectRef);
                if (pawn != null) return pawn.LabelShort;

                if (!string.IsNullOrEmpty(rule.subjectName))
                {
                    return "RuleCore.Edit.Subject.Lost".Translate(rule.subjectName);
                }

                return "RuleCore.Edit.Subject.Unpicked".Translate();
            }

            return Label(info.LabelKey, info.key);
        }

        private string CrumbText()
        {
            if (selection == null) return string.Empty;

            string kind;
            if (selection.Binding) return "RuleCore.Edit.CrumbBinding".Translate();
            if (selection.InsideFilter) kind = "RuleCore.Edit.CrumbFilter".Translate();
            else if (selection.Clause != null) kind = "RuleCore.Edit.CrumbOperate".Translate();
            else kind = "RuleCore.Edit.CrumbDetect".Translate();

            string slot;
            if (selection.Slot == Slot.Subject) slot = "RuleCore.Edit.CrumbSubject".Translate();
            else if (selection.Slot == Slot.Predicate) slot = "RuleCore.Edit.CrumbPredicate".Translate();
            else slot = "RuleCore.Edit.CrumbObject".Translate();

            return kind + " · " + slot;
        }

        private RulePath SelectedPath()
        {
            if (selection == null) return null;

            if (selection.InsideFilter && selection.FilterPath != null
                && selection.FilterStepIndex >= 0
                && selection.FilterStepIndex < selection.FilterPath.StepCount)
            {
                var filter = selection.FilterPath.steps[selection.FilterStepIndex].filter;
                if (filter == null) return null;
                return filter.subject;
            }

            if (selection.Leaf != null) return selection.Leaf.subject;
            if (selection.Clause != null) return selection.Clause.subject;
            return null;
        }

        private float DrawSubjectInspector(float y, float width, bool readOnly)
        {
            var path = SelectedPath();

            // **根实体列表总是给出来**，而不是"还没有根时"才给。
            //
            // 曾经只在 path == null 时显示，结果是：新建的叶子预填了根（本主体）→
            // 列表再也不出现 → 玩家永远换不了主语，也就永远读不出任何属性，
            // 检查器只会说"这条路已经走到头了"。而且**已经存下来的规则里全是预填过根的**，
            // 所以这条必须对"已有根"也成立，光改新建逻辑救不了老规则。
            if (!readOnly)
            {
                // 没绑主体时「本主体」是一条死路（读不出任何属性）。
                // 光写一句提示不够——这里给一个**点得动**的入口，直接跳到绑定选择。
                if (!selection.InsideFilter && SubjectKind() == RuleEntityKind.Any)
                {
                    if (Option(y, width, "RuleCore.Edit.BindFirst".Translate(), null, DangerColor))
                    {
                        selection = new Selection { Binding = true, Slot = Slot.Subject };
                        return y;
                    }
                    y += OptionHeight + OptionGap;
                }

                y = Section(y, width, "RuleCore.Edit.Section.Root".Translate());

                rootOptions.Clear();
                CollectRootOptions(rootOptions);

                for (int i = 0; i < rootOptions.Count; i++)
                {
                    var option = rootOptions[i];   // struct：复制一份，闭包里不能用迭代变量
                    bool active = path != null
                        && path.rootKind == option.kind
                        && (option.kind != RuleRootKind.PawnGroup
                            || (!path.RootLiteral.IsMissing
                                && path.RootLiteral.AsKey == option.groupKey));

                    if (Option(y, width, option.label,
                            TypeTagOf(option.valueKind, option.entityKind), EntityColor, active))
                    {
                        // 换根 = 换一整条路径：旧步骤挂在新根上多半不成立，
                        // 留着它们只会得到一串红色的「不能挂在这个类型上」。
                        CreatePath(option);
                    }
                    y += OptionHeight + OptionGap;
                }
            }

            if (path == null)
            {
                return Hint(y + 4f, width, "RuleCore.Edit.PickRootHint".Translate());
            }

            y = Section(y, width, "RuleCore.Edit.Section.Path".Translate());
            y = PathChain(y, width, path, readOnly);

            RuleValueKind kind;
            RuleEntityKind entityKind;
            RulePropertyInfo last;
            RulePathTypes.AdvanceType(path, SubjectKind(), selection.InsideFilter,
                ElementKind(), path.StepCount, out kind, out entityKind, out last);

            y = TypeBadge(y, width, kind, entityKind);

            // 主语的根是「本主体」，而规则又没绑定主体 —— 这时**任何属性都读不出来**
            // （实体类型是「任意」，词表按类型过滤后一个属性都不给）。
            // 底下那句"这条路已经走到头了"在这里是**误导**：不是路到头了，是缺前提。
            if (!readOnly && kind == RuleValueKind.Entity
                && path.rootKind == RuleRootKind.Subject
                && SubjectKind() == RuleEntityKind.Any)
            {
                return Hint(y, width, "RuleCore.Edit.NeedBinding".Translate());
            }

            if (readOnly) return y;

            var properties = new List<RulePropertyInfo>();
            var rejected = new List<RuleRejection>();
            RuleVocabularyCatalog.Current.CollectPropertiesFor(entityKind,
                PathCapabilities(path), properties, rejected);

            if (kind == RuleValueKind.Entity && properties.Count > 0)
            {
                y = Section(y, width, "RuleCore.Edit.Section.AddProperty".Translate());

                for (int i = 0; i < properties.Count; i++)
                {
                    var info = properties[i];
                    if (Option(y, width, "." + Label(info.LabelKey, info.key), TypeTagOf(info), EntityColor))
                    {
                        path.steps.Add(new RulePathStep
                        {
                            kind = RuleStepKind.Property,
                            propertyKey = info.key
                        });
                        AfterPathEdit(path);
                    }
                    y += OptionHeight + OptionGap;
                }
            }

            // **被挡掉的那些也要说出来。**
            //
            // 玩家看着一个只有三行的下拉，看不出是"这个词表就没有饱食度"
            // 还是"我选的主体不对"。把缺的那一位能力摆出来，他才知道下一步改什么——
            // 这比多给三个选了必然报错的选项有用得多。
            if (kind == RuleValueKind.Entity && rejected.Count > 0)
            {
                y = Section(y, width, "RuleCore.Edit.Section.NotForThis".Translate());

                for (int i = 0; i < rejected.Count; i++)
                {
                    var item = rejected[i];
                    y = Hint(y, width, Label(item.labelKey, item.key) + "  —— "
                        + "RuleCore.Edit.MissingCapability".Translate(
                            CapabilityName(item.missing)));
                }
            }

            if (kind == RuleValueKind.EntitySet)
            {
                y = Section(y, width, "RuleCore.Edit.Section.Filter".Translate());

                if (Option(y, width, "+ " + "RuleCore.Edit.AddFilter".Translate(), null, ReduceColor))
                {
                    path.steps.Add(new RulePathStep
                    {
                        kind = RuleStepKind.Filter,
                        filter = NewFilterLeaf()
                    });
                    AfterPathEdit(path);
                }
                y += OptionHeight + OptionGap;

                y = Section(y, width, "RuleCore.Edit.Section.Reduce".Translate());

                var reduces = RuleVocabulary.AllReduces;
                for (int i = 0; i < reduces.Count; i++)
                {
                    var info = reduces[i];
                    if (info.from != kind) continue;

                    if (Option(y, width, "." + Label(info.LabelKey, info.kind.ToString()),
                            "→ " + TypeTagOf(info.to), ReduceColor))
                    {
                        path.steps.Add(new RulePathStep
                        {
                            kind = RuleStepKind.Reduce,
                            reduce = info.kind
                        });
                        AfterPathEdit(path);
                    }
                    y += OptionHeight + OptionGap;
                }

                y = QuantifyEntries(y, width, path, readOnly);

                // ── 这里是最容易走错的一步，所以**当场说清它和主体绑定的区别** ──
                //
                // 玩家的直觉是：「全部机械族.电量 小于 50%」= 每个机械族的电量都小于 50%。
                // 但那个写法在一组东西上**读不出属性**（上面根本没有属性列表），
                // 而且字面意思只能是"这一组东西的电量小于 50%"。想要"每个各判一次"，
                // 那是**主体绑定**的事——而那个入口的名字不叫这个，他找不到。
                if (path.rootKind == RuleRootKind.PawnGroup)
                {
                    var group = RuleVocabularyCatalog.Current.Subject(
                        path.RootLiteral.IsMissing ? null : path.RootLiteral.AsKey);

                    if (group != null)
                    {
                        y = Hint(y, width, "RuleCore.Edit.GroupRootHint".Translate(
                            Label(group.LabelKey, group.key)));
                    }
                }
            }
            else if (kind == RuleValueKind.None)
            {
                y = Hint(y, width, "RuleCore.Edit.BrokenPath".Translate());
            }
            else if (properties.Count == 0)
            {
                y = Hint(y, width, "RuleCore.Edit.PathEnd".Translate());
            }

            return y;
        }

        /// <summary>根列表里的一项。<see cref="RuleRootKind.PawnGroup"/> 的"哪一群"放在 groupKey 里。</summary>
        private struct RootOption
        {
            public RuleRootKind kind;
            public string groupKey;
            public string label;
            public RuleValueKind valueKind;
            public RuleEntityKind entityKind;
        }

        private readonly List<RootOption> rootOptions = new List<RootOption>();

        /// <summary>
        /// 当前可选的根。**"一群"那部分是从绑定表里现场生成的，不是写死的几行。**
        ///
        /// 于是"加一种主体绑定"（比如别的 mod 加"所有机械体"）会自动在根列表里
        /// 多出一项，两处不会漂——这是把 <see cref="RuleRootKind.PawnGroup"/>
        /// 做成"一个值 + 一个键"而不是"每种群体一个枚举值"换来的。
        /// </summary>
        private void CollectRootOptions(List<RootOption> into)
        {
            into.Clear();

            // 「当前元素」只在筛选器里有意义。
            if (selection != null && selection.InsideFilter)
            {
                into.Add(new RootOption
                {
                    kind = RuleRootKind.Element,
                    label = "RuleCore.Edit.Root.Element".Translate(),
                    valueKind = RuleValueKind.Entity,
                    entityKind = ElementKind()
                });
            }

            into.Add(new RootOption
            {
                kind = RuleRootKind.Subject,
                label = "RuleCore.Edit.Root.Subject".Translate(),
                valueKind = RuleValueKind.Entity,
                entityKind = SubjectKind()
            });

            into.Add(new RootOption
            {
                kind = RuleRootKind.Map,
                label = "RuleCore.Edit.Root.Map".Translate(),
                valueKind = RuleValueKind.Entity,
                entityKind = RuleEntityKind.Map
            });

            // 一群：每一种"群体绑定"各一项，名字就是「全部」+ 那个绑定的名字。
            var subjects = new List<RuleSubjectInfo>();
            RuleVocabularyCatalog.Current.CollectSubjects(subjects);

            for (int i = 0; i < subjects.Count; i++)
            {
                var info = subjects[i];
                // 指名绑定不是"一群"，它没有对应的集合根。
                if (info.scope != RuleSubjectScope.Group) continue;

                into.Add(new RootOption
                {
                    kind = RuleRootKind.PawnGroup,
                    groupKey = info.key,
                    label = "RuleCore.Edit.Root.AllOf".Translate(Label(info.LabelKey, info.key)),
                    valueKind = RuleValueKind.EntitySet,
                    entityKind = info.entityKind
                });
            }

            into.Add(new RootOption
            {
                kind = RuleRootKind.AllMaps,
                label = "RuleCore.Edit.Root.AllMaps".Translate(),
                valueKind = RuleValueKind.EntitySet,
                entityKind = RuleEntityKind.Map
            });
        }

        /// <summary>点根列表里的一项：换一条全新的路径。</summary>
        private void CreatePath(RootOption option)
        {
            var path = new RulePath { rootKind = option.kind };

            if (option.kind == RuleRootKind.PawnGroup)
            {
                // 是哪一群存在根的字面量里（Enum 值，键 = 绑定表的 key）。
                // 复用现成的序列化字段，不必给 RulePath 加新成员。
                path.rootLiteral = RuleLiteral.From(RuleValue.OfKey(option.groupKey));
            }

            if (selection.InsideFilter && selection.FilterPath != null
                && selection.FilterStepIndex >= 0
                && selection.FilterStepIndex < selection.FilterPath.StepCount)
            {
                var filter = selection.FilterPath.steps[selection.FilterStepIndex].filter;
                filter.subject = path;
            }
            else if (selection.Leaf != null)
            {
                selection.Leaf.subject = path;
            }
            else if (selection.Clause != null)
            {
                selection.Clause.subject = path;
            }

            AfterPathEdit(path);
        }

        /// <summary>
        /// 一条路径的根在界面上的名字。
        ///
        /// 静态版（给 <see cref="RuleText"/> 用）和实例版最终都走
        /// <see cref="RootText"/>，所以规则列表和编辑器不会显示成两个样子。
        /// </summary>
        public static string RootText(RulePath path, Rule rule)
        {
            if (path == null) return "?";

            if (path.rootKind == RuleRootKind.PawnGroup)
            {
                string key = path.RootLiteral.IsMissing ? null : path.RootLiteral.AsKey;
                var info = RuleVocabularyCatalog.Current.Subject(key);

                string name = info != null
                    ? Label(info.LabelKey, info.key)
                    : "⚠" + (key ?? "?");

                return "RuleCore.Edit.Root.AllOf".Translate(name);
            }

            return RootName(path.rootKind, rule);
        }

        /// <summary>
        /// 主语路径被改过之后的收尾。**凡改动主语路径的地方都必须走这里。**
        ///
        /// 它做两件事：
        ///   1. 记结构改动（立刻归一化 + 落盘）；
        ///   2. 如果改的正是当前选中子句的主语，**复查谓语还成不成立**。
        ///
        /// 第 2 件是修一个真实的坑：玩家先写好 `本主体.血量 大于 10`，
        /// 再点路径上的 `本主体` 把它截断——主语变回一个小人，
        /// 而 `大于` 还挂在那里。子句表面上完整，实际永远不会成立，
        /// 唯一的提示是底下"校验"里多一条红字，离他刚做的那步很远。
        /// 复查之后谓语槽会退回灰色的"选择谓语…"，一眼就知道要重选。
        /// </summary>
        private void AfterPathEdit(RulePath path)
        {
            StructureChanged = true;

            if (path == null || path != SelectedPath()) return;
            if (selection == null || currentRule == null) return;

            var currentVerb = RuleVocabularyCatalog.Current.Verb(CurrentVerbKey());
            if (currentVerb == null) return;

            RuleValueKind kind;
            RuleEntityKind entityKind;
            RulePropertyInfo last;
            RulePathTypes.AdvanceType(path, SubjectKind(), selection.InsideFilter,
                ElementKind(), path.StepCount, out kind, out entityKind, out last);

            var category = selection.Clause != null && !selection.InsideFilter
                ? RuleVerbCategory.Operate
                : RuleVerbCategory.Detect;

            // 别再自己写一遍过滤判据 —— 问词表，和检查器列出来的是同一套规则。
            var allowed = new List<RuleVerbInfo>();
            RuleVocabularyCatalog.Current.CollectVerbsFor(entityKind, kind, category,
                PathCapabilities(path), allowed, null);

            for (int i = 0; i < allowed.Count; i++)
            {
                if (allowed[i].key == currentVerb.key) return;
            }

            if (selection.Leaf != null)
            {
                selection.Leaf.verbKey = null;
                selection.Leaf.argument = null;
            }
            else if (selection.Clause != null)
            {
                selection.Clause.verbKey = null;
                selection.Clause.argument = null;
            }
        }

        private static readonly RuleRootKind[] RootOrder =
        {
            RuleRootKind.Subject, RuleRootKind.Element, RuleRootKind.Map,
            RuleRootKind.Colonists, RuleRootKind.AllMaps
        };

        private float PathChain(float y, float width, RulePath path, bool readOnly)
        {
            float cursor = 0f;
            float row = y;

            if (ChainSegment(ref cursor, ref row, width, RootText(path, currentRule),
                    EntityColor, readOnly))
            {
                path.steps.Clear();
                AfterPathEdit(path);
            }

            for (int i = 0; i < path.StepCount; i++)
            {
                var step = path.steps[i];
                string text;
                Color color;

                if (step.kind == RuleStepKind.Property)
                {
                    var info = RuleVocabularyCatalog.Current.Property(step.propertyKey);
                    text = "." + (info != null ? Label(info.LabelKey, info.key) : "⚠" + step.propertyKey);
                    color = new Color(0.62f, 0.80f, 1f);
                }
                else if (step.kind == RuleStepKind.Filter)
                {
                    text = FilterSummary(step.filter, currentRule);
                    color = new Color(1f, 0.83f, 0.47f);
                }
                else if (step.kind == RuleStepKind.Quantify)
                {
                    text = Label(RuleQuantifiers.LabelKey(step.quantifier),
                        step.quantifier.ToString()) + FilterSummary(step.filter, currentRule);
                    color = new Color(0.86f, 0.72f, 1f);
                }
                else
                {
                    var info = RuleVocabulary.ReduceInfo(step.reduce);
                    text = "." + (info != null ? Label(info.LabelKey, step.reduce.ToString())
                        : step.reduce.ToString());
                    color = ReduceColor;
                }

                int capture = i;
                if (ChainSegment(ref cursor, ref row, width, text, color, readOnly))
                {
                    if (step.kind == RuleStepKind.Filter || step.kind == RuleStepKind.Quantify)
                    {
                        // 点筛选段/量词段 = 选中它内部的条件（而不是截断路径）
                        Select(step.filter, null, Slot.Predicate, true);
                        selection.FilterPath = path;
                        selection.FilterStepIndex = capture;
                    }
                    else
                    {
                        // 点其他段 = 截断到它之前（原型的行为：删掉这一步及其后）
                        path.steps.RemoveRange(capture, path.StepCount - capture);
                        AfterPathEdit(path);
                    }
                }
            }

            return row + OptionHeight + 6f;
        }

        private bool ChainSegment(ref float cursor, ref float row, float width, string text,
            Color color, bool readOnly)
        {
            float w = FlowTextWidth(text) + 14f;
            float maxW = Mathf.Max(width - 4f, 16f);
            if (w > maxW) w = maxW;

            if (cursor + w > width)
            {
                cursor = 0f;
                row += OptionHeight + 2f;
            }

            var rect = new Rect(cursor, row, w, OptionHeight);

            if (Mouse.IsOver(rect)) Widgets.DrawBoxSolid(rect, HoverBg);

            var prev = GUI.color;
            GUI.color = color;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.LabelEllipses(rect, text);
            GUI.color = prev;
            Text.Anchor = TextAnchor.MiddleLeft;

            cursor += w + 2f;
            return Widgets.ButtonInvisible(rect) && !readOnly;
        }

        /// <summary>
        /// 「这一组怎么样」——量词入口。
        ///
        /// 它和归约挨着放，是因为玩家此刻站的位置就是"我手上是一组东西，现在能干嘛"。
        /// 而两者的区别**必须在界面上说清**：
        /// 归约是**选出一个单值**（一个 / 最近的一个 / 几个），
        /// 量词是**给出一句话**（这一组全都满足 / 有一个满足 / 一个都不满足）。
        ///
        /// 点一下就顺手把新条件的编辑器打开——和「选完谓语自动跳到宾语槽」同一个道理：
        /// 界面不该让人去找那个刚生成的 <c>[条件]</c>。
        /// </summary>
        private float QuantifyEntries(float y, float width, RulePath path, bool readOnly)
        {
            if (path == null) return y;

            y = Section(y, width, "RuleCore.Edit.Section.Quantify".Translate());

            var all = RuleQuantifiers.All;
            for (int i = 0; i < all.Length; i++)
            {
                var quantifier = all[i];

                if (Option(y, width, Label(RuleQuantifiers.LabelKey(quantifier), quantifier.ToString()),
                        "→ " + TypeTagOf(RuleValueKind.Bool), ReduceColor) && !readOnly)
                {
                    var step = new RulePathStep
                    {
                        kind = RuleStepKind.Quantify,
                        quantifier = quantifier,
                        filter = NewFilterLeaf()
                    };
                    path.steps.Add(step);

                    // 先让路径复查一次谓语（集合 → 布尔，原来那个谓语多半已经不成立了），
                    // **再**把编辑焦点移进新条件。顺序反了复查就会被跳过。
                    AfterPathEdit(path);

                    Select(step.filter, null, Slot.Predicate, true);
                    selection.FilterPath = path;
                    selection.FilterStepIndex = path.StepCount - 1;
                }

                y += OptionHeight + OptionGap;
            }

            return Hint(y, width, "RuleCore.Edit.QuantifyHint".Translate());
        }

        private float DrawPredicateInspector(float y, float width, bool readOnly)
        {
            var subject = SelectedPath();

            RuleValueKind kind;
            RuleEntityKind entityKind;
            RulePropertyInfo last;
            RulePathTypes.AdvanceType(subject, SubjectKind(), selection.InsideFilter,
                ElementKind(), subject != null ? subject.StepCount : 0,
                out kind, out entityKind, out last);

            var category = selection.Clause != null && !selection.InsideFilter
                ? RuleVerbCategory.Operate
                : RuleVerbCategory.Detect;

            var list = new List<RuleVerbInfo>();
            var rejectedVerbs = new List<RuleRejection>();
            RuleVocabularyCatalog.Current.CollectVerbsFor(entityKind, kind, category,
                PathCapabilities(subject), list, rejectedVerbs);

            y = Section(y, width, "RuleCore.Edit.Section.Predicate".Translate(
                TypeTagOf(kind, entityKind)));

            // 一组东西时，这里必然是空的——说清**为什么**，并且**把下一步做成点得动的**。
            if (kind == RuleValueKind.EntitySet)
            {
                y = Hint(y, width, "RuleCore.Edit.NeedsReduceFirst".Translate());

                // 原来这里只有一句"先接一个归约"。而玩家此刻正站在**谓语槽**上，
                // 要照做就得回上一层、找到主语、再翻到归约那一节。
                // **光告诉他"先做 X"却不给他 X，等于没说。**
                // 归约本来就是主语路径上的一步，就地接上去是完全一样的东西。
                if (subject == null) return y;

                y = Section(y, width, "RuleCore.Edit.Section.Reduce".Translate());

                var reduces = RuleVocabulary.AllReduces;
                for (int i = 0; i < reduces.Count; i++)
                {
                    var reduce = reduces[i];
                    if (reduce.from != kind) continue;

                    if (Option(y, width, "." + Label(reduce.LabelKey, reduce.kind.ToString()),
                            "→ " + TypeTagOf(reduce.to), ReduceColor) && !readOnly)
                    {
                        subject.steps.Add(new RulePathStep
                        {
                            kind = RuleStepKind.Reduce,
                            reduce = reduce.kind
                        });
                        AfterPathEdit(subject);
                        return y;
                    }
                    y += OptionHeight + OptionGap;
                }

                return QuantifyEntries(y, width, subject, readOnly);
            }

            if (list.Count == 0 && rejectedVerbs.Count == 0)
            {
                return Hint(y, width, "RuleCore.Edit.NoVerbForType".Translate());
            }

            string current = CurrentVerbKey();

            for (int i = 0; i < list.Count; i++)
            {
                var info = list[i];

                // 标签右侧的注记就是"这个谓词的特殊之处"：边沿 / 权限 / 安静让开。
                string tag = null;
                if (info.edge == RuleEdge.Edge)
                {
                    tag = "RuleCore.Edit.TagEdge".Translate();
                }
                else if (info.tier == RuleTier.Developer)
                {
                    // **把"现在到底放不放行"写在标签上。**
                    //
                    // 只标"开发者级"是不够的：没拿到授权的人会写出一条语法完全正确、
                    // 却被引擎静默挡下的规则，而原因只在时间线里、不在他眼前。
                    // 这里直接说"未授权"，再把去哪儿开写在下面。
                    tag = RuleEngine.AllowedTier >= RuleTier.Developer
                        ? "RuleCore.Edit.TagGod".Translate()
                        : "RuleCore.Edit.TagGodDenied".Translate();
                }
                else if (info.failure == RuleFailureMode.QuietSkip)
                {
                    tag = "RuleCore.Edit.TagQuiet".Translate();
                }

                var color = category == RuleVerbCategory.Detect ? DetectColor : OperateColor;

                if (Option(y, width, Label(info.LabelKey, info.key), tag, color,
                        current == info.key)
                    && !readOnly)
                {
                    SetVerb(info);
                }

                y += OptionHeight + OptionGap;
            }

            // 列表里只要有"开发者级"的动作、而当前没拿到授权，就说清去哪儿开。
            // 这条提示比把它从列表里删掉强：规则是**可以分享的数据**，
            // 别人可能在有授权的机器上打开它；藏起来会让那条规则看起来像坏了。
            if (!readOnly && RuleEngine.AllowedTier < RuleTier.Developer)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].tier != RuleTier.Developer) continue;
                    y = Hint(y, width, "RuleCore.Edit.GodDeniedHint".Translate());
                    break;
                }
            }

            // 与属性那边同一个道理：**"没有它"和"你选错了主体"必须分开说**。
            if (rejectedVerbs.Count > 0)
            {
                y = Section(y, width, "RuleCore.Edit.Section.NotForThis".Translate());

                for (int i = 0; i < rejectedVerbs.Count; i++)
                {
                    var item = rejectedVerbs[i];
                    y = Hint(y, width, Label(item.labelKey, item.key) + "  —— "
                        + "RuleCore.Edit.MissingCapability".Translate(
                            CapabilityName(item.missing)));
                }
            }

            // 已经挂着的那条谓语如果不在列表里（老规则、手改 XML、或者这条子句是
            // 从别处改过来的），**明说它为什么不在**——
            // 否则玩家看到的是"列表里没有高亮项"，而句子上明明写着一个词。
            var stale = RuleVocabularyCatalog.Current.Verb(current);
            if (stale != null && !readOnly)
            {
                bool present = false;
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].key == stale.key) { present = true; break; }
                }

                if (!present)
                {
                    y = Hint(y, width, "RuleCore.Edit.VerbNoLongerValid".Translate(
                        Label(stale.LabelKey, stale.key), TypeTagOf(kind, entityKind)));
                }
            }

            return y;
        }

        /// <summary>单一位的能力名，如 <c>NeedFood</c> → 「饱食需求」。多位用 + 连起来。</summary>
        private static readonly RuleCapability[] capabilityBits =
        {
            RuleCapability.Pawn, RuleCapability.Map, RuleCapability.Thing,
            RuleCapability.Room, RuleCapability.Cell, RuleCapability.Humanlike,
            RuleCapability.Mechanoid, RuleCapability.Animal, RuleCapability.Biological,
            RuleCapability.NeedMood, RuleCapability.NeedFood, RuleCapability.NeedRest,
            RuleCapability.NeedEnergy, RuleCapability.NeedJoy, RuleCapability.NeedComfort,
            RuleCapability.NeedBeauty, RuleCapability.Apparel, RuleCapability.Equipment,
            RuleCapability.Inventory, RuleCapability.Bed, RuleCapability.Colonist,
            RuleCapability.Prisoner, RuleCapability.Slave, RuleCapability.Guest,
            RuleCapability.Hostile, RuleCapability.CanDraft, RuleCapability.Skills,
            RuleCapability.Ideo, RuleCapability.Age
        };

        public static string CapabilityName(RuleCapability caps)
        {
            if (caps == RuleCapability.None) return "—";

            var sb = new System.Text.StringBuilder(32);

            for (int i = 0; i < capabilityBits.Length; i++)
            {
                if ((caps & capabilityBits[i]) == 0) continue;
                if (sb.Length > 0) sb.Append('+');
                sb.Append(Label(RulePawnFacts.CapabilityLabelKey(capabilityBits[i]),
                    capabilityBits[i].ToString()));
            }

            return sb.Length > 0 ? sb.ToString() : caps.ToString();
        }

        private string CurrentVerbKey()
        {
            if (selection == null) return null;
            if (selection.Leaf != null) return selection.Leaf.verbKey;
            if (selection.Clause != null) return selection.Clause.verbKey;
            return null;
        }

        private void SetVerb(RuleVerbInfo info)
        {
            RuleOperand operand = selection.Leaf != null ? selection.Leaf.argument
                : selection.Clause.argument;

            // 换了谓词，原来那个宾语可能类型不对了——**换成一个类型正确的默认宾语**。
            //
            // 之前是 `new RuleOperand()`，它的 kind 是 None。那个空壳有一个很坏的后果：
            // 玩家在界面上把路径/数值填好了，kind 仍然是 None → 校验说"宾语没有配置"、
            // 求值器当它不存在。**填了却说没填**，而且看不出为什么。
            bool needsDefault = operand == null
                || operand.Kind == RuleValueKind.None
                || operand.Kind != info.argKind;

            if (needsDefault)
            {
                var fresh = DefaultOperand(info);
                if (selection.Leaf != null) selection.Leaf.argument = fresh;
                else selection.Clause.argument = fresh;
            }

            if (selection.Leaf != null) selection.Leaf.verbKey = info.key;
            else selection.Clause.verbKey = info.key;

            // **谓语要宾语、而宾语还空着 → 直接把宾语槽选中。**
            //
            // 玩家的下一个动作必然是"填宾语"。不跳的话他会在谓语列表里停着，
            // 然后看到底下校验说"宾语还没选值"——**那句话是对的，但它不告诉他该点哪里**。
            // 把下一步要点的那个槽位直接选中，这个问题就不存在了。
            //
            // 只对"真的空着"的宾语跳：数值默认是 0（一个有意义的值），不跳——
            // 否则每点一个比较符都被弹走，浏览谓语会变得很难受。
            var installed = selection.Leaf != null ? selection.Leaf.argument
                : selection.Clause.argument;
            if (info.NeedsArgument && IsOperandBlank(installed))
            {
                selection.Slot = Slot.Object;
            }

            StructureChanged = true;
        }

        /// <summary>
        /// 宾语**还没有值**。和"类型不对"是两回事：
        /// 这里判断的是"玩家还没填"，校验消息里的措辞也跟着分（"还没选值"而不是"类型不对"）。
        /// </summary>
        private static bool IsOperandBlank(RuleOperand operand)
        {
            if (operand == null) return true;

            switch (operand.Kind)
            {
                // 枚举：选了具体一项才算填。
                case RuleValueKind.Enum:
                    return string.IsNullOrEmpty(operand.literal.key);

                // 实体：连路径都没建才算没填（建了路径哪怕还没读属性，也是"有个东西"了）。
                case RuleValueKind.Entity:
                    return operand.path == null;

                // 文本：空白算没填。
                case RuleValueKind.Text:
                    return string.IsNullOrEmpty(operand.literal.key);

                // 数值/布尔/坐标：默认值本身就有意义（0 / false），不算空。
                default:
                    return false;
            }
        }

        /// <summary>
        /// 一个谓词对应的**类型正确的空宾语**。
        ///
        /// 初值必须把 `kind` 定下来，而不是留 `None`：None 在语义上是"还没填"，
        /// 但玩家一旦在界面上把值填进去、`kind` 却还是 None，就会出现"填了却说没填"。
        ///
        /// **实体宾语刻意不预填路径。** 原来不管谓词要什么，都预填成 `本主体`——
        /// 于是「前往」（要格子）生下来就带着一个"本主体"（一个小人），
        /// 规则一建出来就有一条校验错误，而玩家在界面上**没有地方能改它**：
        /// 属性列表是按"格子"去词表里查的，一条都不存在。
        /// 留空之后，宾语检查器会直接列出"能产出这个类型的走法"。
        /// </summary>
        private static RuleOperand DefaultOperand(RuleVerbInfo info)
        {
            switch (info.argKind)
            {
                case RuleValueKind.Number:
                    return RuleOperand.OfNumber(0f);

                case RuleValueKind.Enum:
                    return new RuleOperand { kind = RuleValueKind.Enum };

                case RuleValueKind.Entity:
                    // kind 定下来（"这是个实体宾语"），但路径留空（"还没选是哪条"）。
                    return RuleOperand.OfEntity(null);

                case RuleValueKind.Text:
                    return RuleOperand.OfText(string.Empty);

                default:
                    return new RuleOperand();
            }
        }

        private float DrawObjectInspector(float y, float width, bool readOnly)
        {
            var verb = RuleVocabularyCatalog.Current.Verb(CurrentVerbKey());
            if (verb == null)
            {
                return Hint(y, width, "RuleCore.Edit.PickPredicateFirst".Translate());
            }

            if (!verb.NeedsArgument)
            {
                return Hint(y, width, "RuleCore.Edit.NoArgument".Translate());
            }

            RuleOperand operand = selection.Leaf != null ? selection.Leaf.argument
                : selection.Clause.argument;

            if (operand == null)
            {
                operand = new RuleOperand();
                if (selection.Leaf != null) selection.Leaf.argument = operand;
                else selection.Clause.argument = operand;
            }

            // 阈值的单位来自主语路径最后读到的属性——不然规则读起来是"0"而不是"0℃"。
            var subject = SelectedPath();

            RuleValueKind kind;
            RuleEntityKind entityKind;
            RulePropertyInfo unitSource;
            RulePathTypes.AdvanceType(subject, SubjectKind(), selection.InsideFilter,
                ElementKind(), subject != null ? subject.StepCount : 0,
                out kind, out entityKind, out unitSource);

            // 主语路径上读不到单位时，用**谓语自己声明的**。
            // 操作的典型情况：`本主体 充电 [__]` 的主语是执行者，路径上什么都没有，
            // 不给这一步的话输入框就是个裸数字（输入 1 = 100%）。
            if (unitSource == null) unitSource = verb.argDisplay;

            y = Section(y, width, "RuleCore.Edit.Section.Object".Translate());

            switch (verb.argKind)
            {
                case RuleValueKind.Number:
                    return NumberObject(y, width, operand, unitSource, readOnly);

                case RuleValueKind.Enum:
                    return EnumObject(y, width, operand, verb, unitSource, readOnly);

                case RuleValueKind.Entity:
                    return EntityObject(y, width, operand, verb, readOnly);

                default:
                    return TextObject(y, width, operand, readOnly);
            }
        }

        private float NumberObject(float y, float width, RuleOperand operand,
            RulePropertyInfo unitSource, bool readOnly)
        {
            bool percent = unitSource != null && unitSource.percent;
            float value = RuleFormat.ToEditValue(operand.literal.number, percent);

            float min;
            float max;
            RuleFormat.EditRange(unitSource, out min, out max);

            string suffix = RuleFormat.EditSuffix(unitSource);
            float suffixWidth = string.IsNullOrEmpty(suffix) ? 0f : 30f;
            var fieldRect = new Rect(0f, y, Mathf.Max(width - suffixWidth, 24f), 26f);

            if (readOnly)
            {
                Widgets.LabelEllipses(fieldRect, RuleFormat.Format(unitSource, operand.literal.number));
            }
            else
            {
                string key = "num";
                string buffer;
                buffers.TryGetValue(key, out buffer);
                Widgets.TextFieldNumeric(fieldRect, ref value, ref buffer, min, max);
                buffers[key] = buffer;

                float back = RuleFormat.FromEditValue(value, percent);
                if (Mathf.Abs(back - operand.literal.number) > 0.00001f)
                {
                    operand.kind = RuleValueKind.Number;
                    operand.literal.kind = RuleValueKind.Number;
                    operand.literal.number = back;
                    ValueChanged = true;
                }

                if (!string.IsNullOrEmpty(suffix))
                {
                    var prev = GUI.color;
                    GUI.color = MutedColor;
                    Widgets.Label(new Rect(width - suffixWidth + 4f, y, suffixWidth, 26f), suffix);
                    GUI.color = prev;
                }
            }

            y += 30f;

            // ── 常用值 ────────────────────────────────────────────────
            //
            // **锚点必须从这个属性自己的单位和范围里长出来。**
            //
            // 原来这里是写死的一串 `0 / 10 / 0℃ / -5℃`——于是
            // 「本图.天空亮度 大于 __」的宾语旁边摆着两个**温度**锚点。
            // 那不只是没用：它会让玩家以为自己填错了地方，或者以为
            // 这个属性是个温度。**和"念内部键"是同一类错**：界面说了不该说的话。
            y = Section(y, width, "RuleCore.Edit.Section.Presets".Translate());

            float[] anchors = AnchorValues(unitSource, min, max);
            for (int i = 0; i < anchors.Length; i++)
            {
                y = Preset(y, width, FormatAnchor(anchors[i], unitSource, percent),
                    operand, anchors[i]);
            }

            return y;
        }

        private static readonly float[] GenericAnchors = { 0f, 1f, 10f, 100f };
        private static readonly float[] PercentAnchors = { 0.25f, 0.5f, 0.75f, 1f };
        private static readonly float[] TempAnchors = { -10f, 0f, 20f };

        /// <summary>
        /// 几个"一看就懂的锚点"，**按属性自己的类型挑，并剔掉落在范围外的**。
        ///
        /// `min`/`max` 是**输入框那边的刻度**（百分比是 0~100），所以比较前要换算。
        /// </summary>
        private static float[] AnchorValues(RulePropertyInfo info, float min, float max)
        {
            float[] pool;
            if (info != null && info.percent) pool = PercentAnchors;
            else if (info != null && info.unit == "℃") pool = TempAnchors;
            else pool = GenericAnchors;

            float lo = RuleFormat.ToEditValue(min, info != null && info.percent);
            float hi = RuleFormat.ToEditValue(max, info != null && info.percent);

            var kept = new List<float>(pool.Length);
            for (int i = 0; i < pool.Length; i++)
            {
                float shown = RuleFormat.ToEditValue(pool[i], info != null && info.percent);
                if (shown < lo || shown > hi) continue;
                kept.Add(pool[i]);
            }

            // 范围窄到一个锚点都不剩时，至少给下界——空着比候选差更让人无从下手。
            if (kept.Count == 0) kept.Add(RuleFormat.FromEditValue(lo, info != null && info.percent));

            return kept.ToArray();
        }

        private static string FormatAnchor(float value, RulePropertyInfo info, bool percent)
        {
            return RuleFormat.FormatNumber(value, info != null ? info.decimals : 2,
                info != null ? info.unit : null, percent);
        }

        private float Preset(float y, float width, string label, RuleOperand operand, float value)
        {
            if (Option(y, width, label, null, ObjectColor))
            {
                operand.kind = RuleValueKind.Number;
                operand.literal.kind = RuleValueKind.Number;
                operand.literal.number = value;
                ValueChanged = true;
            }

            return y + OptionHeight + OptionGap;
        }

        /// <summary>
        /// 枚举宾语。
        ///
        /// <b>这一版修的是"玩家被迫手填 defName"。</b> 原来的布局是：
        ///
        /// <code>
        /// 宾语
        ///   [选一个值…]        ← 一行字，看起来像标签
        /// 手填 defName
        ///   [___________]     ← 一个文本框，**这一屏里唯一看起来能编辑的东西**
        /// </code>
        ///
        /// 于是玩家的第一反应就是往文本框里打字——而 defName 是内部标识符，
        /// 他不可能知道该填什么。**这不是玩家的问题，是界面把错的东西做得最显眼。**
        ///
        /// 现在：
        ///   · 候选清单**当场查出来**，数量写在按钮上（「从 152 个里选一个…」）；
        ///   · 声明了取值来源时，**根本不提供手填**——不需要知道 defName 是什么；
        ///   · 声明了来源却一条候选都没有时，**说实话**（那是我们坏了，不是他没得选），
        ///     并记进时间线，免得这种情况静默地装作"没有可以选的"。
        /// </summary>
        private float EnumObject(float y, float width, RuleOperand operand, RuleVerbInfo verb,
            RulePropertyInfo unitSource, bool readOnly)
        {
            // 取值来源优先取谓词声明的，其次取主语路径最后那个属性——
            // "是哪个枚举"是**属性**的性质（同一个「是」可以比天气，也可以比物品类型）。
            Type domain = verb.argDefType != null
                ? verb.argDefType
                : (unitSource != null ? unitSource.enumDefType : null);

            // 取值**不来自 Def 表**的枚举（如"身份"）：没有 Def 可查，但有作者写好的固定清单。
            RuleEnumOption[] options = verb.argOptions != null
                ? verb.argOptions
                : (unitSource != null ? unitSource.enumOptions : null);

            // **第三种取值域：存档里算出来的。**
            // 活动区、着装方案、药物政策都不是 Def，是玩家自己建的对象——
            // 数量与名字每次读档都可能不同，所以只能**现在**问一遍。
            // 只有前两种都为空时才问：作者写死的清单优先，因为那是有意为之的取舍。
            List<RuleEnumOption> runtime = null;
            if ((options == null || options.Length == 0) && domain == null)
            {
                var collector = verb.argCandidates != null
                    ? verb.argCandidates
                    : (unitSource != null ? unitSource.enumCandidates : null);

                if (collector != null)
                {
                    runtime = new List<RuleEnumOption>();
                    collector(runtime);
                }
            }

            // 候选清单现在就查（DefsOf 有缓存，不花钱），因为下面三件事都要用它：
            // 按钮文案、走菜单还是走搜索窗口、以及"一条都没有"时该说什么。
            var defs = domain != null ? Dialog_DefPicker.DefsOf(domain) : null;

            // **再按"此刻能不能用"筛一道。**
            // 类型对不代表用得上：「触发事件」要的事件里，日蚀/太阳耀斑/极光的目标
            // 只有世界，从地图上发出去会被原版第一关挡下。列出来 = 选了才发现不行。
            int rawCount = defs != null ? defs.Count : 0;
            defs = FilterCandidates(defs, verb);

            int candidateCount = defs != null
                ? defs.Count
                : (options != null ? options.Length
                    : (runtime != null ? runtime.Count : 0));

            bool hasSource = domain != null || candidateCount > 0;
            bool blank = string.IsNullOrEmpty(operand.literal.key);

            string shown;
            if (!blank)
            {
                shown = DisplayKeyOf(operand.literal.key, domain, options, runtime);
            }
            else if (candidateCount > 0)
            {
                // 数量是关键：它让"点下去会看到什么"变得可预期，
                // 也让这一行看起来像一个**选择器**而不是一个标签。
                shown = "RuleCore.Edit.PickFromList".Translate(candidateCount);
            }
            else
            {
                shown = "RuleCore.Edit.PickValue".Translate();
            }

            if (!readOnly && Option(y, width, shown, null, ObjectColor))
            {
                OpenEnumPicker(operand, domain, options, defs, runtime);
            }

            y += OptionHeight + OptionGap;

            if (!readOnly && candidateCount == 0 && hasSource)
            {
                // 两种"0 个"要分开说：
                //   · 查出来就是空的   → 我们坏了
                //   · 筛完才变空的     → 登记了 N 个，但一个都用不上
                // 混成一句的话，玩家不知道该去修 bug 还是该换个宾语。
                if (rawCount > 0)
                {
                    y = Hint(y, width, "RuleCore.Edit.NoneUsableAfterFilter".Translate(rawCount));
                }
                else
                {
                    y = Hint(y, width, "RuleCore.Edit.NoCandidateAtAll".Translate(
                        domain != null ? domain.Name : verb.key));
                }
            }
            else if (!readOnly && blank)
            {
                y = Hint(y, width, "RuleCore.Edit.PickOneHint".Translate());
            }

            // **手填只留给"压根没声明取值来源"的情况。**
            //
            // 声明了来源时它是有害的：它把"你不知道 defName"变成一个看起来必须回答的问题。
            // 没有来源时才真的只能手填，那时它才该出现。
            if (!readOnly && !hasSource)
            {
                y = Section(y, width, "RuleCore.Edit.Section.RawKey".Translate());
                var rect = new Rect(0f, y, width, 26f);
                string edited = Widgets.TextField(rect, operand.literal.key ?? string.Empty);
                if (edited != (operand.literal.key ?? string.Empty))
                {
                    operand.kind = RuleValueKind.Enum;
                    operand.literal.kind = RuleValueKind.Enum;
                    operand.literal.key = edited;
                    ValueChanged = true;
                }
                y += 30f;
            }

            return y;
        }

        /// <summary>
        /// 候选里"此刻真的能用"的那些。
        ///
        /// **不改 <see cref="Dialog_DefPicker.DefsOf"/> 返回的那份缓存**——它是共享的，
        /// 就地删元素会把别的调用方（以及下一帧）一起带坏。所以另建一个列表。
        /// </summary>
        private static List<Def> FilterCandidates(List<Def> all, RuleVerbInfo verb)
        {
            if (all == null) return null;
            if (verb == null || verb.argFilter == null) return all;

            var kept = new List<Def>(all.Count);
            for (int i = 0; i < all.Count; i++)
            {
                var def = all[i];
                if (def != null && verb.argFilter(def)) kept.Add(def);
            }
            return kept;
        }

        /// <summary>一条候选在界面上的显示名。运行时候选直接带名字，静态候选查语言键。</summary>
        private static string OptionLabel(RuleEnumOption opt)
        {
            if (opt == null) return "?";
            if (!string.IsNullOrEmpty(opt.label)) return opt.label;
            return Label(opt.labelKey, opt.key);
        }

        /// <summary>已选值在界面上的显示名：Def 翻译名 → 静态清单 → 运行时候选 → 回落成键。</summary>
        private string DisplayKeyOf(string key, Type domain, RuleEnumOption[] options,
            List<RuleEnumOption> runtime)
        {
            if (string.IsNullOrEmpty(key)) return key;

            if (domain != null)
            {
                Def def = GenDefDatabase.GetDefSilentFail(domain, key);
                if (def != null)
                {
                    string cap = def.LabelCap;
                    if (!string.IsNullOrEmpty(cap)) return cap;
                }
                return key;
            }

            if (options != null)
            {
                for (int i = 0; i < options.Length; i++)
                {
                    if (options[i].key == key) return OptionLabel(options[i]);
                }
            }

            if (runtime != null)
            {
                for (int i = 0; i < runtime.Count; i++)
                {
                    if (runtime[i].key == key) return OptionLabel(runtime[i]);
                }

                // 运行时候选里找不到：**活动区可能刚被删掉**。
                // 这时显示成「(已不存在) 名字」而不是一个光秃秃的数字 ID——
                // 玩家要能看出"这条规则指向的东西没了"。
                return "RuleCore.Edit.MissingOption".Translate(key);
            }

            return key;
        }

        /// <summary>
        /// 一屏能扫完就给菜单，扫不完才交给带搜索的窗口。
        ///
        /// **300 而不是 40**：菜单是原版控件（一定画得出来），那个搜索窗口是本模组自己的。
        /// 阈值定得太低会把"天气 / 信件类型 / 事件"这些中等长度的清单全推给自己的窗口，
        /// 而玩家在那里一旦看不到东西，得到的信息是"**没有宾语可以选**"——
        /// 一个查不出答案的结论。宁可他多滚两下。
        /// </summary>
        private const int FloatMenuLimit = 300;

        /// <summary>超过菜单上限时，先在菜单里放多少条。剩下的靠"搜索全部"。</summary>
        private const int FloatMenuPreview = 200;

        private void OpenEnumPicker(RuleOperand operand, Type domain, RuleEnumOption[] options,
            List<Def> defs, List<RuleEnumOption> runtime)
        {
            // 固定清单优先：它是作者写好的完整取值域，语言名也是现成的。
            if (options != null && options.Length > 0)
            {
                var fixedOptions = new List<FloatMenuOption>();

                for (int i = 0; i < options.Length; i++)
                {
                    var captured = options[i];
                    fixedOptions.Add(new FloatMenuOption(
                        OptionLabel(captured),
                        delegate { PickEnumKey(operand, captured.key); }));
                }

                Find.WindowStack.Add(new FloatMenu(fixedOptions));
                return;
            }

            // 运行时候选：活动区 / 着装方案 / 药物政策。
            // **一条都没有时要分开说**——"你还没建过任何活动区"是完全正常的，
            // 而"读不到"才是我们坏了。
            if (runtime != null)
            {
                if (runtime.Count == 0)
                {
                    Messages.Message("RuleCore.Edit.NoRuntimeOption".Translate(),
                        MessageTypeDefOf.NeutralEvent, false);
                    return;
                }

                var runtimeOptions = new List<FloatMenuOption>();
                for (int i = 0; i < runtime.Count; i++)
                {
                    var captured = runtime[i];
                    runtimeOptions.Add(new FloatMenuOption(
                        OptionLabel(captured),
                        delegate { PickEnumKey(operand, captured.key); }));
                }

                Find.WindowStack.Add(new FloatMenu(runtimeOptions));
                return;
            }

            if (domain == null)
            {
                // 没有声明取值来源就只能手填——**不猜**。
                Messages.Message("RuleCore.Edit.EnumNeedsTyping".Translate(),
                    MessageTypeDefOf.NeutralEvent, false);
                return;
            }

            if (defs == null) defs = Dialog_DefPicker.DefsOf(domain);

            if (defs.Count == 0)
            {
                // **"一条候选都没有"必须说出来。** 静默地摆一个空菜单，
                // 玩家得到的结论是"这个功能没得选"——而真相是 `DefsOf` 出错了。
                // 同时记进时间线，这样它有个能被查到的落点。
                string msg = "读不到 " + domain.Name + " 的候选清单——这不是\"没得选\"，是查找失败了。";
                RuleLog.Error(null, "Picker", RuleEvalStatus.Error, "picker.empty", null, msg);
                Messages.Message("RuleCore.Edit.NoCandidateAtAll".Translate(domain.Name),
                    MessageTypeDefOf.NegativeEvent, false);
                return;
            }

            var menuOptions = new List<FloatMenuOption>();

            // 清单太长时：菜单里放"搜索全部"的入口 + 前 N 条。
            // **不是把菜单整个换成搜索窗口**——搜索窗口要是画不出来，
            // 玩家就彻底没得选了；这样至少那前 N 条还能用。
            if (defs.Count > FloatMenuLimit)
            {
                var capturedDomain = domain;
                menuOptions.Add(new FloatMenuOption(
                    "RuleCore.Edit.SearchAll".Translate(defs.Count),
                    delegate
                    {
                        Find.WindowStack.Add(new Dialog_DefPicker(capturedDomain,
                            operand.literal.key, delegate(Def picked)
                            {
                                PickEnumKey(operand, picked.defName);
                            }));
                    }));

                for (int i = 0; i < FloatMenuPreview && i < defs.Count; i++)
                {
                    menuOptions.Add(DefOption(operand, defs[i]));
                }

                menuOptions.Add(new FloatMenuOption(
                    "RuleCore.Edit.MoreItems".Translate(
                        Mathf.Max(defs.Count - FloatMenuPreview, 0)), null));
            }
            else
            {
                for (int i = 0; i < defs.Count; i++)
                {
                    menuOptions.Add(DefOption(operand, defs[i]));
                }
            }

            Find.WindowStack.Add(new FloatMenu(menuOptions));
        }

        /// <summary>一条候选。显示成「中文名 [defName]」——**两个都给**，因为贴给别人时要用键。</summary>
        private FloatMenuOption DefOption(RuleOperand operand, Def def)
        {
            var captured = def;
            string label = (def.LabelCap + "  [" + def.defName + "]").ToString();
            return new FloatMenuOption(label, delegate
            {
                PickEnumKey(operand, captured.defName);
            });
        }

        /// <summary>把选中的键写进宾语。三条路径（固定清单 / 菜单 / 搜索窗口）共用一处。</summary>
        private void PickEnumKey(RuleOperand operand, string key)
        {
            operand.kind = RuleValueKind.Enum;
            operand.literal.kind = RuleValueKind.Enum;
            operand.literal.key = key;
            ValueChanged = true;
        }

        /// <summary>
        /// 宾语是实体时的编辑器。
        ///
        /// **这一版修的是一个"生下来就错"的默认值**：原来不管谓词要什么，
        /// 宾语路径都预填成 `本主体`。于是「前往」要格子、拿到的是一个小人，
        /// 规则一建出来就带着一条校验错误，而玩家在界面上**没有地方能改它**——
        /// 属性列表是按"格子"去词表里查的，一条都没有。
        ///
        /// 现在这里是"只列能用的"在宾语侧的实现：
        ///   · 路径还没建 → 直接列出**能产出这个类型的走法**（`本主体.避难格` → 格子）；
        ///   · 路径建好了 → 用**诚实的类型**往下走（不再拿谓词的要求去顶替根的类型）。
        /// </summary>
        private float EntityObject(float y, float width, RuleOperand operand, RuleVerbInfo verb,
            bool readOnly)
        {
            if (operand.kind != RuleValueKind.Entity)
            {
                // 路径在，就是实体宾语。`kind` 落后于 `path` 正是"填了却说没填"的根源。
                operand.kind = RuleValueKind.Entity;
                StructureChanged = true;
            }

            y = Section(y, width, "RuleCore.Edit.Section.EntityPath".Translate());

            if (operand.path == null || operand.path.IsEmpty)
            {
                return readOnly
                    ? Hint(y, width, "RuleCore.Edit.NoObject".Translate())
                    : BuildObjectPath(y, width, operand, verb);
            }

            RuleValueKind kind;
            RuleEntityKind entityKind;
            RulePropertyInfo last;
            // **主语类型传 SubjectKind()，不是 verb.argEntity。** 后者是"我希望它是什么"，
            // 前者的才是"这条路径真的产出什么"——拿希望顶替事实，正是上面那个 bug 的根。
            RulePathTypes.AdvanceType(operand.path, SubjectKind(), false, RuleEntityKind.Any,
                operand.path.StepCount, out kind, out entityKind, out last);

            y = PathChain(y, width, operand.path, readOnly);
            y = TypeBadge(y, width, kind, entityKind);

            // 类型对不对**当场说**。原来只有底下的"校验"里会报，而那句话离玩家刚做的那步很远。
            y = ObjectTypeCheck(y, width, kind, entityKind, verb);

            if (readOnly) return y;

            // 换根：宾语是另一条路径，和主语互不相干，所以这里也给一份根列表。
            y = Section(y, width, "RuleCore.Edit.Section.Root".Translate());

            for (int i = 0; i < RootOrder.Length; i++)
            {
                var rootKind = RootOrder[i];
                if (rootKind == RuleRootKind.Element) continue;

                bool active = operand.path.rootKind == rootKind;

                if (Option(y, width, RootName(rootKind, currentRule),
                        TypeTagOf(RuleValueKind.Entity, RootHostKind(rootKind)), EntityColor, active))
                {
                    operand.path = new RulePath { rootKind = rootKind };
                    StructureChanged = true;
                    return y;
                }
                y += OptionHeight + OptionGap;
            }

            var properties = new List<RulePropertyInfo>();
            RuleVocabularyCatalog.Current.CollectPropertiesFor(entityKind,
                PathCapabilities(operand.path), properties);

            if (kind == RuleValueKind.Entity && properties.Count > 0)
            {
                y = Section(y, width, "RuleCore.Edit.Section.AddProperty".Translate());

                for (int i = 0; i < properties.Count; i++)
                {
                    var info = properties[i];
                    if (Option(y, width, "." + Label(info.LabelKey, info.key), TypeTagOf(info), EntityColor))
                    {
                        operand.path.steps.Add(new RulePathStep
                        {
                            kind = RuleStepKind.Property,
                            propertyKey = info.key
                        });
                        StructureChanged = true;
                    }
                    y += OptionHeight + OptionGap;
                }
            }

            if (kind == RuleValueKind.EntitySet)
            {
                var reduces = RuleVocabulary.AllReduces;
                for (int i = 0; i < reduces.Count; i++)
                {
                    var info = reduces[i];
                    if (info.from != kind) continue;

                    if (Option(y, width, "." + Label(info.LabelKey, info.kind.ToString()),
                            "→ " + TypeTagOf(info.to), ReduceColor))
                    {
                        operand.path.steps.Add(new RulePathStep
                        {
                            kind = RuleStepKind.Reduce,
                            reduce = info.kind
                        });
                        StructureChanged = true;
                    }
                    y += OptionHeight + OptionGap;
                }

                y = QuantifyEntries(y, width, operand.path, false);
            }

            return y;
        }

        /// <summary>
        /// 宾语路径还没建时的入口：**只列能产出这个类型的走法**。
        ///
        /// 「前往」要格子，而格子只能从某个属性读出来（`本主体.避难格`）——
        /// 玩家不需要知道这件事，他只需要在这里看见它并点一下。
        /// 点完之后路径就是 `本主体.避难格`，类型当场对上。
        /// </summary>
        private float BuildObjectPath(float y, float width, RuleOperand operand, RuleVerbInfo verb)
        {
            y = Section(y, width, "RuleCore.Edit.Section.WaysToMake".Translate(
                TypeTagOf(RuleValueKind.Entity, verb.argEntity)));

            int shown = 0;

            for (int r = 0; r < RootOrder.Length; r++)
            {
                var rootKind = RootOrder[r];
                if (rootKind == RuleRootKind.Element) continue;

                var hostKind = RootHostKind(rootKind);
                if (hostKind == RuleEntityKind.Any) continue;

                var candidates = new List<RulePropertyInfo>();
                RuleVocabularyCatalog.Current.CollectPropertiesFor(hostKind,
                    rootKind == RuleRootKind.Subject ? SubjectCapabilities() : RuleCapability.All,
                    candidates);

                for (int i = 0; i < candidates.Count; i++)
                {
                    var info = candidates[i];
                    if (info.result != RuleValueKind.Entity) continue;
                    if (!RuleVocabulary.EntityKindMatches(verb.argEntity, info.resultEntity)) continue;

                    var capturedProperty = info;
                    var capturedRoot = rootKind;

                    if (Option(y, width,
                            RootName(rootKind, currentRule) + "." + Label(info.LabelKey, info.key),
                            TypeTagOf(info), EntityColor))
                    {
                        operand.path = new RulePath { rootKind = capturedRoot };
                        operand.path.steps.Add(new RulePathStep
                        {
                            kind = RuleStepKind.Property,
                            propertyKey = capturedProperty.key
                        });
                        StructureChanged = true;
                    }
                    y += OptionHeight + OptionGap;
                    shown++;
                }
            }

            if (shown == 0)
            {
                y = Hint(y, width, "RuleCore.Edit.NoWayToMake".Translate(
                    TypeTagOf(RuleValueKind.Entity, verb.argEntity)));
            }

            // 手拼的入口：和主语那份一样的根列表。
            y = Section(y, width, "RuleCore.Edit.Section.Root".Translate());

            for (int i = 0; i < RootOrder.Length; i++)
            {
                var rootKind = RootOrder[i];
                if (rootKind == RuleRootKind.Element) continue;

                if (Option(y, width, RootName(rootKind, currentRule),
                        TypeTagOf(RuleValueKind.Entity, RootHostKind(rootKind)), EntityColor))
                {
                    operand.path = new RulePath { rootKind = rootKind };
                    StructureChanged = true;
                    return y;
                }
                y += OptionHeight + OptionGap;
            }

            return y;
        }

        /// <summary>
        /// 宾语的类型和谓词要的对不对，当场画出来。
        ///
        /// 它和底下"校验"那一栏是**两件事**：校验是"这条规则跑不起来"，
        /// 这里是"你刚做的这一步对不对"。后者必须在做的那一秒就能看见。
        /// </summary>
        private float ObjectTypeCheck(float y, float width, RuleValueKind kind,
            RuleEntityKind entityKind, RuleVerbInfo verb)
        {
            bool ok;

            if (kind == RuleValueKind.Entity)
            {
                ok = RuleVocabulary.EntityKindMatches(verb.argEntity, entityKind);
            }
            else if (kind == RuleValueKind.EntitySet)
            {
                // 一组东西不能直接喂给任何谓词——先归约。
                ok = false;
            }
            else
            {
                ok = kind == verb.argKind;
            }

            string want = TypeTagOf(verb.argKind, verb.argEntity);
            string have = TypeTagOf(kind, entityKind);

            var rect = new Rect(0f, y, width, 22f);
            var prev = GUI.color;
            GUI.color = ok ? ReduceColor : DangerColor;
            Widgets.LabelEllipses(rect, ok
                ? "RuleCore.Edit.ObjectTypeOk".Translate(have)
                : "RuleCore.Edit.ObjectTypeBad".Translate(want, have));
            GUI.color = prev;

            return y + 26f;
        }

        /// <summary>
        /// 某个根**真的**产出什么实体类型。
        ///
        /// 和 <see cref="RootEntity"/> 的区别：那个是给类型标签用的（`本主体` 一律显示"小人"），
        /// 这个是给"能不能从这里往下走"用的——所以它必须问当前规则绑的是什么。
        /// </summary>
        private RuleEntityKind RootHostKind(RuleRootKind kind)
        {
            switch (kind)
            {
                case RuleRootKind.Subject: return SubjectKind();
                case RuleRootKind.Element: return ElementKind();
                case RuleRootKind.Map:
                case RuleRootKind.AllMaps: return RuleEntityKind.Map;
                case RuleRootKind.Colonists: return RuleEntityKind.Pawn;
                default: return RuleEntityKind.Any;
            }
        }

        private float TextObject(float y, float width, RuleOperand operand, bool readOnly)
        {
            var rect = new Rect(0f, y, width, 26f);

            if (readOnly)
            {
                Widgets.LabelEllipses(rect, operand.literal.key ?? string.Empty);
                return y + 30f;
            }

            string edited = Widgets.TextField(rect, operand.literal.key ?? string.Empty);
            if (edited != (operand.literal.key ?? string.Empty))
            {
                operand.kind = RuleValueKind.Text;
                operand.literal.kind = RuleValueKind.Text;
                operand.literal.key = edited;
                ValueChanged = true;
            }

            return y + 30f;
        }

        // ══ 小控件 ════════════════════════════════════════════════════

        private bool Option(float y, float width, string text, string tag, Color color,
            bool active = false)
        {
            var rect = new Rect(0f, y, width, OptionHeight);

            if (active) Widgets.DrawBoxSolid(rect, SelectedBg);
            else if (Mouse.IsOver(rect)) Widgets.DrawBoxSolid(rect, OptionHover);
            else Widgets.DrawBoxSolid(rect, OptionColor);

            var prev = GUI.color;
            GUI.color = color;
            Text.Anchor = TextAnchor.MiddleLeft;
            float tagWidth = string.IsNullOrEmpty(tag) ? 0f : FlowTextWidth(tag) + 12f;
            float labelWidth = Mathf.Max(rect.width - tagWidth - 14f, 4f);
            Widgets.LabelEllipses(new Rect(rect.x + 8f, rect.y, labelWidth, rect.height), text);
            GUI.color = prev;

            if (!string.IsNullOrEmpty(tag))
            {
                GUI.color = MutedColor;
                Text.Anchor = TextAnchor.MiddleRight;
                Widgets.LabelEllipses(new Rect(rect.x + rect.width - tagWidth - 6f, rect.y,
                    tagWidth, rect.height), tag);
                GUI.color = prev;
            }

            Text.Anchor = TextAnchor.MiddleLeft;
            return Widgets.ButtonInvisible(rect);
        }

        private bool Button(Rect rect, string label, Color color)
        {
            if (Mouse.IsOver(rect)) Widgets.DrawBoxSolid(rect, OptionHover);
            else Widgets.DrawBoxSolid(rect, OptionColor);

            var prev = GUI.color;
            GUI.color = color;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.LabelEllipses(rect, label);
            GUI.color = prev;
            Text.Anchor = TextAnchor.MiddleLeft;

            return Widgets.ButtonInvisible(rect);
        }

        private bool DeleteButton(Rect rect)
        {
            bool over = Mouse.IsOver(rect);
            var prev = GUI.color;
            GUI.color = over ? DangerColor : MutedColor;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.LabelEllipses(rect, "×");
            GUI.color = prev;
            Text.Anchor = TextAnchor.MiddleLeft;
            return Widgets.ButtonInvisible(rect);
        }

        private bool SlotButton(Rect rect, string text, Color color, bool selected, string placeholder)
        {
            bool empty = string.IsNullOrEmpty(text);

            if (selected) Widgets.DrawBoxSolid(rect, SelectedBg);
            else if (Mouse.IsOver(rect)) Widgets.DrawBoxSolid(rect, HoverBg);

            var prev = GUI.color;
            GUI.color = empty ? MutedColor : color;
            Text.Anchor = TextAnchor.MiddleLeft;
            Widgets.LabelEllipses(rect.ContractedBy(4f, 0f), empty ? placeholder : text);
            GUI.color = prev;

            return Widgets.ButtonInvisible(rect);
        }

        private float SlotWidth(string text, string placeholder, float min, float available)
        {
            string shown = string.IsNullOrEmpty(text) ? placeholder : text;
            float w = FlowTextWidth(shown) + SlotPadding * 2f;
            if (w < min) w = min;
            if (w > available) w = Mathf.Max(min, available);
            return w;
        }

        private float Section(float y, float width, string title)
        {
            var prev = GUI.color;
            GUI.color = MutedColor;
            Text.Font = GameFont.Tiny;
            // 18 而不是 16：Tiny 的字高加上下沿差不多就是这个数，
            // 16 会把汉字的下半截切掉（"字体被遮挡"最常见的来源）。
            Widgets.LabelEllipses(new Rect(0f, y + 6f, width, 18f), title);
            Text.Font = GameFont.Small;
            GUI.color = prev;
            return y + 24f;
        }

        /// <summary>
        /// 一段灰字说明。**高度按实测来，而且要开折行。**
        ///
        /// 原来是 `Widgets.Label(new Rect(0f, y + 6f, width, 40f), text)`：高度写死 40，
        /// 而检查器里 <c>Text.WordWrap</c> 是 <b>false</b>（为了槽位不折行）——
        /// 于是每一句较长的说明都是**一行画出去、被矩形横向切掉半句**，
        /// 后面还跟着一块空白。这正是"字体被遮挡"里最难看的一处：
        /// 玩家看到的是半句话，而剩下半句永远不会显示。
        ///
        /// 现在按 <see cref="Text.CalcHeight"/> 实测高度，画完把折行状态还原回去。
        /// </summary>
        private float Hint(float y, float width, string text)
        {
            if (string.IsNullOrEmpty(text)) return y;

            var prevColor = GUI.color;
            var prevWrap = Text.WordWrap;
            var prevAnchor = Text.Anchor;

            GUI.color = MutedColor;
            Text.WordWrap = true;
            Text.Anchor = TextAnchor.UpperLeft;

            float usable = Mathf.Max(width, 16f);
            float h = Mathf.Max(Text.CalcHeight(text, usable), 18f);
            Widgets.Label(new Rect(0f, y + 4f, usable, h), text);

            Text.WordWrap = prevWrap;
            Text.Anchor = prevAnchor;
            GUI.color = prevColor;

            return y + h + 10f;
        }

        private float TypeBadge(float y, float width, RuleValueKind kind, RuleEntityKind entityKind)
        {
            string text = "RuleCore.Edit.CurrentType".Translate(TypeTagOf(kind, entityKind));
            var rect = new Rect(0f, y, width, 22f);

            var prev = GUI.color;
            Widgets.DrawBoxSolid(rect, new Color(0.31f, 0.76f, 0.97f, 0.12f));
            GUI.color = DetectColor;
            Text.Anchor = TextAnchor.MiddleCenter;
            Widgets.LabelEllipses(rect, text);
            GUI.color = prev;
            Text.Anchor = TextAnchor.MiddleLeft;

            return y + 26f;
        }

        private float DrawIssues(float y, float width, Rule rule)
        {
            issues.Clear();
            rule.CollectConfigErrors(issues);

            var prev = GUI.color;

            if (issues.Count == 0)
            {
                GUI.color = MutedColor;
                Widgets.Label(new Rect(0f, y + 8f, width, 22f), "RuleCore.Edit.NoIssues".Translate());
                GUI.color = prev;
                return y + 34f;
            }

            y = Section(y, width, "RuleCore.Form.Issues".Translate(issues.Count));

            GUI.color = DangerColor;
            Text.WordWrap = true;

            for (int i = 0; i < issues.Count; i++)
            {
                float lineWidth = Mathf.Max(width - 8f, 16f);
                float h = Mathf.Max(18f, Text.CalcHeight(issues[i], lineWidth));
                Widgets.Label(new Rect(4f, y, lineWidth, h), "· " + issues[i]);
                y += h + 2f;
            }

            Text.WordWrap = false;
            GUI.color = prev;
            return y + 6f;
        }

        private static float FlowTextWidth(string text)
        {
            if (string.IsNullOrEmpty(text)) return 0f;
            return Text.CalcSize(text).x;
        }

        // ══ 选中 ══════════════════════════════════════════════════════

        private void Select(RuleExprNode leaf, RuleClause clause, Slot slot, bool insideFilter)
        {
            if (selection == null) selection = new Selection();

            // **必须清掉"正在编辑主体绑定"这个标记。**
            //
            // 它是一个一次性的跳转（头栏那个"主体"按钮、或者"还没绑定主体 ⚠"那一行），
            // 点了之后检查器就一直停在绑定面板上。忘清它的后果是：
            // **点句子里的任何槽位都没反应**——点了，`Leaf`/`Slot` 都设了，
            // 但 `DrawInspector` 只认 `Binding`，于是一直画绑定面板。
            // 玩家的感受是"界面卡住了"，而正确的感受应该是"我换了个槽位在编辑"。
            selection.Binding = false;

            selection.Leaf = leaf;
            selection.Clause = clause;
            selection.Slot = slot;
            selection.InsideFilter = insideFilter;

            if (!insideFilter)
            {
                selection.FilterPath = null;
                selection.FilterStepIndex = -1;
                return;
            }

            if (selection.FilterPath != null) return;

            // 从句子行点筛选子句时没有路径上下文——回头找容得下它的那个筛选步骤。
            var found = FindFilterStep(currentRule != null ? currentRule.detect : null, leaf, null);
            if (found >= 0)
            {
                selection.FilterPath = foundPath;
                selection.FilterStepIndex = found;
            }
        }

        private static RulePath foundPath;

        private static int FindFilterStep(RuleExprNode node, RuleExprNode target, RulePath inside)
        {
            if (node == null) return -1;

            if (node.subject != null)
            {
                for (int i = 0; i < node.subject.StepCount; i++)
                {
                    var step = node.subject.steps[i];
                    if (step.kind == RuleStepKind.Filter && step.filter == target)
                    {
                        foundPath = node.subject;
                        return i;
                    }

                    if (step.kind == RuleStepKind.Quantify && step.filter == target)
                    {
                        foundPath = node.subject;
                        return i;
                    }

                    if (step.kind == RuleStepKind.Filter || step.kind == RuleStepKind.Quantify)
                    {
                        int hit = FindFilterStep(step.filter, target, null);
                        if (hit >= 0) return hit;
                    }
                }
            }

            for (int i = 0; i < node.ChildCount; i++)
            {
                int hit = FindFilterStep(node.children[i], target, null);
                if (hit >= 0) return hit;
            }

            return -1;
        }

        private bool IsSelected(RuleExprNode leaf, RuleClause clause, Slot slot)
        {
            if (selection == null || selection.Slot != slot) return false;

            if (leaf != null) return selection.Leaf == leaf;
            if (clause != null) return selection.Clause == clause;
            return false;
        }

        // ══ 派生 ══════════════════════════════════════════════════════

        private RuleEntityKind SubjectKind()
        {
            if (currentRule == null) return RuleEntityKind.Any;

            var info = RuleVocabularyCatalog.Current.Subject(currentRule.subjectKey);
            return info != null ? info.entityKind : RuleEntityKind.Any;
        }

        private RuleEntityKind ElementKind()
        {
            if (selection == null || selection.FilterPath == null || selection.FilterStepIndex < 0)
            {
                return RuleEntityKind.Any;
            }

            RuleValueKind kind;
            RuleEntityKind entityKind;
            RulePropertyInfo last;
            RulePathTypes.AdvanceType(selection.FilterPath, SubjectKind(), false,
                RuleEntityKind.Any, selection.FilterStepIndex + 1,
                out kind, out entityKind, out last);
            return entityKind;
        }

        /// <summary>
        /// 新建的检测叶子。**刻意不预填根**——预填了根，检查器就只会显示路径链，
        /// 「选择根实体」那张列表再也不会出现，玩家于是永远换不了主语。
        /// 空路径在句子行上显示成灰色的"选择实体…"，本来就该是"从这里开始选"。
        /// </summary>
        private static RuleExprNode NewLeaf()
        {
            return RuleExprNode.Leaf(null, null, null);
        }

        /// <summary>
        /// 新筛选器的初值：主语是「当前元素」，谓词空着。
        /// **不预设"恒真"**——空谓词在句子行上显示成灰色的"选择谓语…"，
        /// 比给一个玩家不认识的谓词更清楚他要做什么。
        /// </summary>
        private static RuleExprNode NewFilterLeaf()
        {
            return RuleExprNode.Leaf(null, null, null);
        }

        private static void AddDetectClause(Rule rule)
        {
            var leaf = NewLeaf();

            if (rule.detect == null)
            {
                rule.detect = leaf;
                return;
            }

            if (rule.detect.kind == RuleExprNodeKind.And)
            {
                rule.detect.children.Add(leaf);
                return;
            }

            // 顶层不是「且」时，把现有整棵树包进一个新的「且」——
            // A 变成 A且B，语义不变，而玩家看到的是"又加了一句"。
            var wrapper = new RuleExprNode
            {
                kind = RuleExprNodeKind.And,
                children = new List<RuleExprNode> { rule.detect, leaf }
            };
            rule.detect = wrapper;
        }

        // ══ 文本 ══════════════════════════════════════════════════════

        public static string RuleText(Rule rule)
        {
            if (rule == null) return string.Empty;

            var sb = new System.Text.StringBuilder(256);
            bool first = true;

            AppendDetect(sb, rule.detect, rule, ref first);

            if (rule.operate != null)
            {
                bool seenOperate = false;

                for (int i = 0; i < rule.operate.Count; i++)
                {
                    if (!first) sb.Append('\n');

                    if (first)
                    {
                        sb.Append("RuleCore.Edit.KwWhen".Translate());
                        first = false;
                        seenOperate = true;
                    }
                    else if (!seenOperate)
                    {
                        sb.Append("RuleCore.Edit.KwComma".Translate());
                        seenOperate = true;
                    }
                    else
                    {
                        sb.Append("RuleCore.Edit.KwAnd".Translate());
                    }

                    sb.Append(' ').Append(ClauseText(rule.operate[i], rule));
                }
            }

            if (sb.Length == 0)
            {
                return "RuleCore.Edit.ClauseEmpty".Translate();
            }

            return sb.ToString();
        }

        private static void AppendDetect(System.Text.StringBuilder sb, RuleExprNode node,
            Rule rule, ref bool first)
        {
            if (node == null) return;

            if (node.IsLeaf)
            {
                if (!first) sb.Append('\n');
                sb.Append(first ? "RuleCore.Edit.KwWhen".Translate() : "RuleCore.Edit.KwAnd".Translate());
                first = false;
                sb.Append(' ').Append(LeafText(node, rule));
                return;
            }

            for (int i = 0; i < node.ChildCount; i++)
            {
                AppendDetect(sb, node.children[i], rule, ref first);
            }
        }

        public static string LeafText(RuleExprNode leaf)
        {
            return LeafText(leaf, null);
        }

        public static string LeafText(RuleExprNode leaf, Rule rule)
        {
            if (leaf == null) return "?";

            var verb = RuleVocabularyCatalog.Current.Verb(leaf.verbKey);
            string subject = leaf.subject != null
                ? PathText(leaf.subject, rule)
                : "RuleCore.Edit.TextNoSubject".Translate().ToString();
            string predicate = verb != null
                ? Label(verb.LabelKey, verb.key)
                : "RuleCore.Edit.TextNoPredicate".Translate().ToString();
            string obj = leaf.argument != null ? OperandText(leaf.argument, rule) : null;

            return string.IsNullOrEmpty(obj)
                ? subject + " " + predicate
                : subject + " " + predicate + " " + obj;
        }

        public static string ClauseText(RuleClause clause)
        {
            return ClauseText(clause, null);
        }

        public static string ClauseText(RuleClause clause, Rule rule)
        {
            if (clause == null) return "?";

            var verb = RuleVocabularyCatalog.Current.Verb(clause.verbKey);
            string subject = clause.subject != null
                ? PathText(clause.subject, rule)
                : "RuleCore.Edit.TextNoSubject".Translate().ToString();
            string predicate = verb != null
                ? Label(verb.LabelKey, verb.key)
                : "RuleCore.Edit.TextNoPredicate".Translate().ToString();
            string obj = clause.argument != null ? OperandText(clause.argument, rule) : null;

            return string.IsNullOrEmpty(obj)
                ? subject + " " + predicate
                : subject + " " + predicate + " " + obj;
        }

        public static string PathText(RulePath path)
        {
            return PathText(path, null);
        }

        public static string PathText(RulePath path, Rule rule)
        {
            if (path == null) return "?";

            var sb = new System.Text.StringBuilder(64);
            sb.Append(RootText(path, rule));

            for (int i = 0; i < path.StepCount; i++)
            {
                var step = path.steps[i];

                if (step.kind == RuleStepKind.Property)
                {
                    var info = RuleVocabularyCatalog.Current.Property(step.propertyKey);
                    sb.Append('.').Append(info != null ? Label(info.LabelKey, info.key)
                        : "⚠" + step.propertyKey);
                }
                else if (step.kind == RuleStepKind.Filter)
                {
                    sb.Append(FilterSummary(step.filter, rule));
                }
                else if (step.kind == RuleStepKind.Quantify)
                {
                    sb.Append(Label(RuleQuantifiers.LabelKey(step.quantifier),
                        step.quantifier.ToString())).Append(FilterSummary(step.filter, rule));
                }
                else
                {
                    var info = RuleVocabulary.ReduceInfo(step.reduce);
                    sb.Append('.').Append(info != null ? Label(info.LabelKey, step.reduce.ToString())
                        : step.reduce.ToString());
                }
            }

            return sb.ToString();
        }

        public static string FilterSummary(RuleExprNode filter)
        {
            return FilterSummary(filter, null);
        }

        public static string FilterSummary(RuleExprNode filter, Rule rule)
        {
            if (filter == null) return "[ ]";

            if (filter.IsLeaf)
            {
                var verb = RuleVocabularyCatalog.Current.Verb(filter.verbKey);
                string subject = filter.subject != null ? PathText(filter.subject, rule) : "?";
                string predicate = verb != null ? Label(verb.LabelKey, verb.key) : "……";
                string obj = filter.argument != null ? OperandText(filter.argument, rule) : null;
                string body = string.IsNullOrEmpty(obj)
                    ? subject + " " + predicate
                    : subject + " " + predicate + " " + obj;

                return "[" + body + "]";
            }

            return "[" + "RuleCore.Edit.ComplexFilter".Translate() + "]";
        }

        public static string OperandText(RuleOperand operand)
        {
            return OperandText(operand, null);
        }

        public static string OperandText(RuleOperand operand, Rule rule)
        {
            if (operand == null || operand.Kind == RuleValueKind.None) return null;

            if (operand.Kind == RuleValueKind.Entity)
            {
                return operand.path != null ? PathText(operand.path, rule) : "?";
            }

            if (operand.literal.kind == RuleValueKind.Text)
            {
                string text = operand.literal.key ?? string.Empty;
                return text.Length == 0 ? null : text;
            }

            // 枚举宾语：显示当前语言的名字，而不是内部键。
            // 「身份 是 prisoner」在中文界面上必须是「身份 是 囚犯」。
            if (operand.literal.kind == RuleValueKind.Enum
                && !string.IsNullOrEmpty(operand.literal.key))
            {
                return EnumOperandText(operand.literal.key, rule);
            }

            // **还没选值的枚举宾语报成"没有值"，让槽位显示灰色的占位符。**
            //
            // 原来它会走到 `RuleValue.None.ToString()`，也就是**显示成 `(none)`**——
            // 那一串看起来像一个已经填好的值，玩家会以为宾语配好了，
            // 而底下校验还在说"还没选值"，两边对不上。
            if (operand.Kind == RuleValueKind.Enum) return null;

            return operand.literal.ToValue().ToString();
        }

        /// <summary>
        /// 把枚举键翻译成显示名。查的顺序和编辑器一致：
        /// 先按"这个键属于哪个属性的取值域"找现成的语言键，找不到再退回键名本身。
        /// </summary>
        private static string EnumOperandText(string key, Rule rule)
        {
            if (string.IsNullOrEmpty(key)) return key;

            // 身份是唯一一个"不来自 Def 表"的固定清单，直接查它。
            var categories = RulePawnFacts.CategoryOptions;
            for (int i = 0; i < categories.Length; i++)
            {
                if (categories[i].key == key)
                {
                    return Label(categories[i].labelKey, key);
                }
            }

            return key;
        }

        public static string RootName(RuleRootKind kind)
        {
            return RootName(kind, null);
        }

        public static string RootName(RuleRootKind kind, Rule rule)
        {
            switch (kind)
            {
                // 「本主体」显示成**那到底是谁**：指名就是一个名字，群体就是那个群体的名字。
                // 这一个改动让整句话从"本主体.饱食度 小于 30%"变成"小明.饱食度 小于 30%"。
                case RuleRootKind.Subject: return SubjectDisplay(rule);
                case RuleRootKind.Element: return "RuleCore.Edit.Root.Element".Translate();
                case RuleRootKind.Map: return "RuleCore.Edit.Root.Map".Translate();
                case RuleRootKind.Colonists: return "RuleCore.Edit.Root.Colonists".Translate();
                case RuleRootKind.AllMaps: return "RuleCore.Edit.Root.AllMaps".Translate();

                // PawnGroup 的名字要**那条路径**才知道（是哪一群），所以走 RootText。
                // 这里是兜底：拿到这里说明调用方没带路径，不该发生。
                case RuleRootKind.PawnGroup: return "RuleCore.Edit.Root.PawnGroup".Translate();

                default: return kind.ToString();
            }
        }

        public static string TypeTagOf(RulePropertyInfo info)
        {
            if (info == null) return "-";

            if (info.result == RuleValueKind.Number)
            {
                if (info.percent) return "RuleCore.Edit.Type.Percent".Translate();
                if (info.unit == "℃") return "RuleCore.Edit.Type.Temp".Translate();
                return "RuleCore.Edit.Type.Number".Translate();
            }

            return TypeTagOf(info.result, info.resultEntity);
        }

        public static string TypeTagOf(RuleValueKind kind)
        {
            return TypeTagOf(kind, RuleEntityKind.Any);
        }

        public static string TypeTagOf(RuleValueKind kind, RuleEntityKind entityKind)
        {
            switch (kind)
            {
                case RuleValueKind.Number: return "RuleCore.Edit.Type.Number".Translate();
                case RuleValueKind.Bool: return "RuleCore.Edit.Type.Bool".Translate();
                case RuleValueKind.Enum: return "RuleCore.Edit.Type.Enum".Translate();
                case RuleValueKind.Coord: return "RuleCore.Edit.Type.Coord".Translate();
                case RuleValueKind.Text: return "RuleCore.Edit.Type.Text".Translate();
                case RuleValueKind.Entity:
                case RuleValueKind.EntitySet: return EntityLabel(entityKind);
                default: return "RuleCore.Edit.Type.Unknown".Translate();
            }
        }

        private static string EntityLabel(RuleEntityKind kind)
        {
            string key = "RuleCore.Enum.RuleEntityKind." + kind;
            if (key.CanTranslate())
            {
                string translated = key.Translate();
                return translated;
            }
            return kind.ToString();
        }

        /// <summary>
        /// 一行的显示名。**委托给 <see cref="RuleVocabularyCatalog.LabelOf"/>**——
        /// 校验消息也走那一份，两边各写一遍迟早会漂（一个显示中文、一个念内部键）。
        /// </summary>
        public static string Label(string key, string fallback)
        {
            return RuleVocabularyCatalog.LabelOf(key, fallback);
        }
    }
}
