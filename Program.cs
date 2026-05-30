using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

// 🔥 辅助函数：将postgresql://URL转换为Npgsql格式
string ConvertPostgresUrlToNpgsql(string postgresUrl)
{
    var uri = new Uri(postgresUrl);
    var userInfo = uri.UserInfo.Split(':');
    var username = userInfo[0];
    var password = userInfo[1];
    var host = uri.Host;
    var port = uri.Port;
    var database = uri.AbsolutePath.TrimStart('/');

    return $"Host={host};Port={port};Database={database};Username={username};Password={password}";
}

var builder = WebApplication.CreateBuilder(args);

// 🔥 强制端口 1000（Render 唯一认）
builder.WebHost.UseUrls("http://0.0.0.0:1000");

// 🔥 从环境变量读取PostgreSQL连接字符串
var dbUrl = Environment.GetEnvironmentVariable("DATABASE_URL");
var logger = LoggerFactory.Create(config => config.AddConsole()).CreateLogger<Program>();
logger.LogInformation($"[DEBUG] DATABASE_URL: {(string.IsNullOrEmpty(dbUrl) ? "NULL/EMPTY" : "FOUND")}");

string pgConnectionString;
if (!string.IsNullOrEmpty(dbUrl))
{
    // 将postgresql://格式转换为Npgsql格式
    pgConnectionString = ConvertPostgresUrlToNpgsql(dbUrl);
    logger.LogInformation($"[DEBUG] Connection string from DATABASE_URL: {pgConnectionString.Substring(0, 30)}...");
}
else
{
    // 🔥 临时硬编码连接字符串用于测试
    logger.LogWarning("[DEBUG] DATABASE_URL not found, using hardcoded connection string");
    pgConnectionString = "Host=dpg-d8d7mk4p3tds73f8uqv0-a;Database=zibo_gis_db_us7h;Username=zibo_gis_db_us7h_user;Password=QR6i28uFFLK5HqmqAc7wteQKWMZkr5cC;Port=5432";
    logger.LogInformation($"[DEBUG] Hardcoded connection string: {pgConnectionString.Substring(0, 30)}...");
}

builder.Configuration["ConnectionStrings:DefaultConnection"] = pgConnectionString;

builder.Services.AddControllers();

// JWT
var jwtKey = builder.Configuration["Jwt:Key"] ?? "your-secret-key-here-at-least-32-characters";
var jwtIssuer = builder.Configuration["Jwt:Issuer"] ?? "ZIBOGIS";
var jwtAudience = builder.Configuration["Jwt:Audience"] ?? "ZIBOGIS-Client";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = jwtIssuer,
            ValidAudience = jwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient("Amap", client =>
{
    client.BaseAddress = new Uri("https://restapi.amap.com/");
    client.Timeout = TimeSpan.FromSeconds(12);
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// 跨域全开
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

// ==========================================
// 🔥 直接强制启用 swagger，不判断环境！
// ==========================================
app.UseSwagger();
app.UseSwaggerUI(c =>
{
    c.SwaggerEndpoint("/swagger/v1/swagger.json", "API");
});

// 关闭 https 重定向
// app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseCors("AllowAll"); // 🔥 跨域放最前面，保证生效
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();