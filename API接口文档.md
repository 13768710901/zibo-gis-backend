# ZIBOGIS 后端 API 接口文档

> 项目路径：`f:\毕设\后端\ZIBOGIS`  
> 技术栈：ASP.NET Core 8 + PostgreSQL  
> 最后更新：2026-05-20

---

## 目录

1. [概述](#1-概述)
2. [认证说明](#2-认证说明)
3. [通用约定](#3-通用约定)
4. [非 API 资源](#4-非-api-资源)
5. [认证模块 `/api/auth`](#5-认证模块-apiauth)
6. [设施管理 `/api/facilities`](#6-设施管理-apifacilities)
7. [设施选址分析 `/api/siteselection`](#7-设施选址分析-apisiteselection)
8. [灾情管理 `/api/disaster`](#8-灾情管理-apidisaster)
9. [高德地图代理 `/api/amap`](#9-高德地图代理-apiamap)
10. [模板接口](#10-模板接口)
11. [接口总览](#11-接口总览)
12. [调用示例](#12-调用示例)

---

## 1. 概述

| 项目 | 说明 |
|------|------|
| 项目名称 | ZIBOGIS |
| API 前缀 | `/api`（业务接口） |
| 路由大小写 | 不区分（`/api/Facilities` 与 `/api/facilities` 等价） |
| 在线文档 | Swagger：`/swagger` |
| 生产 Base URL | `https://zibo-gis-backend.onrender.com` |
| 本地端口 | `http://0.0.0.0:1000`（`Program.cs` 强制，覆盖 launchSettings） |

**数据库**：环境变量 `DATABASE_URL`（`postgresql://` 格式自动转为 Npgsql 连接串）。

**跨域**：`AllowAll`（任意源、任意头、任意方法）。

---

## 2. 认证说明

### 2.1 JWT Bearer

| 配置项 | 默认值 |
|--------|--------|
| Issuer | `ZIBOGIS` |
| Audience | `ZIBOGIS-Client` |
| Key | 配置项 `Jwt:Key`，默认 `your-secret-key-here-at-least-32-characters` |
| 有效期 | **8 小时** |

**请求头**（需要登录的接口）：

```
Authorization: Bearer <token>
```

**密码规则**：注册/登录时，服务端对**明文密码**做 SHA256，得到小写十六进制字符串后与数据库 `password_hash` 比对。

**JWT Claims**：

| Claim | 含义 |
|-------|------|
| `NameIdentifier` | `userId` |
| `Name` | `username` |
| `Role` | `role` |
| `realName` | 真实姓名 |

### 2.2 鉴权现状（重要）

- 项目**未**使用 `[Authorize]` 属性。
- 仅 `GET /api/auth/me`、`PUT /api/auth/profile` 在代码内校验 Claims。
- 设施、灾情、选址、高德代理等接口**默认无需 Token** 即可访问。
- 灾情审核 `reviewed_by` 目前写死为 `1`（TODO：从 JWT 取用户）。

---

## 3. 通用约定

### 3.1 Content-Type

| 场景 | Content-Type |
|------|----------------|
| JSON 请求 | `application/json` |
| 灾情上报 | `multipart/form-data` |
| 高德代理成功 | `application/json`（高德原始 JSON） |

### 3.2 响应格式（不统一）

**包装型**（auth / disaster / siteselection）：

```json
{
  "success": true,
  "message": "可选说明",
  "data": {}
}
```

**设施 GET**：直接返回 JSON 数组。

**设施 PUT/DELETE 成功**：`204 No Content`，无 body。

**设施 POST 成功**：直接返回新建对象，无 `success` 字段。

**ModelState 校验失败**（设施 POST/PUT）：`400` + ASP.NET 默认 `ModelState` 结构。

### 3.3 HTTP 状态码

| 状态码 | 常见场景 |
|--------|----------|
| 200 | 成功 |
| 204 | 设施更新/删除成功 |
| 400 | 参数错误、业务校验失败 |
| 401 | 未登录、凭据错误 |
| 404 | 资源不存在 |
| 429 | 高德 QPS 超限 |
| 500 | 服务器异常 |
| 502 | 高德上游错误 |

---

## 4. 非 API 资源

| 类型 | 路径 | 说明 |
|------|------|------|
| Swagger UI | `/swagger` | 交互式 API 文档 |
| 静态文件 | `/uploads/disasters/{filename}` | 灾情上报图片 |
| 模板接口 | `GET /WeatherForecast` | ASP.NET 默认示例，非业务 |

---

## 5. 认证模块 `/api/auth`

控制器：`Controllers/AuthController.cs`

### 5.1 用户注册

**POST** `/api/auth/register`

**请求体**：

```json
{
  "username": "string",
  "password": "string",
  "realName": "string"
}
```

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| username | string | 是 | 用户名 |
| password | string | 是 | 明文密码（服务端 SHA256） |
| realName | string | 否 | 真实姓名 |

**成功 200**：

```json
{
  "success": true,
  "message": "注册成功",
  "userId": 1
}
```

**失败**：

| 状态码 | 响应 |
|--------|------|
| 400 | `{ "success": false, "message": "用户名已存在" }` |
| 500 | `{ "success": false, "message": "注册失败，请稍后重试" }` |

**说明**：新用户默认 `role = user`，`status = active`。

---

### 5.2 用户登录

**POST** `/api/auth/login`

**请求体**：

```json
{
  "username": "string",
  "password": "string"
}
```

**成功 200**：

```json
{
  "success": true,
  "message": "登录成功",
  "token": "eyJhbGciOiJIUzI1NiIs...",
  "user": {
    "userId": 1,
    "username": "admin",
    "realName": "管理员",
    "role": "admin"
  }
}
```

**失败**：

| 状态码 | message |
|--------|---------|
| 401 | `用户名或密码错误` |
| 401 | `账号已被禁用`（status ≠ active） |
| 500 | `登录失败，请稍后重试` |

---

### 5.3 获取当前用户

**GET** `/api/auth/me`

**需要**：`Authorization: Bearer {token}`

**成功 200**：

```json
{
  "success": true,
  "user": {
    "userId": 1,
    "username": "admin",
    "realName": "管理员",
    "phone": "13800138000",
    "email": "user@example.com",
    "role": "admin",
    "status": "active"
  }
}
```

**失败**：

| 状态码 | message |
|--------|---------|
| 401 | `未登录` |
| 404 | `用户不存在` |

---

### 5.4 更新个人资料

**PUT** `/api/auth/profile`

**需要**：Bearer Token

**请求体**：

```json
{
  "phone": "13800138000",
  "email": "a@b.com"
}
```

| 字段 | 类型 | 必填 | 校验 |
|------|------|------|------|
| phone | string | 否 | 11 位、以 1 开头；最长 20 |
| email | string | 否 | 标准邮箱格式；最长 100 |

**成功 200**：

```json
{
  "success": true,
  "message": "个人信息更新成功"
}
```

**失败**：400（格式错误）、401、404、500

---

## 6. 设施管理 `/api/facilities`

控制器：`Controllers/FacilitiesController.cs`

### 6.1 获取全部设施

**GET** `/api/facilities`

**响应 200**：数组

```json
[
  {
    "id": 1,
    "name": "某某医院",
    "type": "医疗卫生",
    "lon": 118.05,
    "lat": 36.81,
    "address": "淄博市张店区..."
  }
]
```

---

### 6.2 设施网格统计

**GET** `/api/facilities/stats`

**说明**：张店区范围（经度 117.9–118.1，纬度 36.7–36.9），约 1km 网格聚合。

**响应 200**：

```json
{
  "grids": [
    {
      "x": 117.9,
      "y": 36.7,
      "count": 5,
      "medical": 1,
      "education": 2,
      "shelter": 0,
      "resident": 1,
      "commercial": 1,
      "other": 0
    }
  ]
}
```

**类型归一化**：含「医/hospital」→ 医疗卫生；「教育/学/school」→ 教育服务；「避/应急/shelter」→ 应急避难；「居民/小区/社区」→ 居民/小区；「商业/商场/超市/mall」→ 商业/商场；其余 → 其他设施。

---

### 6.3 新增设施

**POST** `/api/facilities`

**请求体**：

```json
{
  "name": "string",
  "type": "string",
  "longitude": 118.05,
  "latitude": 36.81,
  "address": "string"
}
```

| 字段 | 类型 | 必填 |
|------|------|------|
| name | string | 是 |
| type | string | 否 |
| longitude | double | 是 |
| latitude | double | 是 |
| address | string | 否 |

**成功 200**：

```json
{
  "id": 100,
  "name": "某某医院",
  "type": "医疗卫生",
  "lon": 118.05,
  "lat": 36.81,
  "address": "..."
}
```

---

### 6.4 更新设施

**PUT** `/api/facilities/{id}`

**路径参数**：`id`（int）

**请求体**：同 6.3

**成功**：`204 No Content`

**失败**：`404`（设施不存在）；`400`（ModelState）

---

### 6.5 删除设施

**DELETE** `/api/facilities/{id}`

**成功**：`204 No Content`

**失败**：`404`

---

## 7. 设施选址分析 `/api/siteselection`

控制器：`Controllers/SiteSelectionController.cs`

### 7.1 选址分析

**POST** `/api/siteselection/analyze`

**说明**：基于网格供需比、服务能力加权、人口代理（由设施估算）生成 TopN 推荐点。

**请求体**：

```json
{
  "facilityType": "hospital",
  "gridSizeMeters": 1000,
  "bounds": [117.9, 36.7, 118.1, 36.9],
  "topN": 10
}
```

| 字段 | 类型 | 必填 | 默认 | 说明 |
|------|------|------|------|------|
| facilityType | string | 是 | — | 见下表 |
| gridSizeMeters | double | 否 | 1000 | 网格边长（米） |
| bounds | double[4] | 是 | — | `[minLon, minLat, maxLon, maxLat]` |
| topN | int | 否 | 10 | 推荐数量 |

**facilityType 映射**：

| 代码 | 数据库类型匹配 |
|------|----------------|
| hospital | 医疗卫生 |
| school | 教育服务、学校 |
| shelter | 应急避难、避难、应急 |
| commercial | 商业/商场、商业、商场 |
| resident | 居民/小区、居民、小区 |

**成功 200**：

```json
{
  "success": true,
  "gridAnalysis": [
    {
      "gridId": "0_1",
      "centerLon": 118.0,
      "centerLat": 36.75,
      "bounds": [117.95, 36.7, 118.0, 36.75],
      "facilityCount": 2,
      "totalFacilities": 5,
      "residentCount": 3,
      "totalPopulation": 4500,
      "nearbyFacilities": 4,
      "serviceCapacity": 6000,
      "supplyRatio": 0.75,
      "isShortage": true,
      "score": 85.5,
      "serviceCapacityByLevel": { "三甲": 10000 },
      "suggestedLevel": "二甲医院"
    }
  ],
  "recommendations": [
    {
      "rank": 1,
      "gridId": "2_3",
      "lon": 118.02,
      "lat": 36.78,
      "score": 120.5,
      "priority": "high",
      "estimatedPopulation": 8000,
      "existingFacilitiesNearby": 1,
      "serviceGap": 5000,
      "reasons": ["该区域设施严重不足", "周边2km范围内无同类设施，服务盲区"],
      "suggestedFacilityName": "建议新建三甲医院 - 区域综合医疗中心 （缺口大） 【优先选址】"
    }
  ],
  "summary": {
    "totalGrids": 100,
    "shortageGrids": 35,
    "avgSupplyRatio": 1.2
  }
}
```

**priority**：rank ≤ 3 → `high`；≤ 6 → `medium`；其余 → `low`

**失败 500**：`{ "success": false, "message": "选址分析失败，请稍后重试" }`

---

## 8. 灾情管理 `/api/disaster`

控制器：`Controllers/DisasterController.cs`  
模型：`Model/Disaster.cs`

### 8.1 灾情类型编码

| type_code | 名称 | 后果序号 1/2/3 |
|-----------|------|----------------|
| WATERLOG | 积水内涝 | 轻度 / 中度 / 重度 |
| COLLAPSE | 道路塌陷 | 同上 |
| TREEFALL | 树木倒伏 | 同上 |
| DAMAGE | 设施损毁 | 同上 |
| FIRE | 火灾险情 | 同上 |
| TRAPPED | 人员被困 | 同上 |

**影响半径（米）**：

| 类型 | L1 | L2 | L3 |
|------|----|----|-----|
| WATERLOG | 150 | 300 | 500 |
| COLLAPSE | 100 | 200 | 400 |
| TREEFALL | 80 | 150 | 300 |
| DAMAGE | 50 | 100 | 200 |
| FIRE | 200 | 500 | 1000 |
| TRAPPED | 100 | 200 | 500 |

**颜色**：L1 `#FFD700`，L2 `#FF8C00`，L3 `#FF4500`

### 8.2 灾情状态

| 状态 | 说明 |
|------|------|
| 待审核 | 新上报默认 |
| 已确认 | 众包验证 ≥3 人自动确认 |
| 已通过 | 人工审核通过 |
| 已驳回 | 人工审核驳回 |

---

### 8.3 灾情上报

**POST** `/api/disaster/report`

**Content-Type**：`multipart/form-data`

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| disasterType | string | 是 | 类型编码，如 `WATERLOG` |
| consequenceIndex | int | 是 | 后果选项序号 1–3 |
| lon | double | 是 | 经度 |
| lat | double | 是 | 纬度 |
| description | string | 否 | 文字描述 |
| deviceId | string | 否 | 设备 ID（众包去重） |
| images | file[] | 否 | 最多 **5** 张 |

**成功 200**：

```json
{
  "success": true,
  "disasterId": 42,
  "status": "待审核",
  "impactLevel": 2,
  "impactRadius": 300,
  "confirmCount": 1,
  "message": "灾情已上报，等待审核"
}
```

众包自动确认时：`status` 为 `已确认`，`message` 为 `灾情已上报并自动确认`。

**众包规则**：72 小时内、500m 内、同类型、不同设备上报 ≥3 次 → 自动 `已确认`。

---

### 8.4 灾情列表

**GET** `/api/disaster/list`

**Query 参数**：

| 参数 | 类型 | 说明 |
|------|------|------|
| status | string | 按状态筛选 |
| type | string | 按 disaster_type 筛选 |
| startTime | datetime | 上报时间起 |
| endTime | datetime | 上报时间止 |

**成功 200**：

```json
{
  "success": true,
  "count": 10,
  "data": []
}
```

`data` 为 Disaster 对象数组，字段见 [8.9](#89-disaster-对象字段)。

---

### 8.5 灾情类型列表

**GET** `/api/disaster/types`

**成功 200**：

```json
{
  "success": true,
  "data": [
    {
      "typeCode": "WATERLOG",
      "typeName": "积水内涝",
      "consequenceOptions": ["轻微积水", "道路受阻", "严重内涝"],
      "radiusLevel1": 150,
      "radiusLevel2": 300,
      "radiusLevel3": 500
    }
  ]
}
```

---

### 8.6 灾情详情

**GET** `/api/disaster/{id}`

**路径参数**：`id`（int）

**成功 200**：`{ "success": true, "data": { /* Disaster */ } }`

**失败**：404 `灾情不存在`；500

---

### 8.7 灾情审核

**POST** `/api/disaster/{id}/review`

**请求体**：

```json
{
  "status": "已通过",
  "comment": "审核意见"
}
```

| 字段 | 类型 | 必填 | 说明 |
|------|------|------|------|
| status | string | 是 | 仅 `已通过` 或 `已驳回` |
| comment | string | 否 | 审核意见 |

**成功 200**：

```json
{
  "success": true,
  "message": "审核成功，状态: 已通过",
  "status": "已通过"
}
```

**失败**：400（状态非法）、404

---

### 8.8 附近灾情（众包验证）

**GET** `/api/disaster/nearby`

**Query 参数**：

| 参数 | 类型 | 必填 | 默认 | 说明 |
|------|------|------|------|------|
| type | string | 是 | — | 灾情类型编码 |
| lat | double | 是 | — | 纬度 |
| lon | double | 是 | — | 经度 |
| radius | int | 否 | 500 | 半径（米） |
| hours | int | 否 | 72 | 时间窗口（小时） |

**筛选状态**：`待审核`、`已确认`、`已通过`

**成功 200**：

```json
{
  "success": true,
  "count": 2,
  "data": [
    {
      "id": 10,
      "type": "WATERLOG",
      "lon": 118.05,
      "lat": 36.81,
      "reportedAt": "2026-05-20T10:00:00",
      "status": "待审核",
      "distanceM": 120.5
    }
  ]
}
```

---

### 8.9 Disaster 对象字段

| 字段 | 类型 | 说明 |
|------|------|------|
| disasterId | int | 灾情 ID |
| disasterType | string | 类型编码 |
| typeName | string | 类型中文名 |
| consequenceIndex | int | 后果序号 |
| consequenceText | string | 后果描述 |
| reporterId | int? | 上报人 ID |
| reporterDevice | string? | 设备 ID |
| reporterIp | string? | 上报 IP |
| reportedAt | datetime | 上报时间 |
| status | string | 状态 |
| lon / lat | double | 坐标 |
| address | string? | 地址 |
| description | string? | 描述 |
| images | string? | 图片 URL 数组的 JSON 字符串 |
| impactLevel | int | 1–3 |
| impactRadiusM | int | 影响半径（米） |
| confirmCount | int | 确认人数 |
| reviewedAt | datetime? | 审核时间 |
| reviewedBy | int? | 审核人 ID |
| reviewerName | string? | 审核人姓名 |
| reviewComment | string? | 审核意见 |
| color | string | 计算属性，按 impactLevel |

**图片访问**：`images` 中路径如 `/uploads/disasters/xxx.jpg`，完整 URL 为 `{BaseUrl}/uploads/disasters/xxx.jpg`。

---

## 9. 高德地图代理 `/api/amap`

控制器：`Controllers/AmapProxyController.cs`

### 9.1 公交线路查询代理

**GET** `/api/amap/bus/linename`

**说明**：代理高德 `v3/bus/linename`，6 小时内存缓存。

**Query 参数**：

| 参数 | 必填 | 默认 | 说明 |
|------|------|------|------|
| keywords | 是 | — | 线路关键词 |
| city | 否 | 淄博 | 城市 |
| extensions | 否 | all | 扩展信息 |
| offset | 否 | 10 | 1–50 |
| page | 否 | 1 | 1–10 |
| output | 否 | json | 输出格式 |

**成功 200**：高德 API 原始 JSON

**失败**：

| 状态码 | 说明 |
|--------|------|
| 400 | `{ "message": "keywords is required" }` |
| 500 | `{ "message": "Missing config: Amap:SearchKey" }` |
| 502 | 上游错误，含 statusCode、body |
| 429 | 高德 QPS 超限（infocode 10021） |

**配置**：`appsettings.json` → `Amap:SearchKey`

---

## 10. 模板接口

**GET** `/WeatherForecast`

ASP.NET 项目模板，非业务功能。响应字段：`date`、`temperatureC`、`temperatureF`、`summary`。

---

## 11. 接口总览

| # | 方法 | 路径 | 模块 | 需 Token |
|---|------|------|------|----------|
| 1 | POST | /api/auth/register | 认证 | 否 |
| 2 | POST | /api/auth/login | 认证 | 否 |
| 3 | GET | /api/auth/me | 认证 | 是 |
| 4 | PUT | /api/auth/profile | 认证 | 是 |
| 5 | GET | /api/facilities | 设施 | 否 |
| 6 | GET | /api/facilities/stats | 设施 | 否 |
| 7 | POST | /api/facilities | 设施 | 否 |
| 8 | PUT | /api/facilities/{id} | 设施 | 否 |
| 9 | DELETE | /api/facilities/{id} | 设施 | 否 |
| 10 | POST | /api/siteselection/analyze | 选址 | 否 |
| 11 | POST | /api/disaster/report | 灾情 | 否 |
| 12 | GET | /api/disaster/list | 灾情 | 否 |
| 13 | GET | /api/disaster/types | 灾情 | 否 |
| 14 | GET | /api/disaster/{id} | 灾情 | 否 |
| 15 | POST | /api/disaster/{id}/review | 灾情 | 否* |
| 16 | GET | /api/disaster/nearby | 灾情 | 否 |
| 17 | GET | /api/amap/bus/linename | 高德 | 否 |
| 18 | GET | /WeatherForecast | 模板 | 否 |

\* 审核接口业务上应需管理员权限，但代码未强制鉴权。

---

## 12. 调用示例

### 登录

```bash
curl -X POST "https://zibo-gis-backend.onrender.com/api/auth/login" \
  -H "Content-Type: application/json" \
  -d "{\"username\":\"admin\",\"password\":\"your_password\"}"
```

### 获取当前用户

```bash
curl "https://zibo-gis-backend.onrender.com/api/auth/me" \
  -H "Authorization: Bearer YOUR_TOKEN"
```

### 获取设施列表

```bash
curl "https://zibo-gis-backend.onrender.com/api/facilities"
```

### 选址分析

```bash
curl -X POST "https://zibo-gis-backend.onrender.com/api/siteselection/analyze" \
  -H "Content-Type: application/json" \
  -d "{\"facilityType\":\"hospital\",\"bounds\":[117.9,36.7,118.1,36.9],\"topN\":10}"
```

### 灾情上报

```bash
curl -X POST "https://zibo-gis-backend.onrender.com/api/disaster/report" \
  -F "disasterType=WATERLOG" \
  -F "consequenceIndex=2" \
  -F "lon=118.05" \
  -F "lat=36.81" \
  -F "description=道路积水严重" \
  -F "images=@photo1.jpg"
```

### 高德公交线路

```bash
curl "https://zibo-gis-backend.onrender.com/api/amap/bus/linename?keywords=1路&city=淄博"
```

---

## 附录：控制器与源码对照

| 控制器文件 | 路由前缀 |
|------------|----------|
| AuthController.cs | /api/auth |
| FacilitiesController.cs | /api/facilities |
| SiteSelectionController.cs | /api/siteselection |
| DisasterController.cs | /api/disaster |
| AmapProxyController.cs | /api/amap |
| WeatherForecastController.cs | /WeatherForecast |
