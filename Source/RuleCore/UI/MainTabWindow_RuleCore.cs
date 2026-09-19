using System.Collections.Generic;
using System.Text;
using UnityEngine;
using Verse;
using RimWorld;
using RuleCore.Core;

namespace RuleCore
{
    /// <summary>
    /// 指令核心主页面。挂在屏幕下方主按钮栏（MainButtonDef order=5），与「建筑」同级。
    ///
    /// 三栏，从左到右是"选 → 改 → 看"：
    ///   · 左栏 = 规则列表（内置 / 我的，各自打徽章）
    ///   · 中栏 = 表单编辑器（选中规则的线性视图，改完立刻生效）
    ///   · 右栏 = 调试时间线（引擎的结构化记录）
    ///
    /// 为什么编辑器不做成独立标签页：**改规则和看它有没有生效是同一个动作的两半**。
    /// 分成两页就得来回切，而且切换本身会打断"我刚才改的是哪条"的记忆。
    /// 三栏并排，改动和它的后果在同一屏上。
    ///
    /// 时间线刻意采用「按游标拉取」而不是订阅事件：主标签页会被反复开关，
    /// 拉取天然幂等，不会因为漏掉一次退订而留下悬挂订阅。
    /// 订阅式推送留给引擎内部的条件重评估使用。
    /// </summary>
    public class MainTabWindow_RuleCore : MainTabWindow
    {
        private const float TopBarHeight = 30f;
        private const float Gap = 6f;
        private const float RulePaneWidth = 200f;

        /// <summary>
        /// 编辑器栏。**比原来宽得多**：它内部是"句子 + 检查器"两栏，
        /// 400 宽时句子只剩 214 像素，子句盒子会挤成一团——
        /// 而句子的可读性就是整个编辑器的意义所在。
        /// </summary>
        private const float FormPaneWidth = 700f;
        private const float MinRowHeight = 22f;
        private const float BottomPadding = 4f;
        private const float ScrollBarAllowance = 20f;
        private const float RuleRowHeight = 40f;
        private const float RuleRowButtonWidth = 44f;

        private static readonly Color SelectedRowColor = new Color(0.26f, 0.45f, 0.70f, 0.55f);

        private readonly RuleEditorView editorView = new RuleEditorView();
        private readonly List<RuleLogRecord> timeline = new List<RuleLogRecord>();

        /// <summary>当前选中的记录序号；-1 表示未选中。存 Seq 而不是下标——缓冲淘汰会让下标漂移。</summary>
        private long selectedSeq = -1L;

        /// <summary>
        /// 选中的规则**存 id 而不是引用**：规则可能被删、被克隆、被存档重载换掉实例，
        /// 存引用迟早会指向一个已经不在库里的孤儿。
        /// </summary>
        private string selectedRuleId;

        // 布局缓存：行高随文本折行变化，不能写死。只在内容或宽度变化时重建。
        private readonly List<int> visibleRows = new List<int>();
        private readonly List<float> rowHeights = new List<float>();
        private readonly List<string> rowTexts = new List<string>();
        private float layoutTotalHeight;
        private float lastLayoutWidth;
        private float layoutWidthCached = -1f;
        private bool layoutValid;
        private int layoutCount;
        private long layoutLastSeq;
        private bool layoutShowTrace;

        private Vector2 timelineScroll;
        private Vector2 ruleScroll;
        private long lastSeq;
        private bool followLatest = true;
        private bool showTrace = true;

        /// <summary>
        /// 面板大小。**用屏幕尺寸算，不写死**：这个面板要同时装下
        /// 「规则列表 + 句子 + 检查器 + 时间线」四栏，写死 1280 在小屏上溢出、
        /// 在大屏上白白浪费一半。原版会把超出屏幕的部分夹住，所以取屏幕减一圈边距。
        /// </summary>
        public override Vector2 RequestedTabSize
        {
            get
            {
                float width = Mathf.Min(1680f, Mathf.Max(UI.screenWidth - 40f, 640f));
                float height = Mathf.Min(980f, Mathf.Max(UI.screenHeight - 120f, 480f));
                return new Vector2(width, height);
            }
        }

        public override void PostOpen()
        {
            base.PostOpen();

            timeline.Clear();
            var existing = RuleLog.Snapshot();
            for (int i = 0; i < existing.Length; i++)
            {
                timeline.Add(existing[i]);
            }
            lastSeq = RuleLog.LastSeq;
            InvalidateLayout();
            timelineScroll = new Vector2(0f, 999999f);
        }


