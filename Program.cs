using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllers();

// JWT Authentication
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

// CORS policy
var corsPolicyName = "AllowAll";
builder.Services.AddCors(options =>
{
    options.AddPolicy(name: corsPolicyName, policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

// ==============================================
// 🔥 修复 1：生产环境也启用 Swagger
// ==============================================
if (app.Environment.IsDevelopment() || app.Environment.IsProduction())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// ==============================================
// 🔥 修复 2：禁用 HTTPS 重定向（Render 免费版必须关）
// ==============================================
// app.UseHttpsRedirection();

// 静态文件
app.UseStaticFiles();

// ==============================================
// 🔥 修复 3：CORS 位置调整到正确位置
// ==============================================
app.UseCors(corsPolicyName);

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

// ==============================================
// 🔥 修复 4：强制绑定端口 1000（Render 要求）
// ==============================================
app.Urls.Add("http://0.0.0.0:1000");

app.Run();