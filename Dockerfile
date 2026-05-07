# 构建阶段
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["ZIBOGIS.csproj", "."]
RUN dotnet restore
COPY . .
RUN dotnet publish -c Release -o /app/publish

# 运行阶段
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# 🔥 Render 强制端口 1000
ENV PORT=1000
ENV ASPNETCORE_URLS=http://0.0.0.0:1000

# 启动应用
ENTRYPOINT ["dotnet", "ZIBOGIS.dll"]