        public override void PostClose()
        {
            // 关面板是最容易丢改动的一刻（防抖窗口可能还没到期）。
            RuleLibrary.FlushPendingEdits();
            base.PostClose();
        }

        public override void DoWindowContents(Rect inRect)
        {
            // 四栏里到处都会临时改 Text.WordWrap（"这一行不折行"），
            // 而它们各自的 return 路径不少。**一处漏掉就是每帧一条原版报错**
            // （"Word wrap was false at end of frame" + 整条调用栈）。
            // 在唯一的入口上兜一次，比在每处手写还原可靠。
            using (new TextStateScope())
            {
                DrawContents(inRect);
            }
        }

        private void DrawContents(Rect inRect)
        {
            PullNewRecords();

            // 防抖落盘由面板驱动。用真实时间：编辑时游戏多半是暂停的。
            RuleLibrary.TickAutosave(Time.realtimeSinceStartup);


            var topBar = new Rect(inRect.x, inRect.y, inRect.width, TopBarHeight);
            DrawTopBar(topBar);

            float bodyY = inRect.y + TopBarHeight + Gap;
            float bodyHeight = inRect.height - TopBarHeight - Gap - BottomPadding;

            // 编辑器栏按比例给：屏幕大就多给，屏幕小就少给。
            // 它是四栏里唯一"内容宽度不固定"的一栏（句子会折、检查器要给标签留位置）。
            float formWidth = Mathf.Clamp(inRect.width * 0.54f, 420f, 900f);

            var leftPane = new Rect(inRect.x, bodyY, RulePaneWidth, bodyHeight);
            var formPane = new Rect(leftPane.xMax + Gap, bodyY, formWidth, bodyHeight);
            // 屏幕小、或原版把标签页夹窄时剩下的宽度会是负数——
            // 负宽度的 Rect 会让 IMGUI 每帧报错。宁可时间线只剩一条缝，也不报错。
            float timelineWidth = Mathf.Max(
                inRect.width - RulePaneWidth - formWidth - Gap * 2f, 0f);
            var rightPane = new Rect(formPane.xMax + Gap, bodyY, timelineWidth, bodyHeight);

            DrawRulePane(leftPane);
                        var rule = SelectedRule;
            editorView.ClearChanged();
            editorView.Draw(formPane, rule);

            // 编辑器直接改内存里的那个对象 —— 引擎下一个采样点读到的就是新值。
            // 这里只负责把改动反映到"归一化 + 落盘"上：
            // 结构改动立刻落盘（它改变权限层级与边沿语义），值改动防抖合并。
            if (rule != null && rule.IsEditable)
            {
                if (editorView.StructureChanged)
                {
                    RuleLibrary.NotifyStructureChanged(rule);
                }
                else if (editorView.ValueChanged)
                {
                    RuleLibrary.NotifyValueChanged(rule);
                }
            }
            if (timelineWidth > 60f)
            {
                DrawTimelinePane(rightPane);
            }
        }

        /// <summary>按 id 找回选中的规则。被删掉时自然返回 null。</summary>
        private Rule SelectedRule
        {
            get { return RuleLibrary.Find(selectedRuleId); }
        }

        // ── 时间线数据 ────────────────────────────────────────────────

        private void PullNewRecords()
        {
            var fresh = RuleLog.Since(lastSeq);
            if (fresh.Length == 0)
            {
                return;
            }

            for (int i = 0; i < fresh.Length; i++)
            {
                timeline.Add(fresh[i]);
                lastSeq = fresh[i].Seq;
            }

            int cap = RuleLog.Capacity;
            if (timeline.Count > cap)
            {
                timeline.RemoveRange(0, timeline.Count - cap);
            }
        }

        private void InvalidateLayout()
        {
            layoutValid = false;
        }

