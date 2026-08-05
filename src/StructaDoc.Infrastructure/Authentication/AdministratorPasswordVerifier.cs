using Microsoft.AspNetCore.Identity;
using StructaDoc.Infrastructure.Persistence.Entities;

namespace StructaDoc.Infrastructure.Authentication;

public sealed class AdministratorPasswordVerifier
{
    private readonly IPasswordHasher<AdminUserEntity> passwordHasher;
    private readonly AdminUserEntity dummyUser;
    private readonly string dummyPasswordHash;

    public AdministratorPasswordVerifier(IPasswordHasher<AdminUserEntity> passwordHasher)
    {
        this.passwordHasher = passwordHasher;
        dummyUser = new AdminUserEntity
        {
            Id = Guid.Empty,
            Email = "dummy@invalid.example",
            NormalizedEmail = "DUMMY@INVALID.EXAMPLE",
            DisplayName = "Dummy",
            PasswordHash = string.Empty,
            IsActive = false,
            SecurityStamp = Guid.Empty,
            CreatedAtUtc = DateTime.UnixEpoch,
        };
        dummyPasswordHash = passwordHasher.HashPassword(
            dummyUser,
            "StructaDoc dummy password used only for timing normalization");
    }

    public PasswordVerificationResult Verify(
        AdminUserEntity? user,
        string password)
    {
        return user is null
            ? passwordHasher.VerifyHashedPassword(dummyUser, dummyPasswordHash, password)
            : passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);
    }
}
