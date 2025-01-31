using Microsoft.EntityFrameworkCore;

namespace MateM8.ApiService
{
    public class MateDbContext : DbContext
    {
        public DbSet<User> Users { get; set; }

        public DbSet<Mate> Mates { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            var mateBuilder = modelBuilder.Entity<Mate>().ToTable("Mate");
            mateBuilder.HasKey(e => e.Id);
            mateBuilder.Property(e => e.Id).ValueGeneratedOnAdd();
            mateBuilder.Property(e => e.User);
            mateBuilder.Property(e => e.CreatedAt);
            mateBuilder.Property(e => e.Type);

            var userBuilder = modelBuilder.Entity<User>().ToTable("User");
            userBuilder.HasKey(e => e.Email);
            userBuilder.Property(e => e.Otp);
            userBuilder.Property(e => e.Session);
        }

        protected override void OnConfiguring(DbContextOptionsBuilder options)
            => options.UseSqlite($"Data Source={Directory.GetCurrentDirectory()}/data/mate.db");
    }

    public class Mate
    {
        public required long Id { get; init; }
        public required DateTimeOffset CreatedAt { get; init; }
        public required string User { get; init; }
        public required MateType Type { get; init; }
    }

    public enum MateType
    {
        Blue = 1,
        Ginger = 2,
        Mint = 3
    }


    public class User
    {
        public required string Email { get; init; }

        public string? Otp { get; set; }

        public string? Session { get; set; }
    }
}
