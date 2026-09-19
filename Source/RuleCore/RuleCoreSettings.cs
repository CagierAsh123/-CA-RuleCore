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
