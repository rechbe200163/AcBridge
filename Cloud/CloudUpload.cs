using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AcBridge.Telemetry;

namespace AcBridge.Cloud;

public sealed class CloudOptions
{
    /// <summary>z. B. https://xyz.supabase.co</summary>
    public string SupabaseUrl { get; set; } = "";
    /// <summary>Secret key (sb_secret_…) oder legacy service_role. Gehört NICHT ins Git → appsettings.Local.json</summary>
    public string SecretKey { get; set; } = "";

    public bool Enabled => !string.IsNullOrWhiteSpace(SupabaseUrl) && !string.IsNullOrWhiteSpace(SecretKey);
}

// ---------- Datensätze, wie sie in die Tabellen gehen (snake_case) ----------

public record SessionRow(Guid Id, string Track, string Layout, string Car, string? SessionType,
    DateTimeOffset StartedAt, int? BestLapMs, int LapCount);

public record LapRow(Guid Id, Guid SessionId, int LapNumber, int LapMs, int[] SectorMs, bool Valid,
    string? Compound, DateTimeOffset RecordedAt);

public record LapTraceRow(Guid LapId, float SpacingM, Dictionary<string, object> Channels);

/// <summary>Ein Auftrag in der Warteschlange: entweder eine Runde oder ein Streckenumriss.</summary>
public record UploadJob(string Kind, SessionRow? Session = null, LapRow? Lap = null, LapTraceRow? Trace = null,
    TrackOutline? Track = null, List<Corner>? Corners = null);

/// <summary>
/// Warteschlange auf der Platte: ein JSON pro Auftrag unter %LOCALAPPDATA%\AcBridge\outbox.
/// Nach erfolgreichem Upload wird die Datei gelöscht → es bleibt nur liegen, was (noch) nicht hochgeladen ist.
/// </summary>
public sealed class CloudOutbox
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly string _dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AcBridge", "outbox");

    public event Action? Changed;

    public void Enqueue(UploadJob job)
    {
        Directory.CreateDirectory(_dir);
        var name = $"{DateTime.UtcNow:yyyyMMddHHmmssfff}_{job.Kind}_{Guid.NewGuid():N}.json";
        var tmp = Path.Combine(_dir, name + ".tmp");
        File.WriteAllText(tmp, JsonSerializer.Serialize(job, Json));
        File.Move(tmp, Path.Combine(_dir, name));          // erst fertig geschrieben, dann sichtbar
        Changed?.Invoke();
    }

    public IEnumerable<string> Pending() =>
        Directory.Exists(_dir) ? Directory.EnumerateFiles(_dir, "*.json").Order() : [];

    public UploadJob? Read(string file) => JsonSerializer.Deserialize<UploadJob>(File.ReadAllText(file), Json);
}

/// <summary>Minimaler Supabase-REST-Client (PostgREST), nur was die Bridge braucht.</summary>
public sealed class SupabaseRest(HttpClient http, CloudOptions options)
{
    public async Task<JsonArray> UpsertAsync(string table, object rows, string onConflict, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Post, $"{table}?on_conflict={onConflict}");
        req.Headers.Add("Prefer", "resolution=merge-duplicates,return=representation");
        req.Content = new StringContent(JsonSerializer.Serialize(rows, CloudOutbox.Json), Encoding.UTF8, "application/json");
        return await SendAsync(req, ct);
    }

    public async Task DeleteAsync(string table, string filter, CancellationToken ct)
    {
        using var req = Request(HttpMethod.Delete, $"{table}?{filter}");
        await SendAsync(req, ct);
    }

    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var req = new HttpRequestMessage(method, $"{options.SupabaseUrl.TrimEnd('/')}/rest/v1/{path}");
        req.Headers.Add("apikey", options.SecretKey);
        if (options.SecretKey.StartsWith("eyJ"))   // legacy service_role (JWT) braucht zusätzlich Bearer
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.SecretKey);
        return req;
    }

    private async Task<JsonArray> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        using var res = await http.SendAsync(req, ct);
        var body = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
            throw new HttpRequestException($"Supabase {(int)res.StatusCode}: {body}");
        return string.IsNullOrWhiteSpace(body) ? [] : JsonNode.Parse(body) as JsonArray ?? [];
    }
}

