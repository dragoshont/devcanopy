using System.ComponentModel;
using System.Net.Http.Json;
using Microsoft.SemanticKernel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net;
using System.Text;
using System.Globalization;

/// <summary>
/// A simple plugin that provides weather information.
/// The class and its methods are discoverable by Semantic Kernel.
/// </summary>
public class WeatherPlugin
{
    private readonly HttpClient _httpClient;

    public WeatherPlugin(HttpClient client)
    {
        _httpClient = client;
    }

    /// <summary>
    /// The name of the city or location to get coordinates for
    /// </summary>
    /// <param name="locationName"></param>
    /// <returns></returns>
    [KernelFunction, Description("Resolve a place name to its geographic latitude and longitude (first best match).")]
    public async Task<(double Latitude, double Longitude)?> GetCoordinates(
        [Description("City, landmark, or place name (e.g., 'Berlin', 'Golden Gate Bridge').")] string locationName)
    {
        var requestUri = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(locationName)}&count=1";
        var response = await _httpClient.GetFromJsonAsync<GeocodingResponse>(requestUri);
        var location = response?.Results?.FirstOrDefault();

        if (location != null)
        {
            return (location.Latitude, location.Longitude);
        }
        return null;
    }

    /// <summary>
    /// Gets the current weather for a specified location.
    /// </summary>
    /// <param name="latitude">The latitude coordinate of the location</param>
    /// <param name="longitude">The longitude coordinate of the location</param>
    /// <returns></returns>
    [KernelFunction, Description("Get current weather (temperature and humidity) or air-quality (pollen/AQI) for coordinates. Mode: 'weather' (default) or 'airquality'.")]
    public async Task<string?> WeatherSummary(
        [Description("Latitude in decimal degrees (e.g., 52.52)")] double latitude,
        [Description("Longitude in decimal degrees (e.g., 13.41)")] double longitude,
        [Description("Mode: 'weather' to show temperature+humidity (default), 'airquality' to show pollen/AQI grouped summary.")] string mode = "weather")
    {
        if (string.Equals(mode, "airquality", StringComparison.OrdinalIgnoreCase))
        {
            // Return grouped air-quality / pollen summary (human-friendly text)
            var aq = await GetPollenSummary(latitude, longitude);
            return aq;
        }

        // Default: weather mode (temperature + humidity)
        var data = await GetWeatherData(latitude, longitude);
        if (data == null) return "Weather data not available.";

        // Fetch relative humidity for the current time (UTC) near the requested coordinate
        float? humidity = null;
        try
        {
            humidity = await GetHumidityValue(latitude, longitude, data.TimestampUtc);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Humidity fetch failed: {ex.Message}");
        }

        var tempDisplay = data.TemperatureC.HasValue ? $"{data.TemperatureC:0.#}°C ({data.TemperatureF:0.#}°F)" : "N/A";
        var humidityDisplay = humidity.HasValue ? $"{humidity:0.#}%" : "N/A";
        var windDisplay = data.WindSpeedKmh.HasValue ? (data.WindDirectionDeg.HasValue ? $"Wind {data.WindSpeedKmh:0.#} km/h from {DegreesToCompass(data.WindDirectionDeg.Value)} ({data.WindDirectionDeg:0.#}°)" : $"Wind {data.WindSpeedKmh:0.#} km/h") : null;
        var windPhrase = !string.IsNullOrEmpty(windDisplay) ? $" {windDisplay}." : string.Empty;
        return $"As of {data.FriendlyLocalTime}, the temperature is {tempDisplay} and the relative humidity is {humidityDisplay}.{windPhrase} Units: temperature in Celsius (F in parentheses), humidity in percent.";
    }

    /// <summary>
    /// Get structured weather + pollen summary for a place name (returns JSON string).
    /// </summary>
    /// <param name="placeName">City or place name to resolve (e.g., 'Berlin', 'New York').</param>
    /// <returns>JSON string with structured weather and pollen data, or error information.</returns>
    [KernelFunction, Description("Get weather or air-quality for a place. Mode='weather' returns structured JSON (default). Mode='airquality' returns a human-friendly text summary of pollen/AQI. When mode='weather', set includePollen=true to include pollen summary in the JSON.")]
    public async Task<string> WeatherSummaryForPlace(
        [Description("City or place name to resolve (e.g., 'Berlin', 'New York').")] string placeName,
        [Description("Mode: 'weather' (default) or 'airquality' to return a human-friendly pollen/AQI summary.")] string mode = "weather",
        [Description("When true and mode='weather', include pollen summary in the returned JSON via PollenFallbackSummary.")] bool includePollen = false)
    {
        var coords = await GetCoordinates(placeName);
        if (coords is null)
        {
            return JsonSerializer.Serialize(new { error = "Location not found", place = placeName });
        }
        if (string.Equals(mode, "airquality", StringComparison.OrdinalIgnoreCase))
        {
            // Return a human-friendly air-quality (pollen/AQI) summary
            try
            {
                var aq = await GetPollenSummary(coords.Value.Latitude, coords.Value.Longitude);
                return aq;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Air-quality summary failed for place {placeName}: {ex.Message}");
                return JsonSerializer.Serialize(new { error = "Air-quality data not available", place = placeName });
            }
        }

        // Mode == "weather": return a human-friendly sentence for the place
        try
        {
            var weatherText = await WeatherSummary(coords.Value.Latitude, coords.Value.Longitude);
            if (weatherText is null)
            {
                return JsonSerializer.Serialize(new { error = "Weather data not available", place = placeName });
            }

            if (includePollen)
            {
                try
                {
                    var pollenText = await GetPollenSummary(coords.Value.Latitude, coords.Value.Longitude);
                    // append pollen/AQ summary on its own paragraph for readability
                    return weatherText + "\n" + pollenText;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Pollen inclusion failed for place {placeName}: {ex.Message}");
                    return weatherText;
                }
            }

            return weatherText;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WeatherSummaryForPlace failed for {placeName}: {ex.Message}");
            return JsonSerializer.Serialize(new { error = "Weather data not available", place = placeName });
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    // The default set of 'current' fields requested from the air-quality API
    private const string CurrentFields = "ragweed_pollen,olive_pollen,mugwort_pollen,grass_pollen,birch_pollen,alder_pollen,ammonia,uv_index_clear_sky,uv_index,dust,aerosol_optical_depth,ozone,sulphur_dioxide,nitrogen_dioxide,carbon_monoxide,pm2_5,pm10,us_aqi,european_aqi";

    // Core reusable logic that returns structured data
    private async Task<WeatherSummaryData?> GetWeatherData(double latitude, double longitude)
    {
        // Use the forecast endpoint for current temperature. Pollen is fetched separately via /v1/pollen.
        var requestUri = $"https://api.open-meteo.com/v1/forecast?latitude={latitude}&longitude={longitude}&current_weather=true&timezone=UTC";
        using var resp = await _httpClient.GetAsync(requestUri);

        if (!resp.IsSuccessStatusCode)
        {
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                Console.WriteLine($"Forecast request 404: {requestUri}");
                return null;
            }

            Console.WriteLine($"Forecast request failed: {resp.StatusCode} ({requestUri})");
            return null;
        }

        var content = await resp.Content.ReadAsStringAsync();
        var response = JsonSerializer.Deserialize<ForecastResponse>(content, JsonOptions);
        if (response?.CurrentWeather == null)
        {
            Console.WriteLine($"Forecast response for {requestUri} did not contain 'current_weather'. Response body: {content}");
            return null;
        }

        var cw = response.CurrentWeather;
        DateTime? parsedUtc = null;
        string friendlyLocalTime;
        if (DateTime.TryParse(cw.Time, out var parsed))
        {
            parsedUtc = DateTime.SpecifyKind(parsed, DateTimeKind.Utc);
            var local = TimeZoneInfo.ConvertTimeFromUtc(parsedUtc.Value, TimeZoneInfo.Local);
            var offset = TimeZoneInfo.Local.GetUtcOffset(local);
            var sign = offset.TotalMinutes < 0 ? "-" : "+";
            friendlyLocalTime = $"{local:MMMM} {GetDayOrdinal(local.Day)} at {local:HH:mm} (UTC{sign}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes % 60):00})";
        }
        else
        {
            friendlyLocalTime = cw.Time;
        }

        // Convert Celsius to Fahrenheit for convenience
        float? tempF = cw.Temperature.HasValue ? (float?)(cw.Temperature.Value * 9f / 5f + 32f) : null;
        var windSpeed = cw.WindSpeed;
        var windDir = cw.WindDirection;

         return new WeatherSummaryData
         {
             Latitude = latitude,
             Longitude = longitude,
             TimestampUtc = parsedUtc,
             FriendlyLocalTime = friendlyLocalTime,
             TemperatureC = cw.Temperature,
             TemperatureF = tempF,
             WindSpeedKmh = windSpeed,
             WindDirectionDeg = windDir,
             GrassPollen = null,
             BirchPollen = null,
             RagweedPollen = null,
             TemperatureUnit = "C",
             PollenUnit = "grains/m^3"
         };
     }

    [KernelFunction, Description("Get hourly pollen summary (grass, birch, ragweed) for coordinates. Returns a friendly summary string. Nearby search for the nearest available pollen data is enabled by default.")]
    public async Task<string> GetPollenSummary(double latitude, double longitude, bool nearbySearch = true, double maxRadiusDegrees = 0.5, double stepDegrees = 0.02)
    {
        // Use the air-quality API current endpoint to get allergens, pollens and air quality metrics.
        // The air-quality API is available at air-quality-api.open-meteo.com and supports a 'current' parameter
        // returning an object with many pollutant/pollen fields (e.g. ragweed_pollen, grass_pollen, pm2_5, us_aqi...).
        var pollenUri = $"https://air-quality-api.open-meteo.com/v1/air-quality?latitude={latitude}&longitude={longitude}&current={CurrentFields}&timezone=UTC";
        using var pollenResp = await _httpClient.GetAsync(pollenUri);

        string pollenSummary;
        if (pollenResp.IsSuccessStatusCode)
        {
            await using var ps = await pollenResp.Content.ReadAsStreamAsync();
            using var doc = await JsonDocument.ParseAsync(ps);
            var sb = new StringBuilder();

            if (doc.RootElement.TryGetProperty("current", out var curr))
            {
                var grouped = BuildGroupedAirQualitySummary(curr, latitude, longitude);
                sb.Append(grouped);
            }

            pollenSummary = sb.Length > 0 ? sb.ToString().Trim() : "Pollen data available but empty.";
        }
        else
        {
            // Read response body for diagnostics (some Open-Meteo 404 responses include a small JSON message)
            var respBody = await pollenResp.Content.ReadAsStringAsync();

            if (!nearbySearch)
            {
                if (pollenResp.StatusCode == HttpStatusCode.NotFound)
                {
                    pollenSummary = $"Pollen data not available for this location (404). API response: {respBody}";
                    Console.WriteLine($"Pollen request 404: {pollenUri}. Body: {respBody}");
                }
                else
                {
                    pollenSummary = $"Pollen request failed: {pollenResp.StatusCode}. API response: {respBody}";
                    Console.WriteLine($"Pollen request failed: {pollenResp.StatusCode} ({pollenUri}). Body: {respBody}");
                }

                return pollenSummary;
            }

            // nearbySearch == true: probe nearby coordinates in increasing distance
            try
            {
                // build candidate offsets and sort by squared distance
                var candidates = new List<(double lat, double lon, double dist)>();
                for (double latOff = -maxRadiusDegrees; latOff <= maxRadiusDegrees; latOff += stepDegrees)
                {
                    for (double lonOff = -maxRadiusDegrees; lonOff <= maxRadiusDegrees; lonOff += stepDegrees)
                    {
                        if (Math.Abs(latOff) < 1e-12 && Math.Abs(lonOff) < 1e-12) continue; // skip origin
                        var d2 = latOff * latOff + lonOff * lonOff;
                        candidates.Add((latitude + latOff, longitude + lonOff, d2));
                    }
                }

                var ordered = candidates.OrderBy(c => c.dist).ToList();
                var attempts = 0;
                var maxAttempts = Math.Min(ordered.Count, 500); // safeguard against too many calls

                foreach (var c in ordered.Take(maxAttempts))
                {
                    attempts++;
                    var probeUri = $"https://air-quality-api.open-meteo.com/v1/air-quality?latitude={c.lat}&longitude={c.lon}&current={CurrentFields}&timezone=UTC";
                    using var probeResp = await _httpClient.GetAsync(probeUri);
                    if (!probeResp.IsSuccessStatusCode) continue;

                    // Parse the successful response and return a note indicating which coordinate was used
                    await using var ps2 = await probeResp.Content.ReadAsStreamAsync();
                    using var doc2 = await JsonDocument.ParseAsync(ps2);
                    if (doc2.RootElement.TryGetProperty("current", out var curr2))
                    {
                        var foundSummary = BuildGroupedAirQualitySummary(curr2, c.lat, c.lon);
                        var note = $"Air quality for nearest available point at latitude {c.lat:F6}, longitude {c.lon:F6} (searched {attempts} nearby points within {maxRadiusDegrees}°):\n{foundSummary}";
                        Console.WriteLine($"Pollen nearby match for {pollenUri} -> {probeUri} (attempt {attempts})");
                        return note;
                    }
                }

                // nothing found within radius
                pollenSummary = $"No pollen coverage found within {maxRadiusDegrees}° of latitude {latitude:F6}, longitude {longitude:F6}. Original API response: {respBody}";
                Console.WriteLine($"Pollen nearby search exhausted for {pollenUri} after {attempts} attempts. Last response body: {respBody}");
            }
            catch (Exception ex)
            {
                pollenSummary = $"Pollen request failed and nearby search errored: {ex.Message}. Original API response: {respBody}";
                Console.WriteLine($"Pollen nearby search exception for {pollenUri}: {ex}");
            }
         }
 
         return pollenSummary;
     }

    private static string GetLastValueOrNA(JsonElement arr)
    {
        if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() == 0) return "N/A";
        var last = arr[arr.GetArrayLength() - 1];
        if (last.ValueKind == JsonValueKind.Number)
        {
            return last.GetDouble().ToString("0.#", CultureInfo.InvariantCulture);
        }
        if (last.ValueKind == JsonValueKind.Null) return "N/A";
        return last.ToString();
    }
    // Helper for ordinal day formatting (1st, 2nd, 3rd, 4th, ...)
    private static string GetDayOrdinal(int day)
    {
        if (day is 11 or 12 or 13)
            return $"{day}th";
        return (day % 10) switch
        {
            1 => $"{day}st",
            2 => $"{day}nd",
            3 => $"{day}rd",
            _ => $"{day}th"
        };
    }

    private static string ToLabel(string name)
    {
        return name switch
        {
            "temperature_2m" => "Temperature",
            "grass_pollen" => "Grass Pollen",
            "birch_pollen" => "Birch Pollen",
            "ragweed_pollen" => "Ragweed Pollen",
            "olive_pollen" => "Olive Pollen",
            "mugwort_pollen" => "Mugwort Pollen",
            "alder_pollen" => "Alder Pollen",
            "ammonia" => "Ammonia",
            "uv_index_clear_sky" => "UV Index (Clear Sky)",
            "uv_index" => "UV Index",
            "dust" => "Dust",
            "aerosol_optical_depth" => "Aerosol Optical Depth",
            "ozone" => "Ozone",
            "sulphur_dioxide" => "Sulphur Dioxide",
            "nitrogen_dioxide" => "Nitrogen Dioxide",
            "carbon_monoxide" => "Carbon Monoxide",
            "pm2_5" => "PM2.5",
            "pm10" => "PM10",
            "us_aqi" => "US AQI",
            "european_aqi" => "European AQI",
            _ => name
        };
    }

    [KernelFunction, Description("Check a set of common cities and report which ones have pollen data available from Open-Meteo.")]
    public async Task<string> CitiesWithPollen()
    {
        var cities = new[] { "Berlin", "London", "Paris", "New York", "Los Angeles", "Tokyo", "Sydney", "Bucharest", "Madrid", "Rome", "Amsterdam", "Stockholm", "Toronto", "Chicago", "Seoul", "Beijing" };
        var results = new Dictionary<string, object>();

        foreach (var city in cities)
        {
            try
            {
                var coords = await GetCoordinates(city);
                if (coords is null)
                {
                    results[city] = new { available = false, reason = "geocoding_failed" };
                    continue;
                }

                var pollenUri = $"https://air-quality-api.open-meteo.com/v1/air-quality?latitude={coords.Value.Latitude}&longitude={coords.Value.Longitude}&current={CurrentFields}&timezone=UTC";
                using var resp = await _httpClient.GetAsync(pollenUri);
                var body = await resp.Content.ReadAsStringAsync();

                if (resp.IsSuccessStatusCode)
                {
                    results[city] = new { available = true, latitude = coords.Value.Latitude, longitude = coords.Value.Longitude };
                }
                else
                {
                    results[city] = new { available = false, status = (int)resp.StatusCode, reason = body };
                }
            }
            catch (Exception ex)
            {
                results[city] = new { available = false, exception = ex.Message };
            }
        }

        return JsonSerializer.Serialize(results, JsonOptions);
    }

    // Format the 'current' air-quality object into grouped, human-friendly sections.
    private static string BuildGroupedAirQualitySummary(JsonElement curr, double lat, double lon)
    {
        var sb = new StringBuilder();

        // Timestamp
        if (curr.TryGetProperty("time", out var timeEl) && timeEl.ValueKind == JsonValueKind.String)
        {
            sb.AppendLine($"Time (UTC): {timeEl.GetString()}");
        }

        // Helper to read a value as string
        static string? ReadValue(JsonElement e, string name)
        {
            if (!e.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.Null) return null;
            if (v.ValueKind == JsonValueKind.Number) return v.GetDouble().ToString("0.#", CultureInfo.InvariantCulture);
            return v.ToString();
        }

        // Pollen group
        var pollenFields = new[] { "grass_pollen", "birch_pollen", "ragweed_pollen", "olive_pollen", "mugwort_pollen", "alder_pollen" };
        var pollenLines = new List<string>();
        foreach (var f in pollenFields)
        {
            var v = ReadValue(curr, f);
            if (v is not null) pollenLines.Add($"- {ToLabel(f)}: {v} grains/m³");
        }
        if (pollenLines.Count > 0)
        {
            sb.AppendLine("Pollen:");
            foreach (var l in pollenLines) sb.AppendLine("  " + l);
            sb.AppendLine();
        }

        // Gases
        var gasFields = new[] { "ammonia", "ozone", "sulphur_dioxide", "nitrogen_dioxide", "carbon_monoxide" };
        var gasLines = new List<string>();
        foreach (var f in gasFields)
        {
            var v = ReadValue(curr, f);
            if (v is not null) gasLines.Add($"- {ToLabel(f)}: {v} μg/m³");
        }
        if (gasLines.Count > 0)
        {
            sb.AppendLine("Gases:");
            foreach (var l in gasLines) sb.AppendLine("  " + l);
            sb.AppendLine();
        }

        // Particulates
        var partFields = new[] { "pm2_5", "pm10", "dust", "aerosol_optical_depth" };
        var partLines = new List<string>();
        foreach (var f in partFields)
        {
            var v = ReadValue(curr, f);
            if (v is not null)
            {
                var unit = f == "aerosol_optical_depth" ? string.Empty : " μg/m³";
                partLines.Add($"- {ToLabel(f)}: {v}{unit}");
            }
        }
        if (partLines.Count > 0)
        {
            sb.AppendLine("Particulates:");
            foreach (var l in partLines) sb.AppendLine("  " + l);
            sb.AppendLine();
        }

        // UV
        var uvLines = new List<string>();
        var uv1 = ReadValue(curr, "uv_index");
        var uv2 = ReadValue(curr, "uv_index_clear_sky");
        if (uv1 is not null) uvLines.Add($"- UV Index: {uv1}");
        if (uv2 is not null) uvLines.Add($"- UV Index (clear sky): {uv2}");
        if (uvLines.Count > 0)
        {
            sb.AppendLine("UV:");
            foreach (var l in uvLines) sb.AppendLine("  " + l);
            sb.AppendLine();
        }

        // AQI
        var aqiLines = new List<string>();
        var us = ReadValue(curr, "us_aqi");
        var eu = ReadValue(curr, "european_aqi");
        if (us is not null) aqiLines.Add($"- US AQI: {us}");
        if (eu is not null) aqiLines.Add($"- European AQI: {eu}");
        if (aqiLines.Count > 0)
        {
            sb.AppendLine("Air Quality Index:");
            foreach (var l in aqiLines) sb.AppendLine("  " + l);
            sb.AppendLine();
        }

        sb.AppendLine($"Location: {lat:F6}, {lon:F6}");
        return sb.ToString().TrimEnd();
    }

    // Retrieve relative humidity (percent) for the given coordinate and UTC timestamp (or current UTC hour if timestamp null)
    private async Task<float?> GetHumidityValue(double latitude, double longitude, DateTime? timestampUtc)
    {
        var uri = $"https://api.open-meteo.com/v1/forecast?latitude={latitude}&longitude={longitude}&hourly=relativehumidity_2m&timezone=UTC";
        using var resp = await _httpClient.GetAsync(uri);
        if (!resp.IsSuccessStatusCode) return null;
        var content = await resp.Content.ReadAsStringAsync();
        var hum = JsonSerializer.Deserialize<HumidityResponse>(content, JsonOptions);
        if (hum?.Hourly == null) return null;

        // Parse times into UTC and find index matching timestampUtc hour
        var times = hum.Hourly.Time.Select(t => DateTime.SpecifyKind(DateTime.Parse(t), DateTimeKind.Utc)).ToList();
        DateTime target = timestampUtc ?? DateTime.UtcNow;
        var targetHour = new DateTime(target.Year, target.Month, target.Day, target.Hour, 0, 0, DateTimeKind.Utc);

        int idx = times.FindIndex(t => t == targetHour);
        if (idx == -1) idx = times.FindLastIndex(t => t <= targetHour);
        if (idx == -1) return null;

        if (hum.Hourly.RelativeHumidity is { Count: > 0 } rh && rh.Count > idx) return rh[idx];
        return null;
    }

    // DTOs for deserializing API responses
    public record GeocodingResponse(List<LocationResult> Results);
    public record LocationResult(double Latitude, double Longitude);

    public record AirQualityResponse([property: System.Text.Json.Serialization.JsonPropertyName("hourly")] HourlyData Hourly);
    public record HourlyData(
        [property: System.Text.Json.Serialization.JsonPropertyName("time")] List<string> Time,
        [property: System.Text.Json.Serialization.JsonPropertyName("temperature_2m")] List<float?> Temperature,
        [property: System.Text.Json.Serialization.JsonPropertyName("grass_pollen")] List<float?> GrassPollen,
        [property: System.Text.Json.Serialization.JsonPropertyName("birch_pollen")] List<float?>? BirchPollen,
        [property: System.Text.Json.Serialization.JsonPropertyName("ragweed_pollen")] List<float?>? RagweedPollen
    );

    public record WeatherSummaryData
    {
        public double Latitude { get; init; }
        public double Longitude { get; init; }
        public DateTime? TimestampUtc { get; init; }
        public string? FriendlyLocalTime { get; init; }
        public float? TemperatureC { get; init; }
        public float? TemperatureF { get; init; }
        public float? WindSpeedKmh { get; init; }
        public float? WindDirectionDeg { get; init; }
        public float? GrassPollen { get; init; }
        public float? BirchPollen { get; init; }
        public float? RagweedPollen { get; init; }
        public string TemperatureUnit { get; init; } = "C";
        public string PollenUnit { get; init; } = "grains/m^3";
        public string? PollenFallbackSummary { get; init; }
    }

    // DTOs for forecast current weather
    public record ForecastResponse([property: System.Text.Json.Serialization.JsonPropertyName("current_weather")] CurrentWeather? CurrentWeather);
    public record CurrentWeather(
        [property: System.Text.Json.Serialization.JsonPropertyName("temperature")] float? Temperature,
        [property: System.Text.Json.Serialization.JsonPropertyName("time")] string Time,
        [property: System.Text.Json.Serialization.JsonPropertyName("windspeed")] float? WindSpeed,
        [property: System.Text.Json.Serialization.JsonPropertyName("winddirection")] float? WindDirection
    );

    // DTOs for humidity
    public record HumidityResponse(HourlyHumidity Hourly);
    public record HourlyHumidity([property: System.Text.Json.Serialization.JsonPropertyName("time")] List<string> Time, [property: System.Text.Json.Serialization.JsonPropertyName("relativehumidity_2m")] List<float?> RelativeHumidity);

    private static string DegreesToCompass(double degrees)
    {
        string[] cardinals = { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW", "N" };
        var idx = (int)Math.Round(((degrees % 360) / 22.5));
        if (idx < 0) idx = 0;
        if (idx >= cardinals.Length) idx = cardinals.Length - 1;
        return cardinals[idx];
    }
}