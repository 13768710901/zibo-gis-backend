# 构建阶段
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 复制所有文件到构建环境根目录
COPY . .

# 还原依赖并构建发布版本
RUN dotnet restore "ZIBOGIS.csproj"
RUN dotnet publish "ZIBOGIS.csproj" -c Release -o /app/publish

# 运行阶段
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# 从构建阶段复制发布结果
COPY --from=build /app/publish .

# 强制 Render 端口 1000
ENV ASPNETCORE_URLS=http://0.0.0.0:1000

# 启动应用（使用默认程序集名 ZIBOGIS.dll）
ENTRYPOINT ["dotnet", "ZIBOGIS.dll"]