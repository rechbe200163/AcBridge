using AcBridge;
using AcBridge.Telemetry;

var builder = WebApplication.CreateBuilder(args);

var options = builder.Configuration.GetSection("Telemetry").Get<TelemetryOptions>() ?? new();
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<TelemetryStore>();
builder.Services.AddSingleton<TelemetryWebSocket>();
var source = CreateSource(options.Source);
builder.Services.AddSingleton(source);
// Daten aller Autos: echte In-Game-App bei Shared Memory, simulierte KI beim Mock
IFieldSource fieldSource = source switch
{
    MockTelemetrySource => new MockFieldSource(),
    _ when OperatingSystem.IsWindows() => new AcAppFieldSource(),
    _ => new NoFieldSource(),
};
builder.Services.AddSingleton(fieldSource);
builder.Services.AddHostedService<TelemetryPoller>();

// Offen für ein späteres Angular-Dashboard im LAN
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

var app = builder.Build();

app.UseCors();
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

// ---------- WebSocket-Stream ----------
app.Map("/ws/telemetry", (HttpContext ctx, TelemetryWebSocket handler) => handler.HandleAsync(ctx));

// ---------- REST ----------
var api = app.MapGroup("/api");

api.MapGet("/status", (TelemetryStore store) => Results.Ok(store.GetStatus()));

api.MapGet("/session", (TelemetryStore store) =>
    store.Session is { } s ? Results.Ok(s) : Results.NoContent());

api.MapGet("/telemetry", (TelemetryStore store) =>
    store.Latest is { } f ? Results.Ok(f) : Results.NoContent());

app.MapGet("/", () => Results.Text(
    "AC Bridge läuft.\n\nLIVE /live   (Testseite im Browser)\nWS   /ws/telemetry?hz=30\nGET  /api/status\nGET  /api/session\nGET  /api/telemetry\n"));

app.MapGet("/live", () => Results.Content(LivePage.Html, "text/html; charset=utf-8"));

app.Run();

static ITelemetrySource CreateSource(string mode)
{
    var useSharedMemory = mode.Equals("SharedMemory", StringComparison.OrdinalIgnoreCase)
        || (mode.Equals("Auto", StringComparison.OrdinalIgnoreCase) && OperatingSystem.IsWindows());

    if (useSharedMemory)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Shared Memory gibt's nur unter Windows. Setz Telemetry:Source auf \"Mock\".");
        return new AcSharedMemorySource();
    }
    return new MockTelemetrySource();
}
