using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;
using RimWorld;

namespace RuleCore
{
    /// <summary>
    /// 带搜索的**小人选择器** —— "本主体 = 指定的某个人"点进去的那一步。
    ///
    /// 为什么不是 <see cref="FloatMenu"/>：殖民地在场的人动辄几十个（殖民者 + 囚犯 +
    /// 访客 + 机械族），菜单一屏放不下、也不能搜索。而这个窗口是**唯一的**
    /// 指名入口——玩家在这里找不到人，那条规则就写不出来。
    ///
    /// 形状与 <see cref="Dialog_DefPicker"/> 刻意保持一致（搜索框 + 高亮当前 + 回车选中），
    /// 因为这两个窗口是同一件事的两种候选：**在一份可能很长的清单里挑一个**。
    /// </summary>
    public class Dialog_PawnPicker : Window
    {
        private const float RowHeight = 26f;
        private const float SearchHeight = 28f;
        private const string SearchControlName = "RuleCorePawnPickerSearch";

        private readonly List<Pawn> all;
        private readonly string currentId;
        private readonly Action<Pawn> onPick;

        private readonly List<Pawn> filtered = new List<Pawn>();

        private string search = string.Empty;
        private Vector2 scroll;
        private bool focusPending = true;

        public Dialog_PawnPicker(List<Pawn> candidates, string currentThingId, Action<Pawn> onPick)
        {
            all = candidates ?? new List<Pawn>();
            currentId = currentThingId;
            this.onPick = onPick;

            closeOnCancel = true;
            doCloseX = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
            onlyOneOfTypeAllowed = true;
            forcePause = false;
            draggable = true;
            resizeable = false;
            optionalTitle = "RuleCore.Picker.PawnTitle".Translate();

            // 排序在构造期做一次：**按身份分组再按名字**。
            // 玩家想找的人在心里有一个"他是谁"，先按那个排比按字母排更接近他的找法。
            all.Sort(Compare);
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(420f, 520f); }
        }

        public override void PreOpen()
        {
            base.PreOpen();
            RebuildFiltered();
        }

        private static int Compare(Pawn a, Pawn b)
        {
            if (a == null) return b == null ? 0 : 1;
            if (b == null) return -1;

            // 自己人排前面：要指名的多半是自己人。
            int rankA = RankOf(a);
            int rankB = RankOf(b);
            if (rankA != rankB) return rankA - rankB;

            return string.Compare(a.LabelShort, b.LabelShort, StringComparison.CurrentCulture);
        }

        private static int RankOf(Pawn pawn)
        {
            if (pawn.IsFreeColonist) return 0;
            if (pawn.IsPrisonerOfColony) return 1;
            if (pawn.IsSlaveOfColony) return 2;
            if (pawn.IsColonyMech) return 3;
            if (pawn.IsColonyAnimal) return 4;
            return 5;
        }

        public override void DoWindowContents(Rect inRect)
        {
            // 列表里每一行都是"不许折行"，整段套一层状态还原——见 TextStateScope：
            // 漏了它原版会记一条 "Word wrap was false at end of frame"（带调用栈），
            // 并且**把 WordWrap 强行改回 true**，于是下一帧这个列表就开始折行。
            using (new TextStateScope())
            {
                DrawContents(inRect);
            }
        }

        private void DrawContents(Rect inRect)
        {
            var searchRect = new Rect(0f, 0f, inRect.width, SearchHeight);

            GUI.SetNextControlName(SearchControlName);
            string edited = Widgets.TextField(searchRect, search);
            if (edited != search)
            {
                search = edited;
                RebuildFiltered();
                scroll = Vector2.zero;
            }

            if (focusPending)
            {
                focusPending = false;
                GUI.FocusControl(SearchControlName);
            }

            HandleKeys();

            var listRect = new Rect(0f, searchRect.yMax + 4f, inRect.width,
                inRect.height - searchRect.height - 4f);

            DrawList(listRect);
        }

