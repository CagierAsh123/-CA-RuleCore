using Verse;
using Verse.AI;
using RimWorld;

namespace RuleCore
{
    /// <summary>
    /// 「本图 收到 某类信件」的判据 —— 也就是玩家意识到"出事了"的那一刻。
    ///
    /// **它是按到达时间开一个窗口，不是真正的边沿订阅。** 理由算过：
    /// 求值本身就是采样的（250 tick 一档），所以就算给 <c>LetterStack.ReceiveLetter</c>
    /// 打 Harmony 补丁拿到精确时刻，引擎也要等到下一个采样点才看——补丁买不到任何延迟收益。
    /// 代价是三条，必须知道：
    ///   1. 检测延迟最多一个采样档位（250 tick ≈ 4 秒）；
    ///   2. 玩家若在窗口内手动关掉那封信，这一轮不会触发；
    ///   3. 窗口内持续为真，靠操作的幂等与规则冷却收束成"一次"。
    /// 以后真做了订阅式重评估，换掉的是本类的实现，XML 一个字都不用改。
    /// </summary>
    public static class LetterWindow
    {
        /// <summary>到达后多久之内算"刚发生"（tick）。600 = 10 秒。</summary>
        public const int WindowTicks = 600;

        public static bool TryMatch(int tick, string letterDefName, out bool passed,
            out string code, out string reason)
        {
            passed = false;

            var stack = Find.LetterStack;
            if (stack == null)
            {
                code = "letter.no_stack";
                reason = "读不到信件栈。";
                return false;
            }

            if (string.IsNullOrEmpty(letterDefName))
            {
                code = "letter.no_type";
                reason = "没有指定要匹配哪一类信件。";
                return false;
            }

            var letters = stack.LettersListForReading;
            for (int i = 0; i < letters.Count; i++)
            {
                var letter = letters[i];
                if (letter == null || letter.def == null) continue;
                if (letter.def.defName != letterDefName) continue;

                if (tick - letter.arrivalTick <= WindowTicks)
                {
                    passed = true;
                    code = "letter.arrived";
                    reason = "刚收到「" + letter.def.LabelCap + "」。";
                    return true;
                }
            }

            code = "letter.not_in_window";
            reason = "窗口内没有收到这类信件。";
            return true;
        }
    }

    /// <summary>
    /// 关于执行者自身的两种状态判断。
    ///
    /// **它们不是"规则失败"，是"安静让开"。** 这一条是需求的直接落地：
    /// 袭击来了，小明该回家（检测成立）但他精神崩溃（做不了）——
    /// 引擎应当安静地不动作并记一条能力不成立的记录，而不是把规则判成失败，
    /// 更不该硬来。实现手段是谓词声明 <c>failure = QuietSkip</c>。
    /// </summary>
    public static class PawnStates
    {
        /// <summary>他闲着吗。默认把游荡也算闲着——"见谁拽谁"会让人很快把规则删掉。</summary>
        public static bool TryIdle(Pawn pawn, out bool passed, out string code, out string reason)
        {
            passed = false;

            if (pawn.jobs == null)
            {
                code = "idle.no_tracker";
                reason = "读不到 job 状态。";
                return false;
            }

            var job = pawn.CurJob;
            if (job == null)
            {
                passed = true;
                code = null;
                reason = null;
                return true;
            }

            var def = job.def;
            if (def == JobDefOf.Wait
                || def == JobDefOf.GotoWander
                || def == JobDefOf.Wait_Wander)
            {
                passed = true;
                code = null;
                reason = null;
                return true;
            }

            code = "idle.busy";
            reason = "正在干活（" + (def != null ? def.defName : "?") + "），不打扰。";
            return true;
        }

        /// <summary>他现在做得了这件事吗：未倒地、未崩溃、未被征召、还有移动能力。</summary>
        public static bool TryAble(Pawn pawn, out bool passed, out string code, out string reason)
        {
            passed = false;

            if (pawn.Dead || pawn.Destroyed)
            {
                code = "able.dead";
                reason = "这个人已经不在了。";
                return true;
            }

            if (!pawn.Spawned || pawn.Map == null)
            {
                code = "able.not_spawned";
                reason = "不在任何地图上。";
                return true;
            }

            if (pawn.Downed)
            {
                code = "able.downed";
                reason = "倒地了，做不了。";
                return true;
            }

            if (pawn.InMentalState)
            {
                code = "able.mental";
                reason = "精神崩溃中，做不了。";
                return true;
            }

            if (pawn.Drafted)
            {
                // 被征召意味着玩家接管了这个人——"原版第一顺位"在能力层的体现。
                code = "able.drafted";
                reason = "被玩家征召，听玩家的。";
                return true;
            }

            if (pawn.health != null && pawn.health.capacities != null)
            {
                float moving = pawn.health.capacities.GetLevel(PawnCapacityDefOf.Moving);
                if (moving <= 0.01f)
                {
                    code = "able.immobile";
                    reason = "移动能力为 0，走不了。";
                    return true;
                }
            }

            passed = true;
            code = null;
            reason = null;
            return true;
        }
    }
}
