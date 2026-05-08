using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Text.Json;
using System.Linq;
using System;
using System.Data;

namespace ZIBOGIS.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class FacilitiesController : ControllerBase
    {
        private readonly string _connectionString;

        public FacilitiesController(IConfiguration configuration)
        {
            _connectionString = configuration.GetConnectionString("DefaultConnection")
                               ?? throw new InvalidOperationException("Missing connection string 'DefaultConnection'");
        }

        // GET: https://localhost:7274/api/facilities
        [HttpGet]
        public async Task<IActionResult> GetAll()
        {
            var list = new List<object>();

            const string sql = @"
                SELECT id, Name, Type, longitude, latitude, address
                FROM facilities
                ORDER BY id;";

            using (var conn = new NpgsqlConnection(_connectionString))
            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                await conn.OpenAsync();
                using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.CloseConnection);
                while (await reader.ReadAsync())
                {
                    list.Add(new
                    {
                        id = reader.GetInt32(0),
                        name = reader.GetString(1),
                        type = reader.IsDBNull(2) ? null : reader.GetString(2),
                        lon = reader.GetDouble(3),
                        lat = reader.GetDouble(4),
                        address = reader.IsDBNull(5) ? null : reader.GetString(5)
                    });
                }
            }

            return Ok(list);
        }

        // GET: https://localhost:7274/api/facilities/stats
        [HttpGet("stats")]
        public async Task<IActionResult> GetFacilitiesByGrid()
        {
            // 张店区范围（扩大覆盖范围）
            const double minLon = 117.9;
            const double maxLon = 118.1;
            const double minLat = 36.7;
            const double maxLat = 36.9;
            const double gridSize = 0.009; // 约1km

            var facilities = new List<(double lon, double lat, string type)>();

            // 获取所有设施
            const string sql = @"
                SELECT longitude, latitude, Type
                FROM facilities
                WHERE longitude IS NOT NULL AND latitude IS NOT NULL;";

            using (var conn = new NpgsqlConnection(_connectionString))
            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                await conn.OpenAsync();
                using var reader = await cmd.ExecuteReaderAsync(CommandBehavior.CloseConnection);
                while (await reader.ReadAsync())
                {
                    facilities.Add((
                        reader.GetDouble(0),
                        reader.GetDouble(1),
                        reader.IsDBNull(2) ? "其他设施" : reader.GetString(2)
                    ));
                }
            }

            // 网格统计
            var gridStats = new Dictionary<string, (int total, int medical, int education, int shelter, int resident, int commercial, int other)>();

            foreach (var (lon, lat, type) in facilities)
            {
                if (lon < minLon || lon > maxLon || lat < minLat || lat > maxLat) continue;

                int gridX = (int)Math.Floor((lon - minLon) / gridSize);
                int gridY = (int)Math.Floor((lat - minLat) / gridSize);
                string key = $"{gridX}_{gridY}";

                if (!gridStats.ContainsKey(key))
                {
                    gridStats[key] = (0, 0, 0, 0, 0, 0, 0);
                }

                var (total, medical, education, shelter, resident, commercial, other) = gridStats[key];
                total++;

                // 归一化类型
                var normalizedType = NormalizeFacilityType(type);
                switch (normalizedType)
                {
                    case "医疗卫生": medical++; break;
                    case "教育服务": education++; break;
                    case "应急避难": shelter++; break;
                    case "居民/小区": resident++; break;
                    case "商业/商场": commercial++; break;
                    default: other++; break;
                }

                gridStats[key] = (total, medical, education, shelter, resident, commercial, other);
            }

            // 转换为前端需要的格式
            var result = new
            {
                grids = gridStats.Select(kvp =>
                {
                    var parts = kvp.Key.Split('_');
                    int gridX = int.Parse(parts[0]);
                    int gridY = int.Parse(parts[1]);
                    double gridMinLon = minLon + gridX * gridSize;
                    double gridMinLat = minLat + gridY * gridSize;

                    var (total, medical, education, shelter, resident, commercial, other) = kvp.Value;

                    return new
                    {
                        x = Math.Round(gridMinLon, 4),
                        y = Math.Round(gridMinLat, 4),
                        count = total,
                        medical = medical,
                        education = education,
                        shelter = shelter,
                        resident = resident,
                        commercial = commercial,
                        other = other
                    };
                }).ToList()
            };

            return Ok(result);
        }

        private string NormalizeFacilityType(string type)
        {
            if (string.IsNullOrEmpty(type)) return "其他设施";
            var t = type.ToLower();
            if (t.Contains("医") || t.Contains("hospital")) return "医疗卫生";
            if (t.Contains("教育") || t.Contains("学") || t.Contains("school")) return "教育服务";
            if (t.Contains("避") || t.Contains("应急") || t.Contains("shelter")) return "应急避难";
            if (t.Contains("居民") || t.Contains("小区") || t.Contains("社区")) return "居民/小区";
            if (t.Contains("商业") || t.Contains("商场") || t.Contains("超市") || t.Contains("mall")) return "商业/商场";
            return "其他设施";
        }

        // POST: https://localhost:7274/api/facilities
        [HttpPost]
        public async Task<IActionResult> Create([FromBody] FacilityDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            const string sql = @"
                INSERT INTO facilities (Name, Type, longitude, latitude, address, createdat)
                VALUES (@Name, @Type, @Longitude, @Latitude, @Address, NOW())
                RETURNING id;";

            int newId;
            using (var conn = new NpgsqlConnection(_connectionString))
            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@Name", dto.Name);
                cmd.Parameters.AddWithValue("@Type", (object?)dto.Type ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Longitude", dto.Longitude);
                cmd.Parameters.AddWithValue("@Latitude", dto.Latitude);
                cmd.Parameters.AddWithValue("@Address", (object?)dto.Address ?? DBNull.Value);

                await conn.OpenAsync();
                newId = (int)await cmd.ExecuteScalarAsync();
            }

            return Ok(new
            {
                id = newId,
                name = dto.Name,
                type = dto.Type,
                lon = dto.Longitude,
                lat = dto.Latitude,
                address = dto.Address
            });
        }

        // PUT: https://localhost:7274/api/facilities/{id}
        [HttpPut("{id}")]
        public async Task<IActionResult> Update(int id, [FromBody] FacilityDto dto)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            const string sql = @"
                UPDATE facilities
                   SET Name = @Name,
                       Type = @Type,
                       longitude = @Longitude,
                       latitude = @Latitude,
                       address = @Address
                 WHERE id = @Id;";

            int affected;
            using (var conn = new NpgsqlConnection(_connectionString))
            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@Id", id);
                cmd.Parameters.AddWithValue("@Name", dto.Name);
                cmd.Parameters.AddWithValue("@Type", (object?)dto.Type ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@Longitude", dto.Longitude);
                cmd.Parameters.AddWithValue("@Latitude", dto.Latitude);
                cmd.Parameters.AddWithValue("@Address", (object?)dto.Address ?? DBNull.Value);

                await conn.OpenAsync();
                affected = await cmd.ExecuteNonQueryAsync();
            }

            if (affected == 0)
            {
                return NotFound();
            }

            return NoContent();
        }

        // DELETE: https://localhost:7274/api/facilities/{id}
        [HttpDelete("{id}")]
        public async Task<IActionResult> Delete(int id)
        {
            const string sql = @"DELETE FROM facilities WHERE id = @Id";

            int affected;
            using (var conn = new NpgsqlConnection(_connectionString))
            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@Id", id);

                await conn.OpenAsync();
                affected = await cmd.ExecuteNonQueryAsync();
            }

            if (affected == 0)
            {
                return NotFound();
            }

            return NoContent();
        }

        public class FacilityDto
        {
            public string Name { get; set; } = null!;
            public string? Type { get; set; }
            public double Longitude { get; set; }
            public double Latitude { get; set; }
            public string? Address { get; set; }
        }
    }
}