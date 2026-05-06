namespace ZIBOGIS.Model
{
    public class Facility
    {
        public int Id { get; set; }

        public string Name { get; set; } = null!;

        public string? Type { get; set; }

        public double Longitude { get; set; }

        public double Latitude { get; set; }

        public string? Address { get; set; }

        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        public DateTime? UpdatedAt { get; set; }
    }
}
