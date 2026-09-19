using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RuleCore
{
    /// <summary>
    /// 带搜索的 Def 选择器 —— 给"可能上千个候选"的字段用。
    ///
    /// <see cref="RulePathEditor"/> 默认给枚举宾语铺一个 <see cref="FloatMenu"/>，
    /// 那对 <c>LetterDef</c>（十来个）是合适的，对 <c>ThingDef</c>（两千多个）是灾难：
    /// 菜单一屏放不下、滚到一半找不到、而且每次点击都要重建整个列表。
    ///
    /// 所以按候选数量分流，分界线是"一屏能不能扫完"，不是"类型是不是 Def"。
    /// 上一版在这个分界处只做了个"只列前 60 个"的兜底——那等于**告诉玩家答案存在但不给他**，
    /// 比不给还糟。这一版把它补成真正的选择器。
    ///
    /// 列表按可见行裁剪后再画：两千行不裁剪的话，虽然 IMGUI 会自己 clip，
    /// 但两千次 Widgets 调用每帧都跑一遍是完全不必要的开销。
    /// </summary>
    public class Dialog_DefPicker : Window
    {
        private const float RowHeight = 24f;
        private const float SearchHeight = 28f;
        private const string SearchControlName = "RuleCoreDefPickerSearch";

        private readonly Type defType;
        private readonly string currentDefName;
        private readonly Action<Def> onPick;

        private readonly List<Def> all = new List<Def>();
        private readonly List<Def> filtered = new List<Def>();

        private string search = string.Empty;
        private Vector2 scroll;
        private bool focusPending = true;

        public Dialog_DefPicker(Type defType, string currentDefName, Action<Def> onPick)
        {
            this.defType = defType;
            this.currentDefName = currentDefName;
            this.onPick = onPick;

            closeOnCancel = true;
            doCloseX = true;
            closeOnClickedOutside = false;
            absorbInputAroundWindow = true;
            onlyOneOfTypeAllowed = true;
            // 刻意不 forcePause：让时间继续走，选择的时候能看到世界在动。
            forcePause = false;
            draggable = true;
            resizeable = false;
            optionalTitle = "RuleCore.Picker.Title".Translate(defType != null ? defType.Name : "Def");
        }

        /// <summary>Def 列表缓存。同一类型只查一次——两千项的 ThingDef 每次点开都重查是白烧。</summary>
        private static readonly Dictionary<Type, List<Def>> defCache =
            new Dictionary<Type, List<Def>>();

        /// <summary>
        /// 某个 Def 类型下的全部候选。**查询失败返回空列表而不是抛异常**：
        /// 该类型没有实例化过 DefDatabase 是可能的（类型写错、Def 卸载），
        /// 那不该让编辑器每帧报错。
        /// </summary>
        public static List<Def> DefsOf(Type defType)
        {
            List<Def> cached;
            if (defType != null && defCache.TryGetValue(defType, out cached))
            {
                return cached;
            }

            var defs = new List<Def>();
            if (defType != null)
            {
                try
                {
                    foreach (Def def in GenDefDatabase.GetAllDefsInDatabaseForDef(defType))
                    {
                        if (def != null) defs.Add(def);
                    }
                }
                catch (Exception)
                {
                    defs.Clear();
                }
            }

            defs.Sort(delegate(Def a, Def b)
            {
                return string.CompareOrdinal(a.defName, b.defName);
            });

            if (defType != null) defCache[defType] = defs;
            return defs;
        }

        public override Vector2 InitialSize
        {
            get { return new Vector2(460f, 540f); }
        }

        public override void PreOpen()
        {
            base.PreOpen();

            all.Clear();
            all.AddRange(DefsOf(defType));

            // 当前值排在最前：打开选择器最常见的心情是"我看看现在选的是哪个"。
            string current = currentDefName;
            all.Sort(delegate(Def a, Def b)
            {
                bool aCurrent = a != null && a.defName == current;
                bool bCurrent = b != null && b.defName == current;
                if (aCurrent != bCurrent) return aCurrent ? -1 : 1;
                return string.CompareOrdinal(a != null ? a.defName : string.Empty,
                    b != null ? b.defName : string.Empty);
            });

            RebuildFiltered();
        }

        public override void DoWindowContents(Rect inRect)
        {
            // 列表里每一行都是"不许折行"，所以整段必须套一层状态还原——
            // 见 TextStateScope：漏了它原版会记一条 "Word wrap was false at end of frame"，
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

            // 打开时把焦点给搜索框：这个窗口存在的意义就是"打字过滤"，
            // 让玩家还要先用鼠标点一下输入框是没道理的。
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

            // 回车选中第一条——"打字 → 回车"是搜索框最自然的用法。
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

            int first = Mathf.Max(0, Mathf.FloorToInt(scroll.y / RowHeight));
            int last = Mathf.Min(filtered.Count - 1,
                Mathf.CeilToInt((scroll.y + inner.height) / RowHeight));

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.MiddleLeft;
            Text.WordWrap = false;

            for (int i = first; i <= last; i++)
            {
                Def def = filtered[i];
                var rowRect = new Rect(0f, i * RowHeight, contentWidth, RowHeight - 1f);
                bool isCurrent = def != null && def.defName == currentDefName;

                if (isCurrent)
                {
                    Widgets.DrawBoxSolid(rowRect, new Color(0.26f, 0.45f, 0.70f, 0.55f));
                }
                else if (Mouse.IsOver(rowRect))
                {
                    Widgets.DrawHighlight(rowRect);
                }

                string label = (def.LabelCap + "   [" + def.defName + "]").ToString();
                if (isCurrent)
                {
                    label = "✔ " + label;
                }

                Widgets.LabelEllipses(rowRect, label);

                if (Widgets.ButtonInvisible(rowRect))
                {
                    Pick(def);
                    break;
                }
            }

            Widgets.EndScrollView();

            Text.Font = GameFont.Small;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        private void Pick(Def def)
        {
            if (onPick != null && def != null)
            {
                onPick(def);
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

            // 同时匹配 defName 和显示名：玩家有时记得 "Steel"，有时只记得"钢铁"。
            for (int i = 0; i < all.Count; i++)
            {
                Def def = all[i];
                if (def == null) continue;

                if (Matches(def.defName, search) || Matches(def.LabelCap, search))
                {
                    filtered.Add(def);
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
