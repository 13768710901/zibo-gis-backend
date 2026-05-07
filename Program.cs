using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// 🔥 强制端口 1000（Render 唯一认）
builder.WebHost.UseUrls("http://0.0.0.0:1000");

// 🔥 从环境变量读取PostgreSQL连接字符串
var pgConnectionString = Environment.GetEnvironmentVariable("PG_CONNECTION_STRING");
Console.WriteLine($"[DEBUG] PG_CONNECTION_STRING: {(string.IsNullOrEmpty(pgConnectionString) ? "NULL/EMPTY" : "FOUND")}");
if (!string.IsNullOrEmpty(pgConnectionString))
{
    builder.Configuration["ConnectionStrings:DefaultConnection"] = pgConnectionString;
    Console.WriteLine($"[DEBUG] Connection string set: {pgConnectionString.Substring(0, 30)}...");
}
else
{
    Console.WriteLine("[DEBUG] PG_CONNECTION_STRING not found in environment");
}

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