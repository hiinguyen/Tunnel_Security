using BackendV2.Models;
using Microsoft.EntityFrameworkCore;

namespace BackendV2.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Node> Nodes => Set<Node>();
    public DbSet<Camera> Cameras => Set<Camera>();
    public DbSet<Sensor> Sensors => Set<Sensor>();
    public DbSet<Line> Lines => Set<Line>();
    public DbSet<DeviceIdentity> DeviceIdentities => Set<DeviceIdentity>();
    public DbSet<Alert> Alerts => Set<Alert>();
    public DbSet<Incident> Incidents => Set<Incident>();

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<FunctionGroup> FunctionGroups => Set<FunctionGroup>();
    public DbSet<RoleFunctionGroup> RoleFunctionGroups => Set<RoleFunctionGroup>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Line>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.Name).HasMaxLength(128);
            e.Property(x => x.GatewayHardwareId).HasMaxLength(64);
            e.Property(x => x.GatewayIp).HasMaxLength(64);
            e.Property(x => x.GatewayMac).HasMaxLength(64);
            e.Property(x => x.GatewayVersion).HasMaxLength(64);
            // Một NM chỉ có một tuyến đang hoạt động; tuyến đã gỡ không chặn NM đó được cấp tuyến mới.
            e.HasIndex(x => x.GatewayHardwareId).IsUnique().HasFilter("[RetiredAt] IS NULL");
        });

        modelBuilder.Entity<Node>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne<Line>().WithMany().HasForeignKey(x => x.LineId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new { x.LineId, x.NodeByteId })
                .IsUnique()
                .HasFilter("[LineId] IS NOT NULL AND [NodeByteId] IS NOT NULL");
            e.HasIndex(x => x.PhysicalId).IsUnique().HasFilter("[PhysicalId] IS NOT NULL");
        });

        modelBuilder.Entity<DeviceIdentity>(e =>
        {
            e.HasKey(x => x.PhysicalId);
            e.Property(x => x.PhysicalId).ValueGeneratedNever();
            e.Property(x => x.Label).HasMaxLength(128);
        });

        modelBuilder.Entity<Alert>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Key).HasMaxLength(128);
            e.Property(x => x.Rule).HasMaxLength(64);
            e.Property(x => x.SensorKind).HasMaxLength(32);
            e.Property(x => x.Message).HasMaxLength(512);
            e.HasIndex(x => new { x.State, x.CreatedAt });
            e.HasIndex(x => new { x.LineId, x.CreatedAt });
            e.HasIndex(x => x.Key);
            e.HasOne<Incident>().WithMany().HasForeignKey(x => x.IncidentId).OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Incident>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.NodeByteIds).HasMaxLength(1024);
            e.HasIndex(x => new { x.State, x.CreatedAt });
        });

        modelBuilder.Entity<Camera>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne<Node>().WithMany().HasForeignKey(x => x.NodeId);
        });

        modelBuilder.Entity<Sensor>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasOne<Node>().WithMany().HasForeignKey(x => x.NodeId);
        });

        modelBuilder.Entity<User>(b =>
        {
            b.HasKey(u => u.Id);
            b.HasIndex(u => u.Username).IsUnique();
            b.Property(u => u.FailedLoginAttempts).HasDefaultValue(0);
            b.HasOne(u => u.Role)
                .WithMany(r => r.Users)
                .HasForeignKey(u => u.RoleId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<Role>(b =>
        {
            b.HasKey(r => r.Id);
            b.HasIndex(r => r.Name).IsUnique();
            b.HasIndex(r => r.Code).IsUnique();
        });

        modelBuilder.Entity<FunctionGroup>(b =>
        {
            b.HasKey(f => f.Id);
            b.HasIndex(f => f.Code).IsUnique();
            b.HasIndex(f => f.Name).IsUnique();
        });

        modelBuilder.Entity<RoleFunctionGroup>(b =>
        {
            b.HasKey(rfg => new { rfg.RoleId, rfg.FunctionGroupId });
            b.HasOne(rfg => rfg.Role)
                .WithMany(r => r.RoleFunctionGroups)
                .HasForeignKey(rfg => rfg.RoleId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(rfg => rfg.FunctionGroup)
                .WithMany(fg => fg.RoleFunctionGroups)
                .HasForeignKey(rfg => rfg.FunctionGroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditLog>(b =>
        {
            b.HasKey(log => log.Id);
            b.Property(log => log.Action).IsRequired();
            b.Property(log => log.TargetType).IsRequired();
            b.Property(log => log.TargetId).IsRequired();
            b.HasOne(log => log.ActorUser)
                .WithMany()
                .HasForeignKey(log => log.ActorUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<RefreshToken>(b =>
        {
            b.HasKey(t => t.Id);
            b.HasOne(t => t.User).WithMany(u => u.RefreshTokens).HasForeignKey(t => t.UserId);
            b.HasIndex(t => t.TokenHash).IsUnique(false);
        });
    }
}
