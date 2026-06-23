using HoaVoting.Api.Models;

namespace HoaVoting.Api.Authorization;

/// <summary>
/// Tenancy rule: a SuperAdmin may act on any association; an Owner only on the one they own.
/// Pure function so it can be unit-tested without hosting. Controllers call <see cref="CanAccess"/>.
/// </summary>
public static class AssociationAccess
{
    public static bool CanAccess(AppRole role, int userId, Association association) =>
        role == AppRole.SuperAdmin || association.OwnerUserId == userId;
}
