using Futures.ReadModel;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();
builder.Services.Configure<FuturesDataOptions>(builder.Configuration.GetSection(FuturesDataOptions.SectionName));
builder.Services.AddSingleton<FuturesDashboardService>();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapGet("/api/dashboard", async (FuturesDashboardService dashboardService, CancellationToken cancellationToken) =>
    Results.Ok(await dashboardService.GetSnapshotAsync(cancellationToken)));

app.MapGet("/api/minute-k-bars", async (
    int? intervalMinutes,
    FuturesDashboardService dashboardService,
    CancellationToken cancellationToken) =>
{
    var interval = intervalMinutes ?? 1;
    if (!FuturesDashboardService.SupportedMinuteKIntervals.Contains(interval))
    {
        return Results.BadRequest(new
        {
            message = "Unsupported intervalMinutes. Supported values are 1, 5, 15, 30, and 60.",
            supportedIntervalMinutes = FuturesDashboardService.SupportedMinuteKIntervals
        });
    }

    return Results.Ok(await dashboardService.GetMinuteKBarsAsync(interval, cancellationToken));
});

app.MapRazorPages();

app.Run();
