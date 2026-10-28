using EstateHub.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EstateHub.Infrastructure.Data.Configurations;

// Auth tables belong to users, not estates, so none of them is tenant-scoped or under RLS.
// Tokens, codes and challenges are stored as SHA-256 hashes only; TOTP secrets encrypted.

public class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> entity)
    {
        entity.ToTable("users");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Email).HasMaxLength(254);
        entity.Property(x => x.Phone).HasMaxLength(16);
        entity.Property(x => x.DisplayName).IsRequired().HasMaxLength(200);
        entity.Property(x => x.PasswordHash).HasMaxLength(200);
        entity.Property(x => x.Status).IsRequired().HasMaxLength(16);
        entity.Property(x => x.TotpSecretEncrypted).HasMaxLength(1000);
        entity.Property(x => x.SecurityStamp).IsRequired().HasMaxLength(64);

        entity.HasIndex(x => x.Email).IsUnique();
        entity.HasIndex(x => x.Phone).IsUnique();
        entity.Ignore(x => x.Contact);
    }
}

public class AuthSessionConfiguration : IEntityTypeConfiguration<AuthSession>
{
    public void Configure(EntityTypeBuilder<AuthSession> entity)
    {
        entity.ToTable("auth_sessions");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.AuthMethods).IsRequired().HasMaxLength(64);
        entity.Property(x => x.SecurityStamp).IsRequired().HasMaxLength(64);
        entity.Property(x => x.IpAddress).HasMaxLength(45);
        entity.Property(x => x.UserAgent).HasMaxLength(512);
        entity.Property(x => x.RevokedReason).HasMaxLength(64);
        entity.Property(x => x.ImpersonationReason).HasMaxLength(500);
        entity.HasIndex(x => x.GuardDeviceId);

        entity.HasIndex(x => new { x.UserId, x.RevokedAt });
        entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        entity.HasOne<Estate>().WithMany().HasForeignKey(x => x.ActiveEstateId).OnDelete(DeleteBehavior.SetNull);
    }
}

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> entity)
    {
        entity.ToTable("refresh_tokens");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.TokenHash).IsRequired().HasMaxLength(64);

        entity.HasIndex(x => x.TokenHash).IsUnique();
        entity.HasIndex(x => x.SessionId);
        entity.HasOne<AuthSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class RecoveryCodeConfiguration : IEntityTypeConfiguration<RecoveryCode>
{
    public void Configure(EntityTypeBuilder<RecoveryCode> entity)
    {
        entity.ToTable("recovery_codes");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.CodeHash).IsRequired().HasMaxLength(64);

        entity.HasIndex(x => x.UserId);
        entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class OtpChallengeConfiguration : IEntityTypeConfiguration<OtpChallenge>
{
    public void Configure(EntityTypeBuilder<OtpChallenge> entity)
    {
        entity.ToTable("otp_challenges");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Purpose).IsRequired().HasMaxLength(16);
        entity.Property(x => x.Channel).IsRequired().HasMaxLength(8);
        entity.Property(x => x.IdentifierHash).IsRequired().HasMaxLength(64);
        entity.Property(x => x.TokenHash).IsRequired().HasMaxLength(64);
        entity.Property(x => x.CodeHash).IsRequired().HasMaxLength(64);
        entity.Property(x => x.IpAddress).HasMaxLength(45);
        entity.Property(x => x.NewContact).HasMaxLength(254);

        entity.HasIndex(x => x.TokenHash).IsUnique();
        entity.HasIndex(x => new { x.IdentifierHash, x.CreatedAt });
        entity.HasIndex(x => new { x.IpAddress, x.CreatedAt });
        entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class SigningKeyConfiguration : IEntityTypeConfiguration<SigningKey>
{
    public void Configure(EntityTypeBuilder<SigningKey> entity)
    {
        entity.ToTable("signing_keys");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Id).HasMaxLength(32);
        entity.Property(x => x.PublicKey).IsRequired().HasMaxLength(500);
        entity.Property(x => x.PrivateKeyEncrypted).IsRequired().HasMaxLength(2000);
    }
}

public class UserPasskeyConfiguration : IEntityTypeConfiguration<UserPasskey>
{
    public void Configure(EntityTypeBuilder<UserPasskey> entity)
    {
        entity.ToTable("user_passkeys");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.CredentialId).IsRequired().HasMaxLength(1024);
        entity.Property(x => x.PublicKey).IsRequired().HasMaxLength(2048);
        entity.Property(x => x.Name).IsRequired().HasMaxLength(100);
        entity.Property(x => x.Transports).IsRequired().HasMaxLength(200);

        entity.HasIndex(x => x.CredentialId).IsUnique();
        entity.HasIndex(x => x.UserId);
        entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class PasskeyChallengeConfiguration : IEntityTypeConfiguration<PasskeyChallenge>
{
    public void Configure(EntityTypeBuilder<PasskeyChallenge> entity)
    {
        entity.ToTable("passkey_challenges");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.Purpose).IsRequired().HasMaxLength(16);
        entity.Property(x => x.TokenHash).IsRequired().HasMaxLength(64);
        entity.Property(x => x.State).IsRequired().HasMaxLength(8000);

        entity.HasIndex(x => x.TokenHash).IsUnique();
        entity.HasIndex(x => x.ExpiresAt);
        entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class KnownDeviceConfiguration : IEntityTypeConfiguration<KnownDevice>
{
    public void Configure(EntityTypeBuilder<KnownDevice> entity)
    {
        entity.ToTable("known_devices");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.DeviceHash).IsRequired().HasMaxLength(64);
        entity.Property(x => x.Label).IsRequired().HasMaxLength(100);

        entity.HasIndex(x => new { x.UserId, x.DeviceHash }).IsUnique();
        entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class SignInReportConfiguration : IEntityTypeConfiguration<SignInReport>
{
    public void Configure(EntityTypeBuilder<SignInReport> entity)
    {
        entity.ToTable("sign_in_reports");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.TokenHash).IsRequired().HasMaxLength(64);
        entity.Property(x => x.Device).IsRequired().HasMaxLength(100);
        entity.Property(x => x.IpAddress).HasMaxLength(45);

        entity.HasIndex(x => x.TokenHash).IsUnique();
        entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class MfaChallengeConfiguration : IEntityTypeConfiguration<MfaChallenge>
{
    public void Configure(EntityTypeBuilder<MfaChallenge> entity)
    {
        entity.ToTable("mfa_challenges");
        entity.HasKey(x => x.Id);
        entity.Property(x => x.TokenHash).IsRequired().HasMaxLength(64);
        entity.Property(x => x.PendingTotpSecretEncrypted).HasMaxLength(1000);
        entity.Property(x => x.FirstFactor).IsRequired().HasMaxLength(8);
        entity.Property(x => x.IpAddress).HasMaxLength(45);

        entity.HasIndex(x => x.TokenHash).IsUnique();
        entity.HasIndex(x => x.ExpiresAt);
        entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
