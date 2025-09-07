using BackendWebAPI.Models;
using Microsoft.EntityFrameworkCore;

namespace BackendWebAPI.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Player> Players => Set<Player>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("tttu");

        b.Entity<Player>(e =>
        {
            e.ToTable("players");
            e.HasKey(x => x.PlayerId);
            e.Property(x => x.PlayerId).HasColumnName("player_id").ValueGeneratedOnAdd();
            e.Property(x => x.Sub).HasColumnName("sub").IsRequired();
            e.Property(x => x.Username).HasColumnName("username").HasMaxLength(32).IsRequired();
            e.Property(x => x.Elo).HasColumnName("elo").HasDefaultValue(500);
            e.Property(x => x.Email).HasColumnName("email").IsRequired();
            e.Property(x => x.Age).HasColumnName("age");
            e.HasIndex(x => x.Username).IsUnique();
            e.HasIndex(x => x.Email).IsUnique();
            e.HasIndex(x => x.Sub).IsUnique();
        });
    }
}