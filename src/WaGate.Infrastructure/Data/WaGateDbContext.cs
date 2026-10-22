using Microsoft.EntityFrameworkCore;
using WaGate.Domain;

namespace WaGate.Infrastructure.Data;

public class WaGateDbContext(DbContextOptions<WaGateDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<Instance> Instances => Set<Instance>();
    public DbSet<Webhook> Webhooks => Set<Webhook>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();
    public DbSet<InboundEvent> InboundEvents => Set<InboundEvent>();
    public DbSet<StripeEventLog> StripeEvents => Set<StripeEventLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Account>(e =>
        {
            e.ToTable("Accounts");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(120).IsRequired();
            e.Property(x => x.Email).HasMaxLength(200).IsRequired();
            e.HasIndex(x => x.Email).IsUnique();
            e.Property(x => x.PasswordHash).HasMaxLength(300).IsRequired();
            e.Property(x => x.ClientToken).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.ClientToken).IsUnique();
            e.Property(x => x.Phone).HasMaxLength(20);
            e.Property(x => x.Document).HasMaxLength(14);
            e.HasIndex(x => x.Document).IsUnique().HasFilter("[Document] IS NOT NULL");
            e.Property(x => x.CompanyName).HasMaxLength(160);
            e.Property(x => x.SignupIp).HasMaxLength(64);
            e.Property(x => x.Plan).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.SubscriptionStatus).HasMaxLength(30);
            e.Property(x => x.StripeCustomerId).HasMaxLength(64);
            e.Property(x => x.StripeSubscriptionId).HasMaxLength(64);
            e.HasIndex(x => x.StripeCustomerId);
            e.HasIndex(x => x.StripeSubscriptionId);
            e.Ignore(x => x.HasUsableSubscription);
        });

        b.Entity<StripeEventLog>(e =>
        {
            e.ToTable("StripeEvents");
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).HasMaxLength(100);
            e.Property(x => x.Type).HasMaxLength(100);
        });

        b.Entity<Instance>(e =>
        {
            e.ToTable("Instances");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(80).IsRequired();
            e.Property(x => x.SessionName).HasMaxLength(64).IsRequired();
            e.HasIndex(x => x.SessionName).IsUnique();
            e.Property(x => x.Token).HasMaxLength(64).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.PhoneNumber).HasMaxLength(32);
            e.Property(x => x.PushName).HasMaxLength(120);
            e.HasOne(x => x.Account).WithMany(a => a.Instances).HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Webhook>(e =>
        {
            e.ToTable("Webhooks");
            e.HasKey(x => x.Id);
            e.Property(x => x.Url).HasMaxLength(500).IsRequired();
            e.Property(x => x.Events).HasMaxLength(1000).IsRequired();
            e.Property(x => x.Secret).HasMaxLength(64).IsRequired();
            e.Property(x => x.Description).HasMaxLength(200);
            e.Ignore(x => x.EventList);
            e.HasOne(x => x.Instance).WithMany(i => i.Webhooks).HasForeignKey(x => x.InstanceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<Message>(e =>
        {
            e.ToTable("Messages");
            e.HasKey(x => x.Id);
            e.Property(x => x.ExternalId).HasMaxLength(200);
            e.Property(x => x.ChatId).HasMaxLength(100).IsRequired();
            e.Property(x => x.Phone).HasMaxLength(32);
            e.Property(x => x.Direction).HasConversion<string>().HasMaxLength(10);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(15);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(15);
            e.Property(x => x.MediaUrl).HasMaxLength(2000);
            e.Property(x => x.FileName).HasMaxLength(260);
            e.Property(x => x.SenderName).HasMaxLength(120);
            e.Property(x => x.Error).HasMaxLength(1000);
            e.HasIndex(x => new { x.InstanceId, x.CreatedAt });
            e.HasIndex(x => new { x.InstanceId, x.ExternalId });
            e.HasOne(x => x.Instance).WithMany().HasForeignKey(x => x.InstanceId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<WebhookDelivery>(e =>
        {
            e.ToTable("WebhookDeliveries");
            e.HasKey(x => x.Id);
            e.Property(x => x.Event).HasMaxLength(60).IsRequired();
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(12);
            e.Property(x => x.LastError).HasMaxLength(1000);
            e.Property(x => x.LastResponse).HasMaxLength(4000);
            e.HasIndex(x => new { x.Status, x.NextAttemptAt });
            e.HasIndex(x => new { x.InstanceId, x.CreatedAt });
            e.HasOne(x => x.Webhook).WithMany().HasForeignKey(x => x.WebhookId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<InboundEvent>(e =>
        {
            e.ToTable("InboundEvents");
            e.HasKey(x => x.Id);
            e.Property(x => x.WahaEventId).HasMaxLength(64);
            e.Property(x => x.Event).HasMaxLength(60);
            e.Property(x => x.Session).HasMaxLength(64);
            e.HasIndex(x => x.WahaEventId);
            e.HasIndex(x => x.ReceivedAt);
        });
    }
}
