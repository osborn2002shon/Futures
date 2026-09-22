using Futures.ReadModel;
using FuturesMcp;
using FuturesMcp.Tools;
using ModelContextProtocol.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<FuturesDataOptions>(builder.Configuration.GetSection(FuturesDataOptions.SectionName));
builder.Services.AddSingleton<FuturesDashboardService>();
builder.Services
    .AddMcpServer()
    .WithHttpTransport(options =>
    {
        options.SessionMode = HttpServerSessionMode.Stateless;
    })
    .WithToolsFromAssembly()
    .WithResourcesFromAssembly()
    .WithPromptsFromAssembly();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new
{
    service = "FuturesMcp",
    status = "ok",
    mcpEndpoint = "/mcp",
    serviceUrl = FuturesServiceInfo.ServiceUrl,
    realTimeQuotesMessage = FuturesServiceInfo.RealTimeQuotesMessage
}));

app.MapMcp("/");

app.Run();
//