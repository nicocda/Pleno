using Casino.BuildingBlocks;

namespace Casino.Modules.Identity.Application;

public static class IdentityRoles
{
    public static readonly string[] All = [Roles.Player, Roles.Backoffice, Roles.Cashier, Roles.HeadCashier];
}
