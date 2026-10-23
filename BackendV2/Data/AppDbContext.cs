using BackendV2.Models;
using Microsoft.EntityFrameworkCore;

namespace BackendV2.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<Node> Nodes => Set<Node>();
    public DbSet<Camera> Cameras => Set<Camera>();
    public DbSet<Sensor> Sensors => Set<Sensor>();

    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<FunctionGroup> FunctionGroups => Set<FunctionGroup>();
    public DbSet<RoleFunctionGroup> RoleFunctionGroups => Set<RoleFunctionGroup>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Node>(e =>
        {
            e.HasKey(x => x.Id);
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
