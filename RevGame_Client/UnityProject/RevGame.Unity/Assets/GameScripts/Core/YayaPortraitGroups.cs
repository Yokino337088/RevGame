using System;

namespace RevGame.Core
{
    /// <summary>鸦鸦差分按业务共同使用的范围归组。生成图集与剧情预载共用这一份映射。</summary>
    public static class YayaPortraitGroups
    {
        private const string Root = "Art/Characters/Yaya/Expressions/";

        public static string AtlasFor(string logicalPath)
        {
            if (string.IsNullOrEmpty(logicalPath) || !logicalPath.StartsWith(Root, StringComparison.Ordinal))
            {
                return null;
            }
            string name = logicalPath.Substring(Root.Length);
            return name switch
            {
                "YayaExpression_01" or "YayaExpression_03" or "YayaExpression_13" or "YayaExpression_23" => "YayaExpressions",
                "YayaExpression_02" or "YayaExpression_08" or "YayaExpression_14" or "YayaExpression_26" => "YayaCombatExpressions",
                "YayaExpression_10" or "YayaExpression_11" or "YayaExpression_17" or "YayaExpression_18" or "YayaExpression_19" => "YayaStoryOpening",
                "GalYaya_06" or "GalYaya_21" => "YayaGalCottage",
                _ when name.StartsWith("YayaExpression_", StringComparison.Ordinal) => "YayaStoryJourney",
                _ when name.StartsWith("GalYaya_", StringComparison.Ordinal) => "YayaGalShared",
                _ => null
            };
        }

        public static bool IsPortraitAtlas(string name)
        {
            return name is "YayaExpressions" or "YayaCombatExpressions" or "YayaStoryOpening"
                or "YayaStoryJourney" or "YayaGalCottage" or "YayaGalShared";
        }
    }
}
