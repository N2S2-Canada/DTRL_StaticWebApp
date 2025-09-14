using Microsoft.EntityFrameworkCore;

namespace API.Data;

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> opts) : base(opts) { }

    public DbSet<PageText> PageTexts => Set<PageText>();        // AboutUs, ContactUs
    public DbSet<ServiceCard> ServiceCards => Set<ServiceCard>();     // cards on landing
    public DbSet<ServiceInfo> ServiceInfos => Set<ServiceInfo>();     // per-service body + photo
    public DbSet<CustomerContent> CustomerContents => Set<CustomerContent>(); // 5-char code + SharePoint link

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<PageText>().HasKey(x => x.Key);
        b.Entity<PageText>().Property(x => x.Key).HasMaxLength(50);

        b.Entity<ServiceCard>().HasIndex(x => x.Slug).IsUnique();
        b.Entity<ServiceCard>().Property(x => x.Title).HasMaxLength(100);
        b.Entity<ServiceCard>().Property(x => x.Summary).HasMaxLength(400);

        b.Entity<ServiceInfo>()
            .HasOne<ServiceCard>()
            .WithOne()
            .HasForeignKey<ServiceInfo>(x => x.ServiceCardId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<CustomerContent>().HasIndex(x => x.Code).IsUnique();
        b.Entity<CustomerContent>().Property(x => x.Code).HasMaxLength(5).IsRequired();
    }
}

public class PageText { public string Key { get; set; } = default!; public string Content { get; set; } = default!; }

public class ServiceCard
{
    public int Id { get; set; }
    public string Slug { get; set; } = default!;
    public string Title { get; set; } = default!;
    public string Summary { get; set; } = default!;
    public int Order { get; set; }
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public class ServiceInfo
{
    public int Id { get; set; }
    public int ServiceCardId { get; set; } // FK -> ServiceCard.Id
    public string PhotoUrl { get; set; } = default!;
    public string Body { get; set; } = default!;
}

public class CustomerContent
{
    public int Id { get; set; }
    public string Code { get; set; } = default!;          // 5-char code
    public string SharePointUrl { get; set; } = default!;
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresUtc { get; set; }
    public DateTime? UsedUtc { get; set; }
}
