namespace RuleCore.Core
{
    /// <summary>
    /// <see cref="RuleStage"/> 的名字缓存。
    /// 求值在热路径上，不能每帧 ToString() 一次枚举——那是纯白送的 GC。
    /// </summary>
    public static class RuleStageNames
    {
        private static readonly string[] names =
        {
            "None", "Engine", "Detect", "Operate"
        };

        public static string NameOf(RuleStage stage)
        {
            int index = (int)stage;
            if (index < 0 || index >= names.Length)
            {
                return "Unknown";
            }
            return names[index];
        }

        public static string Name(this RuleStage stage)
        {
            return NameOf(stage);
        }
    }
}
