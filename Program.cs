using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// 🔥 强制端口 1000（Render 唯一要求）
builder.WebHost.UseUrls("http://0.0.0.0:1000");

builder.Services.AddControllers();

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

// 生产环境也启用 swagger
if (app.Environment.IsDevelopment() || app.Environment.IsProduction())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// 关闭 HTTPS 重定向
// app.UseHttpsRedirection();

app.UseStaticFiles();
app.UseCors("AllowAll");
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();