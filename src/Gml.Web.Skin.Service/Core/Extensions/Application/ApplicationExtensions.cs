using System.Net;
using Gml.Web.Skin.Service.Core.Mapper;
using Gml.Web.Skin.Service.Core.Requests;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.HttpOverrides;

namespace Gml.Web.Skin.Service.Core.Extensions.Application;

public static class ApplicationExtensions
{
    private static string _policyName = "SkinServicePolicy";

    // Matches TextureRequests.MaxUploadBytes — belt-and-suspenders cap at the server level
    // in case a future upload endpoint forgets the per-request check.
    private const long MaxUploadBytes = 10 * 1024 * 1024;

    public static WebApplicationBuilder CreateService(this WebApplicationBuilder builder)
    {
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        builder.Services.AddAntiforgery();
        builder.Services.AddAutoMapper(map =>
        {
            map.AddProfile<TextureMapper>();
        });

        builder.Services
            .AddCors(o => o.AddPolicy(_policyName, policyBuilder =>
            {
                policyBuilder.AllowAnyOrigin()
                    .AllowAnyMethod()
                    .AllowAnyHeader();
            }));

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxRequestBodySize = MaxUploadBytes;
        });

        builder.Services.Configure<FormOptions>(options =>
        {
            options.MultipartBodyLengthLimit = MaxUploadBytes;
        });

        CheckFolders();

        return builder;
    }

    private static void CheckFolders()
    {
        if (!Directory.Exists(SkinHelper.SkinTextureDirectory))
            Directory.CreateDirectory(SkinHelper.SkinTextureDirectory);

        if (!Directory.Exists(SkinHelper.CloakTextureDirectory))
            Directory.CreateDirectory(SkinHelper.CloakTextureDirectory);

        if (!Directory.Exists(SkinHelper.MojangCacheDirectory))
            Directory.CreateDirectory(SkinHelper.MojangCacheDirectory);
    }

    public static WebApplication Run(this WebApplicationBuilder builder)
    {
        var app = builder.Build();

        app.UseSwagger();
        app.UseSwaggerUI();
        app.UseCors(_policyName);

        // Same topology as Jokerge.Proxy: TLS is terminated upstream and forwarded to this
        // container as plain HTTP, so UseHttpsRedirection alone would redirect-loop every
        // request. ForwardedHeadersMiddleware first restores the original scheme from
        // X-Forwarded-Proto (only trusted from the Docker-private network ranges below).
        var forwardedHeadersOptions = new ForwardedHeadersOptions
        {
            ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
        };
        forwardedHeadersOptions.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("10.0.0.0"), 8));
        forwardedHeadersOptions.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("172.16.0.0"), 12));
        forwardedHeadersOptions.KnownNetworks.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(IPAddress.Parse("192.168.0.0"), 16));
        app.UseForwardedHeaders(forwardedHeadersOptions);
        app.UseHttpsRedirection();

        app.AddRoutes();
        app.UseAntiforgery();

        app.Run();

        return app;
    }

    private static WebApplication AddRoutes(this WebApplication app)
    {
        app.MapGet("/{userName}", TextureRequests.GetUserTexture);

        app.MapPost("/skin/{userName}", TextureRequests.LoadSkin).DisableAntiforgery();
        app.MapDelete("/skin/{userName}", TextureRequests.DeleteSkin);
        app.MapGet("/skin/{userName}/{uuid?}", TextureRequests.GetSkin);
        app.MapGet("/skin/{userName}/head/{size}", TextureRequests.GetSkinHead);
        app.MapGet("/skin/{userName}/front/{size}", TextureRequests.GetSkinFront);
        app.MapGet("/skin/{userName}/back/{size}", TextureRequests.GetSkinBack);
        app.MapGet("/skin/{userName}/full-back/{size}", TextureRequests.GetSkinAndCloakBack);

        app.MapGet("/cloak/{userName}", TextureRequests.GetCloakTexture);
        app.MapPost("/cloak/{userName}", TextureRequests.LoadCloak).DisableAntiforgery();;
        app.MapDelete("/cloak/{userName}", TextureRequests.DeleteCloak);
        app.MapGet("/cloak/{userName}/front/{size}", TextureRequests.GetCloak);

        app.MapGet("/refresh/{userName}", TextureRequests.RefreshCache);

        return app;
    }
}
