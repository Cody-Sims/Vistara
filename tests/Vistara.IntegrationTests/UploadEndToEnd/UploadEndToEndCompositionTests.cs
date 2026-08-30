using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Vistara.Api.Composition.Platform;
using Vistara.Api.Features.Uploads;
using Vistara.Application.Assets.Ingest;
using Vistara.Worker.Composition.Platform;
using Vistara.Worker.Features.Ingest;
using Xunit;

namespace Vistara.IntegrationTests.UploadEndToEnd;

public sealed class UploadEndToEndCompositionTests
{
    [Fact]
    public async Task UploadEndToEnd_production_api_composition_maps_the_six_upload_routes()
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddVistaraApiPlatform(builder.Configuration);
        await using WebApplication app = builder.Build();

        app.MapVistaraPlatformEndpoints();

        string[] uploadRoutes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .Where(route => route?.StartsWith(
                "/api/v1/uploads",
                StringComparison.Ordinal) == true)
            .Cast<string>()
            .ToArray();

        Assert.Equal(6, uploadRoutes.Length);
    }

    [Fact]
    public void UploadEndToEnd_production_api_composition_registers_upload_auth_and_application()
    {
        ServiceCollection services = [];
        services.AddVistaraApiPlatform(new ConfigurationManager());

        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IUploadAuthorizationPort));
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IUploadApplicationPort));
    }

    [Fact]
    public void UploadEndToEnd_production_worker_composition_registers_ingest_transactions()
    {
        ServiceCollection services = [];
        services.AddVistaraWorkerPlatform(WorkerConfiguration());

        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IIngestTransactionPort));
        Assert.Contains(
            services,
            descriptor => descriptor.ServiceType == typeof(IAssetIngestUnitOfWork));
    }

    private static IConfiguration WorkerConfiguration()
    {
        var values = new Dictionary<string, string?>
        {
            ["Persistence:Provider"] = "Sqlite",
            ["Persistence:ConnectionString"] = "Data Source=:memory:",
            ["Worker:InstanceId"] = "upload-end-to-end",
            ["Worker:Jobs:MaximumConcurrency"] = "1",
        };
        return new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
    }
}
