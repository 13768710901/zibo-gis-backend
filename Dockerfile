FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 复制你的项目文件并还原依赖
COPY ["ZIBOGIS.csproj", "."]
RUN dotnet restore

# 复制所有代码并发布
COPY . .
RUN dotnet publish -c Release -o /app/publish

# 运行阶段
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# 监听 Render 要求的端口
ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT}

# 启动你的应用
ENTRYPOINT ["dotnet", "ZIBOGIS.dll"]FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# 复制你的项目文件并还原依赖
COPY ["ZIBOGIS.csproj", "."]
RUN dotnet restore

# 复制所有代码并发布
COPY . .
RUN dotnet publish -c Release -o /app/publish

# 运行阶段
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# 监听 Render 要求的端口
ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT}

# 启动你的应用
ENTRYPOINT ["dotnet", "ZIBOGIS.dll"]