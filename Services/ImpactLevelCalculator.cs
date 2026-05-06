namespace ZIBOGIS.Services
{
    // 影响等级计算器
    public static class ImpactLevelCalculator
    {
        // 等级配置表：类型编码 -> [Level1半径, Level2半径, Level3半径]
        private static readonly Dictionary<string, int[]> RadiusMap = new()
        {
            { "WATERLOG", new[] { 150, 300, 500 } },   // 积水内涝
            { "COLLAPSE", new[] { 100, 200, 400 } },   // 道路塌陷
            { "TREEFALL", new[] { 80, 150, 300 } },    // 树木倒伏
            { "DAMAGE", new[] { 50, 100, 200 } },      // 设施损毁
            { "FIRE", new[] { 200, 500, 1000 } },      // 火灾险情
            { "TRAPPED", new[] { 100, 200, 500 } }     // 人员被困
        };

        // 颜色映射
        private static readonly Dictionary<int, string> ColorMap = new()
        {
            { 1, "#FFD700" },  // 黄色
            { 2, "#FF8C00" },  // 橙色
            { 3, "#FF4500" }   // 红色
        };

        /// <summary>
        /// 根据灾情类型和后果选项计算等级和半径
        /// </summary>
        public static (int level, int radius, string color) Calculate(string typeCode, int consequenceIndex)
        {
            // 后果选项序号(1-3)直接对应等级
            int level = consequenceIndex;
            
            // 获取该类型的半径配置
            int radius = 100;  // 默认
            if (RadiusMap.TryGetValue(typeCode, out var radiusArray))
            {
                radius = radiusArray[level - 1];
            }

            string color = ColorMap.TryGetValue(level, out var c) ? c : "#FFD700";
            
            return (level, radius, color);
        }

        /// <summary>
        /// 仅获取半径（用于涟漪效果，实际影响半径的1.2倍）
        /// </summary>
        public static int GetRippleRadius(string typeCode, int level)
        {
            var (_, radius, _) = Calculate(typeCode, level);
            return (int)(radius * 1.2);  // 涟漪半径超出实际范围
        }

        /// <summary>
        /// 获取等级名称
        /// </summary>
        public static string GetLevelName(int level)
        {
            return level switch
            {
                1 => "轻度",
                2 => "中度",
                3 => "重度",
                _ => "未知"
            };
        }
    }
}
