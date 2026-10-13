using Casino.Modules.Identity.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Casino.Modules.Identity.Api;

/// <summary>
/// Solo desarrollo: lee los correos que el servicio "envio" sin SMTP (para seguir los enlaces de verificacion y recuperacion a mano). El buzon solo se registra
/// en Development, asi que fuera de ahi el endpoint responde 404.
/// </summary>
internal static class DevEndpoints
{
    public static void Map(IEndpointRouteBuilder app) =>
        app.MapGet("/identity/dev/emails", (DevMailbox? mailbox) =>
                mailbox is null ? Results.NotFound() : Results.Ok(mailbox.Snapshot().Reverse()))
            .AllowAnonymous()
            .ExcludeFromDescription();
}
