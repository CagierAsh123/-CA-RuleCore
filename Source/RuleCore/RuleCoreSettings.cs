using System.Collections.Generic;
using Verse;
using RuleCore.Core;

namespace RuleCore
{
    public class RuleCoreSettings : ModSettings
    {
        public bool enableLog = true;
        public bool verboseLog = false;
        public int logCapacity = RuleLog.DefaultCapacity;

        /// <summary>把记录同时落盘成 Markdown，便于事后比对与贴给别人。</summary>
        public bool logToFile = true;

        /// <summary>日志目录里保留几份历史文件。</summary>
        public int logRetention = 10;

        /// <summary>
        /// 允许**开发者级操作**真正生效（直接改世界状态：改天气、触发事件）。
        ///
        /// 默认关。**开关放在玩家手里，而不是"看你是不是开了开发者模式"**：
        /// 这类操作确实强，但"强"该由玩家自己决定，不该由他有没有按开 dev mode 决定——
        /// 把功能藏在调试菜单后面，等于让他去猜"是不是我哪里写错了"。
        ///
        /// 打开之后**每条规则的实际层级仍然按它动作的最高级算**：
        /// 一条只含玩家级动作的规则不会因此变成开发者级。
        /// 所以这个开关只影响"要不要放行"，不影响任何规则的语义。
        /// </summary>
        public bool allowDeveloperOps;

        /// <summary>
        /// 玩家**关掉**的规则 id。内置规则也能被关掉。
        ///
        /// 内置规则是模组带来的**定义**：不可编辑、不可删除。但"我不想让它跑"
        /// 是玩家的偏好，跟"它定义成什么样"是两件事——不该因为没有编辑权
        /// 就连关都关不掉。存在这里，载入时套回 <see cref="Rule.enabled"/>。
        /// </summary>
        public List<string> disabledRuleIds = new List<string>();

        /// <summary>
        /// 玩家**明确打开过**的规则 id。
        ///
        /// 存在的理由：作者可以在 XML 里写 `enabled=false`（"随包给你一个例子，
        /// 但默认别跑"）。只有"关掉名单"一份表态的话，那个 `enabled=false`
        /// 要么被无视（作者的意图丢了），要么变成玩家永远开不起来（按钮变死的）。
        /// 两份表态合起来才是三态：没表态 → 听作者的。
        /// </summary>
        public List<string> enabledRuleIds = new List<string>();

        /// <summary>
        /// 玩家**从列表里移除**的规则 id（只对内置规则有意义——玩家规则是直接删）。
        ///
        /// 它**不删任何数据**，只是不再加载、不再显示，所以随时能恢复。
        /// 对内置规则，"清除"只能是这个意思：那些定义住在别人的模组里。
        /// </summary>
        public List<string> hiddenRuleIds = new List<string>();

        /// <summary>
        /// 玩家在游戏内创建的规则。跟着**全局配置**走而不是存档——
        /// 规则是玩家的资产，要能跨存档复用、能分享，所以默认住在全局库里。
        /// （"这条规则只对本殖民地生效"那层筛选以后再加。）
        /// </summary>
        public List<Rule> playerRules = new List<Rule>();

        public override void ExposeData()
        {
            Scribe_Values.Look(ref enableLog, "enableLog", true);
            Scribe_Values.Look(ref verboseLog, "verboseLog", false);
            Scribe_Values.Look(ref logCapacity, "logCapacity", RuleLog.DefaultCapacity);
            Scribe_Values.Look(ref logToFile, "logToFile", true);
            Scribe_Values.Look(ref logRetention, "logRetention", 10);
            Scribe_Values.Look(ref allowDeveloperOps, "allowDeveloperOps", false);
            Scribe_Collections.Look(ref disabledRuleIds, "disabledRuleIds", LookMode.Value);
            Scribe_Collections.Look(ref enabledRuleIds, "enabledRuleIds", LookMode.Value);
            Scribe_Collections.Look(ref hiddenRuleIds, "hiddenRuleIds", LookMode.Value);
            Scribe_Collections.Look(ref playerRules, "playerRules", LookMode.Deep);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                Normalize();
            }
        }

        /// <summary>
        /// 手改 XML 的脏数据不能炸引擎：所有外部输入一律夹取。
        /// </summary>
        public void Normalize()
        {
            if (logCapacity < 64) logCapacity = 64;
            if (logCapacity > 8192) logCapacity = 8192;
            if (logRetention < 1) logRetention = 1;
            if (logRetention > 200) logRetention = 200;

            if (playerRules == null)
            {
                playerRules = new List<Rule>();
            }

            if (disabledRuleIds == null) disabledRuleIds = new List<string>();
            if (enabledRuleIds == null) enabledRuleIds = new List<string>();
            if (hiddenRuleIds == null) hiddenRuleIds = new List<string>();

            // 写坏的 id（空串）清掉：它们在列表里会变成一条点不动、也恢复不了的空行。
            for (int i = disabledRuleIds.Count - 1; i >= 0; i--)
            {
                if (string.IsNullOrEmpty(disabledRuleIds[i])) disabledRuleIds.RemoveAt(i);
            }
            for (int i = enabledRuleIds.Count - 1; i >= 0; i--)
            {
                if (string.IsNullOrEmpty(enabledRuleIds[i])) enabledRuleIds.RemoveAt(i);
            }
            // 同一个 id 在"关掉"和"打开"两份表态里同时出现是不该发生的
            // （SetDisabled 会维持互斥）。真出现了（手改配置文件）就按"关掉优先"清理——
            // 那是更强的一份表态。
            for (int i = enabledRuleIds.Count - 1; i >= 0; i--)
            {
                if (disabledRuleIds.Contains(enabledRuleIds[i])) enabledRuleIds.RemoveAt(i);
            }
            for (int i = hiddenRuleIds.Count - 1; i >= 0; i--)
            {
                if (string.IsNullOrEmpty(hiddenRuleIds[i])) hiddenRuleIds.RemoveAt(i);
            }

            // 手改 XML / 版本迁移留下的空位在这里被清掉，加载完就是干净的。
            for (int i = playerRules.Count - 1; i >= 0; i--)
            {
                var rule = playerRules[i];
                if (rule == null)
                {
                    playerRules.RemoveAt(i);
                    continue;
                }
                rule.Origin = RuleOrigin.Player;
                rule.Resolve();
            }
        }
    }
}
