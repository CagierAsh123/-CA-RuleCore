using System;
using UnityEngine;
using Verse;

namespace RuleCore
{
    /// <summary>
    /// 一次绘制前后的 IMGUI **文本状态**（字体 / 对齐 / 折行 / 颜色）。
    ///
    /// <b>为什么必须有它。</b> <c>Text.WordWrap</c> 是个**全局静态**：
    /// 「我这一行不折行」的写法是把它设成 <c>false</c>，但设完必须还原。
    /// 忘了还原的后果有两层，第二层才是真正难受的：
    ///
    /// 1. 原版在 <c>Text.StartOfOnGUI()</c> 里检查它，不是就往控制台记一条
    ///    <c>Word wrap was false at end of frame.</c> 加整条调用栈。
    ///    （是 <c>Log.ErrorOnce</c>，一次会话只记一条——但那条会一直在，
    ///    而报错位置在 UIRoot，离犯错的那段绘制十万八千里。）
    /// 2. **它顺手把状态改回去**：<c>WordWrap = true</c>、<c>Anchor = UpperLeft</c>。
    ///    于是你精心设的"这行不折行 / 这一段居中对齐"被静默撤掉，
    ///    下一帧的界面就歪了——而现象和"哪一步没还原"看不出任何关系。
    ///    （<c>Anchor</c> 也有同一个检查：<c>Alignment was X at end of frame.</c>）
    ///
    /// <b>为什么不手写还原。</b> 手写就是在每个 return 前补一句，
    /// 而"每个 return"正是会漏的东西（本模组的编辑器与两个选择器窗口都漏过）。
    /// 用 <c>using</c> 包住一次绘制，提前 return 也照样还原。
    /// </summary>
    public sealed class TextStateScope : IDisposable
    {
        private readonly GameFont font;
        private readonly TextAnchor anchor;
        private readonly bool wrap;
        private readonly Color color;

        public TextStateScope()
        {
            font = Text.Font;
            anchor = Text.Anchor;
            wrap = Text.WordWrap;
            color = GUI.color;
        }

        public void Dispose()
        {
            Text.Font = font;
            Text.Anchor = anchor;
            Text.WordWrap = wrap;
            GUI.color = color;
        }
    }
}
