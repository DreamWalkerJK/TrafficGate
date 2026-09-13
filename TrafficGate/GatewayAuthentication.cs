using System.Security.Cryptography;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.IdentityModel.Tokens;

namespace TrafficGate;

public static class GatewayAuthentication
{
    public const string ProxyScheme = "proxy";
    public const string AdminScheme = "admin-bearer";
    public const string AdminPolicy = "trafficgate.admin";

    public static IServiceCollection AddGatewayAuthentication(this IServiceCollection services, IConfiguration config, int managementPort)
    {
        services.AddAuthentication("gateway")
            .AddPolicyScheme("gateway", "Gateway listener identity", options =>
                options.ForwardDefaultSelector = c => c.Connection.LocalPort == managementPort ? AdminScheme : ProxyScheme)
            .AddJwtBearer(ProxyScheme, options => ConfigureJwt(options, config.GetSection("TrafficGate:Jwt")))
            .AddJwtBearer(AdminScheme, options => ConfigureJwt(options, config.GetSection("TrafficGate:AdminJwt")));
        services.AddAuthorization(o =>
        {
            o.DefaultPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
            o.AddPolicy("authenticated", p => p.RequireAuthenticatedUser());
            o.AddPolicy(AdminPolicy, p => p.AddAuthenticationSchemes(AdminScheme).RequireAuthenticatedUser().RequireClaim("role", "trafficgate.admin"));
            foreach (var entry in config.GetSection("TrafficGate:AuthorizationPolicies").GetChildren())
            {
                var claim = entry["Claim"] ?? throw new InvalidOperationException("Authorization policy requires a claim.");
                var value = entry["Value"] ?? throw new InvalidOperationException("Authorization policy requires a value.");
                if (entry.Key is "authenticated" or AdminPolicy) throw new InvalidOperationException("Reserved authorization policy name.");
                o.AddPolicy(entry.Key, p => p.RequireAuthenticatedUser().RequireClaim(claim, value));
            }
        });
        return services;
    }

    private static void ConfigureJwt(JwtBearerOptions options, IConfiguration section)
    {
        options.MapInboundClaims = false;
        options.RequireHttpsMetadata = true;
        options.IncludeErrorDetails = false;
        options.Authority = section["Authority"];
        options.Audience = section["Audience"];
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true,
            ValidateIssuerSigningKey = true, RequireSignedTokens = true, RequireExpirationTime = true,
            ValidIssuer = section["Issuer"], ValidAudience = section["Audience"],
            ClockSkew = TimeSpan.FromSeconds(30), NameClaimType = "sub", RoleClaimType = "role"
        };
        if (section["PublicKeyPath"] is { Length: > 0 } publicKeyPath)
        {
            var rsa = RSA.Create();
            rsa.ImportFromPem(File.ReadAllText(publicKeyPath));
            options.TokenValidationParameters.IssuerSigningKey = new RsaSecurityKey(rsa);
            options.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.RsaSha256];
        }
    }
}
