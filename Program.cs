using Microsoft.Azure.Functions.Worker;
using Microsoft.Azure.Functions.Worker.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Text.Json;
using System.Text.Json.Serialization;
using functions.Converters;
using functions.Services.Interfaces;
using functions.Services.Implementations;

var builder = FunctionsApplication.CreateBuilder(args);

// 👇 Inject user secrets in development
builder.Configuration
    .SetBasePath(Directory.GetCurrentDirectory())
    .AddJsonFile("local.settings.json", optional: true, reloadOnChange: true)
    .AddUserSecrets<Program>(optional: true)
    .AddEnvironmentVariables(); // Still allow environment override

builder.ConfigureFunctionsWebApplication();

builder.Services
    .AddApplicationInsightsTelemetryWorkerService()
    .ConfigureFunctionsApplicationInsights();

// Register HttpClient
builder.Services.AddHttpClient();

// Register JSON options
builder.Services.Configure<JsonSerializerOptions>(options =>
{
    options.PropertyNameCaseInsensitive = true;
    options.WriteIndented = true;
    options.Converters.Add(new JsonStringEnumConverter());
    options.Converters.Add(new DateTimeConverter());
    options.Converters.Add(new DateTimeNullableConverter());
});

// Register services
builder.Services.AddSingleton<IBlobService, BlobService>();
builder.Services.AddSingleton<IAIService, AIService>();

builder.Build().Run();