        /// <summary>
        /// 先量后排：每行的高度由折行后的实际文本高度决定。
        /// 写死行高会让折行的长消息压到下一行上——这正是要修的那个 bug。
        ///
        /// 缓存键包含内容特征（条数 + 日志汇最大序号），因为相邻合并会原地修改
        /// RepeatCount 而不新增条目，只看条数会漏掉这种变化。
        /// </summary>
        private void EnsureLayout(float width)
        {
            lastLayoutWidth = width;

            if (layoutValid
                && Mathf.Approximately(layoutWidthCached, width)
                && layoutShowTrace == showTrace
                && layoutCount == timeline.Count
                && layoutLastSeq == RuleLog.LastSeq)
            {
                return;
            }

            layoutValid = true;
            layoutWidthCached = width;
            layoutShowTrace = showTrace;
            layoutCount = timeline.Count;
            layoutLastSeq = RuleLog.LastSeq;

            visibleRows.Clear();
            rowHeights.Clear();
            rowTexts.Clear();

            var previousFont = Text.Font;
            var previousWrap = Text.WordWrap;
            Text.Font = GameFont.Tiny;
            Text.WordWrap = true;

            float usableWidth = Mathf.Max(width, 1f);
            float total = 0f;

            for (int i = 0; i < timeline.Count; i++)
            {
                var record = timeline[i];
                if (!showTrace && record.Level == RuleLogLevel.Trace)
                {
                    continue;
                }

                string line = FormatRow(record);
                float height = Mathf.Max(MinRowHeight, Text.CalcHeight(line, usableWidth));

                visibleRows.Add(i);
                rowHeights.Add(height);
                rowTexts.Add(line);
                total += height;
            }

            layoutTotalHeight = total;

            Text.Font = previousFont;
            Text.WordWrap = previousWrap;
        }

        // ── 顶栏 ──────────────────────────────────────────────────────

        private void DrawTopBar(Rect rect)
        {
            Text.Anchor = TextAnchor.MiddleLeft;
            Text.WordWrap = false;

            Text.Font = GameFont.Medium;
            Widgets.Label(new Rect(rect.x, rect.y, 160f, rect.height), "RuleCore.Panel.Title".Translate());

            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(rect.x + 160f, rect.y, 420f, rect.height),
                "RuleCore.Panel.Stats".Translate(RuleLog.Count, RuleLog.Capacity, RuleLog.Dropped, RuleLog.LastSeq));

            // 右侧控制区，从右往左排。
            float x = rect.xMax - 56f;
            if (Widgets.ButtonText(new Rect(x, rect.y + 4f, 52f, 22f), "RuleCore.Panel.Clear".Translate()))
            {
                int capacity = RuleCoreMod.Settings != null ? RuleCoreMod.Settings.logCapacity : RuleLog.DefaultCapacity;
                RuleLog.Boot(capacity);
                timeline.Clear();
                lastSeq = 0L;
                InvalidateLayout();
            }

            x -= 56f;
            if (Widgets.ButtonText(new Rect(x, rect.y + 4f, 52f, 22f), "RuleCore.Panel.SelfTest".Translate()))
            {
                EmitSelfTest();
            }

            x -= 76f;
            bool hasSelection = FindSelected() != null;
            var previousColor = GUI.color;
            if (!hasSelection)
            {
                GUI.color = new Color(1f, 1f, 1f, 0.45f);
            }
            bool copySelectedClicked = Widgets.ButtonText(new Rect(x, rect.y + 4f, 72f, 22f),
                "RuleCore.Panel.CopySelected".Translate());
            GUI.color = previousColor;
            if (copySelectedClicked)
            {
                CopySelectedToClipboard();
            }

            x -= 56f;
            if (Widgets.ButtonText(new Rect(x, rect.y + 4f, 52f, 22f), "RuleCore.Panel.Copy".Translate()))
            {
                CopyVisibleToClipboard();
            }

            x -= 104f;
            Widgets.CheckboxLabeled(new Rect(x, rect.y + 4f, 100f, 22f),
                "RuleCore.Panel.FollowLatest".Translate(), ref followLatest);

            x -= 104f;
            Widgets.CheckboxLabeled(new Rect(x, rect.y + 4f, 100f, 22f),
                "RuleCore.Panel.ShowTrace".Translate(), ref showTrace);


