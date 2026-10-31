using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Matterportable.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;

namespace Matterportable.Web
{
    /// <summary>
    /// The web version's server (the browser can't fetch Matterport's data itself: no CORS). Downloads spaces with
    /// the shared Core into the same MatterportCache the Unity and Godot versions use, prepares a compact web bundle
    /// per space (<see cref="WebBundle"/>), and serves the client (web/client), the WebAssembly Core and the photos.
    /// Settings (environment): ASPNETCORE_URLS (default http://localhost:8080), MATTERPORTABLE_CACHE (cache folder),
    /// MATTERPORTABLE_ALLOW_DOWNLOADS=false (serve only spaces already in the cache, e.g. when hosting your own).
    /// </summary>
    public static class Program
    {
        public static void Main(string[] args)
        {
            var builder = WebApplication.CreateBuilder(args);
            builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);
            builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:8080");
            builder.Services.AddResponseCompression(o =>
            {
                o.EnableForHttps = true;
                o.Providers.Add<BrotliCompressionProvider>();
                o.Providers.Add<GzipCompressionProvider>();
                o.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(new[] { "application/octet-stream", "application/wasm" });
            });
            var app = builder.Build();
            var spaces = new SpaceService(CacheRoot(), Environment.GetEnvironmentVariable("MATTERPORTABLE_ALLOW_DOWNLOADS") != "false");
            app.UseResponseCompression();

            var types = new FileExtensionContentTypeProvider();
            types.Mappings[".wasm"] = "application/wasm";
            types.Mappings[".dat"] = "application/octet-stream";
            var client = new CompositeFileProvider(ClientFolders().Select(d => (IFileProvider)new PhysicalFileProvider(d)).ToArray());
            // The WebAssembly runtime ships pre-compressed (.br): send that instead of compressing 5 MB per request.
            app.Use(async (ctx, next) =>
            {
                var path = ctx.Request.Path.Value ?? "";
                if (path.StartsWith("/_framework/") && ctx.Request.Headers.AcceptEncoding.ToString().Contains("br"))
                {
                    var br = client.GetFileInfo(path + ".br");
                    if (br.Exists)
                    {
                        ctx.Response.Headers.ContentEncoding = "br";
                        ctx.Response.Headers.Vary = "Accept-Encoding";
                        ctx.Response.Headers.CacheControl = "no-cache";
                        ctx.Response.ContentType = types.TryGetContentType(path, out var type) ? type : "application/octet-stream";
                        ctx.Response.ContentLength = br.Length;
                        await ctx.Response.SendFileAsync(br);
                        return;
                    }
                }
                await next();
            });
            app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = client });
            app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = client,
                ContentTypeProvider = types,
                OnPrepareResponse = c => c.Context.Response.Headers.CacheControl = "no-cache", // revalidate (ETag): edits show on reload
            });

            app.MapGet("/api/spaces", () => Results.Json(spaces.List()));
            app.MapPost("/api/open", (string link) =>
                MatterportLink.TryParseModelId(link ?? "", out var id) ? Results.Json(spaces.Open(id)) : Results.BadRequest("Not a Matterport link or model ID."));
            app.MapPost("/api/demo", async () => Results.Json(await spaces.OpenDemoAsync()));
            app.MapGet("/api/spaces/{id}/status", (string id) => spaces.Status(id) is { } s ? Results.Json(s) : Results.NotFound());
            app.MapGet("/api/spaces/{id}/web/{file}", (string id, string file) => spaces.BundleFile(id, file) is { } path
                ? Results.File(path, file.EndsWith(".json") ? "application/json" : "application/octet-stream")
                : Results.NotFound());
            app.MapGet("/api/spaces/{id}/files/{**path}", (string id, string path, HttpContext ctx) =>
            {
                var full = spaces.SpaceFile(id, path);
                if (full == null) return Results.NotFound();
                ctx.Response.Headers.CacheControl = "public, max-age=86400";
                return Results.File(full, types.TryGetContentType(full, out var type) ? type : "application/octet-stream");
            });

            Console.WriteLine($"Matterportable web: open {app.Urls.FirstOrDefault() ?? Environment.GetEnvironmentVariable("ASPNETCORE_URLS") ?? "http://localhost:8080"}  (cache: {spaces.Root})");
            app.Run();
        }

        /// <summary>The client sources when run from the repository (edits apply on reload), then the built wwwroot.</summary>
        static IEnumerable<string> ClientFolders()
        {
            var source = FindUp("web/client/index.html");
            if (source != null) yield return Path.GetDirectoryName(source);
            var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
            if (Directory.Exists(wwwroot)) yield return wwwroot;
        }

        /// <summary>MATTERPORTABLE_CACHE, else the repository's MatterportCache (shared with Unity and Godot).</summary>
        static string CacheRoot()
        {
            var env = Environment.GetEnvironmentVariable("MATTERPORTABLE_CACHE");
            if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);
            var package = FindUp("Packages/com.thepromptcook.matterportable/package.json");
            var repo = package != null ? Path.GetFullPath(Path.Combine(Path.GetDirectoryName(package), "..", "..")) : AppContext.BaseDirectory;
            return Path.Combine(repo, CacheLayout.DefaultFolderName);
        }

        static string FindUp(string relative)
        {
            foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
                for (var dir = new DirectoryInfo(start); dir != null; dir = dir.Parent)
                {
                    var candidate = Path.Combine(dir.FullName, relative);
                    if (File.Exists(candidate)) return candidate;
                }
            return null;
        }
    }
}
