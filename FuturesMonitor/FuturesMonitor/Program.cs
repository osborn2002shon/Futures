using FuturesMonitor.Services;

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

app.MapRazorPages();

app.Run();
