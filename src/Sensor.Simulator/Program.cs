using System.Net;
using System.Net.Http.Json;
using Shared.Contracts;

var baseUrl = args.Length > 0 ? args[0] : "http://localhost:5081";
using var client = new HttpClient { BaseAddress = new Uri(baseUrl) };

Console.WriteLine($"=== KIỂM THỬ & GIẢ LẬP CẢM BIẾN IOT ({baseUrl}) ===\n");

// Hoạt động 1: GET / - Kiểm tra trạng thái dịch vụ (Bài TH 1.2 & 2.2)
var rootRes = await client.GetAsync("/");
Console.WriteLine($"[1] GET / -> Status: {(int)rootRes.StatusCode} {rootRes.StatusCode}");
Console.WriteLine($"    Body: {await rootRes.Content.ReadAsStringAsync()}\n");

// Hoạt động 2: POST /sensors với dữ liệu SAI để kiểm tra Validation 400 (Bài TH 2.2 - Bước 6)
var invalidSensor = new CreateSensorRequest("   ", "Temperature", "Phòng Server", 50, 20);
var badRes = await client.PostAsJsonAsync("/sensors", invalidSensor);
Console.WriteLine($"[2] POST /sensors (Dữ liệu sai) -> Status: {(int)badRes.StatusCode} {badRes.StatusCode}");
if (badRes.StatusCode == HttpStatusCode.BadRequest)
{
    var err = await badRes.Content.ReadFromJsonAsync<ApiError>();
    Console.WriteLine($"    ApiError -> Code: {err?.Code}, Message: {err?.Message}\n");
}

// Hoạt động 3: POST /sensors với dữ liệu HỢP LỆ -> Nhận 201 Created + Location
var validSensor = new CreateSensorRequest("  Cảm biến nhiệt độ Server 01  ", "Temperature", "Phòng Server A", 15.0, 35.0);
var createRes = await client.PostAsJsonAsync("/sensors", validSensor);
Console.WriteLine($"[3] POST /sensors (Dữ liệu hợp lệ) -> Status: {(int)createRes.StatusCode} {createRes.StatusCode}");
Console.WriteLine($"    Location Header: {createRes.Headers.Location}");

var createdSensor = await createRes.Content.ReadFromJsonAsync<SensorDto>();
Console.WriteLine($"    Created ID: {createdSensor?.Id} | Name (đã Trim): '{createdSensor?.Name}'\n");

// Hoạt động 4: GET /sensors/{id} & GET /sensors?keyword=Server
if (createdSensor is not null)
{
    var detail = await client.GetFromJsonAsync<SensorDto>($"/sensors/{createdSensor.Id}");
    Console.WriteLine($"[4] GET /sensors/{createdSensor.Id} -> Name: {detail?.Name}, Ngưỡng: [{detail?.MinThreshold} .. {detail?.MaxThreshold}]");

    var filtered = await client.GetFromJsonAsync<List<SensorDto>>("/sensors?keyword=Server");
    Console.WriteLine($"    GET /sensors?keyword=Server -> Tìm thấy {filtered?.Count} cảm biến.\n");

    // Hoạt động 5: Gửi 5 gói dữ liệu đo (có gói vượt ngưỡng > 35.0 để kích hoạt Alert)
    double[] sampleValues = [24.5, 28.0, 36.8, 31.2, 42.5];
    Console.WriteLine("[5] Bắt đầu gửi dữ liệu đo lên RabbitMQ qua API:");
    foreach (var val in sampleValues)
    {
        var readRes = await client.PostAsJsonAsync($"/sensors/{createdSensor.Id}/readings", new CreateReadingRequest(val, DateTime.UtcNow));
        Console.WriteLine($"    Gửi Value = {val} -> Status: {(int)readRes.StatusCode} ({(val > 35.0 ? "VƯỢT NGƯỠNG!" : "Bình thường")})");
        await Task.Delay(1000);
    }
}