            // 未落盘提示。只在真有改动时出现，避免常驻噪声。
            if (RuleLibrary.HasPendingEdits)
            {
                x -= 78f;
                var pendingColor = GUI.color;
                GUI.color = new Color(1f, 0.85f, 0.40f);
                Widgets.Label(new Rect(x, rect.y, 74f, rect.height), "RuleCore.Panel.Pending".Translate());
                GUI.color = pendingColor;
            }

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        /// <summary>
        /// IMGUI 的文本选不中，所以给一个剪贴板出口。
        /// 复制的是**当前可见**的行（受「显示 Trace」过滤影响），内容与屏幕上看到的一致，
        /// 一行一条，方便直接贴进 bug 报告。
        /// </summary>
        private void CopyVisibleToClipboard()
        {
            layoutValid = false;
            EnsureLayout(lastLayoutWidth > 0f ? lastLayoutWidth : 900f);

            var sb = new StringBuilder(4096);
            for (int k = 0; k < visibleRows.Count; k++)
            {
                sb.Append(rowTexts[k]).Append('\n');
            }

            string text = sb.ToString();
            try
            {
                GUIUtility.systemCopyBuffer = text;
                Messages.Message("RuleCore.Panel.Copied".Translate(visibleRows.Count),
                    MessageTypeDefOf.NeutralEvent, false);
            }
            catch (System.Exception ex)
            {
                RuleLog.Error(null, "Panel", RuleEvalStatus.Error, "clipboard.failed", null,
                    "写入剪贴板失败：" + ex.Message);
            }
        }

        /// <summary>按序号找选中的记录。被缓冲淘汰后自然返回 null。</summary>
        private RuleLogRecord FindSelected()
        {
            if (selectedSeq < 0) return null;
            for (int i = 0; i < timeline.Count; i++)
            {
                if (timeline[i].Seq == selectedSeq) return timeline[i];
            }
            return null;
        }

        /// <summary>
        /// 只复制选中的那一条。优先用面板里已经渲染好的文本（与屏幕上完全一致）；
        /// 若该行被「显示 Trace」过滤掉了，就现渲染一份。
        /// </summary>
        private void CopySelectedToClipboard()
        {
            var record = FindSelected();
            if (record == null)
            {
                Messages.Message("RuleCore.Panel.NoSelection".Translate(),
                    MessageTypeDefOf.RejectInput, false);
                return;
            }

            string text = null;
            for (int k = 0; k < visibleRows.Count; k++)
            {
                if (timeline[visibleRows[k]].Seq == record.Seq)
                {
                    text = rowTexts[k];
                    break;
                }
            }
            if (text == null)
            {
                text = FormatRow(record);
            }

            try
            {
                GUIUtility.systemCopyBuffer = text;
                Messages.Message("RuleCore.Panel.CopiedOne".Translate(record.Seq),
                    MessageTypeDefOf.NeutralEvent, false);
            }
            catch (System.Exception ex)
            {
                RuleLog.Error(null, "Panel", RuleEvalStatus.Error, "clipboard.failed", null,
                    "写入剪贴板失败：" + ex.Message);
            }
        }

        /// <summary>
        /// 引擎自证。
        /// 前半是四个级别的样子——颜色、Trace 过滤、相邻合并、清空四条路径的可见证明。
        /// 后半真的跑一遍契约层自检：跑的是真实代码路径而不是手写数据，
        /// 失败项以红行出现，通过项不吵人。
        /// </summary>
        private void EmitSelfTest()
        {
            RuleLog.Trace(null, "SelfTest", "Trace", "trace_sample", null,
                "【样例】Trace：逐次求值记录，受「详细日志」设置门控");
            RuleLog.Info(null, "SelfTest", "Info", "info_sample", "张三",
                "【样例】Info：正常下发动作");
            RuleLog.Warn(null, "SelfTest", "Warn", "warn_sample", "李四",
                "【样例】Warn：能力不匹配，交回原版 AI");
            RuleLog.Error(null, "SelfTest", "Error", "error_sample", null,
                "【样例】Error：动作抛出异常——这条是刻意发的样例，用来展示红色，不是真的出错");

            var results = RuleCoreSelfTest.Run();
            for (int i = 0; i < results.Count; i++)
            {
                var result = results[i];
                if (!result.Passed)
                {
                    RuleLog.Error(null, "SelfTest", RuleEvalStatus.Failed, "assert_failed", null,
                        result.Name + " | " + result.Detail);
                }
            }

            int passed = RuleCoreSelfTest.CountPassed(results);
            bool allOk = passed == results.Count;
            RuleLog.Info(null, "SelfTest", RuleEvalStatus.SelfTest,
                allOk ? "all_passed" : "some_failed", null,
                "契约层自检 " + passed + "/" + results.Count
                + (allOk
                    ? " 全部通过（上方【样例】行是刻意发的，不是真出错）"
                    : " —— 有失败项，见上方红行"));
        }

        // ── 左栏：规则列表 ────────────────────────────────────────────

