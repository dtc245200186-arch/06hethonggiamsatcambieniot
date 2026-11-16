using Microsoft.EntityFrameworkCore;

namespace Sensor.Api.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<SensorEntity> Sensors => Set<SensorEntity>();
    public DbSet<SensorReadingEntity> SensorReadings => Set<SensorReadingEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SensorEntity>(entity =>
        {
            entity.HasKey(s => s.Id);
            entity.Property(s => s.Name).IsRequired().HasMaxLength(100);
            entity.Property(s => s.Type).IsRequired().HasMaxLength(50);
            entity.Property(s => s.Location).IsRequired().HasMaxLength(100);
        });

        modelBuilder.Entity<SensorReadingEntity>(entity =>
        {
            entity.HasKey(r => r.Id);
            entity.HasIndex(r => new { r.SensorId, r.RecordedAt });
            entity.HasOne(r => r.Sensor)
                  .WithMany(s => s.Readings)
                  .HasForeignKey(r => r.SensorId)
                  .OnDelete(DeleteBehavior.Cascade);
        });
    }
}