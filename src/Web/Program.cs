using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using AgenticHotelBooking.Web;

var builder = WebAssemblyHostBuilder.CreateDefault(args);
builder.RootComponents.Add<App>("#app");
builder.RootComponents.Add<HeadOutlet>("head::after");

var apiBaseUrl = builder.Configuration["ApiBaseUrl"] ?? builder.HostEnvironment.BaseAddress;
var apiOrigin = new Uri(apiBaseUrl);
builder.Services.AddScoped(services => new AdministratorSession(
    new HttpClient { BaseAddress = apiOrigin }, services.GetRequiredService<Microsoft.JSInterop.IJSRuntime>()));
builder.Services.AddScoped(services => new HttpClient(new AdministratorSessionHandler(
    services.GetRequiredService<AdministratorSession>(), apiOrigin)
{ InnerHandler = new HttpClientHandler() })
{ BaseAddress = apiOrigin });

await builder.Build().RunAsync();