        private void DrawRulePane(Rect rect)
        {
            Widgets.DrawMenuSection(rect);

            var inner = rect.ContractedBy(8f);
            var rules = RuleLibrary.All;

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
            // 规则行高写死，所以两行文字都必须单行显示；装不下用省略号，不许折行。
            Text.WordWrap = false;

            var header = new Rect(inner.x, inner.y, inner.width, 24f);
            Widgets.Label(new Rect(header.x, header.y, header.width - 84f, header.height),
                "RuleCore.Panel.RulesHeader".Translate(rules.Count));

            if (Widgets.ButtonText(new Rect(header.xMax - 80f, header.y, 80f, 22f),
                    "RuleCore.Panel.NewRule".Translate()))
            {
                // 新建之后立刻选中它——刚建好的规则就是要马上改的那条。
                var created = RuleLibrary.CreatePlayerRule(null);
                if (created != null)
                {
                    selectedRuleId = created.id;
                }
            }

            var body = new Rect(inner.x, inner.y + 28f, inner.width, inner.height - 28f);

            if (rules.Count == 0)
            {
                Widgets.Label(body, "RuleCore.Panel.NoRules".Translate());
                return;
            }

            var viewRect = new Rect(0f, 0f, body.width - 16f, rules.Count * RuleRowHeight);
            Widgets.BeginScrollView(body, ref ruleScroll, viewRect);

            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.UpperLeft;

            float y = 0f;
            for (int i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                var rowRect = new Rect(0f, y, viewRect.width, RuleRowHeight - 2f);
                bool isSelected = selectedRuleId != null && rule.id == selectedRuleId;

                if (isSelected)
                {
                    Widgets.DrawBoxSolid(rowRect, SelectedRowColor);
                }
                else if (Mouse.IsOver(rowRect))
                {
                    Widgets.DrawHighlight(rowRect);
                }

                bool playerRule = rule.IsPlayerRule;
                float textWidth = playerRule ? rowRect.width - RuleRowButtonWidth - 4f : rowRect.width;

                var previous = GUI.color;
                if (!rule.enabled)
                {
                    GUI.color = new Color(1f, 1f, 1f, 0.45f);
                }
                Widgets.LabelEllipses(new Rect(rowRect.x, rowRect.y, textWidth, 16f), rule.DisplayLabel);
                GUI.color = previous;

                // 玩家规则用自己的颜色，一眼能和内置区分开。
                GUI.color = playerRule
                    ? new Color(0.55f, 0.85f, 1f)
                    : (rule.RequiresDeveloperTier ? new Color(1f, 0.72f, 0.35f) : new Color(0.68f, 0.68f, 0.68f));
                Widgets.LabelEllipses(new Rect(rowRect.x, rowRect.y + 16f, textWidth, 16f),
                    "RuleCore.Panel.RuleLine".Translate(
                        (playerRule ? "RuleCore.Origin.Player" : "RuleCore.Origin.Mod").Translate(),
                        ScopeText(rule),
                        TierText(rule),
                        rule.operate.Count));
                GUI.color = previous;

                if (playerRule)
                {
                    float buttonX = rowRect.xMax - RuleRowButtonWidth;

                    if (Widgets.ButtonText(new Rect(buttonX, rowRect.y + 2f, RuleRowButtonWidth, 18f),
                            (rule.enabled ? "RuleCore.Panel.Disable" : "RuleCore.Panel.Enable").Translate()))
                    {
                        RuleLibrary.SetPlayerRuleEnabled(rule.id, !rule.enabled);
                    }

                    if (Widgets.ButtonText(new Rect(buttonX, rowRect.y + 20f, RuleRowButtonWidth, 18f),
                            "RuleCore.Panel.Delete".Translate()))
                    {
                        string deletedId = rule.id;
                        RuleLibrary.RemovePlayerRule(deletedId);
                        if (selectedRuleId == deletedId)
                        {
                            selectedRuleId = null;
                        }
                        // 本帧的列表已经过期，停止绘制剩下的行。
                        break;
                    }
                }

                // 选中区只覆盖文本部分，免得点按钮时把选中也一起触发。
                if (Widgets.ButtonInvisible(new Rect(rowRect.x, rowRect.y, textWidth, rowRect.height)))
                {
                    selectedRuleId = isSelected ? null : rule.id;
                }

                y += RuleRowHeight;
            }

            Widgets.EndScrollView();

            Text.Font = GameFont.Small;
            Text.WordWrap = true;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        // ── 右栏：调试时间线 ──────────────────────────────────────────

        private void DrawTimelinePane(Rect rect)
        {
            Widgets.DrawMenuSection(rect);

            var inner = rect.ContractedBy(6f);
            float contentWidth = Mathf.Max(inner.width - ScrollBarAllowance, 1f);

            EnsureLayout(contentWidth);

            var viewRect = new Rect(0f, 0f, contentWidth, Mathf.Max(layoutTotalHeight, inner.height));

            if (followLatest)
            {
                timelineScroll = new Vector2(0f, 999999f);
            }

            Widgets.BeginScrollView(inner, ref timelineScroll, viewRect);

            Text.Font = GameFont.Tiny;
            Text.Anchor = TextAnchor.UpperLeft;
            Text.WordWrap = true;

            float y = 0f;
            for (int k = 0; k < visibleRows.Count; k++)
            {
                var record = timeline[visibleRows[k]];
                float height = rowHeights[k];
                var rowRect = new Rect(0f, y, contentWidth, height);

                bool selected = selectedSeq >= 0 && record.Seq == selectedSeq;

                if (selected)
                {
                    Widgets.DrawBoxSolid(rowRect, SelectedRowColor);
                }
                else if (Mouse.IsOver(rowRect))
                {
                    Widgets.DrawHighlight(rowRect);
                }

                var previous = GUI.color;
                GUI.color = ColorFor(record.Level);
                Widgets.Label(rowRect, rowTexts[k]);
                GUI.color = previous;

                // 点一次选中，再点一次取消。
                if (Widgets.ButtonInvisible(rowRect))
                {
                    selectedSeq = selected ? -1L : record.Seq;
                }

                y += height;
            }

            Widgets.EndScrollView();

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        private static string FormatRow(RuleLogRecord record)
        {
            var sb = new StringBuilder(140);
            sb.Append('#').Append(record.Seq);
            sb.Append("  t=").Append(record.Tick);
            sb.Append("  [").Append(LevelTag(record.Level)).Append(']');

            if (record.RepeatCount > 1)
            {
                sb.Append(" x").Append(record.RepeatCount);
            }

            Append(sb, "  ", record.RuleId);
            Append(sb, " · ", record.StepId);
            Append(sb, " · ", record.Outcome);
            Append(sb, " · ", record.ReasonCode);
            Append(sb, "  @", record.Entity);

            if (!string.IsNullOrEmpty(record.Message))
            {
                sb.Append("  | ").Append(record.Message);
            }

            return sb.ToString();
        }

        private static void Append(StringBuilder sb, string separator, string value)
        {
            if (string.IsNullOrEmpty(value))
            {
                return;
            }
            sb.Append(separator).Append(value);
        }

        private static string LevelTag(RuleLogLevel level)
        {
            switch (level)
            {
                case RuleLogLevel.Error: return "ERR ";
                case RuleLogLevel.Warn: return "WARN";
                case RuleLogLevel.Trace: return "TRC ";
                default: return "INFO";
            }
        }

        private static Color ColorFor(RuleLogLevel level)
        {
            switch (level)
            {
                case RuleLogLevel.Error: return new Color(1f, 0.42f, 0.42f);
                case RuleLogLevel.Warn: return new Color(1f, 0.85f, 0.40f);
                case RuleLogLevel.Trace: return new Color(0.62f, 0.62f, 0.62f);
                default: return Color.white;
            }
        }

        // ── 列表行的枚举显示名 ────────────────────────────────────────
        //
        // 键的形状是 RuleCore.Enum.<枚举类型>.<成员名>。查不到就退回成员名——
        // 显示英文标识符总比显示一个键名强，而且"哪个键缺了"一眼能看出来。

        private static string TierText(Rule rule)
        {
            string key = "RuleCore.Enum.RuleTier." + rule.EffectiveTier;
            if (key.CanTranslate())
            {
                string translated = key.Translate();
                return translated;
            }
            return rule.EffectiveTier.ToString();
        }

        private static string ScopeText(Rule rule)
        {
            // 走和编辑器同一套显示：**指名绑定显示那个人的名字**。
            // 列表里写着"本主体"的话，玩家看不出这条规则到底锁在谁身上，
            // 而那正是"规则明明写得对却什么都不做"最常见的成因。
            if (rule == null || string.IsNullOrEmpty(rule.subjectKey))
            {
                return "RuleCore.Inspect.NoSubject".Translate();
            }

            return RuleEditorView.SubjectDisplay(rule);
        }    }
}
