using System;

namespace RuleCore.Core
{
    /// <summary>
    /// 规则引擎的结构化日志汇。
    ///
    /// 三条硬约束（都是从 AutoTestRunner / LogSyncMod 的教训里定下来的）：
    ///   1. 定容环形缓冲——绝不 List.RemoveAt(0)，也绝不"到顶就截断"。
    ///   2. 只做相邻合并，不做永久全局去重。
    ///   3. 生产者只发事件、消费者各自订阅；订阅者逐个 try/catch，一个坏订阅者拖不垮管线。
    ///
    /// 本类不依赖 Verse / RimWorld：tick 由 TickProvider 注入，保持 Core 层可单测。
    /// </summary>
    public static class RuleLog
    {
        public const int DefaultCapacity = 512;
        private const int MinCapacity = 64;
        private const int MaxCapacity = 8192;

        private static readonly object gate = new object();

        private static RuleLogRecord[] buffer = new RuleLogRecord[DefaultCapacity];
        private static int capacity = DefaultCapacity;
        private static int head;   // 下一个写入槽
        private static int count;  // 当前有效条目数
        private static long seq;
        private static long dropped;

        /// <summary>游戏 tick 提供者，由 Verse 层在启动时注入。未注入时记 0。</summary>
        public static Func<int> TickProvider;

        /// <summary>是否记录 Trace 级。由设置驱动。</summary>
        public static Func<bool> VerboseProvider;

        /// <summary>是否开启普通日志。由设置驱动；Error 永远输出，不受此门控。</summary>
        public static Func<bool> EnabledProvider;

        /// <summary>记录流。时间线面板、落盘、外部断言都订阅这里。</summary>
        public static event Action<RuleLogRecord> Recorded;

        /// <summary>
        /// 自身诊断输出的落点，由宿主注入——游戏内是 UnityEngine.Debug，测试宿主是控制台。
        /// 注入而不是硬编码，是为了让 Core 层保持纯 BCL、能脱离 Unity 独立运行与单测。
        /// </summary>
        public static Action<string> RawLogSink;

        public static int Capacity
        {
            get { lock (gate) { return capacity; } }
        }

        public static int Count
        {
            get { lock (gate) { return count; } }
        }

        /// <summary>因缓冲满而被淘汰的条目数。只增不减，用于面板上标注"已丢弃 N 条"。</summary>
        public static long Dropped
        {
            get { lock (gate) { return dropped; } }
        }

        /// <summary>
        /// 唯一的重置入口。任何新增的静态状态都必须在这里登记，
        /// 否则读档 / 热重载后会留下上一局的污染。
        /// </summary>
        public static void Boot(int capacityHint = DefaultCapacity)
        {
            lock (gate)
            {
                capacity = Clamp(capacityHint);
                buffer = new RuleLogRecord[capacity];
                head = 0;
                count = 0;
                seq = 0;
                dropped = 0;
            }
        }

        public static void Write(RuleLogLevel level, string ruleId, string stepId, string outcome,
            string reasonCode, string entity, string message)
        {
            if (!ShouldRecord(level)) return;

            int tick = 0;
            var tickProvider = TickProvider;
            if (tickProvider != null)
            {
                try { tick = tickProvider(); }
                catch { tick = 0; }
            }

            RuleLogRecord published = null;

            lock (gate)
            {
                seq++;
                var last = count > 0 ? buffer[(head - 1 + capacity) % capacity] : null;

                // 相邻合并：内容相同就只加计数，不新增条目。
                if (last != null && last.SameAs(level, ruleId, stepId, outcome, reasonCode, entity, message))
                {
                    last.RepeatCount++;
                    last.LastSeq = seq;
                    return;
                }

                var record = new RuleLogRecord(seq, tick, DateTime.UtcNow, level,
                    ruleId, stepId, outcome, reasonCode, entity, message);

                if (count == capacity)
                {
                    dropped++;
                }
                else
                {
                    count++;
                }

                buffer[head] = record;
                head = (head + 1) % capacity;
                published = record;
            }

            Publish(published);
        }

