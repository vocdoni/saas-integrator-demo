using HoaVoting.Api.Authorization;
using HoaVoting.Api.Models;
using Xunit;

namespace HoaVoting.Tests;

public class AuthorizationTests
{
    private static Association OwnedBy(int ownerId) => new() { Id = 1, OwnerUserId = ownerId };

    [Fact]
    public void Owner_can_access_their_own_association()
    {
        Assert.True(AssociationAccess.CanAccess(AppRole.Owner, userId: 7, OwnedBy(7)));
    }

    [Fact]
    public void Owner_cannot_access_another_associations()
    {
        Assert.False(AssociationAccess.CanAccess(AppRole.Owner, userId: 99, OwnedBy(7)));
    }

    [Fact]
    public void SuperAdmin_can_access_any_association()
    {
        Assert.True(AssociationAccess.CanAccess(AppRole.SuperAdmin, userId: 1, OwnedBy(7)));
    }
}
