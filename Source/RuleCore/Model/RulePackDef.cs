using System.Collections.Generic;
using Verse;

namespace RuleCore
{
    /// <summary>
    /// 规则包：模组内置规则的容器。
    ///
    /// 为什么规则本身是普通类、而包是 Def：
    ///   · **Def** 提供跨 mod 可寻址（defName）、归属（modContentPack）、XML 加载与校验生命周期；
    ///   · **普通类** 才能被玩家在运行期创建、编辑、Scribe。
    ///
    /// 这个组合让两个来源共用同一个 <see cref="Rule"/> 模型——
    /// 也就是"一个模型，两种视图"能成立的前提。
    ///
    /// XML 形状：
    /// <code>
    /// &lt;RuleCore.RulePackDef&gt;
    ///   &lt;defName&gt;MyMod_Rules&lt;/defName&gt;
    ///   &lt;rules&gt;
    ///     &lt;li Class="RuleCore.Rule"&gt;
    ///       &lt;id&gt;raid-go-home&lt;/id&gt;
    ///       ...
    ///     &lt;/li&gt;
    ///   &lt;/rules&gt;
    /// &lt;/RuleCore.RulePackDef&gt;
    /// </code>
    /// </summary>
    public class RulePackDef : Def
    {
        public List<Rule> rules = new List<Rule>();

        public override void ResolveReferences()
        {
            base.ResolveReferences();

            if (rules == null)
            {
                rules = new List<Rule>();
            }

            for (int i = rules.Count - 1; i >= 0; i--)
            {
                if (rules[i] == null)
                {
                    rules.RemoveAt(i);
                }
            }

            for (int i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];

                // id 是运行态的键，必须跨所有规则包唯一。
                // 作者写了的就用，但补上包前缀——否则两个 mod 各写一个 "raid-go-home" 就撞了。
                string localId = string.IsNullOrEmpty(rule.id)
                    ? i.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : rule.id;

                rule.id = (defName ?? "pack") + ":" + localId;
                rule.Origin = RuleOrigin.Mod;
                rule.SourcePack = defName;
                rule.Resolve();
            }
        }

        public override IEnumerable<string> ConfigErrors()
        {
            foreach (string error in base.ConfigErrors())
            {
                yield return error;
            }

            if (rules.Count == 0)
            {
                yield return "规则包里没有任何规则。";
            }

            var seen = new HashSet<string>();
            var buffer = new List<string>();

            for (int i = 0; i < rules.Count; i++)
            {
                var rule = rules[i];
                if (rule == null) continue;

                if (!seen.Add(rule.id))
                {
                    yield return "规则 id 重复：" + rule.id + "（id 是运行态的键，必须唯一）。";
                }

                buffer.Clear();
                rule.CollectConfigErrors(buffer);
                for (int k = 0; k < buffer.Count; k++)
                {
                    yield return "规则 " + (rule.DisplayLabel ?? rule.id) + "：" + buffer[k];
                }
            }
        }

        public override string ToString()
        {
            return (defName ?? "(无 defName)") + " 含 " + rules.Count + " 条规则";
        }
    }
}
