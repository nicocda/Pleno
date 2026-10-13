using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration.UserSecrets;
using Microsoft.Extensions.Hosting;

namespace Casino.AppHost;

/// <summary>
/// Claves de desarrollo que se crean solas la primera vez: si falta el secreto, se genera (32 bytes aleatorios en base64) y se guarda en los user-secrets de esta
/// maquina, igual que haria `dotnet user-secrets set`. Asi `aspire run` funciona sin pasos previos. Solo en Development; nada de esto se versiona.
/// Importante: la clave del email se guarda UNA vez y se reutiliza siempre (si se perdiera, los emails ya guardados en la base quedarian ilegibles).
/// </summary>
internal static class DevSecrets
{
    public static void EnsureKey(IDistributedApplicationBuilder builder, string key)
    {
        if (!string.IsNullOrWhiteSpace(builder.Configuration[key]) || !builder.Environment.IsDevelopment())
        {
            return;
        }

        var id = Assembly.GetEntryAssembly()?.GetCustomAttribute<UserSecretsIdAttribute>()?.UserSecretsId;
        if (string.IsNullOrEmpty(id))
        {
            return;
        }

        var directory = OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Microsoft", "UserSecrets", id)
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".microsoft", "usersecrets", id);
        var file = Path.Combine(directory, "secrets.json");

        var secrets = File.Exists(file) ? JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? [] : [];
        // Si otro proceso ya lo guardo, se usa ese valor (nunca se pisa una clave existente).
        var value = secrets[key]?.GetValue<string>() is { Length: > 0 } existing ? existing : Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        secrets[key] = value;

        Directory.CreateDirectory(directory);
        File.WriteAllText(file, secrets.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        builder.Configuration[key] = value;
    }
}
