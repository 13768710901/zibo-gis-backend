using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace ZIBOGIS.Controllers
{
    [ApiController]
    [Route("api/auth")]
    public class AuthController : ControllerBase
    {
        private readonly IConfiguration _configuration;
        private readonly string _connectionString;

        public AuthController(IConfiguration configuration)
        {
            _configuration = configuration;
            _connectionString = _configuration.GetConnectionString("DefaultConnection")!;
        }

        /// <summary>
        /// POST: /api/auth/register
        /// 用户注册
        /// </summary>
        [HttpPost("register")]
        public async Task<IActionResult> Register([FromBody] RegisterRequest request)
        {
            try
            {
                // 检查用户名是否已存在
                const string checkSql = "SELECT COUNT(*) FROM users WHERE username = @username";
                using (var conn = new NpgsqlConnection(_connectionString))
                using (var cmd = new NpgsqlCommand(checkSql, conn))
                {
                    cmd.Parameters.AddWithValue("@username", request.Username);
                    await conn.OpenAsync();
                    var count = (int)await cmd.ExecuteScalarAsync();
                    if (count > 0)
                    {
                        return BadRequest(new { success = false, message = "用户名已存在" });
                    }
                }

                // 密码SHA256哈希
                var passwordHash = ComputeSha256Hash(request.Password);

                // 插入新用户，默认角色为user
                const string insertSql = @"
                    INSERT INTO users (username, password_hash, real_name, role, status, created_at)
                    VALUES (@username, @password_hash, @real_name, 'user', 'active', NOW())
                    RETURNING user_id;";

                int newUserId;
                using (var conn = new NpgsqlConnection(_connectionString))
                using (var cmd = new NpgsqlCommand(insertSql, conn))
                {
                    cmd.Parameters.AddWithValue("@username", request.Username);
                    cmd.Parameters.AddWithValue("@password_hash", passwordHash);
                    cmd.Parameters.AddWithValue("@real_name", (object?)request.RealName ?? DBNull.Value);
                    
                    await conn.OpenAsync();
                    newUserId = Convert.ToInt32(await cmd.ExecuteScalarAsync());
                }

                return Ok(new
                {
                    success = true,
                    message = "注册成功",
                    userId = newUserId
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] 注册失败: {ex.Message}");
                return StatusCode(500, new { success = false, message = "注册失败，请稍后重试" });
            }
        }

        /// <summary>
        /// POST: /api/auth/login
        /// 用户登录
        /// </summary>
        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] LoginRequest request)
        {
            try
            {
                // 验证用户凭据
                User? user = null;
                string? passwordHash = null;
                
                const string sql = @"
                    SELECT user_id, username, password_hash, real_name, role, status
                    FROM users
                    WHERE username = @username";

                using (var conn = new NpgsqlConnection(_connectionString))
                using (var cmd = new NpgsqlCommand(sql, conn))
                {
                    cmd.Parameters.AddWithValue("@username", request.Username);

                    await conn.OpenAsync();
                    using var reader = await cmd.ExecuteReaderAsync();

                    if (await reader.ReadAsync())
                    {
                        user = new User
                        {
                            UserId = reader.GetInt32(0),
                            Username = reader.GetString(1),
                            RealName = reader.IsDBNull(3) ? null : reader.GetString(3),
                            Role = reader.GetString(4),
                            Status = reader.GetString(5)
                        };
                        passwordHash = reader.GetString(2);
                    }
                }

                if (user == null || passwordHash == null)
                {
                    return Unauthorized(new { success = false, message = "用户名或密码错误" });
                }

                // 验证密码（前端传来的明文密码计算SHA256后与数据库比对）
                var inputHash = ComputeSha256Hash(request.Password);
                if (inputHash != passwordHash)
                {
                    return Unauthorized(new { success = false, message = "用户名或密码错误" });
                }

                if (user.Status.ToLower() != "active")
                {
                    return Unauthorized(new { success = false, message = "账号已被禁用" });
                }

                // 生成JWT Token
                var token = GenerateJwtToken(user);

                return Ok(new
                {
                    success = true,
                    message = "登录成功",
                    token = token,
                    user = new
                    {
                        userId = user.UserId,
                        username = user.Username,
                        realName = user.RealName,
                        role = user.Role
                    }
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] 登录失败: {ex.Message}");
                return StatusCode(500, new { success = false, message = "登录失败，请稍后重试" });
            }
        }

        /// <summary>
        /// GET: /api/auth/me
        /// 获取当前登录用户信息
        /// </summary>
        [HttpGet("me")]
        public async Task<IActionResult> GetCurrentUser()
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            var username = User.FindFirst(ClaimTypes.Name)?.Value;
            var role = User.FindFirst(ClaimTypes.Role)?.Value;

            if (userId == null)
            {
                return Unauthorized(new { success = false, message = "未登录" });
            }

            // 从数据库获取完整的用户信息（包括电话和邮箱）
            User? user = null;
            const string sql = @"
                SELECT user_id, username, real_name, phone, email, role, status
                FROM users
                WHERE user_id = @userId";

            using (var conn = new NpgsqlConnection(_connectionString))
            using (var cmd = new NpgsqlCommand(sql, conn))
            {
                cmd.Parameters.AddWithValue("@userId", int.Parse(userId));
                await conn.OpenAsync();
                using var reader = await cmd.ExecuteReaderAsync();

                if (await reader.ReadAsync())
                {
                    user = new User
                    {
                        UserId = reader.GetInt32(0),
                        Username = reader.GetString(1),
                        RealName = reader.IsDBNull(2) ? null : reader.GetString(2),
                        Phone = reader.IsDBNull(3) ? null : reader.GetString(3),
                        Email = reader.IsDBNull(4) ? null : reader.GetString(4),
                        Role = reader.GetString(5),
                        Status = reader.GetString(6)
                    };
                }
            }

            if (user == null)
            {
                return NotFound(new { success = false, message = "用户不存在" });
            }

            return Ok(new
            {
                success = true,
                user = new
                {
                    userId = user.UserId,
                    username = user.Username,
                    realName = user.RealName,
                    phone = user.Phone,
                    email = user.Email,
                    role = user.Role,
                    status = user.Status
                }
            });
        }

        /// <summary>
        /// PUT: /api/auth/profile
        /// 更新当前用户个人信息（电话和邮箱）
        /// </summary>
        [HttpPut("profile")]
        public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequest request)
        {
            try
            {
                var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
                if (userId == null)
                {
                    return Unauthorized(new { success = false, message = "未登录" });
                }

                // 验证输入
                // 电话验证：11位数字，以1开头
                if (!string.IsNullOrEmpty(request.Phone))
                {
                    if (request.Phone.Length > 20)
                    {
                        return BadRequest(new { success = false, message = "电话号码过长" });
                    }
                    // 中国手机号格式验证：11位，以1开头
                    if (!System.Text.RegularExpressions.Regex.IsMatch(request.Phone, @"^1\d{10}$"))
                    {
                        return BadRequest(new { success = false, message = "请输入正确的11位手机号码" });
                    }
                }

                // 邮箱验证：标准邮箱格式
                if (!string.IsNullOrEmpty(request.Email))
                {
                    if (request.Email.Length > 100)
                    {
                        return BadRequest(new { success = false, message = "邮箱地址过长" });
                    }
                    // 邮箱格式验证
                    var emailPattern = @"^[^@\s]+@[^@\s]+\.[^@\s]+$";
                    if (!System.Text.RegularExpressions.Regex.IsMatch(request.Email, emailPattern))
                    {
                        return BadRequest(new { success = false, message = "请输入正确的邮箱格式" });
                    }
                }

                // 更新用户信息
                const string updateSql = @"
                    UPDATE users 
                    SET phone = @phone, email = @email, last_login_at = NOW()
                    WHERE user_id = @userId";

                using (var conn = new NpgsqlConnection(_connectionString))
                using (var cmd = new NpgsqlCommand(updateSql, conn))
                {
                    cmd.Parameters.AddWithValue("@phone", (object?)request.Phone ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@email", (object?)request.Email ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@userId", int.Parse(userId));

                    await conn.OpenAsync();
                    var rowsAffected = await cmd.ExecuteNonQueryAsync();

                    if (rowsAffected == 0)
                    {
                        return NotFound(new { success = false, message = "用户不存在" });
                    }
                }

                return Ok(new
                {
                    success = true,
                    message = "个人信息更新成功"
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] 更新个人信息失败: {ex.Message}");
                return StatusCode(500, new { success = false, message = "更新失败，请稍后重试" });
            }
        }

        private string ComputeSha256Hash(string rawData)
        {
            using (SHA256 sha256Hash = SHA256.Create())
            {
                byte[] bytes = sha256Hash.ComputeHash(Encoding.UTF8.GetBytes(rawData));
                StringBuilder builder = new StringBuilder();
                for (int i = 0; i < bytes.Length; i++)
                {
                    builder.Append(bytes[i].ToString("x2"));
                }
                return builder.ToString();
            }
        }

        private string GenerateJwtToken(User user)
        {
            var jwtKey = _configuration["Jwt:Key"] ?? "your-secret-key-here-at-least-32-characters";
            var jwtIssuer = _configuration["Jwt:Issuer"] ?? "ZIBOGIS";
            var jwtAudience = _configuration["Jwt:Audience"] ?? "ZIBOGIS-Client";

            var securityKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey));
            var credentials = new SigningCredentials(securityKey, SecurityAlgorithms.HmacSha256);

            var claims = new[]
            {
                new Claim(ClaimTypes.NameIdentifier, user.UserId.ToString()),
                new Claim(ClaimTypes.Name, user.Username),
                new Claim(ClaimTypes.Role, user.Role),
                new Claim("realName", user.RealName ?? "")
            };

            var token = new JwtSecurityToken(
                issuer: jwtIssuer,
                audience: jwtAudience,
                claims: claims,
                expires: DateTime.Now.AddHours(8), // Token有效期8小时
                signingCredentials: credentials
            );

            return new JwtSecurityTokenHandler().WriteToken(token);
        }
    }

    public class LoginRequest
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }

    public class RegisterRequest
    {
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
        public string? RealName { get; set; }
    }

    public class User
    {
        public int UserId { get; set; }
        public string Username { get; set; } = "";
        public string? RealName { get; set; }
        public string? Phone { get; set; }
        public string? Email { get; set; }
        public string Role { get; set; } = "";
        public string Status { get; set; } = "";
    }

    public class UpdateProfileRequest
    {
        public string? Phone { get; set; }
        public string? Email { get; set; }
    }
}