        public static void Trace(string ruleId, string stepId, string outcome, string reasonCode,
            string entity = null, string message = null)
        {
            Write(RuleLogLevel.Trace, ruleId, stepId, outcome, reasonCode, entity, message);
        }

        public static void Info(string ruleId, string stepId, string outcome, string reasonCode,
            string entity = null, string message = null)
        {
            Write(RuleLogLevel.Info, ruleId, stepId, outcome, reasonCode, entity, message);
        }

        public static void Warn(string ruleId, string stepId, string outcome, string reasonCode,
            string entity = null, string message = null)
        {
            Write(RuleLogLevel.Warn, ruleId, stepId, outcome, reasonCode, entity, message);
        }

        public static void Error(string ruleId, string stepId, string outcome, string reasonCode,
            string entity = null, string message = null)
        {
            Write(RuleLogLevel.Error, ruleId, stepId, outcome, reasonCode, entity, message);
        }

        /// <summary>按时间顺序取出当前全部记录。供面板一次性重绘用。</summary>
        public static RuleLogRecord[] Snapshot()
        {
            lock (gate)
            {
                var result = new RuleLogRecord[count];
                int start = count == capacity ? head : 0;
                for (int i = 0; i < count; i++)
                {
                    result[i] = buffer[(start + i) % capacity];
                }
                return result;
            }
        }

        /// <summary>
        /// 增量拉取：只取序号大于 afterSeq 的记录。
        /// 这是面板分页刷新的游标接口——和 AutoTestRunner 的 GetEntries(afterSequence) 同构。
        /// </summary>
        public static RuleLogRecord[] Since(long afterSeq)
        {
            lock (gate)
            {
                int n = 0;
                int start = count == capacity ? head : 0;
                for (int i = 0; i < count; i++)
                {
                    if (buffer[(start + i) % capacity].Seq > afterSeq) n++;
                }

                var result = new RuleLogRecord[n];
                int k = 0;
                for (int i = 0; i < count; i++)
                {
                    var r = buffer[(start + i) % capacity];
                    if (r.Seq > afterSeq) result[k++] = r;
                }
                return result;
            }
        }

        /// <summary>当前最大序号。面板下次 Since() 的游标。</summary>
        public static long LastSeq
        {
            get { lock (gate) { return seq; } }
        }

        private static bool ShouldRecord(RuleLogLevel level)
        {
            if (level == RuleLogLevel.Error) return true;
            if (level == RuleLogLevel.Trace && !ReadProvider(VerboseProvider)) return false;
            return ReadProvider(EnabledProvider);
        }

        private static bool ReadProvider(Func<bool> provider)
        {
            if (provider == null) return true;
            try { return provider(); }
            catch { return true; }
        }

        private static void Publish(RuleLogRecord record)
        {
            var handler = Recorded;
            if (handler == null) return;

            // 订阅者隔离：一个坏订阅者不能拖垮整条日志管线。
            var list = handler.GetInvocationList();
            for (int i = 0; i < list.Length; i++)
            {
                try
                {
                    ((Action<RuleLogRecord>)list[i]).Invoke(record);
                }
                catch (Exception ex)
                {
                    LogRaw("[RuleCore] 日志订阅者抛出异常: " + ex);
                }
            }
        }

        private static int Clamp(int value)
        {
            if (value < MinCapacity) return MinCapacity;
            if (value > MaxCapacity) return MaxCapacity;
            return value;
        }

        /// <summary>
        /// 自身诊断绕开被 patch 的管道，避免递归。
        /// 目前 RuleCore 不 patch Verse.Log，但接口先留好——以后接了就不能再改。
        /// </summary>
        private static void LogRaw(string text)
        {
            var sink = RawLogSink;
            if (sink == null) return;
            try { sink(text); }
            catch { }
        }
    }
}
