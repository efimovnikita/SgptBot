using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace VectorStoreWebApi.Health;

public class DataBaseHealthCheck : IHealthCheck
{
    private readonly IHttpClientFactory _httpClientFactory;

    public DataBaseHealthCheck(IHttpClientFactory httpClientFactory)
    {
        _httpClientFactory = httpClientFactory;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context,
        CancellationToken cancellationToken = new())
    {
        try
        {
            string endpoint = Environment.GetEnvironmentVariable("CHROMADBENDPOINT") ?? "";

            HttpClient client = _httpClientFactory.CreateClient();

            HttpResponseMessage response = await client.GetAsync(endpoint + "/api/v1");
            response.EnsureSuccessStatusCode();

            return HealthCheckResult.Healthy();
        }
        catch (Exception e)
        {
            return HealthCheckResult.Unhealthy(exception: e);
        }
    }
}