using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using System.Data;
using Npgsql;
using System.Text.Json;
using ZIBOGIS.Model;
using ZIBOGIS.Services;

namespace ZIBOGIS.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class DisasterController : ControllerBase
    {
        private readonly string _connectionString;
        private readonly IWebHostEnvironment _environment;

        public DisasterController(IConfiguration configuration, IWebHostEnvironment environment)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                               ?? throw new InvalidOperationException("Missing connection string 'DefaultConnection'");
            _environment = environment;
        }

        #region 1. 灾情上报
        
        /// <summary>
        /// POST: /api/disaster/report
        /// 移动端灾情上报
        /// </summary>
        [HttpPost("report")]
        public async Task<IActionResult> Report([FromForm] DisasterReportRequest request, [FromForm] List<IFormFile> images)
        {
            try
            {
                // 1. 计算等级和半径
                var (level, radius, color) = ImpactLevelCalculator.Calculate(request.DisasterType, request.ConsequenceIndex);

                // 2. 保存图片
                var imageUrls = new List<string>();
                if (images != null && images.Count > 0)
                {
                    // 确定wwwroot路径（WebRootPath为空时手动拼接）
                    var webRootPath = _environment.WebRootPath 
                        ?? Path.Combine(_environment.ContentRootPath, "wwwroot");
                    var uploadPath = Path.Combine(webRootPath, "uploads", "disasters");
                    if (!Directory.Exists(uploadPath))
                        Directory.CreateDirectory(uploadPath);

                    foreach (var image in images.Take(5))  // 最多5张
                    {
                        var fileName = $"{Guid.NewGuid()}_{image.FileName}";
                        var filePath = Path.Combine(uploadPath, fileName);
                        using (var stream = new FileStream(filePath, FileMode.Create))
                        {
                            await image.CopyToAsync(stream);
                        }
                        imageUrls.Add($"/uploads/disasters/{fileName}");
                    }
                }

                // 3. 获取IP地址
                var ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";

                // 4. 插入数据库
                const string insertSql = @"
                    INSERT INTO disasters (disaster_type, consequence_index, reporter_device, reporter_ip, 
                        status, lon, lat, description, images, impact_level, impact_radius_m, confirm_count)
                    VALUES (@type, @consequence, @device, @ip, '待审核', @lon, @lat, @desc, @images, @level, @radius, 1);
                    RETURNING disaster_id;";

                int disasterId;
                using (var conn = new NpgsqlConnection(_connectionString))
                using (var cmd = new NpgsqlCommand(insertSql, conn))
                {
                    cmd.Parameters.AddWithValue("@type", request.DisasterType);
                    cmd.Parameters.AddWithValue("@consequence", request.ConsequenceIndex);
                    cmd.Parameters.AddWithValue("@device", (object?)request.DeviceId ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ip", ipAddress);
                    cmd.Parameters.AddWithValue("@lon", request.Lon);
                    cmd.Parameters.AddWithValue("@lat", request.Lat);
                    cmd.Parameters.AddWithValue("@desc", (object?)request.Description ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@images", imageUrls.Count > 0 ? JsonSerializer.Serialize(imageUrls) : DBNull.Value);
                    cmd.Parameters.AddWithValue("@level", level);
                    cmd.Parameters.AddWithValue("@radius", radius);

                    await conn.OpenAsync();
                    disasterId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                }

                // 5. 执行众包验证（检查是否自动确认）
                var verification = await CheckCrowdVerification(disasterId, request.DisasterType, request.Lon, request.Lat, request.DeviceId);

                return Ok(new
                {
                    success = true,
                    disasterId = disasterId,
                    status = verification.AutoConfirmed ? "已确认" : "待审核",
                    impactLevel = level,
                    impactRadius = radius,
                    confirmCount = verification.TotalConfirmCount,
                    message = verification.AutoConfirmed ? "灾情已上报并自动确认" : "灾情已上报，等待审核"
                });
            }
            catch (Exception ex)
            {
                // 详细错误日志
                Console.WriteLine($"[ERROR] 上报失败: {ex.Message}");
                Console.WriteLine($"[ERROR] 堆栈跟踪: {ex.StackTrace}");
                if (ex.InnerException != null)
                {
                    Console.WriteLine($"[ERROR] 内部异常: {ex.InnerException.Message}");
                }
                return StatusCode(500, new { success = false, message = $"上报失败: {ex.Message}" });
            }
        }

        #endregion

        #region 2. 灾情列表查询

        /// <summary>
        /// GET: /api/disaster/list?status=&type=&startTime=&endTime=
        /// 获取灾情列表（支持筛选）
        /// </summary>
        [HttpGet("list")]
        public async Task<IActionResult> GetList([FromQuery] DisasterQueryParams param)
        {
            var list = new List<Disaster>();

            var whereClause = "WHERE 1=1";
            var parameters = new List<NpgsqlParameter>();

            if (!string.IsNullOrEmpty(param.Status))
            {
                whereClause += " AND d.status = @status";
                parameters.Add(new NpgsqlParameter("@status", param.Status));
            }
            if (!string.IsNullOrEmpty(param.Type))
            {
                whereClause += " AND d.disaster_type = @type";
                parameters.Add(new NpgsqlParameter("@type", param.Type));
            }
            if (param.StartTime.HasValue)
            {
                whereClause += " AND d.reported_at >= @startTime";
                parameters.Add(new NpgsqlParameter("@startTime", param.StartTime.Value));
            }
            if (param.EndTime.HasValue)
            {
                whereClause += " AND d.reported_at <= @endTime";
                parameters.Add(new NpgsqlParameter("@endTime", param.EndTime.Value));
            }

            var sql = $@"
                SELECT d.disaster_id, d.disaster_type, d.consequence_index, d.reporter, d.reporter_device,
                       d.reporter_ip, d.reported_at, d.status, d.lon, d.lat, d.description,
                       d.images, d.impact_level, d.impact_radius_m, d.confirm_count, d.reviewed_at,
                       d.reviewed_by, d.review_comment,
                       t.type_name, t.consequence_options, u.real_name as reviewer_name
                FROM disasters d
                LEFT JOIN disaster_types t ON d.disaster_type = t.type_code
                LEFT JOIN users u ON d.reviewed_by = u.user_id
                {whereClause}
                ORDER BY d.reported_at DESC";

            try
            {
                using (var conn = new NpgsqlConnection(_connectionString))
                using (var cmd = new NpgsqlCommand(sql, conn))
                {
                    cmd.Parameters.AddRange(parameters.ToArray());
                    await conn.OpenAsync();
                    using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.CloseConnection);
                    while (await reader.ReadAsync())
                    {
                        list.Add(MapDisasterFromReader(reader));
                    }
                }

                return Ok(new { success = true, data = list, count = list.Count });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] 查询灾情列表失败: {ex.Message}");
                Console.WriteLine($"[ERROR] SQL: {sql}");
                return StatusCode(500, new { success = false, message = $"查询失败: {ex.Message}" });
            }
        }

        /// <summary>
        /// GET: /api/disaster/types
        /// 获取灾情类型列表（含后果选项）
        /// </summary>
        [HttpGet("types")]
        public async Task<IActionResult> GetTypes()
        {
            var list = new List<DisasterType>();

            const string sql = "SELECT * FROM disaster_types ORDER BY type_code";

            using (var conn = new NpgsqlConnection(_connectionString))
            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                await conn.OpenAsync();
                using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.CloseConnection);
                while (await reader.ReadAsync())
                {
                    var optionsJson = reader.GetString(2);
                    list.Add(new DisasterType
                    {
                        TypeCode = reader.GetString(0),
                        TypeName = reader.GetString(1),
                        ConsequenceOptions = JsonSerializer.Deserialize<List<string>>(optionsJson) ?? new List<string>(),
                        RadiusLevel1 = Convert.ToInt32(reader.GetValue(3)),
                        RadiusLevel2 = Convert.ToInt32(reader.GetValue(4)),
                        RadiusLevel3 = Convert.ToInt32(reader.GetValue(5))
                    });
                }
            }

            return Ok(new { success = true, data = list });
        }

        #endregion

        #region 3. 灾情详情与审核

        /// <summary>
        /// GET: /api/disaster/{id}
        /// 获取灾情详情
        /// </summary>
        [HttpGet("{id:int}")]
        public async Task<IActionResult> GetById(int id)
        {
            try
            {
                const string sql = @"
                    SELECT d.disaster_id, d.disaster_type, d.consequence_index, d.reporter, d.reporter_device,
                           d.reporter_ip, d.reported_at, d.status, d.lon, d.lat, d.description,
                           d.images, d.impact_level, d.impact_radius_m, d.confirm_count, d.reviewed_at,
                           d.reviewed_by, d.review_comment,
                           t.type_name, t.consequence_options, u.real_name as reviewer_name
                    FROM disasters d
                    LEFT JOIN disaster_types t ON d.disaster_type = t.type_code
                    LEFT JOIN users u ON d.reviewed_by = u.user_id
                    WHERE d.disaster_id = @id";

                Disaster? disaster = null;
                using (var conn = new NpgsqlConnection(_connectionString))
                using (var cmd = new NpgsqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@id", id);
                    await conn.OpenAsync();
                    using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.CloseConnection);
                    if (await reader.ReadAsync())
                    {
                        disaster = MapDisasterFromReader(reader);
                    }
                }

                if (disaster == null)
                    return NotFound(new { success = false, message = "灾情不存在" });

                return Ok(new { success = true, data = disaster });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] 查询灾情详情失败: {ex.Message}");
                return StatusCode(500, new { success = false, message = $"查询失败: {ex.Message}" });
            }
        }

        /// <summary>
        /// POST: /api/disaster/{id}/review
        /// 审核灾情（通过/驳回）
        /// </summary>
        [HttpPost("{id:int}/review")]
        public async Task<IActionResult> Review(int id, [FromBody] DisasterReviewRequest request)
        {
            // TODO: 从JWT获取当前用户ID
            int reviewerId = 1;  // 临时使用admin

            if (request.Status != "已通过" && request.Status != "已驳回")
            {
                return BadRequest(new { success = false, message = "审核状态只能是'已通过'或'已驳回'" });
            }

            const string sql = @"
                UPDATE disasters 
                SET status = @status, reviewed_at = NOW(), reviewed_by = @reviewerId, review_comment = @comment
                WHERE disaster_id = @id;
                
                SELECT status FROM disasters WHERE disaster_id = @id;";

            string newStatus;
            using (var conn = new NpgsqlConnection(_connectionString))
            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@id", id);
                cmd.Parameters.AddWithValue("@status", request.Status);
                cmd.Parameters.AddWithValue("@reviewerId", reviewerId);
                cmd.Parameters.AddWithValue("@comment", (object?)request.Comment ?? DBNull.Value);

                await conn.OpenAsync();
                var result = await cmd.ExecuteScalarAsync();
                if (result == null)
                    return NotFound(new { success = false, message = "灾情不存在" });
                
                newStatus = result.ToString()!;
            }

            return Ok(new { success = true, message = $"审核成功，状态: {newStatus}", status = newStatus });
        }

        #endregion

        #region 4. 众包验证

        /// <summary>
        /// GET: /api/disaster/nearby?type=&lat=&lon=&radius=500&hours=72
        /// 查询附近同类灾情（用于众包验证）
        /// </summary>
        [HttpGet("nearby")]
        public async Task<IActionResult> GetNearby(
            [FromQuery] string type,
            [FromQuery] double lat,
            [FromQuery] double lon,
            [FromQuery] int radius = 500,
            [FromQuery] int hours = 72)
        {
            const string sql = @"
                SELECT d.disaster_id, d.disaster_type, d.lon, d.lat, d.reported_at, d.status,
                       6371000 * ACOS(
                           COS(RADIANS(@lat)) * COS(RADIANS(d.lat)) * 
                           COS(RADIANS(d.lon) - RADIANS(@lon)) + 
                           SIN(RADIANS(@lat)) * SIN(RADIANS(d.lat))
                       ) AS distance_m
                FROM disasters d
                WHERE d.disaster_type = @type
                  AND d.reported_at >= NOW() - INTERVAL '@hours hours'
                  AND d.status IN ('待审核', '已确认', '已通过')
                  AND 6371000 * ACOS(
                      COS(RADIANS(@lat)) * COS(RADIANS(d.lat)) * 
                      COS(RADIANS(d.lon) - RADIANS(@lon)) + 
                      SIN(RADIANS(@lat)) * SIN(RADIANS(d.lat))
                  ) <= @radius
                ORDER BY distance_m";

            var list = new List<object>();
            using (var conn = new NpgsqlConnection(_connectionString))
            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@type", type);
                cmd.Parameters.AddWithValue("@lat", lat);
                cmd.Parameters.AddWithValue("@lon", lon);
                cmd.Parameters.AddWithValue("@radius", radius);
                cmd.Parameters.AddWithValue("@hours", hours);

                await conn.OpenAsync();
                using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.CloseConnection);
                while (await reader.ReadAsync())
                {
                    list.Add(new
                    {
                        id = reader.GetInt32(0),
                        type = reader.GetString(1),
                        lon = reader.GetDouble(2),
                        lat = reader.GetDouble(3),
                        reportedAt = reader.GetDateTime(4),
                        status = reader.GetString(5),
                        distanceM = reader.GetDouble(6)
                    });
                }
            }

            return Ok(new { success = true, count = list.Count, data = list });
        }

        #endregion

        #region 私有方法

        /// <summary>
        /// 检查众包验证并更新状态
        /// </summary>
        private async Task<CrowdVerificationResult> CheckCrowdVerification(int disasterId, string type, double lon, double lat, string? deviceId)
        {
            // 查询附近同类灾情（排除当前记录）
            const string sql = @"
                SELECT COUNT(DISTINCT COALESCE(reporter_device, disaster_id::text)) as unique_reporters
                FROM disasters
                WHERE disaster_type = @type
                  AND disaster_id != @id
                  AND reported_at >= NOW() - INTERVAL '72 hours'
                  AND status IN ('待审核', '已确认', '已通过')
                  AND 6371000 * ACOS(
                      COS(RADIANS(@lat)) * COS(RADIANS(lat)) * 
                      COS(RADIANS(lon) - RADIANS(@lon)) + 
                      SIN(RADIANS(@lat)) * SIN(RADIANS(lat))
                  ) <= 500";

            int nearbyCount = 0;
            using (var conn = new NpgsqlConnection(_connectionString))
            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@type", type);
                cmd.Parameters.AddWithValue("@id", disasterId);
                cmd.Parameters.AddWithValue("@lat", lat);
                cmd.Parameters.AddWithValue("@lon", lon);

                await conn.OpenAsync();
                var result = await cmd.ExecuteScalarAsync();
                nearbyCount = result != DBNull.Value ? Convert.ToInt32(result) : 0;
            }

            int totalConfirm = nearbyCount + 1;
            bool autoConfirmed = totalConfirm >= 3;

            // 更新确认人数和状态
            const string updateSql = @"
                UPDATE disasters 
                SET confirm_count = @count, status = CASE WHEN @confirmed = 1 THEN '已确认' ELSE status END
                WHERE disaster_id = @id";

            using (var conn = new NpgsqlConnection(_connectionString))
            using (var cmd = new NpgsqlCommand(updateSql, conn))
            {
                cmd.Parameters.AddWithValue("@id", disasterId);
                cmd.Parameters.AddWithValue("@count", totalConfirm);
                cmd.Parameters.AddWithValue("@confirmed", autoConfirmed ? 1 : 0);
                await conn.OpenAsync();
                await cmd.ExecuteNonQueryAsync();
            }

            // 如果自动确认，同时更新其他关联记录的确认人数
            if (autoConfirmed && nearbyCount > 0)
            {
                const string batchUpdateSql = @"
                    UPDATE disasters 
                    SET confirm_count = confirm_count + 1
                    WHERE disaster_type = @type
                      AND reported_at >= NOW() - INTERVAL '72 hours'
                      AND status IN ('待审核', '已确认')
                      AND 6371000 * ACOS(
                          COS(RADIANS(@lat)) * COS(RADIANS(lat)) * 
                          COS(RADIANS(lon) - RADIANS(@lon)) + 
                          SIN(RADIANS(@lat)) * SIN(RADIANS(lat))
                      ) <= 500";

                using (var conn = new NpgsqlConnection(_connectionString))
                using (var cmd = new NpgsqlCommand(batchUpdateSql, conn))
                {
                    cmd.Parameters.AddWithValue("@type", type);
                    cmd.Parameters.AddWithValue("@lat", lat);
                    cmd.Parameters.AddWithValue("@lon", lon);
                    await conn.OpenAsync();
                    await cmd.ExecuteNonQueryAsync();
                }
            }

            return new CrowdVerificationResult
            {
                NearbyCount = nearbyCount,
                TotalConfirmCount = totalConfirm,
                AutoConfirmed = autoConfirmed
            };
        }

        /// <summary>
        /// 从DataReader映射Disaster实体
        /// </summary>
        private Disaster MapDisasterFromReader(NpgsqlDataReader reader)
        {
            var disaster = new Disaster();

            // 获取列名集合
            var columnNames = new HashSet<string>();
            for (int i = 0; i < reader.FieldCount; i++)
            {
                columnNames.Add(reader.GetName(i).ToLower());
            }

            // 基础字段（一定有）
            disaster.DisasterId = reader.GetInt32(reader.GetOrdinal("disaster_id"));
            disaster.DisasterType = reader.GetString(reader.GetOrdinal("disaster_type"));
            disaster.ConsequenceIndex = SafeGetInt32(reader, "consequence_index") ?? 0;
            disaster.ReportedAt = reader.GetDateTime(reader.GetOrdinal("reported_at"));
            disaster.Status = reader.GetString(reader.GetOrdinal("status"));
            // SQL Server中lon/lat可能是decimal类型，需要安全转换
            disaster.Lon = Convert.ToDouble(reader.GetValue(reader.GetOrdinal("lon")));
            disaster.Lat = Convert.ToDouble(reader.GetValue(reader.GetOrdinal("lat")));
            disaster.ImpactLevel = SafeGetInt32(reader, "impact_level") ?? 1;

            // 可选字段 - 安全读取
            disaster.ReporterId = SafeGetInt32(reader, "reporter");
            disaster.ReporterDevice = SafeGetString(reader, "reporter_device");
            disaster.ReporterIp = SafeGetString(reader, "reporter_ip");
            disaster.Address = SafeGetString(reader, "address");
            disaster.Description = SafeGetString(reader, "description");
            disaster.Images = SafeGetString(reader, "images");
            disaster.ImpactRadiusM = SafeGetInt32(reader, "impact_radius_m") ?? (disaster.ImpactLevel == 3 ? 200 : disaster.ImpactLevel == 2 ? 100 : 50);
            disaster.ConfirmCount = SafeGetInt32(reader, "confirm_count") ?? 1;
            disaster.ReviewedAt = SafeGetDateTime(reader, "reviewed_at");
            disaster.ReviewedBy = SafeGetInt32(reader, "reviewed_by");
            disaster.ReviewComment = SafeGetString(reader, "review_comment");

            // JOIN字段
            disaster.TypeName = SafeGetString(reader, "type_name") ?? "";
            disaster.ReviewerName = SafeGetString(reader, "reviewer_name");

            // 从consequence_options JSON中解析后果描述
            var consequenceOptionsJson = SafeGetString(reader, "consequence_options");
            if (!string.IsNullOrEmpty(consequenceOptionsJson))
            {
                try
                {
                    var options = JsonSerializer.Deserialize<List<string>>(consequenceOptionsJson);
                    if (options != null && disaster.ConsequenceIndex > 0 && disaster.ConsequenceIndex <= options.Count)
                    {
                        disaster.ConsequenceText = options[disaster.ConsequenceIndex - 1];
                    }
                }
                catch { /* 解析失败则留空 */ }
            }

            // Color是计算属性，根据ImpactLevel自动计算，无需赋值

            return disaster;
        }

        // 安全读取辅助方法
        private int? SafeGetInt32(NpgsqlDataReader reader, string columnName)
        {
            try
            {
                var ordinal = reader.GetOrdinal(columnName);
                if (reader.IsDBNull(ordinal)) return null;
                // 先读取为对象，再尝试转换为整数
                var value = reader.GetValue(ordinal);
                if (value is int intValue) return intValue;
                if (value is string strValue && int.TryParse(strValue, out var parsed))
                    return parsed;
                return null;
            }
            catch (IndexOutOfRangeException)
            {
                return null;
            }
            catch (InvalidCastException)
            {
                return null;
            }
        }

        private string? SafeGetString(NpgsqlDataReader reader, string columnName)
        {
            try
            {
                var ordinal = reader.GetOrdinal(columnName);
                return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
            }
            catch (IndexOutOfRangeException)
            {
                return null;
            }
        }

        private DateTime? SafeGetDateTime(NpgsqlDataReader reader, string columnName)
        {
            try
            {
                var ordinal = reader.GetOrdinal(columnName);
                return reader.IsDBNull(ordinal) ? null : reader.GetDateTime(ordinal);
            }
            catch (IndexOutOfRangeException)
            {
                return null;
            }
        }

        #endregion
    }
}
