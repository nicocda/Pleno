using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Casino.Modules.Identity.Application;
using Casino.Modules.Identity.Domain;
using Casino.Modules.Identity.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;

namespace Casino.Modules.Identity.Api;

public static class IdentityModule
{
    public const string Audience = "casino-api";
    public const string WebClientId = "casino-web";
    public const string RateLimitPolicy = "identity-auth";

    public static WebApplicationBuilder AddIdentityModule(this WebApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var services = builder.Services;

        services.AddOptions<EmailProtectionOptions>().Bind(builder.Configuration.GetSection(EmailProtectionOptions.SectionName));
        services.AddSingleton<EmailProtector>();
        services.TryAddSingleton(TimeProvider.System);

        services.AddDbContext<IdentityDb>(options =>
        {
            options.UseNpgsql(builder.Configuration.GetConnectionString("identitydb")
                ?? throw new InvalidOperationException("Falta la cadena de conexion 'identitydb'. La inyecta Aspire."));
            options.UseOpenIddict();
        });

        services.AddIdentityCore<PlayerUser>(options =>
            {
                options.Password.RequiredLength = 10;
                options.Password.RequireNonAlphanumeric = false; // largo antes que reglas de composicion (recomendacion NIST)
                options.Password.RequireDigit = false;
                options.Password.RequireUppercase = false;
                options.Password.RequireLowercase = false;
                options.User.RequireUniqueEmail = false; // la unicidad del email se controla por su hash, no por la columna estandar
                options.User.AllowedUserNameCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._";
                options.Lockout.MaxFailedAccessAttempts = 5;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                options.Lockout.AllowedForNewUsers = true;
            })
            .AddRoles<IdentityRole<Guid>>()
            .AddEntityFrameworkStores<IdentityDb>()
            .AddDefaultTokenProviders();
        services.AddScoped<AccountService>();
        services.AddAuthorization();

        var permitLimit = builder.Configuration.GetValue("Identity:RateLimitPerMinute", 20);
        services.AddRateLimiter(limiter =>
        {
            limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            limiter.AddPolicy(RateLimitPolicy, http => RateLimitPartition.GetFixedWindowLimiter(
                http.Connection.RemoteIpAddress?.ToString() ?? "anon",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = permitLimit, Window = TimeSpan.FromMinutes(1) }));
        });

        services.AddOpenIddict()
            .AddCore(core => core.UseEntityFrameworkCore().UseDbContext<IdentityDb>())
            .AddServer(server =>
            {
                server.SetTokenEndpointUris("/connect/token");
                server.AllowPasswordFlow().AllowRefreshTokenFlow();
                server.SetAccessTokenLifetime(TimeSpan.FromMinutes(10));
                server.SetRefreshTokenLifetime(TimeSpan.FromDays(14));
                server.RegisterScopes(OpenIddictConstants.Scopes.OfflineAccess);
                server.DisableAccessTokenEncryption(); // los servicios validan el JWT con las claves publicas (JWKS), sin compartir secretos
                ConfigureKeys(server, builder);
                var aspNet = server.UseAspNetCore().EnableTokenEndpointPassthrough();
                if (builder.Environment.IsDevelopment())
                {
                    aspNet.DisableTransportSecurityRequirement(); // en local no hay HTTPS; en la nube TLS lo termina el proxy
                }
            })
            .AddValidation(validation =>
            {
                validation.UseLocalServer();
                validation.UseAspNetCore();
            });

        services.AddHostedService<IdentityInitializer>();
        return builder;
    }

    public static IEndpointRouteBuilder MapIdentityModule(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        AccountEndpoints.Map(app);
        TokenEndpoint.Map(app);
        return app;
    }

    /// <summary>
    /// Claves de firma (RSA, PEM) y de cifrado de los tokens opacos (refresh): secretos <c>Identity:SigningKeyPem</c> y <c>Identity:TokenEncryptionKey</c>
    /// (32 bytes en base64). Solo en Development, si faltan, se generan en memoria (los tokens dejan de valer al reiniciar).
    /// </summary>
    private static void ConfigureKeys(OpenIddictServerBuilder server, WebApplicationBuilder builder)
    {
        var pem = builder.Configuration["Identity:SigningKeyPem"];
        var encryption = builder.Configuration["Identity:TokenEncryptionKey"];
        if (string.IsNullOrWhiteSpace(pem) || string.IsNullOrWhiteSpace(encryption))
        {
            if (!builder.Environment.IsDevelopment())
            {
                throw new InvalidOperationException("Faltan los secretos 'Identity:SigningKeyPem' y 'Identity:TokenEncryptionKey'. Ver docs/autenticacion.md.");
            }

            server.AddEphemeralSigningKey().AddEphemeralEncryptionKey();
            return;
        }

        var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        server.AddSigningKey(new RsaSecurityKey(rsa));
        server.AddEncryptionKey(new SymmetricSecurityKey(Convert.FromBase64String(encryption)));
    }
}
