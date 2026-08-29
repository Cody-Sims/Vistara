using Microsoft.Extensions.FileProviders;
using Vistara.Api.Composition.Media;
using Vistara.Api.Composition.Platform;
using Vistara.Api.OpenApi.Gallery;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddVistaraApiPlatform(builder.Configuration);
builder.Services.AddVistaraApiPersistence(builder.Configuration);
builder.Services.AddVistaraMedia(builder.Configuration);

WebApplication app = builder.Build();
app.Services.ValidateVistaraApiPlatformComposition();
app.UseVistaraPlatform();
app.MapVistaraPlatformEndpoints();
app.MapVistaraGalleryOpenApi();
app.UseStaticFiles();
app.UseVistaraSpaFallback(async context =>
{
    IFileInfo index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
    if (!index.Exists)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.SendFileAsync(index, context.RequestAborted);
});

await app.RunAsync();

public partial class Program;
