using ExpertAssignment.Domain;
using Microsoft.EntityFrameworkCore;

namespace ExpertAssignment.Infrastructure;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Specialty> Specialties => Set<Specialty>();
    public DbSet<Expert> Experts => Set<Expert>();
    public DbSet<ExpertSpecialty> ExpertSpecialties => Set<ExpertSpecialty>();
    public DbSet<ServiceRequest> Requests => Set<ServiceRequest>();
    public DbSet<AssignmentHistory> AssignmentHistory => Set<AssignmentHistory>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Specialty>(e => { e.ToTable("specialties"); e.HasKey(x => x.Id); e.Property(x => x.Name).HasMaxLength(100).IsRequired(); e.HasIndex(x => x.Name).IsUnique(); });
        b.Entity<Expert>(e => { e.ToTable("experts"); e.HasKey(x => x.Id); e.Property(x => x.Name).HasMaxLength(160).IsRequired(); e.ToTable(t => t.HasCheckConstraint("ck_experts_capacity_positive", "capacity > 0")); e.HasIndex(x => x.IsActive); });
        b.Entity<ExpertSpecialty>(e => { e.ToTable("expert_specialties"); e.HasKey(x => new { x.ExpertId, x.SpecialtyId }); e.HasOne(x => x.Expert).WithMany(x => x.ExpertSpecialties).HasForeignKey(x => x.ExpertId).OnDelete(DeleteBehavior.Cascade); e.HasOne(x => x.Specialty).WithMany(x => x.ExpertSpecialties).HasForeignKey(x => x.SpecialtyId).OnDelete(DeleteBehavior.Cascade); });
        b.Entity<ServiceRequest>(e => { e.ToTable("requests"); e.HasKey(x => x.Id); e.Property(x => x.RequestKey).HasMaxLength(200).IsRequired(); e.HasIndex(x => x.RequestKey).IsUnique(); e.HasIndex(x => new { x.Status, x.Priority, x.CreatedAt }); e.HasIndex(x => new { x.AssignedExpertId, x.Status }); e.HasOne(x => x.Specialty).WithMany().HasForeignKey(x => x.SpecialtyId).OnDelete(DeleteBehavior.Restrict); e.HasOne(x => x.AssignedExpert).WithMany().HasForeignKey(x => x.AssignedExpertId).OnDelete(DeleteBehavior.Restrict); });
        b.Entity<AssignmentHistory>(e => { e.ToTable("assignment_history"); e.HasKey(x => x.Id); e.HasIndex(x => x.RequestId); e.HasOne<ServiceRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict); e.HasOne<Expert>().WithMany().HasForeignKey(x => x.ExpertId).OnDelete(DeleteBehavior.Restrict); });
    }
}