        private void HandleKeys()
        {
            var evt = Event.current;
            if (evt == null || evt.type != EventType.KeyDown) return;

            if (evt.keyCode == KeyCode.Escape)
            {
                Close(true);
                evt.Use();
                return;
            }

            if ((evt.keyCode == KeyCode.Return || evt.keyCode == KeyCode.KeypadEnter)
                && filtered.Count > 0)
            {
                Pick(filtered[0]);
                evt.Use();
            }
        }

        private void DrawList(Rect rect)
        {
            Widgets.DrawMenuSection(rect);
            var inner = rect.ContractedBy(4f);

            if (filtered.Count == 0)
            {
                Text.Font = GameFont.Small;
                Text.Anchor = TextAnchor.UpperLeft;
                Text.WordWrap = false;
                Widgets.Label(inner, "RuleCore.Picker.NoMatch".Translate());
                return;
            }

            float contentWidth = Mathf.Max(inner.width - 18f, 1f);
            var viewRect = new Rect(0f, 0f, contentWidth, filtered.Count * RowHeight);

            Widgets.BeginScrollView(inner, ref scroll, viewRect);

            // 裁剪到可见行再画：几百行虽会被 IMGUI 自己 clip，
            // 但那是几百次 Widgets 调用每帧都跑一遍，完全没必要。
            int first = Mathf.Max(0, Mathf.FloorToInt(scroll.y / RowHeight));
            int last = Mathf.Min(filtered.Count - 1,
                Mathf.CeilToInt((scroll.y + inner.height) / RowHeight));

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            Text.WordWrap = false;

            for (int i = first; i <= last; i++)
            {
                Pawn pawn = filtered[i];
                var rowRect = new Rect(0f, i * RowHeight, contentWidth, RowHeight - 1f);
                bool isCurrent = pawn != null && pawn.ThingID == currentId;

                if (isCurrent)
                {
                    Widgets.DrawBoxSolid(rowRect, new Color(0.26f, 0.45f, 0.70f, 0.55f));
                }
                else if (Mouse.IsOver(rowRect))
                {
                    Widgets.DrawHighlight(rowRect);
                }

                string label = (isCurrent ? "✔ " : string.Empty) + LabelFor(pawn);

                var captured = pawn;
                if (Widgets.ButtonInvisible(rowRect))
                {
                    Pick(captured);
                    break;
                }

                // 文字后画：ButtonInvisible 会吃掉行区域的高亮，先画字就被盖住了。
                Widgets.LabelEllipses(rowRect, label);
            }

            Widgets.EndScrollView();

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        /// <summary>一行里同时给"名字"和"他是谁" —— 界面上的每个名字都该带着它的身份。</summary>
        private static string LabelFor(Pawn pawn)
        {
            if (pawn == null) return "?";

            string key = RulePawnFacts.CategoryKeyOf(pawn);
            string category = RuleEditorView.Label(RulePawnFacts.CategoryLabelKey(key), key);

            return pawn.LabelShort + "   (" + category + ")";
        }

        private void Pick(Pawn pawn)
        {
            if (onPick != null && pawn != null)
            {
                onPick(pawn);
            }
            Close(true);
        }

        private void RebuildFiltered()
        {
            filtered.Clear();

            if (string.IsNullOrEmpty(search))
            {
                filtered.AddRange(all);
                return;
            }

            for (int i = 0; i < all.Count; i++)
            {
                var pawn = all[i];
                if (pawn == null) continue;

                // 同时匹配名字和身份：玩家有时记得"小明"，有时只记得"那个囚犯"。
                if (Matches(pawn.LabelShort, search) || Matches(pawn.Name != null
                        ? pawn.Name.ToStringFull : null, search)
                    || Matches(pawn.def != null ? pawn.def.defName : null, search)
                    || Matches(RulePawnFacts.CategoryKeyOf(pawn), search))
                {
                    filtered.Add(pawn);
                }
            }
        }

        private static bool Matches(string haystack, string needle)
        {
            return !string.IsNullOrEmpty(haystack)
                && haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
