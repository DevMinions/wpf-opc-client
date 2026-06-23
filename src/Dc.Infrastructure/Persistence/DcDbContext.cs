using Dc.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace Dc.Infrastructure.Persistence;

public class DcDbContext : DbContext
{
    public DcDbContext(DbContextOptions<DcDbContext> options) : base(options) { }

    public DbSet<Tag> Tags => Set<Tag>();
    public DbSet<CollectorTask> Tasks => Set<CollectorTask>();
    public DbSet<ConfigEntry> Configs => Set<ConfigEntry>();
    public DbSet<Formula> Formulas => Set<Formula>();
    public DbSet<FormulaInput> FormulaInputs => Set<FormulaInput>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Tag>(e =>
        {
            e.ToTable("dc_tags");
            e.HasKey(x => x.Id);
            // 分组层已去除:Tag 直接挂任务,任务内 Item 唯一。
            e.HasIndex(x => new { x.Item, x.TaskId }).IsUnique().HasDatabaseName("udx_name");
        });

        modelBuilder.Entity<CollectorTask>(e =>
        {
            e.ToTable("dc_tasks");
            e.HasKey(x => x.Id);
            e.HasMany(x => x.Tags).WithOne().HasForeignKey(t => t.TaskId).OnDelete(DeleteBehavior.NoAction);
        });

        modelBuilder.Entity<Formula>(e =>
        {
            e.ToTable("dc_formulas");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TaskId, x.Name }).IsUnique().HasDatabaseName("udx_formula_name");
            e.HasMany(x => x.Inputs).WithOne().HasForeignKey(i => i.FormulaId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FormulaInput>(e =>
        {
            e.ToTable("dc_formula_inputs");
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.FormulaId, x.Alias }).IsUnique().HasDatabaseName("udx_formula_input_alias");
        });

        modelBuilder.Entity<ConfigEntry>(e =>
        {
            e.ToTable("dc_configs");
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Key).IsUnique();
            e.Property(x => x.Description).HasColumnName("dc_description").HasDefaultValue("");
        });
    }

    public override int SaveChanges()
    {
        ApplyAutoFields();
        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        ApplyAutoFields();
        return base.SaveChangesAsync(cancellationToken);
    }

    private void ApplyAutoFields()
    {
        var now = DateTime.UtcNow;
        foreach (var entry in ChangeTracker.Entries<EntityBase>())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity.CreatedAt == default)
                    entry.Entity.CreatedAt = now;
                entry.Entity.UpdatedAt = now;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAt = now;
            }
        }
    }
}
