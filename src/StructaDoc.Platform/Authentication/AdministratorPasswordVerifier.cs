using Microsoft.AspNetCore.Identity;
using StructaDoc.Platform.ControlPlane.Entities;

namespace StructaDoc.Platform.Authentication;

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
            Username = "dummy-account",
            NormalizedUsername = "DUMMY-ACCOUNT",
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
