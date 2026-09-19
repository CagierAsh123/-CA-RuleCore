namespace RuleCore.Core
{
    /// <summary>
    /// 单条规则的持久化运行态。
    ///
    /// 规则本身是**无状态**的——Def 里没有一个计数器，读档后行为由游戏状态重新推导。
    /// 这里只放推导不出来的那一丁点东西：冷却需要的"上次触发是第几 tick"，
    /// 以及自触发环检测需要的窗口计数。
    ///
    /// 这一层必须可 Scribe（存进存档），否则读档后冷却会失效、环检测会重来。
    /// </summary>
    public sealed class RuleRuntimeState
    {
        /// <summary>上次成功下发的 tick。-1 表示本局还没触发过。</summary>
        public int LastFiredTick = -1;

        /// <summary>当前观察窗口的起点 tick。-1 表示窗口未开。</summary>
        public int WindowStartTick = -1;

        /// <summary>当前窗口内已下发的次数。</summary>
        public int IssuesInWindow;

        /// <summary>已被判定为自触发环并自动停用。</summary>
        public bool LoopSuspected;

        public void Reset()
        {
            LastFiredTick = -1;
            WindowStartTick = -1;
            IssuesInWindow = 0;
            LoopSuspected = false;
        }

        /// <summary>还要等几 tick 才能再次触发。0 表示现在就可以。</summary>
        public int TicksUntilReady(int tick, int cooldownTicks)
        {
            if (cooldownTicks <= 0 || LastFiredTick < 0)
            {
                return 0;
            }

            int elapsed = tick - LastFiredTick;
            return elapsed >= cooldownTicks ? 0 : cooldownTicks - elapsed;
        }
    }
}
