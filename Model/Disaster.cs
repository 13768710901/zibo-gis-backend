namespace ZIBOGIS.Model
{
    // 灾情实体
    public class Disaster
    {
        public int DisasterId { get; set; }
        public string DisasterType { get; set; } = "";
        public string TypeName { get; set; } = "";  // 关联查询出的类型名称
        public int ConsequenceIndex { get; set; }
        public string ConsequenceText { get; set; } = "";  // 后果描述文字
        public int? ReporterId { get; set; }
        public string? ReporterDevice { get; set; }
        public string? ReporterIp { get; set; }
        public DateTime ReportedAt { get; set; }
        public string Status { get; set; } = "待审核";
        public double Lon { get; set; }
        public double Lat { get; set; }
        public string? Address { get; set; }
        public string? Description { get; set; }
        public string? Images { get; set; }  // JSON数组
        public int ImpactLevel { get; set; }
        public int ImpactRadiusM { get; set; }
        public int ConfirmCount { get; set; }
        public DateTime? ReviewedAt { get; set; }
        public int? ReviewedBy { get; set; }
        public string? ReviewerName { get; set; }  // 关联查询出的审核人姓名
        public string? ReviewComment { get; set; }
        
        // 计算属性：颜色
        public string Color => ImpactLevel switch
        {
            1 => "#FFD700",  // 黄色
            2 => "#FF8C00",  // 橙色
            3 => "#FF4500",  // 红色
            _ => "#FFD700"
        };
    }

    // 灾情类型配置
    public class DisasterType
    {
        public string TypeCode { get; set; } = "";
        public string TypeName { get; set; } = "";
        public List<string> ConsequenceOptions { get; set; } = new();
        public int RadiusLevel1 { get; set; }
        public int RadiusLevel2 { get; set; }
        public int RadiusLevel3 { get; set; }
    }

    // 上报请求DTO
    public class DisasterReportRequest
    {
        public string DisasterType { get; set; } = "";
        public int ConsequenceIndex { get; set; }
        public double Lon { get; set; }
        public double Lat { get; set; }
        public string? Description { get; set; }
        public string? DeviceId { get; set; }
    }

    // 审核请求DTO
    public class DisasterReviewRequest
    {
        public string Status { get; set; } = "";  // 已通过/已驳回
        public string? Comment { get; set; }
    }

    // 列表查询参数
    public class DisasterQueryParams
    {
        public string? Status { get; set; }
        public string? Type { get; set; }
        public DateTime? StartTime { get; set; }
        public DateTime? EndTime { get; set; }
    }

    // 众包验证结果
    public class CrowdVerificationResult
    {
        public int NearbyCount { get; set; }  // 附近同类灾情数
        public int TotalConfirmCount { get; set; }  // 确认后总人数
        public bool AutoConfirmed { get; set; }  // 是否自动确认
    }
}