/// <summary>Arbeitet die Warteschlange ab. Ohne Internet bleibt alles liegen und wird später nachgeholt.</summary>
public sealed class CloudUploader(CloudOutbox outbox, SupabaseRest rest, CloudOptions options, ILogger<CloudUploader> log)
    : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Backoff = TimeSpan.FromSeconds(60);

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Enabled)
        {
            log.LogInformation("Cloud-Upload aus (Cloud:SupabaseUrl / Cloud:SecretKey nicht gesetzt) → es wird nichts aufgezeichnet");
            return;
        }
        log.LogInformation("Cloud-Upload aktiv → {Url}", options.SupabaseUrl);

        var signal = new SemaphoreSlim(0);
        outbox.Changed += () => signal.Release();

        while (!ct.IsCancellationRequested)
        {
            var failed = false;
            foreach (var file in outbox.Pending())
            {
                try
                {
                    if (outbox.Read(file) is { } job) await UploadAsync(job, ct);
                    File.Delete(file);
                }
                catch (OperationCanceledException) { return; }
                catch (Exception ex)
                {
                    log.LogWarning("Upload fehlgeschlagen ({File}), neuer Versuch in {Sec} s: {Msg}",
                        Path.GetFileName(file), Backoff.TotalSeconds, ex.Message);
                    failed = true;
                    break;   // Reihenfolge einhalten: Session vor Runde
                }
            }
            try { await signal.WaitAsync(failed ? Backoff : Idle, ct); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task UploadAsync(UploadJob job, CancellationToken ct)
    {
        switch (job.Kind)
        {
            case "lap" when job.Session is { } s && job.Lap is { } lap && job.Trace is { } trace:
                await rest.UpsertAsync("sessions", new[] { s }, "id", ct);
                await rest.UpsertAsync("laps", new[] { lap }, "id", ct);
                await rest.UpsertAsync("lap_traces", new[] { trace }, "lap_id", ct);
                log.LogInformation("Runde {Lap} hochgeladen ({Ms} ms, {Valid})",
                    lap.LapNumber, lap.LapMs, lap.Valid ? "gültig" : "ungültig");
                break;

            case "track" when job.Track is { } t:
                var length = 0.0;
                for (var i = 1; i < t.Points.Count; i++)
                    length += Math.Sqrt(Math.Pow(t.Points[i].X - t.Points[i - 1].X, 2) + Math.Pow(t.Points[i].Z - t.Points[i - 1].Z, 2));

                var saved = await rest.UpsertAsync("tracks", new[]
                {
                    new
                    {
                        track = t.Track, layout = t.Layout, length_m = Math.Round(length),
                        outline = t.Points.Select(p => new { s = p.S, x = p.X, z = p.Z }),
                        recorded_car = t.Car, recorded_lap_ms = t.LapMs,
                    },
                }, "track,layout", ct);
                var trackId = saved.FirstOrDefault()?["id"]?.GetValue<string>()
                              ?? throw new InvalidOperationException("Supabase hat keine Track-ID zurückgegeben");

                await rest.DeleteAsync("track_corners", $"track_id=eq.{trackId}", ct);
                var corners = job.Corners ?? [];
                if (corners.Count > 0)
                    await rest.UpsertAsync("track_corners", corners.Select(c => new
                    {
                        track_id = trackId, number = c.Number, direction = c.Direction,
                        entry_s = c.EntryS, apex_s = c.ApexS, exit_s = c.ExitS,
                    }).ToArray(), "track_id,number", ct);
                log.LogInformation("Strecke {Track} {Layout} hochgeladen ({Corners} Kurven)", t.Track, t.Layout, corners.Count);
                break;

            default:
                log.LogWarning("Unbekannter Upload-Auftrag {Kind}, wird verworfen", job.Kind);
                break;
        }
    }
}
