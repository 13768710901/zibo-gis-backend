using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Data.SqlClient;
using System.Data;

namespace ZIBOGIS.Controllers
{
    /// <summary>
    /// 设施选址推荐分析API
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class SiteSelectionController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly IConfiguration _configuration;

        public SiteSelectionController(IConfiguration configuration)
        {
            _configuration = configuration;
            _connectionString = _configuration.GetConnectionString("DefaultConnection")!;
        }

        /// <summary>
        /// POST: api/siteselection/analyze
        /// 设施选址分析 - 基于供需比和空间盲区
        /// </summary>
        [HttpPost("analyze")]
        public async Task<IActionResult> Analyze([FromBody] SiteSelectionRequest request)
        {
            try
            {
                // 1. 获取所有设施数据
                var facilities = await GetAllFacilitiesAsync();
                
                // 2. 获取居民点（小区）数据作为人口代理
                var residents = await GetResidentsAsync();
                
                // 3. 执行网格分析
                var gridSize = request.GridSizeMeters; // 网格大小（米）
                var bounds = request.Bounds; // 分析区域边界 [minLon, minLat, maxLon, maxLat]
                
                var analysisResult = await PerformGridAnalysis(
                    facilities, 
                    residents, 
                    request.FacilityType,
                    gridSize,
                    bounds
                );
                
                // 4. 生成选址推荐
                var recommendations = GenerateRecommendations(
                    analysisResult,
                    request.FacilityType,
                    request.TopN
                );

                return Ok(new
                {
                    success = true,
                    gridAnalysis = analysisResult,
                    recommendations = recommendations,
                    summary = new
                    {
                        totalGrids = analysisResult.Count,
                        shortageGrids = analysisResult.Count(g => g.IsShortage),
                        avgSupplyRatio = analysisResult.Any() 
                            ? analysisResult.Average(g => g.SupplyRatio) 
                            : 0
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] 选址分析失败: {ex.Message}");
                Console.WriteLine($"[ERROR] Stack: {ex.StackTrace}");
                return StatusCode(500, new 
                { 
                    success = false, 
                    message = "选址分析失败，请稍后重试" 
                });
            }
        }

        /// <summary>
        /// 根据设施类型和等级计算服务能力指数（匹配实际数据库等级）
        /// </summary>
        private double CalculateServiceCapacity(string type, string level)
        {
            // 处理医院等级：先检查是否包含"甲等"，避免重复
            var normalizedLevel = level?.Trim() ?? "";
            
            var baseCapacity = type switch
            {
                // 医疗卫生：三甲医院、三级医院、二甲医院、二级医院、一级医院、专科医院等
                var t when t.Contains("医疗") || t.Contains("卫生") => normalizedLevel switch
                {
                    var l when l.Contains("三甲") || (l.Contains("三级") && l.Contains("甲等")) => 10000,
                    var l when l.Contains("二甲") || (l.Contains("二级") && l.Contains("甲等")) => 6000,
                    var l when l.Contains("三级") => 8000,
                    var l when l.Contains("二级") => 4000,
                    var l when l.Contains("一级") => 2000,
                    var l when l.Contains("专科") => 3000,
                    var l when l.Contains("疾控") => 5000,
                    var l when l.Contains("康复") => 1500,
                    var l when l.Contains("社区") => 1000,
                    _ => 2000
                },
                
                // 教育服务：幼儿园、小学、初中、九年一贯制、高中
                var t when t.Contains("教育") || t.Contains("学校") => normalizedLevel switch
                {
                    var l when l.Contains("高中") => 3000,
                    var l when l.Contains("九年一贯") => 2500,
                    var l when l.Contains("初中") => 1500,
                    var l when l.Contains("小学") => 1000,
                    var l when l.Contains("幼儿园") => 300,
                    _ => 800
                },
                
                // 商业商场：百货商场、便民市场、超市、购物中心、家居建材、酒店、科技园、商业街、专业市场
                var t when t.Contains("商业") || t.Contains("商场") => normalizedLevel switch
                {
                    var l when l.Contains("百货") || l.Contains("购物") => 5000,
                    var l when l.Contains("商业街") => 3000,
                    var l when l.Contains("便民") || l.Contains("超市") => 1500,
                    var l when l.Contains("专业") => 2000,
                    var l when l.Contains("家居") => 2500,
                    var l when l.Contains("科技") => 1000,
                    var l when l.Contains("酒店") => 800,
                    _ => 1200
                },
                
                // 其他设施：交通枢纽、酒店/会展、商务办公、文体场馆、政府机关
                var t when t.Contains("其他") => normalizedLevel switch
                {
                    var l when l.Contains("会展") || l.Contains("文体") => 3000,
                    var l when l.Contains("政府") => 2000,
                    var l when l.Contains("商务") || l.Contains("办公") => 1500,
                    var l when l.Contains("交通") => 2500,
                    var l when l.Contains("酒店") => 800,
                    _ => 1000
                },
                
                // 应急避难（无等级）
                var t when t.Contains("避难") || t.Contains("应急") => 3000,
                
                // 居民/小区
                var t when t.Contains("居民") || t.Contains("小区") => 1000,
                
                _ => 500
            };
            
            return baseCapacity;
        }

        /// <summary>
        /// 根据设施类型和等级估算服务人口（用于人口分布分析）
        /// </summary>
        private int EstimatePopulationByFacility(string type, string level)
        {
            // 居民/小区类型：直接使用固定人口
            if (type.Contains("居民") || type.Contains("小区"))
            {
                return 1000; // 1个小区 ≈ 1000人
            }
            
            // 其他类型：将服务能力转换为估算人口
            var capacity = CalculateServiceCapacity(type, level);
            // 人口估算 = 服务能力 × 1.5（周边辐射）
            return (int)(capacity * 1.5);
        }

        /// <summary>
        /// 将前端设施类型代码映射为数据库中文类型
        /// </summary>
        private string[] MapFacilityType(string typeCode)
        {
            return typeCode.ToLower() switch
            {
                "hospital" => new[] { "医疗卫生" },
                "school" => new[] { "教育服务", "学校" },
                "shelter" => new[] { "应急避难", "避难", "应急" },
                "commercial" => new[] { "商业/商场", "商业", "商场" },
                "resident" => new[] { "居民/小区", "居民", "小区" },
                _ => new[] { typeCode }
            };
        }

        /// <summary>
        /// 获取所有设施数据
        /// </summary>
        private async Task<List<FacilityPoint>> GetAllFacilitiesAsync()
        {
            var facilities = new List<FacilityPoint>();
            const string sql = @"
                SELECT Id, Name, Type, Longitude, Latitude, ISNULL(FacilityLevel, '') as FacilityLevel
                FROM Facilities
                WHERE Longitude IS NOT NULL AND Latitude IS NOT NULL";

            using var conn = new SqlConnection(_connectionString);
            using var cmd = new SqlCommand(sql, conn);
            await conn.OpenAsync();
            using var reader = await cmd.ExecuteReaderAsync();
            
            while (await reader.ReadAsync())
            {
                var typeName = reader.GetString(2);
                var facilityLevel = reader.GetString(5);
                
                facilities.Add(new FacilityPoint
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    TypeCode = typeName,
                    TypeName = typeName,
                    FacilityLevel = facilityLevel,
                    ServiceCapacity = CalculateServiceCapacity(typeName, facilityLevel),
                    Lon = Convert.ToDouble(reader.GetValue(3)),
                    Lat = Convert.ToDouble(reader.GetValue(4))
                });
            }
            
            return facilities;
        }

        /// <summary>
        /// 获取居民点数据（作为人口代理）
        /// </summary>
        private async Task<List<ResidentPoint>> GetResidentsAsync()
        {
            var residents = new List<ResidentPoint>();
            // 居民点：Type包含"居民"或"小区"的设施，或使用所有设施作为人口代理
            const string sql = @"
                SELECT Id, Name, Longitude, Latitude, Type, ISNULL(FacilityLevel, '') as FacilityLevel
                FROM Facilities
                WHERE Longitude IS NOT NULL AND Latitude IS NOT NULL";

            using var conn = new SqlConnection(_connectionString);
            using var cmd = new SqlCommand(sql, conn);
            await conn.OpenAsync();
            using var reader = await cmd.ExecuteReaderAsync();
            
            while (await reader.ReadAsync())
            {
                var typeName = reader.GetString(4);
                var facilityLevel = reader.GetString(5);
                
                // 根据设施类型和等级估算服务人口
                var popEstimate = EstimatePopulationByFacility(typeName, facilityLevel);
                
                residents.Add(new ResidentPoint
                {
                    Id = reader.GetInt32(0),
                    Name = reader.GetString(1),
                    Lon = Convert.ToDouble(reader.GetValue(2)),
                    Lat = Convert.ToDouble(reader.GetValue(3)),
                    PopulationEstimate = popEstimate,
                    FacilityType = typeName,
                    FacilityLevel = facilityLevel
                });
            }
            
            return residents;
        }

        /// <summary>
        /// 执行网格分析
        /// </summary>
        private Task<List<GridAnalysisResult>> PerformGridAnalysis(
            List<FacilityPoint> facilities,
            List<ResidentPoint> residents,
            string targetFacilityType,
            double gridSizeMeters,
            double[] bounds)
        {
            var results = new List<GridAnalysisResult>();
            
            // 简化的网格划分（基于经纬度近似）
            // 1度经度 ≈ 111km * cos(lat)，1度纬度 ≈ 111km
            var regionCenterLat = (bounds[1] + bounds[3]) / 2;
            var lonPerMeter = 1.0 / (111320 * Math.Cos(regionCenterLat * Math.PI / 180));
            var latPerMeter = 1.0 / 111320;
            
            var gridLonSize = gridSizeMeters * lonPerMeter;
            var gridLatSize = gridSizeMeters * latPerMeter;
            
            // 生成网格
            var gridLonCount = (int)((bounds[2] - bounds[0]) / gridLonSize);
            var gridLatCount = (int)((bounds[3] - bounds[1]) / gridLatSize);
            
            // 限制网格数量避免性能问题（最多100个网格，保证分析精度）
            gridLonCount = Math.Min(gridLonCount, 100);
            gridLatCount = Math.Min(gridLatCount, 100);
            
            var adjustedGridLonSize = (bounds[2] - bounds[0]) / gridLonCount;
            var adjustedGridLatSize = (bounds[3] - bounds[1]) / gridLatCount;
            
            for (int i = 0; i < gridLonCount; i++)
            {
                for (int j = 0; j < gridLatCount; j++)
                {
                    var gridMinLon = bounds[0] + i * adjustedGridLonSize;
                    var gridMaxLon = bounds[0] + (i + 1) * adjustedGridLonSize;
                    var gridMinLat = bounds[1] + j * adjustedGridLatSize;
                    var gridMaxLat = bounds[1] + (j + 1) * adjustedGridLatSize;
                    var gridCenterLon = (gridMinLon + gridMaxLon) / 2;
                    var gridCenterLat = (gridMinLat + gridMaxLat) / 2;
                    
                    // 统计网格内的设施
                    var facilitiesInGrid = facilities.Where(f => 
                        f.Lon >= gridMinLon && f.Lon < gridMaxLon &&
                        f.Lat >= gridMinLat && f.Lat < gridMaxLat
                    ).ToList();
                    
                    // 统计网格内的小区（人口代理）
                    var residentsInGrid = residents.Where(r => 
                        r.Lon >= gridMinLon && r.Lon < gridMaxLon &&
                        r.Lat >= gridMinLat && r.Lat < gridMaxLat
                    ).ToList();
                    
                    // 映射前端类型代码为数据库中文类型
                    var targetTypeNames = MapFacilityType(targetFacilityType);
                    
                    // 获取目标类型设施（按服务能力加权）
                    var targetFacilities = facilitiesInGrid
                        .Where(f => targetTypeNames.Any(t => f.TypeCode.Contains(t)))
                        .ToList();
                    
                    // 计算周边（相邻网格）的目标设施
                    var nearbyFacilities = facilities.Where(f =>
                        targetTypeNames.Any(t => f.TypeCode.Contains(t)) &&
                        CalculateDistance(f.Lon, f.Lat, gridCenterLon, gridCenterLat) <= 2000 // 2km内
                    ).ToList();
                    
                    // 计算总服务能力（加权）
                    var totalServiceCapacity = targetFacilities.Sum(f => f.ServiceCapacity);
                    var nearbyServiceCapacity = nearbyFacilities.Sum(f => f.ServiceCapacity);
                    
                    // 计算需求人口（必须先声明）
                    var totalPopulation = residentsInGrid.Sum(r => r.PopulationEstimate);
                    
                    // 统计各等级服务能力（用于推荐具体等级）
                    var capacityByLevel = targetFacilities
                        .GroupBy(f => f.FacilityLevel)
                        .ToDictionary(g => g.Key, g => g.Sum(f => f.ServiceCapacity));
                    
                    // 分析应该建什么等级的设施（缺口最大的等级）
                    var suggestedLevel = AnalyzeSuggestedLevel(
                        targetFacilityType, 
                        totalPopulation, 
                        capacityByLevel,
                        targetFacilities
                    );
                    
                    // 计算供需比（需求人口 / 服务能力）
                    var demandSupplyRatio = totalServiceCapacity > 0 
                        ? totalPopulation / totalServiceCapacity 
                        : 10; // 无设施时需求很大
                    
                    // 判断是否为短缺区域（考虑服务能力加权）
                    var isShortage = IsShortageAreaByCapacity(
                        targetFacilityType, 
                        totalServiceCapacity, 
                        totalPopulation,
                        nearbyServiceCapacity
                    );
                    
                    // 计算选址评分（基于服务能力供需缺口）
                    var score = CalculateSiteScoreByCapacity(
                        targetFacilityType,
                        totalPopulation,
                        totalServiceCapacity,
                        nearbyServiceCapacity,
                        isShortage
                    );
                    
                    results.Add(new GridAnalysisResult
                    {
                        GridId = $"{i}_{j}",
                        CenterLon = gridCenterLon,
                        CenterLat = gridCenterLat,
                        Bounds = new[] { gridMinLon, gridMinLat, gridMaxLon, gridMaxLat },
                        FacilityCount = targetFacilities.Count,
                        TotalFacilities = facilitiesInGrid.Count,
                        ResidentCount = residentsInGrid.Count,
                        TotalPopulation = totalPopulation,
                        NearbyFacilities = nearbyFacilities.Count,
                        ServiceCapacity = totalServiceCapacity,
                        ServiceCapacityByLevel = capacityByLevel,
                        SuggestedLevel = suggestedLevel,
                        SupplyRatio = Math.Round(demandSupplyRatio, 2),
                        IsShortage = isShortage,
                        Score = Math.Round(score, 2)
                    });
                }
            }
            
            return Task.FromResult(results);
        }

        /// <summary>
        /// 分析应该推荐建设什么等级的设施（基于各等级缺口）
        /// </summary>
        private string AnalyzeSuggestedLevel(string facilityType, int population, 
            Dictionary<string, double> capacityByLevel, List<FacilityPoint> facilities)
        {
            // 根据人口规模计算各等级应有的服务能力配比
            var suggestedLevel = facilityType.ToLower() switch
            {
                "hospital" => AnalyzeHospitalLevel(population, capacityByLevel),
                "school" => AnalyzeSchoolLevel(population, capacityByLevel, facilities),
                "shelter" => population > 8000 ? "大型应急避难中心" : population > 4000 ? "应急避难场所" : "微型避难所",
                "commercial" => population > 12000 ? "购物中心" : population > 6000 ? "便民市场" : "便利店",
                _ => "标准"
            };
            
            return suggestedLevel;
        }

        /// <summary>
        /// 分析医院等级缺口，推荐应该建什么等级
        /// 根据该网格的人口规模和周边现有设施，推荐最适合的等级
        /// </summary>
        private string AnalyzeHospitalLevel(int population, Dictionary<string, double> capacityByLevel)
        {
            // 统计现有各等级设施数量（简化后的等级体系）
            var sanjiaCount = capacityByLevel.Count(c => c.Key.Contains("三甲"));
            var sanjiCount = capacityByLevel.Count(c => c.Key.Contains("三级") && !c.Key.Contains("甲等"));
            var erjiaCount = capacityByLevel.Count(c => c.Key.Contains("二甲"));
            var erjiCount = capacityByLevel.Count(c => c.Key.Contains("二级") && !c.Key.Contains("甲等"));
            var yijiCount = capacityByLevel.Count(c => c.Key.Contains("一级"));
            var zhuankeCount = capacityByLevel.Count(c => c.Key.Contains("专科"));
            
            // 根据该网格的人口规模，推荐最适合的等级（多面开花策略）
            // 简化等级：三甲→三级→二甲→二级→一级→专科→社区
            
            if (population >= 15000)
            {
                // 大人口区优先高等级
                if (sanjiaCount == 0) return "三甲医院";
                if (erjiaCount < 2) return "二甲医院";
                if (sanjiCount < 2) return "三级医院";
                return "一级医院";
            }
            else if (population >= 8000)
            {
                // 中等偏大人口区
                if (erjiaCount == 0) return "二甲医院";
                if (erjiCount < 2) return "二级医院";
                if (yijiCount < 2) return "一级医院";
                return "专科医院";
            }
            else if (population >= 4000)
            {
                // 中等人口区
                if (erjiCount == 0) return "二级医院";
                if (yijiCount < 2) return "一级医院";
                return "专科医院";
            }
            else if (population >= 2000)
            {
                // 小人口区
                if (yijiCount == 0) return "一级医院";
                if (zhuankeCount == 0) return "专科医院";
                return "社区医院";
            }
            else
            {
                // 微小区
                return "社区医院";
            }
        }

        /// <summary>
        /// 分析学校等级缺口，推荐应该建什么等级
        /// 根据该网格的人口规模和现有设施结构，推荐最适合的等级
        /// 等级范围：幼儿园、小学、初中、九年一贯制、高中
        /// </summary>
        private string AnalyzeSchoolLevel(int population, Dictionary<string, double> capacityByLevel, 
            List<FacilityPoint> facilities)
        {
            // 统计现有各等级设施数量（只统计幼儿园到高中）
            var kindergartenCount = facilities.Count(f => f.FacilityLevel.Contains("幼儿园"));
            var primaryCount = facilities.Count(f => f.FacilityLevel.Contains("小学"));
            var middleCount = facilities.Count(f => f.FacilityLevel.Contains("初中"));
            var highCount = facilities.Count(f => f.FacilityLevel.Contains("高中"));
            var nineYearCount = facilities.Count(f => f.FacilityLevel.Contains("九年一贯"));
            
            // 根据人口规模和教育链完整性推荐等级（多面开花策略）
            // 教育链：幼儿园→小学→初中→高中（九年一贯制可以替代小学+初中）
            
            if (population >= 10000)
            {
                // 大人口区
                if (highCount < 2) return "高中";
                if (nineYearCount < 2) return "九年一贯制";
                if (middleCount < 3) return "初中";
                if (primaryCount < 3) return "小学";
                return "幼儿园";
            }
            else if (population >= 6000)
            {
                // 中等偏大人口区
                if (highCount == 0) return "高中";
                if (nineYearCount == 0) return "九年一贯制";
                if (middleCount < 2) return "初中";
                if (primaryCount < 2) return "小学";
                return "幼儿园";
            }
            else if (population >= 3000)
            {
                // 中等人口区
                if (nineYearCount == 0 && middleCount < 2) return "九年一贯制";
                if (middleCount == 0) return "初中";
                if (primaryCount < 2) return "小学";
                if (kindergartenCount < 2) return "幼儿园";
                return "小学"; // 保底推荐小学
            }
            else if (population >= 1500)
            {
                // 小人口区
                if (primaryCount == 0) return "小学";
                if (kindergartenCount == 0) return "幼儿园";
                return "小学"; // 保底推荐小学
            }
            else
            {
                // 微小区
                if (kindergartenCount == 0) return "幼儿园";
                return "小学"; // 保底推荐小学
            }
        }

        /// <summary>
        /// 判断是否为设施短缺区域（基于服务能力加权）
        /// </summary>
        private bool IsShortageAreaByCapacity(string facilityType, double serviceCapacity, 
            int population, double nearbyServiceCapacity)
        {
            // 供需比阈值：需求人口 / 服务能力
            var demandSupplyRatio = serviceCapacity > 0 ? population / serviceCapacity : 999;
            var nearbyRatio = nearbyServiceCapacity > 0 ? population / nearbyServiceCapacity : 999;
            
            return facilityType.ToLower() switch
            {
                "hospital" => demandSupplyRatio > 1.5 || (serviceCapacity == 0 && population > 3000),
                "school" => demandSupplyRatio > 2.0 || (serviceCapacity == 0 && population > 2000),
                "shelter" => serviceCapacity == 0 && population > 1000,
                "commercial" => demandSupplyRatio > 3.0 || (serviceCapacity == 0 && population > 1500),
                _ => demandSupplyRatio > 2.0
            };
        }

        /// <summary>
        /// 计算选址评分（基于服务能力供需缺口）
        /// </summary>
        private double CalculateSiteScoreByCapacity(string facilityType, int population, 
            double serviceCapacity, double nearbyServiceCapacity, bool isShortage)
        {
            // 计算供需缺口（正数表示供给不足）
            var demandSupplyGap = Math.Max(0, population - serviceCapacity);
            var nearbyGap = Math.Max(0, population - nearbyServiceCapacity);
            
            // 供需压力系数（越高越需要建设）
            var pressureRatio = serviceCapacity > 0 ? population / serviceCapacity : 5;
            
            var baseScore = facilityType.ToLower() switch
            {
                "hospital" => 
                    demandSupplyGap * 0.02 +           // 缺口权重（医院重要性高）
                    (isShortage ? 60 : 0) +            // 短缺奖励
                    (nearbyServiceCapacity < 5000 ? 40 : -nearbyGap * 0.01) + // 周边服务能力不足时加分
                    pressureRatio * 10,                 // 供需压力
                "school" => 
                    demandSupplyGap * 0.015 + 
                    (isShortage ? 50 : 0) +
                    (nearbyServiceCapacity < 3000 ? 30 : 0) +
                    pressureRatio * 8,
                "shelter" => 
                    (isShortage ? 80 : 15) +           // 避难所优先填充空白
                    demandSupplyGap * 0.01,
                "commercial" => 
                    demandSupplyGap * 0.01 + 
                    (nearbyServiceCapacity < 2000 ? 20 : 0) + // 商业可以适度聚集
                    pressureRatio * 5,
                _ => demandSupplyGap * 0.01 + (isShortage ? 25 : 0)
            };
            
            return Math.Max(0, baseScore);
        }

        /// <summary>
        /// 判断是否为设施短缺区域（旧版，保留兼容性）
        /// </summary>
        private bool IsShortageArea(string facilityType, int facilityCount, int population, int nearbyCount)
        {
            // 不同类型有不同的短缺标准
            return facilityType switch
            {
                "hospital" => facilityCount == 0 && population > 5000, // 5000人至少1个医院
                "school" => facilityCount == 0 && population > 3000,  // 3000人至少1个学校
                "shelter" => facilityCount == 0,                     // 每个网格都应该有避难所
                "commercial" => facilityCount < 1 && population > 2000,
                _ => facilityCount == 0 && population > 1000
            };
        }

        /// <summary>
        /// 计算选址评分（旧版，保留兼容性）
        /// </summary>
        private double CalculateSiteScore(string facilityType, int population, 
            int facilityCount, int nearbyCount, bool isShortage)
        {
            var baseScore = facilityType switch
            {
                "hospital" => 
                    population * 0.01 +           // 人口权重
                    (isShortage ? 50 : 0) +       // 短缺奖励
                    (nearbyCount == 0 ? 30 : -nearbyCount * 5), // 避开现有医院
                "school" => 
                    population * 0.015 + 
                    (isShortage ? 40 : 0) +
                    (nearbyCount < 2 ? 20 : -nearbyCount * 3),
                "shelter" => 
                    (isShortage ? 100 : 20) +   // 避难所优先填充空白
                    population * 0.005,
                "commercial" => 
                    population * 0.02 + 
                    (nearbyCount < 3 ? 25 : 0), // 商业可以适度聚集
                _ => population * 0.01 + (isShortage ? 30 : 0)
            };
            
            return Math.Max(0, baseScore);
        }

        /// <summary>
        /// 生成选址推荐
        /// </summary>
        private List<SiteRecommendation> GenerateRecommendations(
            List<GridAnalysisResult> gridResults,
            string facilityType,
            int topN)
        {
            // 按评分排序，取前N个
            var topGrids = gridResults
                .Where(g => g.Score > 20) // 最低分数门槛
                .OrderByDescending(g => g.Score)
                .Take(topN)
                .ToList();
            
            var recommendations = new List<SiteRecommendation>();
            var rank = 1;
            
            foreach (var grid in topGrids)
            {
                var reasons = GenerateRecommendationReasons(grid, facilityType);
                
                // 计算服务能力缺口（正数表示供给不足）
                var serviceGap = Math.Max(0, grid.TotalPopulation - grid.ServiceCapacity);
                
                // 根据排名梯队确定推荐等级（实现多面开花）
                // Top 1-3: 高等级，Top 4-6: 中等级，Top 7-10: 基础等级
                var recommendedLevel = GetLevelByRank(facilityType, rank, grid.SuggestedLevel);
                
                recommendations.Add(new SiteRecommendation
                {
                    Rank = rank,
                    GridId = grid.GridId,
                    Lon = grid.CenterLon,
                    Lat = grid.CenterLat,
                    Score = grid.Score,
                    Priority = rank <= 3 ? "high" : rank <= 6 ? "medium" : "low",
                    EstimatedPopulation = grid.TotalPopulation,
                    ExistingFacilitiesNearby = grid.NearbyFacilities,
                    ServiceGap = serviceGap,
                    Reasons = reasons,
                    SuggestedFacilityName = GenerateSuggestedNameByLevel(facilityType, rank, serviceGap, grid.TotalPopulation, recommendedLevel)
                });
                
                rank++;
            }
            
            return recommendations;
        }

        /// <summary>
        /// 根据排名梯队确定推荐等级（多面开花策略）
        /// 不同排名推荐不同等级，避免全部推荐同一等级
        /// </summary>
        private string GetLevelByRank(string facilityType, int rank, string suggestedBaseLevel)
        {
            return facilityType.ToLower() switch
            {
                "hospital" => rank switch
                {
                    1 => "三甲医院",      // Top1: 三甲（合并三甲/三级甲等）
                    2 => "三级医院",      // Top2: 三级（普通三级）
                    3 => "二甲医院",      // Top3: 二甲（合并二甲/二级甲等）
                    4 => "二级医院",      // Top4: 二级（普通二级）
                    5 => "一级医院",      // Top5: 一级
                    6 => "专科医院",      // Top6: 专科
                    _ => "社区医院"       // 其他: 社区/基层
                },
                "school" => rank switch
                {
                    1 => "高中",          // Top1: 高中
                    2 => "九年一贯制",    // Top2: 九年一贯
                    3 => "初中",          // Top3: 初中
                    4 => "小学",          // Top4: 小学
                    5 => "幼儿园",        // Top5: 幼儿园
                    _ => "小学"           // 其他: 小学（基础保障）
                },
                "shelter" => rank switch
                {
                    <= 3 => "大型应急避难中心",
                    <= 6 => "应急避难场所",
                    _ => "微型避难所"
                },
                "commercial" => rank switch
                {
                    <= 3 => "购物中心",
                    <= 6 => "便民市场",
                    _ => "便利店"
                },
                _ => "标准"
            };
        }

        /// <summary>
        /// 生成推荐理由
        /// </summary>
        private List<string> GenerateRecommendationReasons(GridAnalysisResult grid, string facilityType)
        {
            var reasons = new List<string>();
            
            if (grid.IsShortage)
                reasons.Add("该区域设施严重不足");
            
            if (grid.TotalPopulation > 5000)
                reasons.Add($"覆盖人口约{grid.TotalPopulation}人，需求旺盛");
            else if (grid.TotalPopulation > 2000)
                reasons.Add($"覆盖人口约{grid.TotalPopulation}人");
            
            if (grid.NearbyFacilities == 0)
                reasons.Add("周边2km范围内无同类设施，服务盲区");
            else if (grid.NearbyFacilities < 2)
                reasons.Add("周边同类设施较少，竞争压力小");
            
            var typeReason = facilityType switch
            {
                "hospital" => "医疗服务覆盖不足，居民就医不便",
                "school" => "教育资源缺乏，适龄儿童就学困难",
                "shelter" => "应急避难设施缺失，安全风险较高",
                "commercial" => "商业配套不完善，生活便利性差",
                _ => "公共服务设施不足"
            };
            reasons.Add(typeReason);
            
            return reasons;
        }

        /// <summary>
        /// 生成建议设施名称（根据分析出的具体等级推荐）
        /// </summary>
        private string GenerateSuggestedNameByLevel(string facilityType, int rank, double serviceGap, int population, string suggestedLevel)
        {
            // 根据分析出的具体等级生成推荐名称
            var (prefix, level) = facilityType.ToLower() switch
            {
                "hospital" => suggestedLevel switch
                {
                    var l when l.Contains("三甲") => ("建议新建三甲医院", "区域综合医疗中心"),
                    var l when l.Contains("二甲") => ("建议新建二甲医院", "区域医疗副中心"),
                    var l when l.Contains("三级") => ("建议新建三级医院", "区域医疗中心"),
                    var l when l.Contains("二级") => ("建议新建二级医院", "片区医疗中心"),
                    var l when l.Contains("一级") => ("建议新建一级医院", "社区卫生服务中心"),
                    var l when l.Contains("专科") => ("建议新建专科医院", "特色医疗中心"),
                    _ => ("建议新建社区医院", "基层医疗服务点")
                },
                "school" => suggestedLevel switch
                {
                    "高中" => ("建议新建高中", "高级中学"),
                    "九年一贯制" => ("建议新建九年一贯制学校", "综合基础教育中心"),
                    "初中" => ("建议新建初中", "初级中学"),
                    "小学" => ("建议新建小学", "基础小学"),
                    "幼儿园" => ("建议新建幼儿园", "学前教育中心"),
                    _ => ("建议新建学校", "基础教育点")
                },
                "shelter" => suggestedLevel switch
                {
                    "大型应急避难中心" => ("建议新建大型应急避难中心", "区域级避难中心（容纳万人以上）"),
                    "应急避难场所" => ("建议新建应急避难场所", "社区级避难所（容纳3-5千人）"),
                    _ => ("建议新建微型避难所", "紧急避险点（容纳1-2千人）")
                },
                "commercial" => suggestedLevel switch
                {
                    "购物中心" => ("建议新建购物中心/百货", "区域商业中心"),
                    "便民市场" => ("建议新建便民市场/超市", "社区商业服务点"),
                    _ => ("建议新建便利店/零售点", "便民零售点")
                },
                _ => ("建议新建公共服务设施", "服务站点")
            };
            
            var suffix = rank switch
            {
                1 => "【优先选址】",
                2 => "【推荐选址】",
                3 => "【优质选址】",
                _ => $"【{rank}号选址点】"
            };
            
            // 添加缺口信息
            var gapInfo = serviceGap > 5000 ? "（缺口大）" : serviceGap > 2000 ? "（缺口中等）" : "（缺口小）";
            
            return $"{prefix} - {level} {gapInfo} {suffix}";
        }

        /// <summary>
        /// 生成建议设施名称（根据服务能力缺口推荐等级）
        /// </summary>
        private string GenerateSuggestedNameByGap(string facilityType, int rank, double serviceGap, int population)
        {
            // 根据服务能力缺口和人口规模推荐设施等级
            var (prefix, level) = facilityType.ToLower() switch
            {
                "hospital" => (serviceGap, population) switch
                {
                    // 大缺口 + 大人口 = 高级别医院
                    ( > 8000, > 15000 ) => ("建议新建三甲医院", "区域综合医疗中心"),
                    ( > 5000, > 10000 ) => ("建议新建二甲医院", "区域医疗中心"),
                    ( > 2000, > 5000 ) => ("建议新建二级医院", "社区卫生服务中心"),
                    // 小缺口或小区人口 = 基础医疗
                    ( > 0, > 2000 ) => ("建议新建一级医院", "基层医疗点"),
                    _ => ("建议新建社区诊所", "便民医疗点")
                },
                "school" => (serviceGap, population) switch
                {
                    // 大缺口 = 综合学校
                    ( > 4000, > 10000 ) => ("建议新建九年一贯制学校", "综合教育中心"),
                    ( > 2500, > 6000 ) => ("建议新建小学", "基础教育点"),
                    ( > 1000, > 3000 ) => ("建议新建幼儿园", "学前教育点"),
                    _ => ("建议新建社区教育站", "社区教学点")
                },
                "shelter" => (serviceGap, population) switch
                {
                    // 避难所：大人口区域需要大型避难中心
                    ( > 5000, > 10000 ) => ("建议新建大型应急避难中心", "区域级避难中心"),
                    ( > 2000, > 5000 ) => ("建议新建应急避难场所", "社区级避难所"),
                    _ => ("建议新建微型避难所", "紧急避险点")
                },
                "commercial" => (serviceGap, population) switch
                {
                    // 商业：大人口需要大型商业
                    ( > 5000, > 15000 ) => ("建议新建购物中心/百货", "区域商业中心"),
                    ( > 2000, > 8000 ) => ("建议新建便民市场/超市", "社区商业服务"),
                    _ => ("建议新建便民店/便利店", "便民零售点")
                },
                _ => ("建议新建公共服务设施", "服务站点")
            };
            
            var suffix = rank switch
            {
                1 => "【优先选址】",
                2 => "【推荐选址】",
                3 => "【优质选址】",
                _ => $"【{rank}号选址点】"
            };
            
            return $"{prefix} - {level} {suffix}";
        }

        /// <summary>
        /// 生成建议设施名称（旧版，保留兼容性）
        /// </summary>
        private string GenerateSuggestedName(string facilityType, int rank, double score)
        {
            // 根据评分确定设施等级
            var (prefix, level) = facilityType.ToLower() switch
            {
                "hospital" => score switch
                {
                    >= 80 => ("新建三甲医院", "综合医疗中心"),
                    >= 50 => ("新建二甲医院", "区域医疗中心"),
                    >= 30 => ("新建社区医院", "社区卫生服务中心"),
                    _ => ("新建社区诊所", "基层医疗点")
                },
                "school" => score switch
                {
                    >= 80 => ("新建重点学校", "优质教育中心"),
                    >= 50 => ("新建九年一贯制学校", "综合教育中心"),
                    >= 30 => ("新建小学", "基础教育点"),
                    _ => ("新建教学点", "社区教育站")
                },
                "shelter" => score switch
                {
                    >= 50 => ("新建大型应急避难中心", "区域避难中心"),
                    >= 30 => ("新建应急避难场所", "社区避难所"),
                    _ => ("新建微型避难所", "紧急避险点")
                },
                "commercial" => score switch
                {
                    >= 80 => ("新建大型商业中心", "商业综合体"),
                    >= 50 => ("新建购物中心", "便民购物中心"),
                    >= 30 => ("新建便民超市", "社区服务站"),
                    _ => ("新建便利店", "便民零售点")
                },
                _ => ("新建公共服务设施", "服务站点")
            };
            
            var suffix = rank switch
            {
                1 => "（优先选址点）",
                2 => "（推荐选址点）",
                3 => "（优质选址点）",
                _ => $"（{rank}号选址点）"
            };
            
            return $"{prefix} - {level}{suffix}";
        }

        /// <summary>
        /// 计算两点间距离（米）
        /// </summary>
        private double CalculateDistance(double lon1, double lat1, double lon2, double lat2)
        {
            const double R = 6371000; // 地球半径（米）
            var dLat = (lat2 - lat1) * Math.PI / 180;
            var dLon = (lon2 - lon1) * Math.PI / 180;
            var a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                    Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) *
                    Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
            var c = 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
            return R * c;
        }
    }

    // 请求和响应模型
    public class SiteSelectionRequest
    {
        public string FacilityType { get; set; } = "";
        public double GridSizeMeters { get; set; } = 1000; // 默认1km网格
        public double[] Bounds { get; set; } = new double[4]; // [minLon, minLat, maxLon, maxLat]
        public int TopN { get; set; } = 10;
    }

    public class FacilityPoint
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public string TypeCode { get; set; } = "";
        public string TypeName { get; set; } = "";
        public string FacilityLevel { get; set; } = "";  // 设施等级
        public double ServiceCapacity { get; set; }      // 服务能力指数
        public double Lon { get; set; }
        public double Lat { get; set; }
    }

    public class ResidentPoint
    {
        public int Id { get; set; }
        public string Name { get; set; } = "";
        public double Lon { get; set; }
        public double Lat { get; set; }
        public int PopulationEstimate { get; set; }
        public string FacilityType { get; set; } = "";   // 设施类型
        public string FacilityLevel { get; set; } = "";   // 设施等级
    }

    public class GridAnalysisResult
    {
        public string GridId { get; set; } = "";
        public double CenterLon { get; set; }
        public double CenterLat { get; set; }
        public double[] Bounds { get; set; } = new double[4];
        public int FacilityCount { get; set; }
        public int TotalFacilities { get; set; }
        public int ResidentCount { get; set; }
        public int TotalPopulation { get; set; }
        public int NearbyFacilities { get; set; }
        public double ServiceCapacity { get; set; }  // 网格内总服务能力
        public double SupplyRatio { get; set; }
        public bool IsShortage { get; set; }
        public double Score { get; set; }
        
        // 各等级服务能力详细统计（用于推荐具体等级）
        public Dictionary<string, double> ServiceCapacityByLevel { get; set; } = new();
        // 推荐的具体等级（缺口最大的等级）
        public string SuggestedLevel { get; set; } = "";
    }

    public class SiteRecommendation
    {
        public int Rank { get; set; }
        public string GridId { get; set; } = "";
        public double Lon { get; set; }
        public double Lat { get; set; }
        public double Score { get; set; }
        public string Priority { get; set; } = "";
        public int EstimatedPopulation { get; set; }
        public int ExistingFacilitiesNearby { get; set; }
        public double ServiceGap { get; set; }  // 服务能力缺口
        public List<string> Reasons { get; set; } = new();
        public string SuggestedFacilityName { get; set; } = "";
    }
}
